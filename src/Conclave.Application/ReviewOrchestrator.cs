using System.Collections.Concurrent;
using Conclave.Application.Ports;
using Conclave.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conclave.Application;

/// <summary>
/// 编排循环：认领席位 → 跑评审 → 投票 → 收齐 quorum 后公布。
/// </summary>
/// <remarks>
/// <para>
/// 席位分配每一轮都从 Acta 与当前 mesh 视图重新计算，不缓存。因为它是纯函数
/// （<see cref="SeatAssignment.Seats"/>），重算的结果与其他节点一致，
/// 于是「谁评审哪个 PR」不需要任何协商消息。
/// </para>
/// <para>
/// 席位用的 <see cref="PrMeta"/> 一律取自 Summons 块的快照，而不是重新查 Azure DevOps ——
/// 否则 PR 描述被人改一下就可能让不同节点算出不同席位。
/// </para>
/// </remarks>
public sealed class ReviewOrchestrator : BackgroundService
{
    private readonly IActaStore _acta;
    private readonly IMesh _mesh;
    private readonly IReviewRunner _runner;
    private readonly IPrSource _prSource;
    private readonly NodeState _state;
    private readonly ConclaveOptions _options;
    private readonly ILogger<ReviewOrchestrator> _logger;
    private readonly ReservedMatters _reserved = ReservedMatters.Default;

    /// <summary>正在跑的 <c>revisionId#round</c>，防止同一席位被重复开跑。</summary>
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);

    /// <summary>UI 手动点过「评审」的 Revision。绕过 <see cref="ConclaveOptions.AutoReview"/>。</summary>
    private readonly ConcurrentDictionary<string, byte> _requested = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _slots;

    public ReviewOrchestrator(
        IActaStore acta,
        IMesh mesh,
        IReviewRunner runner,
        IPrSource prSource,
        NodeState state,
        ConclaveOptions options,
        ILogger<ReviewOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _acta = acta;
        _mesh = mesh;
        _runner = runner;
        _prSource = prSource;
        _state = state;
        _options = options;
        _logger = logger;
        _slots = new SemaphoreSlim(Math.Max(1, options.MaxConcurrent));
    }

    /// <summary>UI 手动触发某个 Revision 的评审。</summary>
    public void RequestReview(string revisionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);
        _requested[revisionId] = 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.OrchestratorInterval);
        do
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "编排循环失败");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var self = _mesh.Self;
        var views = new List<PrView>();

        foreach (var chainId in await _acta.ReadOpenChainsAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            var blocks = await _acta.ReadChainAsync(chainId, ct).ConfigureAwait(false);

            // 只处理链上最新的 Revision。旧版本没跑完也不补 —— PR 已经变了，评它没意义。
            var revision = ActaProjection.RevisionsOf(blocks).LastOrDefault();
            if (revision is null)
            {
                continue;
            }

            var chain = ActaProjection.Project(blocks, revision.Id);
            if (chain.Summons is null)
            {
                continue;
            }

            var pr = chain.Summons.Pr;
            var seats = SeatAssignment.Seats(revision, pr, _mesh.Alive, _reserved, now);
            var mySeat = IndexOfSelf(seats, self.Id);

            views.Add(BuildView(revision, chain, mySeat));

            if (chain.IsFinished)
            {
                _ = _requested.TryRemove(revision.Id, out _);
                continue;
            }

            var wanted = _options.AutoReview || _requested.ContainsKey(revision.Id);

            await ReleaseTimedOutSeatsAsync(revision, chain, now, ct).ConfigureAwait(false);

            if (wanted && mySeat >= 0 && !chain.Seatings.ContainsKey(mySeat))
            {
                await TakeSeatAsync(revision, mySeat, self.Id, ct).ConfigureAwait(false);
                continue;   // 下一轮再跑评审，让 Seating 先落链
            }

            if (mySeat >= 0
                && chain.Seatings.TryGetValue(mySeat, out var seated)
                && seated.ElectorId == self.Id
                && !chain.Ballots.ContainsKey(mySeat))
            {
                StartReview(revision, pr, mySeat);
            }

            if (chain.CanPromulgate && IsPromulgator(seats, self.Id))
            {
                await PromulgateAsync(revision, pr, chain, ct).ConfigureAwait(false);
            }
        }

        _state.SetPipeline(views);
        _state.SetRecentBlocks(await _acta.ReadRecentAsync(60, ct).ConfigureAwait(false));
    }

    private static int IndexOfSelf(IReadOnlyList<string> seats, string selfId)
    {
        for (var i = 0; i < seats.Count; i++)
        {
            if (seats[i] == selfId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// round=0 的节点负责公布。席位表为空（合格节点都掉线了）时由任意节点兜底，
    /// 否则一个 PR 会永远停在「票齐但没人公布」。
    /// </summary>
    private static bool IsPromulgator(IReadOnlyList<string> seats, string selfId)
        => seats.Count == 0 || seats[0] == selfId;

    private async Task TakeSeatAsync(Revision revision, int round, string selfId, CancellationToken ct)
    {
        var block = await _acta.AppendAsync(
            revision.ChainId,
            BlockKind.Seating,
            new SeatingPayload(revision.Id, round, selfId),
            ct).ConfigureAwait(false);

        await _mesh.BroadcastAsync(block, ct).ConfigureAwait(false);
        _logger.LogInformation("入席 {Revision} round={Round}", revision.Id, round);
    }

    /// <summary>
    /// 认领后超时未出票的席位写 Recess，让下一轮重新 HRW 接管。
    /// </summary>
    /// <remarks>
    /// 任何节点都可以写 Recess，不必是原认领者 —— 原认领者可能已经掉线了，这正是要处理的情况。
    /// </remarks>
    private async Task ReleaseTimedOutSeatsAsync(
        Revision revision, ChainState chain, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (round, at) in chain.SeatedAt)
        {
            if (chain.Ballots.ContainsKey(round) || chain.Recessed.Contains(round))
            {
                continue;
            }

            if (now - at <= _options.SeatingTimeout)
            {
                continue;
            }

            // 本节点自己正在跑的席位不能弃权 —— 只是慢，不是掉线。
            if (_inFlight.ContainsKey(SlotKey(revision.Id, round)))
            {
                continue;
            }

            var block = await _acta.AppendAsync(
                revision.ChainId,
                BlockKind.Recess,
                new RecessPayload(revision.Id, round, $"seating timeout after {_options.SeatingTimeout}"),
                ct).ConfigureAwait(false);

            await _mesh.BroadcastAsync(block, ct).ConfigureAwait(false);
            _logger.LogWarning("席位超时 {Revision} round={Round}，已弃权", revision.Id, round);
        }
    }

    private static string SlotKey(string revisionId, int round) => $"{revisionId}#{round}";

    /// <summary>
    /// 后台跑评审。刻意不 await —— 一次评审可能十几分钟，编排循环必须马上继续转。
    /// </summary>
    private void StartReview(Revision revision, PrMeta pr, int round)
    {
        var slot = SlotKey(revision.Id, round);
        if (!_inFlight.TryAdd(slot, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var acquired = false;
            try
            {
                await _slots.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                acquired = true;
                UpdateLoad(+1);

                _logger.LogInformation("开始评审 {Revision} round={Round} {Repo}", revision.Id, round, pr.Repo);

                using var timeout = new CancellationTokenSource(_options.ReviewTimeout);
                var ballot = await _runner.RunAsync(revision, pr, round, timeout.Token).ConfigureAwait(false);

                var block = await _acta.AppendAsync(
                    revision.ChainId, BlockKind.Ballot, ballot, CancellationToken.None).ConfigureAwait(false);
                await _mesh.BroadcastAsync(block, CancellationToken.None).ConfigureAwait(false);

                _logger.LogInformation(
                    "投票 {Revision} round={Round} → {Decision}，{Count} 条 finding，耗时 {Ms}ms",
                    revision.Id, round, ballot.Decision, ballot.Findings.Count, ballot.DurationMs);
            }
            catch (Exception ex)
            {
                // 失败也要出票：链上留痕，且计入 quorum 分母，否则这个 PR 会永远等下去。
                _logger.LogError(ex, "评审 {Revision} round={Round} 失败", revision.Id, round);
                try
                {
                    await _acta.AppendAsync(
                        revision.ChainId,
                        BlockKind.Ballot,
                        new BallotPayload(revision.Id, round, ReviewDecision.Error, [], "n/a", 0, ex.Message),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception inner)
                {
                    _logger.LogError(inner, "写 Error 票也失败了 {Revision} round={Round}", revision.Id, round);
                }
            }
            finally
            {
                if (acquired)
                {
                    UpdateLoad(-1);
                    _ = _slots.Release();
                }

                _ = _inFlight.TryRemove(slot, out _);
            }
        });
    }

    private void UpdateLoad(int delta)
        => _mesh.UpdateSelf(self => self with
        {
            RunningJobs = Math.Max(0, self.RunningJobs + delta),
            LastHeartbeat = DateTimeOffset.UtcNow,
        });

    private async Task PromulgateAsync(
        Revision revision, PrMeta pr, ChainState chain, CancellationToken ct)
    {
        var ballots = chain.Ballots.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        var merged = QuorumEngine.Merge(revision.Id, ballots, chain.Quorum);

        int? threadId = null;
        if (_options.PostToAzureDevOps)
        {
            try
            {
                threadId = await _prSource.PostResultAsync(pr, merged, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 投递失败不阻止公布：结论已经算出来了，链上先记下，投递可以人工补。
                _logger.LogError(ex, "投递 {Revision} 到 Azure DevOps 失败，结论仍写入 Acta", revision.Id);
            }
        }

        var block = await _acta.AppendAsync(
            revision.ChainId,
            BlockKind.Promulgation,
            merged with { ThreadId = threadId },
            ct).ConfigureAwait(false);

        await _mesh.BroadcastAsync(block, ct).ConfigureAwait(false);
        _ = _requested.TryRemove(revision.Id, out _);

        _logger.LogInformation(
            "公布 {Revision} → {Decision}，{Count} 条合并 finding（{Actual}/{Expected} 票{Degraded}）",
            revision.Id, merged.Decision, merged.Findings.Count,
            merged.ActualQuorum, merged.ExpectedQuorum, merged.Degraded ? "，降级" : string.Empty);
    }

    private PrView BuildView(Revision revision, ChainState chain, int mySeat)
    {
        var stage = chain switch
        {
            { Promulgation: not null } => "已公布",
            _ when _inFlight.Keys.Any(k => k.StartsWith(revision.Id + "#", StringComparison.Ordinal)) => "评审中",
            { Ballots.Count: > 0 } => $"已投票 {chain.Ballots.Count}/{chain.Quorum}",
            { Seatings.Count: > 0 } => $"已入席 {chain.Seatings.Count}/{chain.Quorum}",
            _ => "待评审",
        };

        return new PrView(
            revision,
            chain.Summons!.Pr,
            stage,
            chain.Quorum,
            chain.Ballots.Count,
            chain.Promulgation?.Decision,
            chain.Promulgation?.Findings.Count ?? 0,
            mySeat);
    }

    public override void Dispose()
    {
        _slots.Dispose();
        base.Dispose();
    }
}

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

    /// <summary>
    /// 正在跑的 <c>revisionId#round</c> → 该次评审的完成信号。
    /// </summary>
    /// <remarks>
    /// 存 Task 而不是占位符，是为了 <see cref="WhenIdleAsync"/> 能等到评审真正收尾 ——
    /// 测试要消掉 fire-and-forget 的不确定性，停机时也需要知道还有活没干完。
    /// </remarks>
    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.Ordinal);

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

    /// <summary>
    /// 单步驱动一轮编排。后台循环按 <see cref="ConclaveOptions.OrchestratorInterval"/> 调它，
    /// 测试直接调它来避免依赖计时器。
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var self = _mesh.Self;
        var views = new List<PrView>();

        // 先把开放的 revision 按 PR 收敛到最新一个。作者连着 push 两次会留下一个
        // 被取代的旧版本，它永远不会有结论也不该再评。ReadOpenRevisionsAsync 按链序
        // （即时间序）返回，所以后面的覆盖前面的。
        var latest = new Dictionary<(string Project, int PrId), (Revision Revision, ChainState Chain)>();

        foreach (var revisionId in await _acta.ReadOpenRevisionsAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            var blocks = await _acta.ReadRevisionAsync(revisionId, ct).ConfigureAwait(false);
            var projected = ActaProjection.Project(blocks, revisionId);
            if (projected.Summons is null)
            {
                continue;
            }

            latest[projected.Summons.Revision.PullRequest] = (projected.Summons.Revision, projected);
        }

        foreach (var (revision, chain) in latest.Values)
        {
            ct.ThrowIfCancellationRequested();

            var pr = chain.Summons!.Pr;

            // 传入 Recess 数：每次弃权都追加一个重试轮次，否则单节点 mesh 上
            // 一次超时会让这个 PR 永远卡在「没票也没人接管」。
            var seats = SeatAssignment.Seats(
                revision, pr, _mesh.Members, _reserved, now, extraRounds: chain.Recessed.Count);
            var mySeat = FirstOpenSeat(seats, self.Id, chain);

            views.Add(BuildView(revision, chain, mySeat));

            if (chain.IsFinished)
            {
                _ = _requested.TryRemove(revision.Id, out _);
                continue;
            }

            var wanted = _options.AutoReview || _requested.ContainsKey(revision.Id);

            var justRecessed = await ReleaseTimedOutSeatsAsync(revision, chain, now, ct).ConfigureAwait(false);

            if (wanted && mySeat >= 0 && !chain.Seatings.ContainsKey(mySeat))
            {
                await TakeSeatAsync(revision, mySeat, self.Id, ct).ConfigureAwait(false);
                continue;   // 下一轮再跑评审，让 Seating 先落链
            }

            // 刚被弃权的席位不能接着开跑 —— 否则同一 tick 里既宣布弃权又开始评审，自相矛盾。
            // 弃权后 round 会 +1 重新 HRW，接管者下一轮自己会认领。
            var recessed = justRecessed.Contains(mySeat) || chain.Recessed.Contains(mySeat);

            if (mySeat >= 0
                && !recessed
                && chain.Seatings.TryGetValue(mySeat, out var seated)
                && seated.ElectorId == self.Id
                && !chain.Ballots.ContainsKey(mySeat))
            {
                StartReview(revision, pr, mySeat);
            }

            if (chain.CanPromulgate && IsPromulgator(seats, self.Id, chain))
            {
                await PromulgateAsync(revision, pr, chain, ct).ConfigureAwait(false);
            }
        }

        _state.SetPipeline(views);
        _state.SetRecentBlocks(await _acta.ReadRecentAsync(60, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// 本节点第一个仍然有效的席位轮次；没有则 -1。
    /// </summary>
    /// <remarks>
    /// 必须跳过已弃权的轮次：重试轮次可能又抽到自己（单节点 mesh 上必然如此），
    /// 若仍返回那个被弃权的轮次，本节点会认为自己无事可做而永远旁观。
    /// </remarks>
    private static int FirstOpenSeat(IReadOnlyList<string> seats, string selfId, ChainState chain)
    {
        for (var round = 0; round < seats.Count; round++)
        {
            if (seats[round] == selfId && !chain.Recessed.Contains(round))
            {
                return round;
            }
        }

        return -1;
    }

    /// <summary>
    /// 由最低的未弃权席位的持有者负责公布。
    /// </summary>
    /// <remarks>
    /// 不能死盯 round=0：它可能已经弃权，那样就没人公布了。席位表为空
    /// （合格节点都掉线）时由任意节点兜底，否则票齐了也永远停在那里。
    /// </remarks>
    private static bool IsPromulgator(IReadOnlyList<string> seats, string selfId, ChainState chain)
    {
        for (var round = 0; round < seats.Count; round++)
        {
            if (!chain.Recessed.Contains(round))
            {
                return seats[round] == selfId;
            }
        }

        return true;
    }

    private async Task TakeSeatAsync(Revision revision, int round, string selfId, CancellationToken ct)
    {
        var block = await _acta.AppendAsync(
            revision.Id,
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
    private async Task<IReadOnlyList<int>> ReleaseTimedOutSeatsAsync(
        Revision revision, ChainState chain, DateTimeOffset now, CancellationToken ct)
    {
        var released = new List<int>();

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
                revision.Id,
                BlockKind.Recess,
                new RecessPayload(revision.Id, round, $"seating timeout after {_options.SeatingTimeout}"),
                ct).ConfigureAwait(false);

            await _mesh.BroadcastAsync(block, ct).ConfigureAwait(false);
            released.Add(round);
            _logger.LogWarning("席位超时 {Revision} round={Round}，已弃权", revision.Id, round);
        }

        return released;
    }

    private static string SlotKey(string revisionId, int round) => $"{revisionId}#{round}";

    /// <summary>
    /// 后台跑评审。刻意不 await —— 一次评审可能十几分钟，编排循环必须马上继续转。
    /// </summary>
    private void StartReview(Revision revision, PrMeta pr, int round)
    {
        var slot = SlotKey(revision.Id, round);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inFlight.TryAdd(slot, completion.Task))
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

                // 把人读的评审者身份盖在票上：审计问的是「谁」，ElectorId 只是公钥指纹。
                // 由评审者自己在签名范围内声明，所以不可否认。
                var stamped = ballot with { ReviewerAz = _mesh.Self.AzIdentity };

                var block = await _acta.AppendAsync(
                    revision.Id, BlockKind.Ballot, stamped, CancellationToken.None).ConfigureAwait(false);
                await _mesh.BroadcastAsync(block, CancellationToken.None).ConfigureAwait(false);

                _logger.LogInformation(
                    "投票 {Revision} round={Round} → {Decision}，{Count} 条 finding，"
                        + "耗时 {Ms}ms，{Tokens} token，折合 ${Cost}（{Basis} 价）",
                    revision.Id, round, ballot.Decision, ballot.Findings.Count, ballot.DurationMs,
                    ballot.Metering.TotalTokens, ballot.Metering.CostUsd.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                    ballot.Metering.CostBasis);
            }
            catch (Exception ex)
            {
                // 失败也要出票：链上留痕，且计入 quorum 分母，否则这个 PR 会永远等下去。
                _logger.LogError(ex, "评审 {Revision} round={Round} 失败", revision.Id, round);
                try
                {
                    await _acta.AppendAsync(
                        revision.Id,
                        BlockKind.Ballot,
                        new BallotPayload(
                            revision.Id, round, ReviewDecision.Error, [], "n/a", 0,
                            ReviewUsage.None, _mesh.Self.AzIdentity, ex.Message),
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

                // 先摘牌再置信号：已经拿到快照的等待方仍会看到完成。
                _ = _inFlight.TryRemove(slot, out _);
                completion.SetResult();
            }
        });
    }

    /// <summary>等当前所有在跑的评审收尾。</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_inFlight.Values.ToArray());

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
            revision.Id,
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

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
/// 席位用的 <see cref="PrMeta"/> 与 quorum 一律取自 Summons 块的快照，而不是重新查
/// Azure DevOps 或重算 —— 否则 PR 描述被人改一下、或某台机器的配置不同，
/// 就可能让不同节点算出不同席位。
/// </para>
/// <para>
/// <b>一个 PR 同时只有一个节点在评。</b> quorum 默认 1，席位表就只有 round=0 一个人；
/// 别的节点看到 <see cref="ChainState.IsRoundSettled"/> 为真就不会插手。只有超时弃权
/// （<see cref="ConclaveOptions.SeatingTimeout"/>）或出了 Error 票时那一轮才算「烧掉」，
/// 由下一轮的节点接管，最多 <see cref="ConclaveOptions.MaxReviewAttempts"/> 次。
/// </para>
/// </remarks>
public sealed class ReviewOrchestrator : BackgroundService
{
    private readonly IActaStore _acta;
    private readonly IMesh _mesh;
    private readonly IReviewRunner _runner;
    private readonly IPrSource _prSource;
    private readonly IReviewLog _reviewLog;
    private readonly INotifier _notifier;
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

    /// <summary>
    /// 上一轮的席位分配指纹，用来只在变化时写日志。
    /// </summary>
    /// <remarks>
    /// 初值是 null 而不是空串：一个席位都没分出去时指纹<b>就是</b>空串，
    /// 拿空串当初值的话「全程零席位」这种状态永远不写日志 —— 而那恰好是最需要
    /// 一行日志的状态（「PR 一个都没人评」跟「编排循环没在转」从外面看一模一样）。
    /// </remarks>
    private string? _lastAssignment;

    /// <summary>
    /// 判定「这次评审卡死了」要在 <see cref="ConclaveOptions.ReviewTimeout"/> 之外再等多久。
    /// </summary>
    /// <remarks>
    /// 超时的正常收尾是：CTS 到点 → <c>ProcessRunner</c> 杀掉子进程 → catch 补一张 Error 票，
    /// 几秒就走完。所以超出这个余量还挂着，说明卡在了取消也拉不回来的地方
    /// （实测过的形状是 <c>Kill</c> 杀不掉、<c>WaitForExitAsync</c> 不返回）。
    /// 给 2 分钟是为了绝不误判 —— 误撤一个还在正常跑的评审，代价是两个节点同时评同一轮。
    /// </remarks>
    private static readonly TimeSpan StuckGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 停机信号。在跑的评审靠它被掐断。
    /// </summary>
    /// <remarks>
    /// 不能只靠 <see cref="BackgroundService"/> 的 stoppingToken：评审是
    /// <c>Task.Run</c> 里 fire-and-forget 跑的，而 <see cref="StartReview"/> 只给了
    /// <c>_runner</c> 一个按 <see cref="ConclaveOptions.ReviewTimeout"/> 计时的 CTS ——
    /// 停机根本传不进去。实测的后果：<c>dev-cluster.sh down</c> 之后进程直接退出，
    /// 正在跑的 claude 子进程被连根砍掉，链上一个字都没留，别的节点还得等心跳窗口
    /// 过期才知道这个席位空了。
    /// </remarks>
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>UI 手动点过「评审」的 Revision。绕过 <see cref="ConclaveOptions.AutoReview"/>。</summary>
    private readonly ConcurrentDictionary<string, byte> _requested = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _slots;

    public ReviewOrchestrator(
        IActaStore acta,
        IMesh mesh,
        IReviewRunner runner,
        IPrSource prSource,
        IReviewLog reviewLog,
        INotifier notifier,
        NodeState state,
        ConclaveOptions options,
        ILogger<ReviewOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _acta = acta;
        _mesh = mesh;
        _runner = runner;
        _prSource = prSource;
        _reviewLog = reviewLog;
        _notifier = notifier;
        _state = state;
        _options = options;
        _logger = logger;
        _slots = new SemaphoreSlim(Math.Max(1, options.MaxConcurrent));
    }

    /// <summary>UI 手动触发某个 Revision 的评审（不改认领状态，只是本节点这一轮想跑）。</summary>
    public void RequestReview(string revisionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);
        _requested[revisionId] = 0;
    }

    /// <summary>
    /// 主动认领一个 PR：声明由本节点来评，优先于加权 HRW 的自动分配。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 认领会随实时状态广播出去，别的节点看到之后就不会再插手。两个节点同时认领时按
    /// <see cref="ReviewClaim.Winner"/> 定胜负（先到者，同刻取 electorId 字典序小者）——
    /// 纯函数，各节点独立算出同一结果，输的一方在下一轮编排里自己撤销。
    /// </para>
    /// <para>
    /// 认领本身就是「明确要评」的信号，所以不必再打开 <see cref="ConclaveOptions.AutoReview"/>。
    /// </para>
    /// </remarks>
    public void Claim(string revisionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);

        var now = DateTimeOffset.UtcNow;
        _mesh.UpdateState(s => s.Claims.Any(c => c.RevisionId == revisionId)
            ? s
            : s with { Claims = [.. s.Claims, new ReviewClaim(revisionId, _mesh.Self.Id, now)] });

        _logger.LogInformation("认领 {Revision}", revisionId);
    }

    /// <summary>
    /// 把一个 PR 指派给某个节点评审。对方同意才生效。
    /// </summary>
    /// <remarks>
    /// 这条路的主要用途是绕开一个硬规则的死角：<b>作者自己的 PR 没有任何合格节点</b>
    /// （<see cref="SeatAssignment.Eligible"/> 排除作者本人），于是席位表为空、永远没人评。
    /// 指派给别人是它唯一的出路。
    /// </remarks>
    /// <returns>请求是否送达；<b>不代表对方同意</b>。</returns>
    public async Task<bool> AssignAsync(
        string revisionId, string toElectorId, string? note, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toElectorId);

        var peer = _mesh.Members.FirstOrDefault(m => m.Id == toElectorId);
        if (peer is null)
        {
            _logger.LogWarning("指派失败：mesh 里没有 {Elector}", toElectorId);
            return false;
        }

        var request = new AssignmentRequest(
            Guid.NewGuid().ToString("N")[..12],
            revisionId,
            _mesh.Self.Id,
            toElectorId,
            DateTimeOffset.UtcNow,
            note);

        var sent = await _mesh.SendAssignmentAsync(peer, request, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "指派 {Revision} → {Elector}：{Result}", revisionId, toElectorId, sent ? "已送达" : "送不到");

        // 记下来：界面要能显示「等谁确认」，收到答复时也要靠它校验来源
        if (sent)
        {
            _state.SetAssignmentSent(request.Id, revisionId, toElectorId);
        }

        return sent;
    }

    /// <summary>
    /// 答复一个待确认的指派请求。
    /// </summary>
    /// <remarks>
    /// 同意就当场转成认领 —— 认领会随实时状态广播出去，于是下一轮编排本节点就开跑，
    /// 别的节点也不会再插手。无论同意还是拒绝，那条 pending 都要清掉。
    /// </remarks>
    public async Task RespondToAssignmentAsync(
        string requestId, bool accepted, string? reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var request = _mesh.State.Pending.FirstOrDefault(p => p.Id == requestId);
        if (request is null)
        {
            _logger.LogWarning("答复失败：没有待确认的指派 {Id}", requestId);
            return;
        }

        _mesh.UpdateState(s => s with
        {
            Pending = [.. s.Pending.Where(p => p.Id != requestId)],
            Claims = accepted && !s.Claims.Any(c => c.RevisionId == request.RevisionId)
                ? [.. s.Claims, new ReviewClaim(request.RevisionId, _mesh.Self.Id, DateTimeOffset.UtcNow)]
                : s.Claims,
        });

        var requester = _mesh.Members.FirstOrDefault(m => m.Id == request.From);
        if (requester is not null)
        {
            _ = await _mesh.SendAssignmentReplyAsync(
                requester,
                new AssignmentReply(request.Id, request.RevisionId, accepted, reason),
                ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "{Result}了 {Elector} 对 {Revision} 的指派",
            accepted ? "接受" : "拒绝", request.From, request.RevisionId);
    }

    /// <summary>
    /// 撤销本节点对某个 PR 的认领。
    /// </summary>
    /// <remarks>
    /// 本来就没认领时直接返回，不动状态也不记日志。实测日志里出现过同一个 revision
    /// 隔几秒「撤销认领」两次 —— 第二次什么也没撤，却照样让版本号 +1，
    /// 于是<b>整个 mesh 都为一次空操作拉了一遍 <c>GET /state</c></b>，
    /// 而日志读起来像是真的撤了两回。
    /// </remarks>
    public void Release(string revisionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);

        if (!_mesh.State.Claims.Any(c => c.RevisionId == revisionId))
        {
            return;
        }

        _mesh.UpdateState(s => s with
        {
            Claims = [.. s.Claims.Where(c => c.RevisionId != revisionId)],
        });

        _logger.LogInformation("撤销认领 {Revision}", revisionId);
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
    /// <remarks>
    /// <para>
    /// 队列来自<b>实时状态的并集</b>（<see cref="QueueProjection.Build"/>），链只回答
    /// 「哪些已经评过了」。这跟原先「链上有 Summons、无 Promulgation」的做法是根本区别：
    /// PR 在 Azure DevOps 上一关闭就从发现节点的上报里消失，队列自动干净。
    /// </para>
    /// <para>
    /// 掉线节点的上报被整体忽略，这就是自动接管的全部机制 —— 不再需要 <c>Seating</c>、
    /// <c>Recess</c> 和 10 分钟的席位超时。
    /// </para>
    /// </remarks>
    public async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var self = _mesh.Self;
        var members = _mesh.Members;
        var alive = members.Where(m => m.IsAlive(now)).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);

        var summary = await _acta.ReadSummaryAsync(ct).ConfigureAwait(false);
        var queue = QueueProjection.Build(_mesh.PeerStates, alive, summary.ValidBallots, summary.Finished);

        var views = new List<PrView>(queue.Count);

        // 手上已经有活（在跑的，或在信号量后面排着的）就这一轮不再接新的。
        // 「一个节点一次只评一个 PR」——见 ConclaveOptions.MaxConcurrent 的说明。
        var busy = !_inFlight.IsEmpty;

        // ── 第一遍：读每个 entry 的链上上下文（轮次、出过票的人、复审归属）。
        // 这些都要 I/O，所以必须先收集齐，才能整队做一次席位分配。
        var prepared = new List<(QueueEntry Entry, ChainState Chain, QueueProjection.SeatCandidate Seat)>(queue.Count);

        foreach (var entry in queue)
        {
            ct.ThrowIfCancellationRequested();

            var incumbent = _options.StickyReviewer
                ? await _reviewLog
                    .ReadLastReviewerAsync(entry.Revision.Project, entry.Revision.PrId, ct)
                    .ConfigureAwait(false)
                : null;

            // 先读链：席位下标要按「已烧掉的轮次」算，而那是链上的信息。
            var blocks = entry.Finished
                ? []
                : await _acta.ReadRevisionAsync(entry.Revision.Id, ct).ConfigureAwait(false);

            var chain = entry.Finished
                ? ChainState.Empty
                : ActaProjection.Project(blocks, entry.Revision.Id);

            // 谁已经在这一版上出过票 —— 那些节点不该再坐后续轮次。
            // 票的署名在<b>区块</b>上而不是载荷里，所以只能从 blocks 取。
            var voted = blocks
                .Where(b => b.Kind == BlockKind.Ballot)
                .Select(b => b.ElectorId)
                .ToHashSet(StringComparer.Ordinal);

            var used = chain.SpentRounds.Count;

            // 席位只给还需要有人评的：票收够了（等公布）、这一轮已经出过票、
            // 或者重试到顶了，都不该再占一个席位 —— 一个节点只有一个席位，
            // 占着不评就把后面真正等着的 PR 全堵住了。
            var needsReviewer = !entry.Finished
                && !chain.CanPromulgate(entry.Quorum, _options.MaxReviewAttempts)
                && !chain.IsRoundSettled(used)
                && used < Math.Max(1, _options.MaxReviewAttempts);

            prepared.Add((
                entry,
                chain,
                new QueueProjection.SeatCandidate(entry, incumbent, used, voted, needsReviewer)));
        }

        // ── 一次全局分配：每个节点最多一个席位。逐个 PR 独立算的话，
        // 本机会同时是好几个 PR 的赢家，而它一次只能跑一个 —— 界面和按钮都会骗人。
        var assignment = QueueProjection.AssignSeats(
            prepared.Select(p => p.Seat), members, now);

        LogAssignment(assignment, prepared, self.Id);

        // ── 第二遍：按分配结果建视图、公布、起评审。
        foreach (var (entry, chain, seat) in prepared)
        {
            ct.ThrowIfCancellationRequested();

            var incumbent = seat.Incumbent;
            var roundsUsed = seat.RoundsUsed;
            var mySeat = assignment.TryGetValue(entry.Revision.Id, out var holder) && holder == self.Id
                ? roundsUsed
                : -1;

            // 没有任何节点有资格评它 —— 最常见的原因是作者就是唯一的节点。
            // 那不是「在等」，界面上必须区分开。
            var eligible = members.Any(
                m => SeatAssignment.Eligible(m, entry.Pr, now, entry.AllowSelfReview));

            views.Add(BuildView(
                entry,
                mySeat,
                !eligible,
                summary.Verdicts.GetValueOrDefault(entry.Revision.Id)));

            if (entry.Finished)
            {
                _ = _requested.TryRemove(entry.Revision.Id, out _);
                continue;
            }

            if (chain.CanPromulgate(entry.Quorum, _options.MaxReviewAttempts)
                && IsPromulgator(entry, self.Id, members, now, incumbent))
            {
                await PromulgateAsync(entry, chain, ct).ConfigureAwait(false);
                continue;
            }

            // 认领是「明确要评」的信号，所以它自己就够了，不必再看 AutoReview。
            var claimedByMe = entry.ClaimedBy == self.Id;
            var wanted = claimedByMe || _options.AutoReview || _requested.ContainsKey(entry.Revision.Id);

            var exhausted = roundsUsed >= Math.Max(1, _options.MaxReviewAttempts);

            // busy 这一条是「一个节点一次只评一个 PR」的落点。
            // 少了它，本节点会在同一轮里把所有中签的 PR 都标成「我在评」，
            // 而实际上它们在信号量后面排队 —— 别的空闲节点看到有人接手就不插手了，
            // 活全堆在一台机器上。评完一个再回来挑下一个，谁空谁接。
            if (!busy
                && wanted
                && !exhausted
                && mySeat >= 0
                && !chain.IsRoundSettled(mySeat)
                && !_inFlight.ContainsKey(SlotKey(entry.Revision.Id, mySeat)))
            {
                StartReview(entry.Revision, entry.Pr, mySeat);
                busy = true;
            }
        }

        ReconcileClaims(queue);
        ReconcileReviewing(now);

        _state.SetPipeline(views);
        _state.SetRecentBlocks(await _acta.ReadRecentAsync(60, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// 清掉站不住的认领。
    /// </summary>
    /// <remarks>
    /// 三种情况要撤：认领冲突输了（别人先到）、PR 已经不在队列里（在 Azure DevOps 上关掉了）、
    /// 以及已经有结论了。
    /// <para>
    /// 不撤的后果是静默的：<see cref="QueueProjection.Build"/> 里胜出者是纯函数算的，
    /// 所以输家其实抢不到活；但它的认领会一直挂在自己的实时状态里、随心跳广播出去，
    /// 界面上看是「我认领了却永远不动」。
    /// </para>
    /// </remarks>
    private void ReconcileClaims(IReadOnlyList<QueueEntry> queue)
    {
        var mine = _mesh.State.Claims;
        if (mine.Count == 0)
        {
            return;
        }

        var byRevision = queue.ToDictionary(e => e.Revision.Id, StringComparer.Ordinal);
        var selfId = _mesh.Self.Id;

        var stale = mine
            .Where(c => !byRevision.TryGetValue(c.RevisionId, out var entry)
                     || entry.Finished
                     || (entry.ClaimedBy is not null && entry.ClaimedBy != selfId))
            .Select(c => c.RevisionId)
            .ToHashSet(StringComparer.Ordinal);

        if (stale.Count == 0)
        {
            return;
        }

        _mesh.UpdateState(s => s with
        {
            Claims = [.. s.Claims.Where(c => !stale.Contains(c.RevisionId))],
        });

        _logger.LogInformation("撤销 {Count} 个站不住的认领：{Revisions}", stale.Count, string.Join(", ", stale));
    }

    /// <summary>
    /// 清掉站不住的「正在评审」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ReconcileClaims"/> 管的是「认领」，这条管的是「在评」。后者以前没人管，
    /// 而它泄漏一条的后果比认领严重得多：<b>那个 PR 对整个 mesh 永久隐身</b> ——
    /// 别的节点看到「有人在评」就不插手，而本节点只要不重启就一直这么广播下去，
    /// 界面上是一行永远转不完的「评审中」。
    /// </para>
    /// <para>
    /// 两种撤法，都记 Warning 而不是静默修好：
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <b>task 已经没了但状态还在</b> —— <c>StartReview</c> 的 finally 是无条件跑的，
    /// 而且它先清 <c>Reviewing</c> 再摘 <c>_inFlight</c>，所以正常情况下
    /// <c>Reviewing ⊆ _inFlight</c> 恒成立。命中说明有 bug，不该让它无声无息。
    /// </item>
    /// <item>
    /// <b>超出 <see cref="ConclaveOptions.ReviewTimeout"/> + <see cref="StuckGrace"/> 还挂着</b>
    /// —— 取消也拉不回来的那种。此时那个 claude 子进程可能还在烧额度，人得知道。
    /// </item>
    /// </list>
    /// <para>
    /// 撤的只是<b>广播出去的状态</b>，不动 <c>_inFlight</c> —— 本节点不会重复起跑
    /// （<c>TryAdd</c> 会挡住），但别的节点从此可以接管。代价是那个卡住的 task 万一
    /// 事后又活过来，会为同一轮补一张票；<c>ActaProjection</c> 按 round 取最后一张，
    /// 不会算错，而这种情况本身已经是病态了。
    /// </para>
    /// </remarks>
    private void ReconcileReviewing(DateTimeOffset now)
    {
        var mine = _mesh.State.Reviewing;
        if (mine.Count == 0)
        {
            return;
        }

        var deadline = _options.ReviewTimeout + StuckGrace;
        var stale = new List<ActiveReview>();

        foreach (var review in mine)
        {
            if (!_inFlight.ContainsKey(SlotKey(review.RevisionId, review.Round)))
            {
                _logger.LogWarning(
                    "撤下 {Revision} round={Round} 的「在评」状态：本节点已经没有这个任务了"
                        + "（StartReview 的 finally 应当已经清掉它，走到这里说明有 bug）",
                    review.RevisionId, review.Round);
                stale.Add(review);
                continue;
            }

            var ran = now - review.StartedAt;
            if (ran > deadline)
            {
                _logger.LogWarning(
                    "撤下 {Revision} round={Round} 的「在评」状态：已跑 {Ran}，"
                        + "超过 {Deadline} 仍未收尾（取消没能拉回来，claude 子进程可能还在跑）",
                    review.RevisionId, review.Round, ran, deadline);
                stale.Add(review);
            }
        }

        if (stale.Count == 0)
        {
            return;
        }

        _mesh.UpdateState(s => s with
        {
            Reviewing = [.. s.Reviewing.Where(
                r => !stale.Any(x => x.RevisionId == r.RevisionId && x.Round == r.Round))],
        });
    }

    /// <summary>
    /// 由本节点负责公布这个 PR 的最终结论。
    /// </summary>
    /// <remarks>
    /// 认领者优先；否则按席位表的第一个人。席位表为空（没有合格节点）时由任意节点兜底 ——
    /// 否则票齐了也永远停在那里。
    /// </remarks>
    private static bool IsPromulgator(
        QueueEntry entry,
        string selfId,
        IEnumerable<Elector> members,
        DateTimeOffset now,
        string? incumbent)
    {
        if (entry.ClaimedBy is not null)
        {
            return entry.ClaimedBy == selfId;
        }

        if (entry.ReviewingBy is not null)
        {
            return entry.ReviewingBy == selfId;
        }

        var seats = SeatAssignment.Seats(
            entry.Revision, entry.Pr, members, entry.Quorum, now, incumbent: incumbent);

        return seats.Count == 0 || seats[0] == selfId;
    }


    /// <summary>
    /// 席位分配变了就写一行。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只在变化时写</b>：编排循环每 15 秒转一圈，稳态下每轮都打一遍没有信息量，
    /// 反而把别的日志刷掉。跟额度读数那条是同一个套路（见 <c>DiscoveryService</c>）。
    /// </para>
    /// <para>
    /// 这一行是排查「PR 为什么没人评」的第一眼。分配是纯函数，所以<b>同一时刻各节点的
    /// 这一行应当一模一样</b> —— 对不上就说明实时状态没同步到（心跳丢了、/state 拉失败、
    /// 或者某个节点的成员表里少人），那是完全不同的一类故障。三台机器
    /// <c>grep 席位分配</c> 一比就知道。
    /// </para>
    /// </remarks>
    private void LogAssignment(
        IReadOnlyDictionary<string, string> assignment,
        IReadOnlyList<(QueueEntry Entry, ChainState Chain, QueueProjection.SeatCandidate Seat)> prepared,
        string selfId)
    {
        var seats = assignment
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + "→" + Short(kv.Value) + (kv.Value == selfId ? "(本节点)" : string.Empty))
            .ToList();

        var fingerprint = string.Join(' ', seats);
        if (fingerprint == _lastAssignment)
        {
            return;
        }

        _lastAssignment = fingerprint;

        var wanting = prepared.Count(
            p => p.Seat.NeedsReviewer && p.Entry.ReviewingBy is null && !p.Entry.Finished);

        var running = prepared
            .Where(p => p.Entry.ReviewingBy is not null)
            .Select(p => Short(p.Entry.ReviewingBy!))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        _logger.LogInformation(
            "席位分配：{Seats} · 在评 {Running} · 等空闲节点 {Waiting} 个",
            seats.Count == 0 ? "无" : string.Join(" · ", seats),
            running.Count == 0 ? "无" : string.Join("、", running),
            Math.Max(0, wanting - assignment.Count));

        static string Short(string electorId)
            => electorId.Length > 8 ? electorId[..8] : electorId;
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

                // 排队等并发闸的时候开始停机了 —— 别再开新的评审。
                // 直接 return 走 finally：它一个 token 都没烧，不该为它补 Error 票。
                if (_stopping.IsCancellationRequested)
                {
                    _logger.LogInformation("停机中，放弃开跑 {Revision} round={Round}", revision.Id, round);
                    return;
                }

                // 广播「我在评这个」。别的节点据此不再插手；本节点崩掉就不再广播，
                // 心跳窗口一过它自动从别人的视图里消失、PR 回到队列 —— 这取代了
                // 原先链上的 Seating + Recess + 10 分钟席位超时。
                _mesh.UpdateState(st => st with
                {
                    Reviewing = [.. st.Reviewing, new ActiveReview(revision.Id, round, DateTimeOffset.UtcNow)],
                    // 认领已经兑现成实际开跑了，那个意向状态可以撤掉。
                    Claims = [.. st.Claims.Where(c => c.RevisionId != revision.Id)],
                });

                _logger.LogInformation("开始评审 {Revision} round={Round} {Repo}", revision.Id, round, pr.Repo);

                using var timeout = new CancellationTokenSource(_options.ReviewTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    timeout.Token, _stopping.Token);

                var ballot = await _runner.RunAsync(revision, pr, round, linked.Token).ConfigureAwait(false);

                // 把人读的评审者身份盖在票上：审计问的是「谁」，ElectorId 只是公钥指纹。
                // 由评审者自己在签名范围内声明，所以不可否认。
                // PR 快照跟着票走：Summons 已不上链，投影表要靠它才知道这一票评的是什么。
                var stamped = ballot with { ReviewerAz = _mesh.Self.AzIdentity, Pr = pr };

                var block = await _acta.AppendAsync(
                    revision.Id, BlockKind.Ballot, stamped, CancellationToken.None).ConfigureAwait(false);
                await _mesh.BroadcastAsync(block, CancellationToken.None).ConfigureAwait(false);

                // 一次评审要跑好几分钟，跑完人大概已经在干别的了 —— 结论必须留下来
                _state.Notify(
                    ballot.Decision == ReviewDecision.Error ? NoticeKind.Bad : NoticeKind.Ok,
                    $"评审完 {revision.Id}：{ballot.Decision}，{ballot.Findings.Count} 条 finding",
                    $"{pr.Repo}「{pr.Title}」· 耗时 {ballot.DurationMs / 1000} 秒");

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
                //
                // 停机取消跟真失败分开记。两者对 quorum 的作用一样（都让出这一轮），
                // 但「节点被停了」不是这次评审的问题 —— 半年后回看链上那张 Error 票时，
                // 「The operation was canceled.」什么也说明不了。
                var stopping = _stopping.IsCancellationRequested;
                var reason = stopping ? "节点停机，评审被取消" : ex.Message;

                if (stopping)
                {
                    // 不是错误，是预期行为。用 Error 级别记会让停机日志看起来像出了事故。
                    _logger.LogWarning("停机掐断评审 {Revision} round={Round}，补一张 Error 票",
                        revision.Id, round);
                }
                else
                {
                    _logger.LogError(ex, "评审 {Revision} round={Round} 失败", revision.Id, round);
                }

                try
                {
                    await _acta.AppendAsync(
                        revision.Id,
                        BlockKind.Ballot,
                        new BallotPayload(
                            revision.Id, round, ReviewDecision.Error, [], "n/a", 0,
                            ReviewUsage.None, _mesh.Self.AzIdentity, reason) with { Pr = pr },
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

                _mesh.UpdateState(st => st with
                {
                    Reviewing = [.. st.Reviewing.Where(
                        r => !(r.RevisionId == revision.Id && r.Round == round))],
                });

                // 先摘牌再置信号：已经拿到快照的等待方仍会看到完成。
                _ = _inFlight.TryRemove(slot, out _);
                completion.SetResult();
            }
        });
    }

    /// <summary>等当前所有在跑的评审收尾。</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_inFlight.Values.ToArray());

    /// <summary>
    /// 停机：先停编排循环，再掐断在跑的评审并等它们各补一张 Error 票。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 顺序不能反。先停循环是为了别在停机过程中又认领新席位；后掐评审是因为
    /// <see cref="StartReview"/> 里的任务不受 stoppingToken 管，只认
    /// <see cref="_stopping"/>。
    /// </para>
    /// <para>
    /// 等待受 <paramref name="cancellationToken"/> 约束 —— 它就是
    /// <c>host.StopAsync(5 秒)</c> 那个预算。等不到就记一条 Warning 走人：
    /// 没写上的那张票不会让 PR 卡死，别的节点在心跳窗口过期后照样会把它捡回队列，
    /// 只是慢一点。硬等下去反而会让 <c>down</c> 又变成需要 kill -9 的样子。
    /// </para>
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        if (_inFlight.IsEmpty)
        {
            return;
        }

        _logger.LogInformation("停机：掐断 {Count} 个在跑的评审", _inFlight.Count);
        await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await WhenIdleAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            _logger.LogWarning(
                "停机宽限期内没等到 {Count} 个评审收尾，它们的 Error 票没写上", _inFlight.Count);
        }
    }

    private void UpdateLoad(int delta)
    {
        _mesh.UpdateSelf(self => self with
        {
            RunningJobs = Math.Max(0, self.RunningJobs + delta),
            LastHeartbeat = DateTimeOffset.UtcNow,
        });

        // 菜单栏图标与顶栏状态点靠它换脸 —— 就在负载变化的这一刻，不等下一轮投影
        _state.SetReviewing(_mesh.Self.RunningJobs > 0);
    }

    private async Task PromulgateAsync(QueueEntry entry, ChainState chain, CancellationToken ct)
    {
        var revision = entry.Revision;
        var pr = entry.Pr;

        // 只把有效票交给合并器；Error 票已经在 SpentRounds 里让出过席位了，
        // 再计入分母会让 Promulgation 上的 actualQuorum 把失败次数也算成「评审次数」。
        var ballots = chain.ValidBallots;
        var merged = QuorumEngine.Merge(revision.Id, ballots, entry.Quorum);

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

        _state.Notify(
            NoticeKind.Ok,
            $"已公布 {revision.Id}：{merged.Decision}，{merged.Findings.Count} 条合并 finding",
            _options.PostToAzureDevOps
                ? $"已发到 AzDO · {merged.ActualQuorum}/{merged.ExpectedQuorum} 票"
                : $"未投递（投递开关关着）· {merged.ActualQuorum}/{merged.ExpectedQuorum} 票");

        _logger.LogInformation(
            "公布 {Revision} → {Decision}，{Count} 条合并 finding（{Actual}/{Expected} 票{Degraded}）",
            revision.Id, merged.Decision, merged.Findings.Count,
            merged.ActualQuorum, merged.ExpectedQuorum, merged.Degraded ? "，降级" : string.Empty);

        try
        {
            await _notifier
                .NotifyPromulgationAsync(pr, merged with { ThreadId = threadId }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 跟投递失败同样处理：结论已经在链上，通知补发的成本远低于让编排循环带着异常退出。
            _logger.LogError(ex, "通知 {Revision} 的作者失败，结论已公布", revision.Id);
        }
    }

    /// <summary>
    /// 把队列里的一项铺成界面上的一行。
    /// </summary>
    /// <remarks>
    /// 阶段文案要能区分三种「什么都没发生」：正在等某个节点开跑（已认领）、
    /// 排队等分配、以及<b>永远不会有人评</b>（没有合格节点，通常是作者就是唯一的节点）。
    /// 最后那种以前跟「待评审」长得一样，于是人会一直等一件不会发生的事。
    /// </remarks>
    private PrView BuildView(
        QueueEntry entry, int mySeat, bool nobodyEligible, Verdict? verdict = null)
    {
        var running = _inFlight.Keys.Any(
            k => k.StartsWith(entry.Revision.Id + "#", StringComparison.Ordinal));

        var stage = entry switch
        {
            // 「已评审」而不是「已公布」：投递开关关着时根本没往 Azure DevOps 公布任何东西，
            // 而人要知道的是「这一版评过了」。「公布」是链上那个块的名字，留在 Acta 那张表里。
            { Finished: true } => "已评审",
            _ when running => "评审中（本节点）",
            { ReviewingBy: not null } => "评审中",
            { ClaimedBy: not null } => "已认领",
            _ when nobodyEligible => "无人可评",
            { Ballots: > 0 } => $"已投票 {entry.Ballots}/{entry.Quorum}",
            _ => "待评审",
        };

        return new PrView(
            entry.Revision,
            entry.Pr,
            stage,
            entry.Quorum,
            entry.Ballots,
            verdict?.Decision,
            verdict?.Findings ?? 0,
            mySeat)
        {
            ReviewingBy = entry.ReviewingBy,
            ClaimedBy = entry.ClaimedBy,
            StartedAt = entry.StartedAt,
            NobodyEligible = nobodyEligible,
        };
    }

    public override void Dispose()
    {
        _stopping.Dispose();
        _slots.Dispose();
        base.Dispose();
    }
}

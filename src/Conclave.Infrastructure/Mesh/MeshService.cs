using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure.Mesh;

/// <summary>
/// 把心跳、HTTP 接口和区块 gossip 串起来的常驻服务。
/// </summary>
/// <remarks>
/// 三个循环各自独立、互不阻塞：发心跳、收心跳、接 HTTP 请求。任何一个抛异常都只记日志
/// 并继续 —— 网络抖动是常态，不该让节点从 mesh 里掉出去。
/// </remarks>
public sealed class MeshService(
    ConclaveOptions options,
    ElectorIdentity identity,
    IActaStore acta,
    HttpMesh mesh,
    NodeState state,
    ReviewProgressLog progress,
    ILogger<MeshService> logger,
    ILogger<MeshHttpServer> serverLogger) : BackgroundService
{
    private MeshHttpServer? _server;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Mesh.Enabled)
        {
            logger.LogInformation("mesh 未启用，单机运行");
            return;
        }

        using var beacon = new MeshBeaconSocket(options.Mesh);
        using var server = new MeshHttpServer(
            options.Mesh, acta, () => mesh.Self, mesh.SignedState,
            OnAssignmentReceivedAsync, OnBlockReceivedAsync, progress.Read, serverLogger);
        _server = server;

        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            // 端口被占或权限不足：明确报错并退成单机，而不是静默地看起来在跑
            logger.LogError(ex, "mesh 接口起不来（端口 {Port}），本节点退为单机", options.Mesh.HttpPort);
            state.SetStatus($"mesh 接口起不来：{ex.Message}（已退为单机）");
            return;
        }

        // 端点地址只有服务器起来之后才知道，回填进自己的心跳供对端回连
        mesh.UpdateSelf(self => self with { Endpoint = server.Endpoint });

        // 协议不一致以前只表现成「那台机器掉线了」。现在它是一条明确的通知。
        mesh.ProtocolMismatch += peer => state.Notify(
            NoticeKind.Warn,
            $"节点 {peer.Id[..Math.Min(8, peer.Id.Length)]} 版本不兼容",
            $"它跑的是 v{(peer.AppVersion.Length > 0 ? peer.AppVersion : "?")}（协议 v{peer.ProtocolVersion}），"
            + $"本机 v{AppInfo.Version}（协议 v{Beacon.ProtocolVersion}）。两边要升到同一版才能互认");

        await Task.WhenAll(
            server.ServeAsync(stoppingToken),
            SendLoopAsync(beacon, stoppingToken),
            ReceiveLoopAsync(beacon, stoppingToken)).ConfigureAwait(false);
    }

    private async Task SendLoopAsync(MeshBeaconSocket beacon, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(options.Mesh.BeaconInterval);
        do
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var gonePeers = await mesh
                    .ConfirmAndPruneAsync(now, ct)
                    .ConfigureAwait(false);

                foreach (var gone in gonePeers)
                {
                    // 掉线必须留痕：它在评的 PR 会静默回到队列、席位重算，
                    // 而「刚才还在评的那个 PR 怎么又没人管了」只有这一条答得上。
                    state.Notify(
                        NoticeKind.Warn,
                        $"节点 {gone.Id[..8]} 掉线",
                        $"{gone.AzIdentity} @ {gone.Endpoint} 心跳超时、HTTP 也不通；"
                            + "它在评的 PR 已回到队列");
                }

                mesh.UpdateSelf(self => self with { LastHeartbeat = now });
                var self = mesh.Self;

                // 心跳带上实时状态的版本号 —— 状态本体塞不进 UDP（实测心跳已 1085 字节，
                // 一条队列项约 279 字节，34 条就 10.5KB，远超 MTU），所以只广播版本，
                // 对端看到它变了才去拉一次 GET /state。
                var version = mesh.State.Version;
                await beacon.SendAsync(
                    new Beacon(self, version, identity.Sign(Beacon.SigningPayload(self, version))), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "发心跳失败");
            }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private async Task ReceiveLoopAsync(MeshBeaconSocket beacon, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var received = await beacon.ReceiveAsync(ct).ConfigureAwait(false);
                if (received is null)
                {
                    continue;
                }

                // 「是不是新面孔」得在收编之前问 —— AcceptBeacon 之后它已经在成员表里了。
                var newcomer = !mesh.Members.Any(m => m.Id == received.Elector.Id);

                if (!mesh.AcceptBeacon(received, DateTimeOffset.UtcNow))
                {
                    continue;
                }

                if (newcomer)
                {
                    var peer = received.Elector;
                    state.Notify(
                        NoticeKind.Info,
                        $"节点 {peer.Id[..8]} 上线",
                        $"{peer.AzIdentity} @ {peer.Endpoint} · {peer.Projects.Count} 个 project"
                            + $" · 额度已用 {peer.Utilization:P0}");
                }

                state.SetStatus($"mesh 内 {mesh.Members.Count(m => m.IsAlive(DateTimeOffset.UtcNow))} 个节点在线");

                // 版本变了才拉状态。刻意不 await —— 收心跳的循环不能被一次 HTTP 往返堵住，
                // 否则同一时刻多个节点发心跳会排队积压。
                if (mesh.NeedsStatePull(received.Elector.Id, received.StateVersion))
                {
                    _ = mesh.PullStateAsync(received.Elector, ct).ContinueWith(
                        t => logger.LogDebug(t.Exception, "拉 {Elector} 的实时状态出错", received.Elector.Id),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "收心跳失败");
            }
        }
    }

    /// <summary>
    /// 收到对端推来的区块。
    /// </summary>
    /// <remarks>
    /// 落不进去分两种：一种是本地链尾还没到那个 index（缺块），要向 mesh 回拉补链；
    /// 另一种是验签/白名单/冲突判负，那属于正常拒收，不必补链。
    /// <see cref="IActaStore.TryApplyAsync"/> 只回一个 bool，所以这里用「落不进去且
    /// 本地链尾更短」来区分缺块。
    /// </remarks>
    private async Task OnBlockReceivedAsync(Block block, CancellationToken ct)
    {
        var result = await acta.TryApplyAsync(block, ct).ConfigureAwait(false);
        if (result.Applied)
        {
            // 只转发新块。对「本来就有」也转发会让区块在两个节点之间无限回弹，
            // 每跳都新起 HTTP 请求 —— 双节点实测时进程直接 OOM。
            if (result.Novel)
            {
                await mesh.BroadcastAsync(block, ct).ConfigureAwait(false);
            }

            // 让位后重挂的块换了索引，必须也发出去 —— 否则对端不知道它们搬了家，
            // 两边的链就收敛不了。
            foreach (var moved in result.Rebased)
            {
                await mesh.BroadcastAsync(moved, ct).ConfigureAwait(false);
            }

            return;
        }

        var tail = await ReadTailIndexAsync(ct).ConfigureAwait(false);
        if (block.Index > tail + 1)
        {
            await CatchUpAsync(tail + 1, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 收到一条指派消息。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>请求</b>进待确认列表，等人在界面上点同意/拒绝 —— 刻意不自动接受：
    /// 别人的评审任务会在你的机器上跑 <c>Bash</c>、烧你的 Claude 额度，
    /// 那是要本人点头的事。
    /// </para>
    /// <para>
    /// <b>答复</b>只认被指派方本人的（<c>expectedFrom</c>）—— 否则任何白名单内的节点都能
    /// 替别人「同意」，把活推给一个根本没答应的机器。同意就当场转成认领，
    /// 于是下一轮编排本节点就会开跑。
    /// </para>
    /// </remarks>
    /// <returns>
    /// 收下了没有。<c>false</c> 会让 HTTP 那层回 403，发起方于是知道<b>对方拒收</b>，
    /// 而不是继续显示「等对方确认」。
    /// </returns>
    private Task<bool> OnAssignmentReceivedAsync(
        SignedAssignment signed, bool isReply, CancellationToken ct)
    {
        if (isReply)
        {
            // 先只验签与白名单，拿到 body 才知道它答的是哪一条请求
            var reply = mesh.AcceptAssignment<AssignmentReply>(signed);
            if (reply is null)
            {
                return Task.FromResult(false);
            }

            // 落到本节点自己那条记录上。返回 null = 本节点没发过这条指派，
            // 直接丢弃：白名单只保证「是团队内的机器」，不保证「这条答复该由它来答」。
            // 进程重启后记录丢失也会走到这里，代价是漏一条答复 —— 比允许任何白名单
            // 节点替别人「同意」、把活推给一台根本没答应的机器要好。
            var assignee = state.TryCompleteAssignment(reply.Id, reply.Accepted, reply.Reason);
            if (assignee is null)
            {
                logger.LogWarning(
                    "丢弃 {Elector} 的指派答复 {Id}：本节点没有发过这条指派（可能是重启前发的）",
                    signed.From, reply.Id);
                return Task.FromResult(false);
            }

            // 只有被指派人本人的答复才算数 —— 这正是 AcceptAssignment 的 expectedFrom
            // 想挡的那件事，而它以前没人传。
            if (mesh.AcceptAssignment<AssignmentReply>(signed, assignee) is null)
            {
                return Task.FromResult(false);
            }

            if (reply.Accepted)
            {
                logger.LogInformation(
                    "{Elector} 接受了对 {Revision} 的指派", signed.From, reply.RevisionId);
                state.SetStatus($"{signed.From[..8]} 接受了 {reply.RevisionId} 的评审指派");
                state.Notify(
                    NoticeKind.Ok, $"{signed.From[..8]} 接受了 {reply.RevisionId} 的评审指派");
            }
            else
            {
                logger.LogInformation(
                    "{Elector} 拒绝了对 {Revision} 的指派：{Reason}",
                    signed.From, reply.RevisionId, reply.Reason ?? "（未说明）");
                state.SetStatus(
                    $"{signed.From[..8]} 拒绝了 {reply.RevisionId} 的评审指派"
                    + (reply.Reason is { Length: > 0 } r ? $"：{r}" : string.Empty));
                state.Notify(
                    NoticeKind.Warn,
                    $"{signed.From[..8]} 拒绝了 {reply.RevisionId} 的评审指派",
                    reply.Reason);
            }

            return Task.FromResult(true);
        }

        var request = mesh.AcceptAssignment<AssignmentRequest>(signed);
        if (request is null)
        {
            return Task.FromResult(false);
        }

        if (request.To != mesh.Self.Id)
        {
            logger.LogWarning(
                "丢弃指派请求 {Id}：收件人是 {To}，不是本节点", request.Id, request.To);
            return Task.FromResult(false);
        }

        mesh.UpdateState(s => s.Pending.Any(x => x.Id == request.Id)
            ? s
            : s with { Pending = [.. s.Pending, request] });

        logger.LogInformation(
            "收到 {Elector} 的评审指派请求：{Revision}（{Note}）",
            request.From, request.RevisionId, request.Note ?? "无附言");
        state.SetStatus($"{request.From[..8]} 请你评审 {request.RevisionId}，待确认");

        // 这条最需要留痕：请求方在等你点同意，而状态栏那句话 60 秒后就被轮询覆盖了
        state.Notify(
            NoticeKind.Info, $"{request.From[..8]} 请你评审 {request.RevisionId}", request.Note);

        return Task.FromResult(true);
    }

    /// <summary>本地链尾索引；空链返回 -1。</summary>
    private async Task<long> ReadTailIndexAsync(CancellationToken ct)
    {
        var recent = await acta.ReadRecentAsync(1, ct).ConfigureAwait(false);
        return recent.Count > 0 ? recent[0].Index : -1;
    }

    /// <summary>
    /// 从 mesh 里的节点回拉 <paramref name="fromIndex"/> 起的链段并顺序应用，补齐缺口。
    /// </summary>
    /// <remarks>
    /// 全局单链之后不能整链重传 —— 链会一直长。所以按索引增量拉，
    /// 拉到第一个能补上东西的节点就停。
    /// </remarks>
    private async Task CatchUpAsync(long fromIndex, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var peer in mesh.Members.Where(
            p => p.Id != mesh.Self.Id && p.Endpoint.Length > 0 && p.IsAlive(now)))
        {
            var remote = await mesh.PullChainAsync(peer, fromIndex, ct).ConfigureAwait(false);
            if (remote.Count == 0)
            {
                continue;
            }

            var applied = 0;
            foreach (var b in remote.OrderBy(x => x.Index))
            {
                if ((await acta.TryApplyAsync(b, ct).ConfigureAwait(false)).Applied)
                {
                    applied++;
                }
            }

            if (applied > 0)
            {
                logger.LogInformation(
                    "从 {Elector} 补链（#{From} 起），补进 {Count} 块", peer.Id, fromIndex, applied);
                return;
            }
        }
    }

    public override void Dispose()
    {
        _server?.Dispose();
        base.Dispose();
    }
}

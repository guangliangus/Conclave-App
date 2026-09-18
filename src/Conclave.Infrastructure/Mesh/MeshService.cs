using System.Collections.Concurrent;
using System.Text.Json.Nodes;
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
    SecretOverlay secretOverlay,
    ILogger<MeshService> logger,
    ILogger<MeshHttpServer> serverLogger) : BackgroundService
{
    private MeshHttpServer? _server;

    /// <summary>
    /// 让位重挂之后等着重新广播的块，按块哈希去重。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不在收块的那条路上直接广播。</b> <c>Novel</c> 那道闸挡的是「本来就有的块
    /// 被再转一次」造成的 A→B→A 回弹，对<b>重挂</b>这条路径是失效的：让位重挂会改
    /// <c>Index</c> 和 <c>PrevHash</c> 并<b>重新签名</b>，对端看到的是一个哈希全新的块，
    /// 永远判作 Novel。于是一个入站块能换来「本地链尾有多长」那么多个出站 POST，
    /// 对端让位之后再把它自己的整条尾巴推回来 —— 扇出随跳数往上翻。
    /// </para>
    /// <para>
    /// 所以改成攒起来，由心跳循环每 <see cref="MeshOptions.BeaconInterval"/> 领
    /// <see cref="RepublishPerTick"/> 个出去。收一个块的即时扇出变成 0，放大就不存在了；
    /// 代价是两边的链要多花几个心跳周期才收敛，而那期间队列与席位读的是<b>实时状态</b>
    /// 而不是链，不受影响。
    /// </para>
    /// <para>
    /// 攒过头（<see cref="MaxRepublish"/>）就丢掉多的：那说明两边已经在互相让位地打架，
    /// 再多推几百个块只会烧得更快，让 <see cref="CatchUpAsync"/> 去兜底。
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, Block> _republish = new(StringComparer.Ordinal);

    /// <summary>一个心跳周期最多补推几个重挂的块。</summary>
    private const int RepublishPerTick = 16;

    /// <summary>待补推队列的上限。超了说明在打架，不是在收敛。</summary>
    private const int MaxRepublish = 256;

    /// <summary>补链一次要几块。对端还会按自己的上限再夹一次。</summary>
    private const int ChainPage = MeshHttpServer.MaxChainPage;

    /// <summary>一个对端最多翻几页。防的是「对面一直给得出块、但一块都补不进去」那种死循环。</summary>
    private const int MaxCatchUpPages = 64;

    /// <summary>
    /// 待确认的指派请求最多留多久。
    /// </summary>
    /// <remarks>
    /// 这个列表原先<b>只增不减</b>：收到就 append，只有人在界面上点了同意/拒绝才移除。
    /// 没人点就永远挂着，而且随<b>每一次</b> <c>GET /state</c> 全量序列化广播出去。
    /// 一小时足够人看见那条通知了；过期的自己消失，请求方那边本来也早就不等了
    /// （界面上的锁只有 60 秒）。
    /// </remarks>
    private static readonly TimeSpan PendingTtl = TimeSpan.FromHours(1);

    /// <summary>同时最多挂几条待确认。满了挤掉最老的。</summary>
    private const int MaxPending = 32;

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
            OnAssignmentReceivedAsync, OnBlockReceivedAsync,
            progress.Read, progress.ReadAll, OfferConfig, serverLogger);
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

        // 启动时先把手上那份的版本摆出来，别等第一次心跳循环 —— 邻居据此决定要不要来问。
        if (EnsureHeld() is { } held && mesh.State.ConfigVersion != held.Version)
        {
            mesh.UpdateState(s => s with { ConfigVersion = held.Version });
        }

        await Task.WhenAll(
            server.ServeAsync(stoppingToken),
            SendLoopAsync(beacon, stoppingToken),
            ReceiveLoopAsync(beacon, stoppingToken),
            ConfigSyncLoopAsync(stoppingToken)).ConfigureAwait(false);
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

                // 广播本机手上那份配置的版本。没有「发布节点」，人人都可能是别人的上游。
                var heldVersion = EnsureHeld()?.Version ?? 0;
                if (mesh.State.ConfigVersion != heldVersion)
                {
                    mesh.UpdateState(s => s with { ConfigVersion = heldVersion });
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

                await DrainRepublishAsync(ct).ConfigureAwait(false);
                PrunePending(now);
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

    /// <summary>还没过期的待确认指派，按时间升序。</summary>
    private static IEnumerable<AssignmentRequest> Fresh(
        IEnumerable<AssignmentRequest> pending, DateTimeOffset now)
        => pending.Where(p => now - p.At < PendingTtl).OrderBy(p => p.At);

    /// <summary>
    /// 清掉过期的待确认指派。
    /// </summary>
    /// <remarks>
    /// 挂在心跳循环上而不是只在收到新请求时清 —— 否则一条没人理的请求会一直挂在界面上、
    /// 也一直跟着 <c>GET /state</c> 广播，直到<b>下一条</b>请求到来才被顺手带走。
    /// </remarks>
    private void PrunePending(DateTimeOffset now)
        => mesh.UpdateState(s =>
        {
            var fresh = Fresh(s.Pending, now).ToList();
            return fresh.Count == s.Pending.Count ? s : s with { Pending = [.. fresh] };
        });

    /// <summary>补推一批让位重挂的块。挂在心跳循环上，所以天然限速。</summary>
    private async Task DrainRepublishAsync(CancellationToken ct)
    {
        if (_republish.IsEmpty)
        {
            return;
        }

        var sent = 0;

        foreach (var hash in _republish.Keys.Take(RepublishPerTick))
        {
            if (!_republish.TryRemove(hash, out var block))
            {
                continue;
            }

            await mesh.BroadcastAsync(block, ct).ConfigureAwait(false);
            sent++;
        }

        if (sent > 0)
        {
            logger.LogDebug(
                "补推 {Sent} 个重挂的块，还剩 {Left} 个", sent, _republish.Count);
        }
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
            // 两边的链就收敛不了。但<b>不在这条路上直接发</b>：理由见 _republish。
            foreach (var moved in result.Rebased)
            {
                if (_republish.Count >= MaxRepublish)
                {
                    logger.LogWarning(
                        "待补推的重挂块已达 {Max} 个，丢弃其余的（两边在互相让位，交给补链兜底）",
                        MaxRepublish);
                    break;
                }

                _republish[moved.Hash()] = moved;
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

        var now = DateTimeOffset.UtcNow;
        mesh.UpdateState(s => s.Pending.Any(x => x.Id == request.Id)
            ? s
            : s with { Pending = [.. Fresh(s.Pending, now).TakeLast(MaxPending - 1), request] });

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
            var next = fromIndex;
            var applied = 0;

            // 分页往前挪。整段一次性要回来的话，对端要把那一段链同时以 List<Block>
            // 和一个完整 byte[] 的形态驻留 —— 而链是永远在长的。
            for (var page = 0; page < MaxCatchUpPages; page++)
            {
                var remote = await mesh.PullChainAsync(peer, next, ChainPage, ct).ConfigureAwait(false);
                if (remote.Count == 0)
                {
                    break;
                }

                var before = applied;
                foreach (var b in remote.OrderBy(x => x.Index))
                {
                    if ((await acta.TryApplyAsync(b, ct).ConfigureAwait(false)).Applied)
                    {
                        applied++;
                    }

                    // 按拿到的实际索引往前挪，不按页号算 —— 对端的页大小可能比我们要的小。
                    next = Math.Max(next, b.Index + 1);
                }

                // 这一页一块都落不进去，再往后翻也只会是同样的结果（缺的那块在更前面，
                // 或者这条链根本对不上）。让下一个对端试。
                if (applied == before)
                {
                    break;
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

    /// <summary>
    /// 本机手上那份配置，<b>原作者签的</b>。
    /// </summary>
    /// <remarks>
    /// 转发时原样给出去，绝不重签 —— 重签会让同一份内容以不同发布者 id 在网里流动，
    /// 同版本抢占就不再收敛。见 <see cref="ConfigDocument"/>。
    /// </remarks>
    private ConfigDocument? _held;

    private bool _heldLoaded;

    /// <summary>
    /// 护住 <see cref="_held"/> 的签发与落盘。
    /// </summary>
    /// <remarks>
    /// <see cref="EnsureHeld"/> 被三个地方调：启动时、心跳循环、配置同步循环，而后两个是
    /// 并发跑的（见 <c>Task.WhenAll</c>）。不护的话两边会同时为同一个版本各签一份 ——
    /// ECDSA 每次签出来的字节不同，于是网里出现两份「同版本同作者、签名不同」的文档。
    /// 它们互不抢占（<c>Supersedes</c> 对相等版本相等作者返回 false），所以不会抖，
    /// 但会白写两次盘，而且日志里会出现两条「本机作者了配置 vN」，看着像出了问题。
    /// </remarks>
    private readonly object _heldGate = new();

    /// <summary><see cref="_held"/> 的落盘位置。跟 meshsettings.json 是两回事：那份是给配置系统读的。</summary>
    private string HeldPath => Path.Combine(options.HomeDirectory, "meshconfig.signed.json");

    /// <summary>
    /// 应答一次 <c>GET /config</c>。
    /// </summary>
    /// <param name="requesterPk">请求方的公钥，机密现封给它。</param>
    /// <returns>本机手上还没有配置时为 <c>null</c>，由调用方回 404。</returns>
    internal ConfigOffer? OfferConfig(string requesterPk)
    {
        if (!options.ConfigSync.Enabled)
        {
            return null;
        }

        var held = EnsureHeld();
        if (held is null)
        {
            return null;
        }

        // 机密现封：本机手上有明文就封一份给来问的人。
        //
        // ⚠️ 这里<b>不查任何名单</b>，是选定的姿态：授权边界就是 mesh 本身，能连上这个端口的
        // 都拿得到（跟 /state、/chain 一致）。加密挡的是链路上的旁观者，不是「谁有资格拿」。
        // 换句话说：同网段任何一台机器都能要到这个 secret。挡住损失的是那个应用的权限面窄
        // （只有 im:message），以及 SyncableConfig 的白名单，不是这一行。
        if (options.Lark.AppSecret.Length == 0)
        {
            return ConfigOffer.Plain(held);
        }

        try
        {
            return new ConfigOffer(
                held,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SyncableConfig.SecretPurpose] = SecretSealing.Seal(
                        options.Lark.AppSecret, requesterPk, identity, SyncableConfig.SecretPurpose),
                });
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            // 请求方给的公钥不合法。正文照给 —— 那部分本来就是公开的。
            logger.LogDebug(ex, "请求方的公钥无效，只给配置正文");
            return ConfigOffer.Plain(held);
        }
    }

    /// <summary>
    /// 本机手上那份；人把版本号调高了就在本机重新签发一份。
    /// </summary>
    /// <remarks>
    /// 「在任意一台上改 <c>meshsettings.json</c> 并把 <c>ConfigSync.Version</c> 调高」
    /// 就是这套东西唯一的写入口。调高之后本机持有全网最大的版本，几十秒内扩散完。
    /// </remarks>
    private ConfigDocument? EnsureHeld()
    {
        lock (_heldGate)
        {
            if (!_heldLoaded)
            {
                _heldLoaded = true;
                _held = LoadHeld();
            }

            var wanted = options.ConfigSync.Version;
            if (wanted <= 0 || (_held is not null && _held.Version >= wanted))
            {
                return _held;
            }

            var document = ConfigDocument.Sign(identity, wanted, SyncableConfig.Serialize(options));
            _held = document;
            PersistHeld(document);
            logger.LogInformation("本机作者了配置 v{Version}，将扩散到 mesh", wanted);
            return document;
        }
    }

    /// <summary>把手上那份从盘上读回来，好在重启后仍然原样转发。</summary>
    private ConfigDocument? LoadHeld()
    {
        try
        {
            if (!File.Exists(HeldPath))
            {
                return null;
            }

            var document = ActaJson.Deserialize<ConfigDocument>(File.ReadAllText(HeldPath));

            // 自己盘上那份也要验一遍：手改坏了、或者上次只写了一半，都不该当成有效配置转出去。
            if (document is null || !document.VerifySignature())
            {
                logger.LogWarning("{Path} 验签不过，忽略", HeldPath);
                return null;
            }

            return document;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "读不回 {Path}，本机当作还没有配置", HeldPath);
            return null;
        }
    }

    private void PersistHeld(ConfigDocument document)
    {
        try
        {
            Directory.CreateDirectory(options.HomeDirectory);
            WriteFileAtomically(HeldPath, ActaJson.Serialize(document));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 落不了盘只影响重启后要重新拉一次，不影响当前这一轮。
            logger.LogWarning(ex, "存不下 {Path}", HeldPath);
        }
    }

    private async Task ConfigSyncLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(options.ConfigSync.PollInterval);
        do
        {
            LoopInterval.Follow(timer, options.ConfigSync.PollInterval);

            try
            {
                if (options.ConfigSync.Enabled)
                {
                    await SyncConfigOnceAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "配置同步这一轮失败");
            }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// 问一圈邻居有没有更新的配置。
    /// </summary>
    /// <remarks>
    /// 没有指定的上游：谁都问，谁的版本大就采用谁的。所以离线过的机器上线后从<b>任何</b>
    /// 一个邻居都能追上，不必等某台特定的机器活着。
    /// </remarks>
    private async Task SyncConfigOnceAsync(CancellationToken ct)
    {
        _ = EnsureHeld();

        foreach (var peer in mesh.Members.Where(m => m.Id != mesh.Self.Id).ToList())
        {
            ct.ThrowIfCancellationRequested();

            // 对端广播的版本不比本机手上的新就不问了 —— 稳态下这一圈零 HTTP 请求。
            // 对端还没上报过状态（ConfigVersion 为 0）时问一次，问不到就 404。
            if (mesh.PeerStates.TryGetValue(peer.Id, out var peerState)
                && peerState.ConfigVersion > 0
                && peerState.ConfigVersion <= (_held?.Version ?? 0))
            {
                continue;
            }

            var offer = await mesh.PullConfigAsync(peer, ct).ConfigureAwait(false);

            // 签名已经在 PullConfigAsync 里验过（验的是原作者的）。这里只判要不要采用。
            if (offer is not null && offer.Document.Supersedes(_held))
            {
                Adopt(offer, peer.Id);
            }
        }
    }

    /// <summary>采用一份配置：落成 meshsettings.json，交给配置热更新去生效。</summary>
    private void Adopt(ConfigOffer offer, string viaPeer)
    {
        var document = offer.Document;

        try
        {
            // 收方重新投影一遍白名单。上游是被信任的，但一台被入侵的机器不该等于全组沦陷 ——
            // 尤其是可执行文件路径那几个键，它们的代价是远程代码执行。
            var filtered = SyncableConfig.Filter(document.Json, out var dropped);
            if (dropped.Count > 0)
            {
                logger.LogWarning(
                    "配置 v{Version}（作者 {Publisher}）里有白名单外的键，已丢弃：{Dropped}",
                    document.Version, document.PublisherId, string.Join("、", dropped));
            }

            var root = JsonNode.Parse(filtered) as JsonObject ?? new JsonObject();
            var conclave = root[SyncableConfig.Section] as JsonObject ?? new JsonObject();
            root[SyncableConfig.Section] = conclave;

            if (offer.Secrets.TryGetValue(SyncableConfig.SecretPurpose, out var envelope))
            {
                var secret = SecretSealing.Open(
                    envelope, document.PublicKey, identity, SyncableConfig.SecretPurpose);

                // 解不开是正常情况：转发的那一跳没有明文，或者它给的信封封给了别人。
                // 正文照常采用，机密等下一轮从有明文的节点那里拿。
                if (secret is not null)
                {
                    var lark = conclave["Lark"] as JsonObject ?? new JsonObject();
                    lark["AppSecret"] = secret;
                    conclave["Lark"] = lark;
                    _ = secretOverlay.Accept(secret, document.PublisherId);
                }
            }

            // 版本号由本机写，不从对端的正文里来（Filter 已经把 ConfigSync 整段剥掉了）。
            conclave["ConfigSync"] = new JsonObject { ["Version"] = document.Version };

            WriteFileAtomically(
                Path.Combine(options.HomeDirectory, "meshsettings.json"),
                root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            lock (_heldGate)
            {
                _held = document;
                PersistHeld(document);
            }

            mesh.UpdateState(s => s with { ConfigVersion = document.Version });

            logger.LogInformation(
                "采用配置 v{Version}（作者 {Publisher}，经 {Peer}）",
                document.Version, document.PublisherId, viaPeer);
            state.Notify(
                NoticeKind.Ok,
                $"已同步集群配置 v{document.Version}",
                $"作者 {document.PublisherId[..Math.Min(8, document.PublisherId.Length)]}；改动已热生效");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "采用配置 v{Version} 失败", document.Version);
        }
    }

    /// <summary>
    /// 先写临时文件再改名。
    /// </summary>
    /// <remarks>
    /// 配置文件正被读的同时被写，读方会拿到半截 JSON。改名在同一个文件系统上是原子的，
    /// 读方要么看到旧的、要么看到新的。权限设在临时文件上 —— 改名保留 inode 和模式，
    /// 而 meshsettings.json 里有飞书的 App Secret，不能是所有人可读。
    /// </remarks>
    private static void WriteFileAtomically(string path, string content)
    {
        var temp = path + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, content);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, path, overwrite: true);
    }

    public override void Dispose()
    {
        _server?.Dispose();
        base.Dispose();
    }
}

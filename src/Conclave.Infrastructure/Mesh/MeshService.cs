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
            options.Mesh, acta, () => mesh.Self, OnBlockReceivedAsync, serverLogger);
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
                _ = mesh.PruneDeadPeers(now);

                mesh.UpdateSelf(self => self with { LastHeartbeat = now });
                var self = mesh.Self;

                await beacon.SendAsync(
                    new Beacon(self, identity.Sign(Beacon.SigningPayload(self))), ct).ConfigureAwait(false);
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
                if (received is not null && mesh.AcceptBeacon(received, DateTimeOffset.UtcNow))
                {
                    state.SetStatus($"mesh 内 {mesh.Members.Count(m => m.IsAlive(DateTimeOffset.UtcNow))} 个节点在线");
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

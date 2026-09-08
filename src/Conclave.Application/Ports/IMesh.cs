using Conclave.Domain;

namespace Conclave.Application.Ports;

/// <summary>
/// mesh 视图。P0 只有 <see cref="LocalMesh"/>（成员就是自己）；P1 起换成 mDNS + gRPC 实现。
/// </summary>
public interface IMesh
{
    /// <summary>本节点。</summary>
    Elector Self { get; }

    /// <summary>
    /// 当前已知的成员，含自己。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不</b>在这里过滤心跳是否新鲜。存活判定要用哪个「现在」由调用方决定 ——
    /// <see cref="Domain.SeatAssignment"/> 已经把 <c>now</c> 当参数传进去了，
    /// 如果 mesh 再各自读一次 <c>UtcNow</c>，两边的「现在」就可能不一致，
    /// 而席位分配的一致性正是整套设计免掉共识算法的前提。
    /// 长期不发心跳的成员由实现自己清理，不靠这个属性过滤。
    /// </remarks>
    IReadOnlyList<Elector> Members { get; }

    /// <summary>广播一个自己写的区块。P0 是空操作。</summary>
    Task BroadcastAsync(Block block, CancellationToken ct);

    /// <summary>刷新本节点的动态字段（负载、近期票数、repo 清单）。</summary>
    void UpdateSelf(Func<Elector, Elector> mutate);
}

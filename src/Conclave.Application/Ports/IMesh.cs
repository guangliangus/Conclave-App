using Conclave.Domain;

namespace Conclave.Application.Ports;

/// <summary>
/// mesh 视图。P0 只有 <see cref="LocalMesh"/>（成员就是自己）；P1 起换成 mDNS + gRPC 实现。
/// </summary>
public interface IMesh
{
    /// <summary>本节点。</summary>
    Elector Self { get; }

    /// <summary>当前心跳存活的成员，含自己。</summary>
    IReadOnlyList<Elector> Alive { get; }

    /// <summary>广播一个自己写的区块。P0 是空操作。</summary>
    Task BroadcastAsync(Block block, CancellationToken ct);

    /// <summary>刷新本节点的动态字段（负载、近期票数、repo 清单）。</summary>
    void UpdateSelf(Func<Elector, Elector> mutate);
}

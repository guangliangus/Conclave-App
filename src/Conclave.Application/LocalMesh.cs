using Conclave.Application.Ports;
using Conclave.Domain;

namespace Conclave.Application;

/// <summary>
/// P0 的 mesh：成员只有自己。P1 换成 mDNS 发现 + gRPC 心跳的实现。
/// </summary>
/// <remarks>
/// 刻意让 P0 也走 <see cref="IMesh"/> 抽象，而不是在服务里直接引用「自己」——
/// 这样席位分配的代码从第一天就跑在多节点语义上，P1 接真 mesh 时不用改编排逻辑。
/// </remarks>
public sealed class LocalMesh : IMesh
{
    private readonly Lock _gate = new();
    private Elector _self;

    public LocalMesh(Elector self)
    {
        ArgumentNullException.ThrowIfNull(self);
        _self = self;
    }

    public Elector Self
    {
        get
        {
            lock (_gate)
            {
                return _self;
            }
        }
    }

    public IReadOnlyList<Elector> Members => [Self];

    /// <summary>P0 无对等节点，空操作。</summary>
    public Task BroadcastAsync(Block block, CancellationToken ct) => Task.CompletedTask;

    public void UpdateSelf(Func<Elector, Elector> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            _self = mutate(_self);
        }
    }
}

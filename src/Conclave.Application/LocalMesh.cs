using Conclave.Application.Ports;
using Conclave.Domain;

namespace Conclave.Application;

/// <summary>
/// 单机模式的 mesh：成员只有自己。组网时换成 <c>Conclave.Infrastructure.Mesh.HttpMesh</c>。
/// </summary>
/// <remarks>
/// 刻意让单机模式也走 <see cref="IMesh"/> 抽象，而不是在服务里直接引用「自己」——
/// 这样席位分配与实时状态的代码始终跑在多节点语义上，接真 mesh 时不用改编排逻辑。
/// </remarks>
public sealed class LocalMesh : IMesh
{
    private readonly Lock _gate = new();
    private Elector _self;
    private LiveState _state = LiveState.Empty;

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

    public LiveState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>单机模式下也照样维护实时状态 —— 队列与「正在评审」就是从它读的。</summary>
    /// <remarks>
    /// 版本号一样递增（虽然没人来拉）：这样单机与组网走的是完全同一条编排逻辑，
    /// 不用为「有没有 peer」分叉出两套代码。
    /// </remarks>
    public IReadOnlyDictionary<string, LiveState> PeerStates
        => new Dictionary<string, LiveState>(StringComparer.Ordinal) { [Self.Id] = State };

    public void UpdateState(Func<LiveState, LiveState> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            var next = mutate(_state);

            // 空操作不递增 —— 见 IMesh.UpdateState 的契约。单机模式下没人拉状态，
            // 但语义必须跟 HttpMesh 一致，否则「本机能跑、组网就错」。
            if (ReferenceEquals(next, _state))
            {
                return;
            }

            _state = next with { Version = _state.Version + 1 };
        }
    }

    /// <summary>单机无对等节点，空操作。</summary>
    public Task BroadcastAsync(Block block, CancellationToken ct) => Task.CompletedTask;

    /// <summary>单机模式下没有别的节点可指派。</summary>
    /// <remarks>
    /// 返回 false 而不是抛：界面上「指派」按钮在单机时本来就该是禁用的，
    /// 但万一走到这里，一个 false 让调用方能如实告诉人「没送到」，而不是崩一个对话框。
    /// </remarks>
    public Task<bool> SendAssignmentAsync(Elector peer, AssignmentRequest request, CancellationToken ct)
        => Task.FromResult(false);

    public Task<bool> SendAssignmentReplyAsync(Elector peer, AssignmentReply reply, CancellationToken ct)
        => Task.FromResult(false);

    /// <summary>单机模式没有对端可问；本机自己的日志由界面直接读 ReviewProgressLog。</summary>
    public Task<LogChunk?> FetchLogAsync(
        Elector peer, string revisionId, long from, CancellationToken ct)
        => Task.FromResult<LogChunk?>(null);

    public void UpdateSelf(Func<Elector, Elector> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            _self = mutate(_self);
        }
    }
}

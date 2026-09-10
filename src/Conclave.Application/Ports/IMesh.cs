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

    /// <summary>广播一个自己写的区块。单机模式下是空操作。</summary>
    Task BroadcastAsync(Block block, CancellationToken ct);

    /// <summary>刷新本节点的动态字段（负载、近期票数、有权限的 project、额度）。</summary>
    void UpdateSelf(Func<Elector, Elector> mutate);

    /// <summary>本节点的实时状态。</summary>
    LiveState State { get; }

    /// <summary>
    /// 改本节点的实时状态。<see cref="LiveState.Version"/> 由实现自动递增。
    /// </summary>
    /// <remarks>
    /// 版本号刻意由实现管而不是让调用方填：漏加一次就会让对端永远不来拉新状态，
    /// 而那种失效是静默的 —— 界面上看是「别人的队列一直不更新」。
    /// </remarks>
    /// <summary>
    /// 改本节点的实时状态。版本号由实现递增，调用方不用管。
    /// </summary>
    /// <remarks>
    /// <b>契约：<paramref name="mutate"/> 原样返回入参时必须当作空操作，不递增版本。</b>
    /// 调用方里到处是 <c>s.Claims.Any(…) ? s : s with {…}</c> 这样的守卫，
    /// 无条件递增会让它们全部形同虚设 —— 版本一变，mesh 里每个节点都要为一次
    /// 什么都没改的调用拉一遍 <c>GET /state</c>。
    /// </remarks>
    void UpdateState(Func<LiveState, LiveState> mutate);

    /// <summary>
    /// mesh 里所有节点的实时状态，含自己。
    /// </summary>
    /// <remarks>
    /// 跟 <see cref="Members"/> 一样<b>不</b>在这里按心跳过滤存活 —— 存活判定用哪个「现在」
    /// 由调用方决定，实现各自读一次时钟会让两边的「现在」不一致。
    /// </remarks>
    IReadOnlyDictionary<string, LiveState> PeerStates { get; }

    /// <summary>请求某个节点评审一个 PR。对方同意才生效。</summary>
    /// <returns>请求是否成功送达（不代表对方同意）。</returns>
    Task<bool> SendAssignmentAsync(Elector peer, AssignmentRequest request, CancellationToken ct);

    /// <summary>答复一个指派请求。</summary>
    Task<bool> SendAssignmentReplyAsync(Elector peer, AssignmentReply reply, CancellationToken ct);

    /// <summary>
    /// 向某个节点要它正在跑的那次评审的实时日志。
    /// </summary>
    /// <param name="peer">正在评的那个节点。</param>
    /// <param name="revisionId">哪一版。</param>
    /// <param name="from">已经拿到的序号；从 0 开始要全部。</param>
    /// <param name="ct">取消。</param>
    /// <returns>拿不到（对端离线、旧版本没有这个接口、它没在评这一版）时为 <c>null</c>。</returns>
    /// <remarks>
    /// 「我的 PR 被谁评了、评到哪一步了」是作者最想知道的事，而评审是十几分钟的黑盒。
    /// 日志只在评审节点的内存里（见 <see cref="ReviewProgressLog"/>），所以只能现问。
    /// </remarks>
    Task<LogChunk?> FetchLogAsync(Elector peer, string revisionId, long from, CancellationToken ct);
}

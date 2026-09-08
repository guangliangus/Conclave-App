using Conclave.Domain;

namespace Conclave.Application.Ports;

/// <summary>应用一个区块的三种结局。</summary>
public enum ApplyOutcome
{
    /// <summary>没进账本：验签失败、不在白名单、缺块、或索引冲突判负。</summary>
    Rejected = 0,

    /// <summary>本来就有这一块（gossip 送重了）。</summary>
    AlreadyPresent = 1,

    /// <summary>刚落进本地账本。</summary>
    Applied = 2,
}

/// <summary>
/// 应用一个区块的结果。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Novel"/> 与 <see cref="Applied"/> 的区分不是洁癖：gossip 转发必须只在
/// <see cref="Novel"/> 时进行。早期实现对「本来就有」也照样再广播一次，于是
/// A→B→A→B 无限回弹，每跳都新起 HTTP 请求 —— 双节点实测时进程直接 Out of memory 崩了。
/// 只转发新块也顺带解决了三节点以上的环路：转一圈回来时本地已经有了。
/// </para>
/// <para>
/// <see cref="Rebased"/> 是索引冲突让位后被重挂到新链尾的本节点区块。调用方**必须**
/// 把它们也广播出去 —— 否则对端永远不知道这些块换了索引，两边的链就收敛不了。
/// </para>
/// </remarks>
/// <param name="Outcome">结局。</param>
/// <param name="Rebased">因让位而重新签名挂到链尾的本节点区块，按新索引升序。</param>
public sealed record ApplyResult(ApplyOutcome Outcome, IReadOnlyList<Block> Rebased)
{
    public static ApplyResult Rejected { get; } = new(ApplyOutcome.Rejected, []);

    public static ApplyResult AlreadyPresent { get; } = new(ApplyOutcome.AlreadyPresent, []);

    public static ApplyResult Accepted { get; } = new(ApplyOutcome.Applied, []);

    /// <summary>区块在本地账本里（刚落进来或本来就有）。</summary>
    public bool Applied => Outcome is ApplyOutcome.Applied or ApplyOutcome.AlreadyPresent;

    /// <summary>刚落进来的新块 —— 只有这种才该继续 gossip 转发。</summary>
    public bool Novel => Outcome == ApplyOutcome.Applied;
}

/// <summary>账本的健康计数，供 UI 与报表展示。</summary>
/// <param name="Blocks">区块总数。</param>
/// <param name="Revisions">出现过的 revision 数。</param>
/// <param name="IndexConflicts">发生过多少次索引冲突。</param>
/// <param name="ConflictsLost">其中本地让位（重挂）的次数。</param>
public sealed record ActaHealth(long Blocks, long Revisions, long IndexConflicts, long ConflictsLost);

/// <summary>Acta 账本的持久化。实现见 <c>Conclave.Infrastructure.SqliteActa</c>。</summary>
public interface IActaStore
{
    /// <summary>
    /// 由本节点签名并追加一个区块到全局链的链尾。
    /// </summary>
    /// <remarks>
    /// <paramref name="revisionId"/> 由调用方给出而不是从载荷里反解 —— 调用方总是知道它，
    /// 反解要多一次 JSON 往返，而且新增区块类型时容易漏。
    /// </remarks>
    Task<Block> AppendAsync<TPayload>(
        string revisionId, BlockKind kind, TPayload payload, CancellationToken ct);

    /// <summary>
    /// 应用一个来自其他节点的区块。
    /// </summary>
    Task<ApplyResult> TryApplyAsync(Block block, CancellationToken ct);

    /// <summary>读某个 revision 的全部区块，按链序升序。</summary>
    Task<IReadOnlyList<Block>> ReadRevisionAsync(string revisionId, CancellationToken ct);

    /// <summary>
    /// 出现过 Summons 但尚无 Promulgation 的 revision，按链序（即时间序）升序。
    /// </summary>
    /// <remarks>
    /// 同一个 PR 可能有多个未结论的版本（作者连着 push 两次）。按链序返回，
    /// 调用方按 PR 分组取最后一个 —— 旧版本已经被取代，评它没意义。
    /// </remarks>
    Task<IReadOnlyList<string>> ReadOpenRevisionsAsync(CancellationToken ct);

    /// <summary>
    /// 读全局链，可从某个索引之后开始。供 mesh 补链与完整性校验。
    /// </summary>
    Task<IReadOnlyList<Block>> ReadChainAsync(long fromIndex, CancellationToken ct);

    /// <summary>最近写入的若干区块，按链序倒序。供 UI 的账本浏览器用。</summary>
    Task<IReadOnlyList<Block>> ReadRecentAsync(int limit, CancellationToken ct);

    /// <summary>本节点近 24 小时出过多少张 Ballot。用于公平性权重。</summary>
    Task<int> CountRecentBallotsAsync(string electorId, CancellationToken ct);

    /// <summary>账本健康计数。</summary>
    Task<ActaHealth> ReadHealthAsync(CancellationToken ct);
}

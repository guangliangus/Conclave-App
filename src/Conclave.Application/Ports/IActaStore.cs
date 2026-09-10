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

/// <summary>一个 revision 的最终结论，摘要里带的那一份。</summary>
/// <param name="Decision">多数决结果。</param>
/// <param name="Findings">合并后的问题数。</param>
public sealed record Verdict(ReviewDecision Decision, int Findings);

/// <summary>
/// 链上已完成评审的摘要。
/// </summary>
/// <param name="ValidBallots">revisionId → 有效票数（Error 票不计）。</param>
/// <param name="Verdicts">revisionId → 最终结论。键集就是「已完成」的那些。</param>
/// <remarks>
/// <para>
/// 票只带计数、不带正文 —— 合并结论时才按 revision 去读完整的票
/// （<see cref="IActaStore.ReadRevisionAsync"/>）。这份摘要每轮编排都要拿一次，
/// 把所有票的正文都捞出来太浪费。
/// </para>
/// <para>
/// 结论则要带上<b>内容</b>而不只是「完成了」这个事实：队列里那一行的「结论」列读的就是它。
/// 早先这里只有一个 <c>Finished</c> 集合，于是评完的行显示「已公布」但结论是「—」，
/// 还挂着一条收票进度条 —— 自相矛盾，而且那一行看起来仍然可操作。
/// </para>
/// </remarks>
public sealed record ChainSummary(
    IReadOnlyDictionary<string, int> ValidBallots,
    IReadOnlyDictionary<string, Verdict> Verdicts)
{
    public static ChainSummary Empty { get; } = new(
        new Dictionary<string, int>(StringComparer.Ordinal),
        new Dictionary<string, Verdict>(StringComparer.Ordinal));

    /// <summary>已有最终结论的 revisionId。</summary>
    /// <remarks>
    /// 从 <see cref="Verdicts"/> 现算，所以两者不可能对不上。<c>with</c> 拷贝出来的实例
    /// 会带着旧的这一份 —— 这份摘要是每轮重新读的，没有人对它做 <c>with</c>。
    /// </remarks>
    public IReadOnlySet<string> Finished { get; } =
        Verdicts.Keys.ToHashSet(StringComparer.Ordinal);
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
    /// 链上「已完成」部分的摘要：每个 revision 有几张有效票、哪些已有最终结论。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 替换掉原先的 <c>ReadOpenRevisionsAsync</c>（「有 Summons 无 Promulgation」）。
    /// 那个查询把队列建在链上历史之上，于是 PR 在 Azure DevOps 上被 merge 之后没人清，
    /// 永远留在队列里 —— 实测积到 41 个 revision 里 36 个是僵尸。
    /// </para>
    /// <para>
    /// 现在队列来自实时状态（<see cref="Domain.QueuedRevision"/>），链只回答「哪些已经评过了」。
    /// 这份摘要喂给 <see cref="Domain.QueueProjection.Build"/>。
    /// </para>
    /// </remarks>
    Task<ChainSummary> ReadSummaryAsync(CancellationToken ct);

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

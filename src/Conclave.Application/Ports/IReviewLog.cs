using Conclave.Domain;

namespace Conclave.Application.Ports;

/// <summary>
/// 一次评审的完整记录：谁、什么时候、什么状态、烧了多少 token、折合多少钱。
/// </summary>
/// <remarks>
/// 这是 <c>reviews</c> 投影表的一行。链是唯一真相，这里每一行都能由
/// <paramref name="BlockHash"/> 追回它来源的那个区块。
/// </remarks>
/// <param name="BlockHash">来源区块的哈希。拿它去链上就能验证这一行没被改过。</param>
/// <param name="RevisionId">评审的是 PR 的哪个版本。</param>
/// <param name="Project">Azure DevOps project。</param>
/// <param name="Repo">仓库名。</param>
/// <param name="PrId">PR 号。</param>
/// <param name="PrTitle">PR 标题。</param>
/// <param name="PrAuthor">PR 作者。</param>
/// <param name="ReviewerId">评审节点的公钥指纹。</param>
/// <param name="ReviewerAz">评审者的 az 登录身份，人读的「谁」。</param>
/// <param name="Round">席位轮次。</param>
/// <param name="Status">评审结论。</param>
/// <param name="Findings">报出的问题数。</param>
/// <param name="ReviewedAt">出票时刻（UTC）。</param>
/// <param name="DurationMs">claude 子进程墙钟耗时。</param>
/// <param name="Model">主模型。</param>
/// <param name="Turns">对话轮数。</param>
/// <param name="Usage">token 与折算金额。</param>
public sealed record ReviewRecord(
    string BlockHash,
    string RevisionId,
    string Project,
    string Repo,
    int PrId,
    string PrTitle,
    string PrAuthor,
    string ReviewerId,
    string ReviewerAz,
    int Round,
    ReviewDecision Status,
    int Findings,
    DateTimeOffset ReviewedAt,
    long DurationMs,
    string Model,
    int Turns,
    ReviewUsage Usage);

/// <summary>
/// 一组评审的汇总。
/// </summary>
/// <param name="Key">分组键：评审者 / 月份 / 仓库 / 模型。</param>
/// <param name="Reviews">评审次数。</param>
/// <param name="InputTokens">未命中缓存的输入 token。</param>
/// <param name="OutputTokens">输出 token。</param>
/// <param name="CacheReadTokens">缓存读 token。</param>
/// <param name="CacheWriteTokens">缓存写 token。</param>
/// <param name="CostUsd">折算金额合计。</param>
public sealed record UsageSummary(
    string Key,
    int Reviews,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    decimal CostUsd)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

    /// <summary>缓存命中率。越高说明上下文越可复用，钱越省。</summary>
    public double CacheHitRatio
    {
        get
        {
            var inputs = InputTokens + CacheReadTokens + CacheWriteTokens;
            return inputs == 0 ? 0 : CacheReadTokens / (double)inputs;
        }
    }
}

/// <summary>
/// 评审记录的查询。
/// </summary>
/// <remarks>
/// 只读。写入发生在区块落链时（见 <c>SqliteActa</c> 的投影），所以这里没有任何写方法 ——
/// 报表不能是另一个真相来源。
/// </remarks>
public interface IReviewLog
{
    /// <summary>最近的评审记录，按时间倒序。</summary>
    Task<IReadOnlyList<ReviewRecord>> ReadRecentAsync(int limit, CancellationToken ct);

    /// <summary>某个 PR 版本的全部评审记录。</summary>
    Task<IReadOnlyList<ReviewRecord>> ReadByRevisionAsync(string revisionId, CancellationToken ct);

    /// <summary>
    /// 评审积分排行榜，按总分倒序。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 分数<b>在这里现算</b>而不是从表里读：表存的是输入（难度、严重度分布），
    /// 公式在 <see cref="Conclave.Domain.PointsProjection"/>。这样调一次权重，
    /// 历史记录跟着一起重算 —— 排行榜上永远只有一把尺子。
    /// </para>
    /// <para>
    /// <paramref name="from"/> 给 null 就是「有史以来」。滚动榜（比如最近 7 天）
    /// 传时间窗即可，不需要另一个方法。
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<ScoreRow>> ReadLeaderboardAsync(
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct);

    /// <summary>按评审者汇总。</summary>
    Task<IReadOnlyList<UsageSummary>> SummariseByReviewerAsync(
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct);

    /// <summary>按月汇总（UTC 月份，形如 <c>2026-09</c>）。</summary>
    Task<IReadOnlyList<UsageSummary>> SummariseByMonthAsync(CancellationToken ct);

    /// <summary>按仓库汇总。</summary>
    Task<IReadOnlyList<UsageSummary>> SummariseByRepoAsync(
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct);

    /// <summary>
    /// 按模型汇总。
    /// </summary>
    /// <remarks>
    /// 读的是分模型明细表，所以能看出 skill 里子代理用的小模型花了多少 ——
    /// 只看每票总计是看不出来的。
    /// </remarks>
    Task<IReadOnlyList<UsageSummary>> SummariseByModelAsync(
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct);

    /// <summary>总计一行。</summary>
    Task<UsageSummary> ReadTotalAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct);

    /// <summary>
    /// 某个 PR 最近一次有效评审的评审节点（公钥指纹）；没有则 null。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用于「作者 fix 之后仍由同一个节点复审」。按 <c>(project, pr_id)</c> 查而不是按
    /// revision —— 要找的正是<b>上一个</b> revision 的评审者。
    /// </para>
    /// <para>
    /// 只认有效票：上一版是 Error 票说明那个节点当时没跑成，没有「读过这份代码」的优势，
    /// 硬把它请回来只会重复同一个失败。
    /// </para>
    /// <para>
    /// <b>这是投影表第一次参与行为决策而不只是报表。</b> 之所以成立：投影完全由链推导
    /// （每行带 <c>block_hash</c>，让位重挂后整体重建），所以它不是第二个真相来源。
    /// 各节点在 gossip 收敛前可能算出不同的归属，跟 <c>extraRounds</c> 同一类瞬时不一致，
    /// 收敛后自愈。
    /// </para>
    /// </remarks>
    Task<string?> ReadLastReviewerAsync(string project, int prId, CancellationToken ct);

    /// <summary>
    /// 某个节点自己在时间窗内烧掉的 token 与折算金额。
    /// </summary>
    /// <remarks>
    /// 用来算 <see cref="Domain.Elector.Utilization"/>。按 <c>reviewer_id</c>（公钥指纹）过滤
    /// 而不是 <c>reviewer_az</c>：投影表里混着 gossip 进来的别人的票，而额度是按机器算的，
    /// 同一个人在两台机器上是两份额度。
    /// </remarks>
    Task<UsageSummary> ReadElectorUsageAsync(
        string electorId, DateTimeOffset from, CancellationToken ct);
}

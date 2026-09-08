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
}

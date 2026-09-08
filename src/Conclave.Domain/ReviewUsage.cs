namespace Conclave.Domain;

/// <summary>
/// 一次评审在单个模型上的消耗。
/// </summary>
/// <remarks>
/// 对应 <c>claude -p --output-format json</c> 输出里 <c>modelUsage</c> 的一项。
/// 一次评审通常会命中多个模型 —— 主模型加上 skill 里子代理用的小模型 ——
/// 所以要分开记，否则看不出钱花在哪。
/// </remarks>
/// <param name="Model">模型 ID，如 <c>claude-opus-5[1m]</c>。</param>
/// <param name="CanonicalModel">规范名，如 <c>claude-opus-5</c>。跨版本汇总时用它。</param>
/// <param name="InputTokens">未命中缓存的输入 token。</param>
/// <param name="OutputTokens">输出 token。</param>
/// <param name="CacheReadTokens">命中缓存读取的 token（计价远低于新输入）。</param>
/// <param name="CacheWriteTokens">写入缓存的 token（计价高于新输入）。</param>
/// <param name="ThinkingTokens">思考 token，已含在 <paramref name="OutputTokens"/> 内。</param>
/// <param name="CostUsd">该模型的折算金额。</param>
public sealed record ModelUsage(
    string Model,
    string CanonicalModel,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long ThinkingTokens,
    decimal CostUsd);

/// <summary>
/// 一次评审的完整消耗。
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="CostUsd"/> 是折算金额，不是实际扣费。</b> <c>claude</c> 报的
/// <c>costBasis</c> 通常是 <c>list</c>，即按 API 目录价折算；走 Max/Pro 订阅时
/// 边际成本其实是 0。所以 <see cref="CostBasis"/> 必须一起记下并在报表里标出来，
/// 否则「这个月花了 40 美元」会被读成账单。
/// </para>
/// <para>
/// token 分四类而不是简单的「输入/输出」：缓存读写的计价与新输入差一个量级，
/// 混在一起算就没法判断「是不是该把 review 的上下文做得更可缓存」。
/// </para>
/// </remarks>
/// <param name="InputTokens">全部模型的未命中缓存输入 token 之和。</param>
/// <param name="OutputTokens">全部模型的输出 token 之和。</param>
/// <param name="CacheReadTokens">全部模型的缓存读 token 之和。</param>
/// <param name="CacheWriteTokens">全部模型的缓存写 token 之和。</param>
/// <param name="ThinkingTokens">全部模型的思考 token 之和。</param>
/// <param name="CostUsd">折算总金额。</param>
/// <param name="CostBasis">计价口径：<c>list</c> 表示按目录价折算。</param>
/// <param name="Turns">对话轮数。</param>
/// <param name="Models">分模型明细。</param>
public sealed record ReviewUsage(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long ThinkingTokens,
    decimal CostUsd,
    string CostBasis,
    int Turns,
    IReadOnlyList<ModelUsage> Models)
{
    /// <summary>子进程没跑起来、拿不到任何计量时用它。</summary>
    public static ReviewUsage None { get; } = new(0, 0, 0, 0, 0, 0m, "unknown", 0, []);

    /// <summary>算进上下文窗口的 token 总数（输入 + 缓存读 + 缓存写）。</summary>
    public long TotalInputTokens => InputTokens + CacheReadTokens + CacheWriteTokens;

    /// <summary>输入 + 输出，报表里「用了多少 token」的那个数。</summary>
    public long TotalTokens => TotalInputTokens + OutputTokens;

    /// <summary>缓存命中率。上下文做得可缓存的话这个数会很高，钱也就省下来了。</summary>
    public double CacheHitRatio
        => TotalInputTokens == 0 ? 0 : CacheReadTokens / (double)TotalInputTokens;
}

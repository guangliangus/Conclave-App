namespace Conclave.Application;

/// <summary>
/// Claude 额度用量的判定参数。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是兜底，不是首选。</b> 订阅额度的真值由 <c>Conclave.Infrastructure.ClaudeCliUsageProbe</c>
/// 跑 <c>claude -p "/usage"</c> 直接拿（零成本、无头、不需要任何配置）。
/// 只有真值拿不到时才回落到这里的折算 —— 找不到 <c>claude</c>、API key 用户没有订阅额度、
/// 或者那条命令的输出格式变了。
/// </para>
/// <para>
/// <b>折算有个已知的严重偏差</b>：它只看得见 Conclave 自己出过的票。实测这台机器近 7 天
/// 真实用量在 45 亿 cache-read token 量级，而 Conclave 自己只有 180 万 —— 低报约三个数量级。
/// 所以别把它当成可以长期依赖的额度判断；它的作用是「真值彻底拿不到时不至于完全没有数」。
/// </para>
/// <para>
/// 阈值（<see cref="Domain.Elector.MaxUtilization"/> = 0.8）是领域常量、全 mesh 一致；
/// <b>预算是每台机器自己的</b>，因为每个人的订阅档位不一样。预算只影响本节点报出去的
/// <see cref="Domain.Elector.Utilization"/>，不影响别人怎么解读它，所以不破坏席位表的一致性。
/// </para>
/// </remarks>
public sealed class ClaudeUsageOptions
{
    /// <summary>
    /// 多久起一次 <c>claude -p "/usage"</c> 取额度真值。
    /// </summary>
    /// <remarks>
    /// 实测那条命令零成本（不打模型）、墙钟约 0.8 秒，但毕竟是起一个子进程 ——
    /// 60 秒足够跟上额度变化（5 小时/7 天的窗口移动得很慢），又不会让面板每次刷新都 fork。
    /// </remarks>
    public TimeSpan CliProbeInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>那条命令的墙钟上限。</summary>
    public TimeSpan CliProbeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>折算兜底的滚动窗口。默认 7 天，对齐订阅的周额度。</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// 窗口内的 token 预算。0 表示不按 token 判定。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>默认值是个待调的估计</b>，不是从订阅里读出来的真实上限 —— 官方没有公开的
    /// token 配额数字。4000 万 token / 7 天 ≈ 72 次评审（实测一次真实评审 555.7k token）。
    /// 跑几天之后用 <c>conclave report</c> 里的真实数字回调这个值。
    /// <para>
    /// 真值正常可用时这个数基本不起作用（两者取更紧的，而真值总是更接近事实）。
    /// 想彻底关掉折算就把它和 <see cref="CostUsdBudget"/> 都设成 0。
    /// </para>
    /// </remarks>
    public long TokenBudget { get; set; } = 40_000_000;

    /// <summary>窗口内的折算金额预算（美元，API 目录价）。0 表示不按金额判定。</summary>
    /// <remarks>
    /// 与 <see cref="TokenBudget"/> 是「取更紧的那个」的关系，都配了就以先触线的为准。
    /// </remarks>
    public decimal CostUsdBudget { get; set; }

    /// <summary>两个预算都没配 —— 此时折算不参与，用量完全取决于有没有真实读数。</summary>
    public bool IsUnbounded => TokenBudget <= 0 && CostUsdBudget <= 0;
}

namespace Conclave.Application.Ports;

/// <summary>
/// 一个额度窗口的读数。
/// </summary>
/// <remarks>
/// 只带机器键，<b>不带显示名</b> —— 「five_hour 在界面上叫什么」是展示层的决定
/// （见 <c>Conclave.App.Labels</c>），而这一层只负责把数字如实报出来。
/// </remarks>
/// <param name="Key">
/// <c>five_hour</c> / <c>seven_day</c>，或 <c>seven_day:&lt;模型&gt;</c>（按模型细分的周额度）。
/// </param>
/// <param name="Utilization">这个窗口的用量比例，0–1。</param>
/// <param name="ResetsAt">这个窗口什么时候重置。</param>
public sealed record UsageWindow(string Key, double Utilization, DateTimeOffset? ResetsAt)
{
    /// <summary>按模型细分的周额度的键前缀，例如 <c>seven_day:Fable</c>。</summary>
    public const string ScopedSevenDayPrefix = "seven_day:";

    /// <summary>
    /// 这个窗口是否参与「还能不能接活」的判定。
    /// </summary>
    /// <remarks>
    /// 按模型细分的子额度不参与：Fable 的周额度打满并不妨碍用 Opus 评审，
    /// 拿它把节点挡在门外是错的。但它照样要显示 —— 人需要知道钱花在哪个模型上。
    /// <para>
    /// 默认 true，所以既有的构造点不用改；只有明确知道自己是子额度的来源才置 false。
    /// </para>
    /// </remarks>
    public bool Gates { get; init; } = true;
}

/// <summary>本节点 Claude 额度用量的读数。</summary>
/// <param name="Utilization">用量比例，0 = 全新，1 = 用满。已夹到 [0,1] 并按 4 位小数取整。</param>
/// <param name="Source">
/// 这个数从哪来，写进日志/UI 便于排查：<c>rate-limits</c>（Claude 报的真实额度）·
/// <c>budget</c>（按预算折算）· <c>rate-limits+budget</c>（两者取更紧的）· <c>unbounded</c>。
/// </param>
/// <param name="Detail">人读的一行说明，例如「5h 21% · 7d 38%」或「7 天内 12.3M/40.0M token」。</param>
/// <param name="ResetsAt">
/// 触线的那个窗口什么时候重置；折算来源没有这个信息，为 null。
/// <para>
/// 值得单独带出来：额度过线的节点会停止入席，日志和 UI 上写明「几点恢复」才不会被当成故障。
/// </para>
/// </param>
public sealed record UsageReading(
    double Utilization, string Source, string Detail, DateTimeOffset? ResetsAt = null)
{
    /// <summary>
    /// 按窗口拆开的读数，用于在界面上逐条画出来；只有 <c>rate-limits</c> 来源有，折算来源为空。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Utilization"/> 是这些窗口的<b>最大值</b>（任一窗口打满都会被限流），
    /// 席位规则只看那一个数。但「5h 用了 34%、7d 用了 39%、几点各自重置」对人是有用的 ——
    /// 压成一个数之后就没法回答「是会话额度快满了还是周额度快满了」。
    /// </para>
    /// <para>
    /// 刻意是 init 属性而不是构造参数：既有的构造点和 <c>with</c> 拷贝都不用改，
    /// 而拿不到窗口明细的来源（折算、手写数字）留空就是正确的表达。
    /// </para>
    /// </remarks>
    public IReadOnlyList<UsageWindow> Windows { get; init; } = [];

    /// <summary>
    /// 这次读数是什么时候拿到的。
    /// </summary>
    /// <remarks>
    /// 界面上要显示它。UI 的重绘时刻不能替代：额度是后台每轮轮询读一次的，
    /// 探针一旦卡死或失败，重绘时刻照样在走，而这个时间戳会停 ——
    /// 「没有变化」和「没在刷新」于是一眼能分开。
    /// </remarks>
    public DateTimeOffset ReadAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>没有配预算也没有真实读数时 —— 恒 0，额度硬规则形同关闭。</summary>
    public static UsageReading Unbounded { get; } =
        new(0, "unbounded", "无真实额度读数且未配预算，额度规则未生效");
}

/// <summary>
/// 量出本节点用掉了多少 Claude 额度。
/// </summary>
/// <remarks>
/// <para>
/// 单独抽一个端口而不是让 <see cref="DiscoveryService"/> 直接算，是因为「用量」有两个来源
/// 且都带 I/O：Claude 报的真实额度（读 <c>~/.conclave/usage</c>，由 statusLine 命令写入）
/// 和按预算折算（读投影表）。实现见 <c>Conclave.Infrastructure.ClaudeUsageMeter</c>。
/// </para>
/// <para>
/// <b>真实额度为什么要绕一个文件。</b> 订阅额度的真值只出现在<b>交互式</b>会话
/// statusLine 命令的 stdin JSON 里（<c>rate_limits.five_hour/seven_day.used_percentage</c>
/// 与 <c>resets_at</c>）。实测 <c>claude -p</c> 根本不触发 statusLine（那是 TUI 组件），
/// 而在 <c>-p</c> 下确实会触发的 <c>Stop</c> / <c>SessionEnd</c> 钩子，其 JSON 里没有
/// <c>rate_limits</c>。所以 Conclave 自己的子进程拿不到这个数，只能由节点主人的交互式
/// 会话顺手写进文件 —— 而那恰好正是折算看不见的那部分用量。
/// </para>
/// <para>
/// 取到的数会进签名心跳，别的节点据此判断要不要给本节点派活 ——
/// 所以它必须是本节点对自己额度的诚实陈述。白名单已经假定成员不作恶（只防掉线不防撒谎），
/// 而且这里撒谎的方向很别扭：报高只会让自己少干活。
/// </para>
/// </remarks>
public interface IUsageMeter
{
    /// <summary>读一次本节点的用量。</summary>
    Task<UsageReading> ReadAsync(string electorId, CancellationToken ct);
}

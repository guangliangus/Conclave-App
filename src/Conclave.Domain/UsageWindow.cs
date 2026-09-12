namespace Conclave.Domain;

/// <summary>
/// 一个额度窗口的读数。
/// </summary>
/// <remarks>
/// <para>
/// 只带机器键，<b>不带显示名</b> —— 「five_hour 在界面上叫什么」是展示层的决定
/// （见 <c>Conclave.App.Labels</c>），而这一层只负责把数字如实报出来。
/// </para>
/// <para>
/// <b>为什么在 Domain 而不是跟 <c>IUsageMeter</c> 一起待在 Ports：</b> 它现在挂在
/// <see cref="LiveState.UsageWindows"/> 上、随 <c>GET /state</c> 过线，
/// 已经不只是「探针的返回值」了。端口层的类型进不了 <c>Conclave.Domain</c>
/// （依赖方向是 Application → Domain），而为了上线再复制一份一模一样的三字段 record，
/// 迟早会有一边先漂。
/// </para>
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

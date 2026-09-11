using System.Globalization;
using Conclave.Application.Ports;
using Conclave.Domain;

namespace Conclave.App.ViewModels;

/// <summary>
/// 额度面板里的一条：一个窗口的用量、重置时刻与进度条。
/// </summary>
/// <remarks>
/// <para>
/// 颜色阈值直接引 <see cref="Elector.MaxUtilization"/>（0.8）—— 那是「不再入席」的那条线，
/// 界面上变红的时刻必须跟真实行为改变的时刻是同一刻，另写一个 0.9 只会骗自己。
/// </para>
/// <para>
/// <b>是 record 而不是 class</b>：它现在既是额度卡自己的一行，也<b>嵌在</b>
/// <see cref="NodeRow.Quotas"/> 里。<see cref="RowSync"/> 的内容指纹用反射遍历公开属性，
/// 嵌套对象走的是 <c>Convert.ToString</c> 那一支 —— class 只会给出类型名，
/// 于是节点表那一列的百分比变了也不会重画。record 的 <c>ToString</c> 带全部属性，
/// 指纹才跟得上。同 <see cref="AssignTarget"/>。
/// </para>
/// </remarks>
public sealed record UsageWindowRow
{
    /// <summary>接近上限：还能接活，但值得看一眼。</summary>
    private const double WarnAt = 0.6;

    public UsageWindowRow(UsageWindow window, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);

        Label = Labels.UsageWindow(window.Key);
        Short = Labels.UsageWindowShort(window.Key);
        Percent = window.Utilization * 100;
        PercentText = Format.Percent(window.Utilization);
        ResetText = Reset(window.ResetsAt, now);

        // 不参与判定的子额度不按入席阈值变色 —— 变红代表「这个节点停了」，
        // 而 Fable 的周额度打满并不妨碍用 Opus 评审，染红只会造成假警报。
        IsBad = window.Gates && window.Utilization >= Elector.MaxUtilization;
        IsWarn = window.Gates && !IsBad && window.Utilization >= WarnAt;

        Tip = window.Gates
            ? IsBad
                ? $"已超过 {Format.Percent(Elector.MaxUtilization)}：本节点暂不接评审任务，{ResetText}后自动恢复"
                : $"超过 {Format.Percent(Elector.MaxUtilization)} 就不再入席"
            : "按模型细分的子额度，不参与入席判定 —— 只用来看钱花在哪个模型上";
    }

    /// <summary>
    /// 按预算折算时没有窗口可拆，用一条来表示整体。
    /// </summary>
    /// <remarks>
    /// 折算没有重置时刻，那一格空着也是浪费 —— 改放折算的分母
    /// （「7 天内 12.3M/40.0M token」），正好是这条为什么是这个百分比的解释。
    /// </remarks>
    public UsageWindowRow(string label, double utilization, string detail)
    {
        Label = label;
        Short = label;
        Percent = utilization * 100;
        PercentText = Format.Percent(utilization);
        ResetText = detail;
        IsBad = utilization >= Elector.MaxUtilization;
        IsWarn = !IsBad && utilization >= WarnAt;
        Tip = detail;
    }

    public string Label { get; }

    /// <summary>窄列里用的短名（<c>5h</c> / <c>7d</c>）。</summary>
    public string Short { get; }

    /// <summary>0–100，喂给进度条。</summary>
    public double Percent { get; }

    public string PercentText { get; }

    public string ResetText { get; }

    public string Tip { get; }

    public bool IsWarn { get; }

    public bool IsBad { get; }

    /// <summary>
    /// 重置时刻。
    /// </summary>
    /// <remarks>
    /// 同一天只写时刻。5 小时窗口的重置基本都在当天，天天带着日期读起来更费劲；
    /// 跨天（周额度）才补上月日。
    /// </remarks>
    private static string Reset(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } at)
        {
            return string.Empty;
        }

        var local = at.ToLocalTime();
        var format = local.Date == now.ToLocalTime().Date ? "HH:mm" : "MM-dd HH:mm";
        return local.ToString(format, CultureInfo.InvariantCulture) + " 重置";
    }
}

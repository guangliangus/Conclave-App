using Conclave.Application.Ports;
using Conclave.Domain;

namespace Conclave.Application;

/// <summary>
/// 把各个额度窗口折成一个「离出局还有多远」的数。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不能对所有窗口用同一个阈值。</b> 以前的规则是
/// <c>max(5h, 7d) &gt;= 0.8 就不入席</c> —— 同一个 0.8 套在重置周期差 33 倍的两个窗口上，
/// 含义完全不同：5h 到 80% 最多歇五小时、当天自愈；<b>7d 到 80% 要歇到本周重置，最长七天</b>。
/// 而周额度是单调爬升的，周三爬到 80% 很正常，那时 5h 可能才 15% ——
/// 这台机器本来还能评一整天，却被自己的规则关在门外直到周末。
/// </para>
/// <para>
/// <b>但也不能干脆不看周额度。</b> 周额度真到 100% 时是被真的拦住的，而此时消耗不了
/// 任何东西，<b>5h 反而会读得很低</b>。只看 5h 会形成反馈陷阱：周额度耗尽 → 5h 掉到接近 0
/// → 规则认为这台最闲 → 加权 HRW 优先派活 → 每次评审都失败。这正是
/// <c>ClaudeUsageMeterTests</c> 点名要防的方向。
/// </para>
/// <para>
/// 所以每个窗口各有各的阈值，再按「离自己那条线还有多远」取最紧的一个：
/// </para>
/// <code>
/// pressure  = max over gating windows of (utilization / gate)
/// 上报 Utilization = pressure * Elector.MaxUtilization
/// </code>
/// <para>
/// 这样 <see cref="Elector.MaxUtilization"/>、<see cref="Elector.HasHeadroom"/>、
/// <see cref="Elector.Weight"/> 和心跳格式一个字都不用改 —— 心跳里仍然只有一个标量。
/// 而且 <see cref="SessionGate"/> 就取自那个常量，于是<b>会话窗口卡线时上报的数
/// 恰好等于 5h 的原始读数</b>，常见情况下含义跟以前完全一致。
/// </para>
/// <para>
/// ⚠️ 这里的阈值是<b>节点间协议</b>，跟 <see cref="Elector.MaxUtilization"/> 同一性质：
/// 各机器算法不一致的话，上报的压力值就不可比，加权 HRW 会系统性偏袒某几台。
/// 所以是常量而不是配置项。
/// </para>
/// <para>
/// 纯函数，<c>now</c> 与<b>时区</b>都由调用方传入 —— 判定要看「现在是周几」，
/// 而这一层既不许读时钟，也不该读进程的环境。时区不显式传的话，同一份输入在
/// UTC 的 CI 机器上和 +08 的开发机上算出的阈值不一样，测试就成了「看运气」
/// （实测：CI 上卡住的窗口从 7d 变成了 5h）。
/// </para>
/// </remarks>
public static class UsagePressure
{
    /// <summary>
    /// 会话（5h）窗口的阈值。
    /// </summary>
    /// <remarks>
    /// 刻意就取 <see cref="Elector.MaxUtilization"/>：这样会话窗口卡线时
    /// <c>utilization / gate * MaxUtilization</c> 恰好还原成原始读数，
    /// 「额度 57%」在改造前后是同一个意思。写成两个各自维护的 0.8 迟早漂移。
    /// </remarks>
    public const double SessionGate = Elector.MaxUtilization;

    /// <summary>
    /// 周（7d）窗口的绝对上限，无论进度如何都不再接活。
    /// </summary>
    /// <remarks>
    /// 按工作日配速算出的阈值到周末会涨到 1.0，那时一台已经用掉 99.9% 的机器
    /// 仍会被判为「还能接」，接到的每一个都会失败。所以要有这条硬顶 ——
    /// 它表达的是「真的没额度了」，不是「省着点用」。
    /// </remarks>
    public const double WeeklyCeiling = 0.95;

    /// <summary>Claude 的周窗口长度。</summary>
    private static readonly TimeSpan WeeklyWindow = TimeSpan.FromDays(7);

    /// <summary>压力最大的那个窗口，以及它的阈值与压力值。</summary>
    /// <param name="Window">卡住本节点的那个窗口。</param>
    /// <param name="Gate">它当下的阈值。</param>
    /// <param name="Value">压力 = 用量 / 阈值。达到 1 即出局。</param>
    public readonly record struct Binding(UsageWindow Window, double Gate, double Value);

    /// <summary>
    /// 找出最紧的那个窗口。
    /// </summary>
    /// <remarks>
    /// 只看 <see cref="UsageWindow.Gates"/> 为真的窗口 —— 按模型细分的子额度
    /// （<c>seven_day:Fable</c>）打满不妨碍用 Opus 评审，拿它挡人是错的。
    /// </remarks>
    /// <returns>没有可判定的窗口时返回 <c>null</c>。</returns>
    /// <param name="windows">候选窗口。</param>
    /// <param name="now">判定时刻，由调用方传入。</param>
    /// <param name="zone">
    /// 判定「周几」用的时区；null = 本机时区。生产上就是本机（工作周是人的作息），
    /// 测试传一个固定时区，免得结论跟着 runner 的 TZ 变。
    /// </param>
    public static Binding? Tightest(
        IEnumerable<UsageWindow> windows, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(windows);

        Binding? tightest = null;
        foreach (var window in windows.Where(w => w.Gates))
        {
            var gate = GateFor(window, now, zone);
            var value = gate <= 0 ? 1.0 : window.Utilization / gate;

            if (tightest is null || value > tightest.Value.Value)
            {
                tightest = new Binding(window, gate, value);
            }
        }

        return tightest;
    }

    /// <summary>
    /// 把最紧的那个窗口折回心跳里那个 0–1 的用量比例。
    /// </summary>
    /// <remarks>
    /// 阈值恰好就是 <see cref="Elector.MaxUtilization"/> 时直接返回原值，不做乘除往返：
    /// <c>(u / 0.8) * 0.8</c> 在浮点上<b>不恒等于</b> u（实测 0.46 会变成
    /// 0.45999999999999996）。而「会话窗口卡线时上报的就是 5h 的原始读数」是这套设计
    /// 对外承诺的性质，不该因为一次往返退化成「约等于」。
    /// </remarks>
    public static double Reported(Binding binding)
        => Math.Clamp(
            binding.Gate == Elector.MaxUtilization
                ? binding.Window.Utilization
                : binding.Window.Utilization / binding.Gate * Elector.MaxUtilization,
            0,
            1);

    /// <summary>
    /// 某个窗口当下的阈值。
    /// </summary>
    /// <remarks>
    /// 未知的键按会话窗口处理 —— 保守方向：宁可少接活，也不要因为多了一种没见过的窗口
    /// 就把它当成不设限。
    /// </remarks>
    public static double GateFor(UsageWindow window, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        return window.Key == SevenDay
            ? WeeklyGate(window.ResetsAt, now, zone ?? TimeZoneInfo.Local)
            : SessionGate;
    }

    private const string SevenDay = "seven_day";

    /// <summary>
    /// 周额度按<b>工作日配速</b>算阈值。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 一周的额度是给五个工作日花的，所以到第 N 个工作日就该只花掉 N/5。走完的工作日
    /// 按小时平滑计入，不是整天跳一格 —— 否则每天 0 点会出现一道阶跃，
    /// 刚过午夜的那一秒突然多出 20% 的额度。
    /// </para>
    /// <para>
    /// 在配速之上再放<b>一个工作日</b>的余量（<c>1/总工作日</c>）。没有余量的话，
    /// 周一上午跑一次大评审就会立刻越线 —— 配速那时几乎还是 0。
    /// 有了它，规则读作「允许超前一个工作日，超前两天就歇着」。
    /// </para>
    /// <para>
    /// 周几按<b>调用方给的时区</b>判定，生产上就是本机时区：工作周是人的作息，不是 UTC 的。
    /// 实测这台机器上周窗口正好是周一 01:59 到周一 01:59，五个工作日整。
    /// <para>
    /// ⚠️ 于是<b>时区也是节点间协议的一部分</b>：跨时区的两个节点对同一个周窗口会算出
    /// 不同的阈值，上报的压力值不完全可比。同处一地的团队无所谓，真要跨时区部署时
    /// 得把这里统一成一个约定时区。
    /// </para>
    /// </para>
    /// <para>
    /// 拿不到重置时刻就退回 <see cref="WeeklyCeiling"/> —— 算不出配速时不该假装能算，
    /// 但也不能因此完全不设限。
    /// </para>
    /// </remarks>
    private static double WeeklyGate(DateTimeOffset? resetsAt, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (resetsAt is not { } reset)
        {
            return WeeklyCeiling;
        }

        var start = reset - WeeklyWindow;
        var total = WorkingDaysBetween(start, reset, zone);
        if (total <= 0)
        {
            // 整个窗口里一个工作日都没有（理论上不会，除非放假配置或时区极端）。
            return WeeklyCeiling;
        }

        var elapsed = WorkingDaysBetween(start, Clamp(now, start, reset), zone);
        var pace = (elapsed + 1.0) / total;   // +1 = 允许超前一个工作日

        return Math.Min(WeeklyCeiling, pace);
    }

    private static DateTimeOffset Clamp(DateTimeOffset value, DateTimeOffset min, DateTimeOffset max)
        => value < min ? min : value > max ? max : value;

    /// <summary>
    /// 区间内有多少个工作日，按小时计的小数。
    /// </summary>
    /// <remarks>
    /// 逐天切开，只把落在周一到周五的那部分按 <c>小时/24</c> 累加。区间最长七天，
    /// 所以最多迭代八次，不值得为它做闭式推导 —— 那种算术很容易在跨月、跨夏令时的
    /// 边界上悄悄错一天。
    /// </remarks>
    private static double WorkingDaysBetween(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        if (to <= from)
        {
            return 0;
        }

        var days = 0.0;
        var cursor = TimeZoneInfo.ConvertTime(from, zone);
        var end = TimeZoneInfo.ConvertTime(to, zone);

        while (cursor < end)
        {
            var dayEnd = cursor.Date.AddDays(1);
            var slice = (dayEnd < end.DateTime ? dayEnd : end.DateTime) - cursor.DateTime;

            if (cursor.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                days += slice.TotalHours / 24.0;
            }

            cursor = new DateTimeOffset(dayEnd, cursor.Offset);
        }

        return days;
    }
}

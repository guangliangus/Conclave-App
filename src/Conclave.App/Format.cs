using System.Globalization;

namespace Conclave.App;

/// <summary>
/// 报表里的数字格式化。
/// </summary>
/// <remarks>
/// UI 面板与终端报表共用同一套写法 —— 两处各写一遍迟早漂移成「同一笔钱两个显示」。
/// </remarks>
internal static class Format
{
    internal static string Tokens(long value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000.0).ToString("F1", CultureInfo.InvariantCulture) + "k",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// 百分比。
    /// </summary>
    /// <remarks>
    /// 不用 <c>P0</c>：InvariantCulture 的百分号前面带一个空格（<c>0 %</c>），
    /// 在中文界面里读起来像少了个数字。
    /// </remarks>
    internal static string Percent(double ratio)
        => (ratio * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// 已经过去多久，人读的写法。
    /// </summary>
    /// <remarks>
    /// 只给一个量级（「3 分钟」而不是「3 分 12 秒」）：这个数用来判断一次评审是不是卡住了，
    /// 而那个判断只需要量级。秒数每次刷新都在变，反而让人以为界面在抖。
    /// </remarks>
    internal static string Elapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            // 对端时钟比本机快时会出现。显示「刚刚」比一个负数好。
            return "刚刚";
        }

        return span switch
        {
            { TotalSeconds: < 60 } => "刚刚",
            { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes} 分钟",
            { TotalHours: < 24 } => $"{(int)span.TotalHours} 小时",
            _ => $"{(int)span.TotalDays} 天",
        };
    }

    /// <summary>单次评审常在 $0.001 量级，所以小额多给两位小数。</summary>
    internal static string Money(decimal value) => value switch
    {
        // 零就写「$0」：四位小数的 $0.0000 会让人以为是个极小的非零数
        0m => "$0",
        >= 1m => "$" + value.ToString("F2", CultureInfo.InvariantCulture),
        _ => "$" + value.ToString("F4", CultureInfo.InvariantCulture),
    };
}

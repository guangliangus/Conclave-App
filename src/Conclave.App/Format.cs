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

    /// <summary>单次评审常在 $0.001 量级，所以小额多给两位小数。</summary>
    internal static string Money(decimal value) => value >= 1m
        ? "$" + value.ToString("F2", CultureInfo.InvariantCulture)
        : "$" + value.ToString("F4", CultureInfo.InvariantCulture);
}

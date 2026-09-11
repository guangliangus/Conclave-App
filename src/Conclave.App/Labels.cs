using Conclave.Application;
using Conclave.Domain;

namespace Conclave.App;

/// <summary>
/// 领域枚举的人读名字。
/// </summary>
/// <remarks>
/// 链上存的是枚举名（<c>Approve</c>、<c>Summons</c>），面板和终端报表都显示中文 ——
/// 跟 <see cref="Format"/> 同一个理由：两处各写一遍迟早漂移成「同一个状态两个说法」。
/// </remarks>
internal static class Labels
{
    /// <summary>
    /// 结论的中文名。
    /// </summary>
    /// <remarks>
    /// 实现挪到了 <see cref="DecisionLabels"/>：飞书通知也要用同一套说法，而它在
    /// Infrastructure 层，够不着这里的 internal。这里保留转发，调用点不必跟着改。
    /// </remarks>
    internal static string Decision(ReviewDecision decision) => DecisionLabels.Decision(decision);

    internal static string Kind(BlockKind kind) => kind switch
    {
        BlockKind.Summons => "召集",
        BlockKind.Seating => "入席",
        BlockKind.Ballot => "出票",
        BlockKind.Promulgation => "公布",
        BlockKind.Recess => "弃权",
        _ => kind.ToString(),
    };

    /// <summary>
    /// 额度窗口的显示名。
    /// </summary>
    /// <remarks>
    /// 对齐 Claude Code <c>/usage</c> 面板的说法（Current session / Current week），
    /// 后面括号里补上窗口长度 —— 光看「会话」看不出它是 5 小时还是别的。
    /// 认不出的键原样显示：以后官方多报一个窗口，这里不会因为没跟上而把它藏掉。
    /// </remarks>
    internal static string UsageWindow(string key) => key switch
    {
        "five_hour" => "会话额度（5h）",
        "seven_day" => "周额度（7d）",
        "spend_limit" => "消费上限",

        // statusLine 的 JSON 用的是上面那三个键。这里额外认一下 /usage 面板上的显示名 ——
        // 手写额度文件或换别的采集脚本时很容易照抄面板上的字，认不出就会在界面上
        // 露出一串英文原文（实测见过一次）。
        "Current session" => "会话额度（5h）",
        "Current week" => "周额度（7d）",

        // seven_day:Fable → 周额度（Fable）。按模型细分的子额度只显示、不参与入席判定。
        _ when key.StartsWith(Application.Ports.UsageWindow.ScopedSevenDayPrefix, StringComparison.Ordinal)
            => "周额度（" + key[Application.Ports.UsageWindow.ScopedSevenDayPrefix.Length..] + "）",

        _ => key,
    };

    /// <summary>
    /// 额度窗口的短名，给「在线节点」表那一列用。
    /// </summary>
    /// <remarks>
    /// 那一列只有 132px，「会话额度（5h）」放不下。短名跟 <see cref="UsageWindow"/> 的长名
    /// 一一对应、不另起一套说法：括号里那截本来就是这个窗口最有信息量的部分。
    /// 认不出的键原样返回，理由同长名。
    /// </remarks>
    internal static string UsageWindowShort(string key) => key switch
    {
        "five_hour" or "Current session" => "5h",
        "seven_day" or "Current week" => "7d",
        "spend_limit" => "消费",

        _ when key.StartsWith(Application.Ports.UsageWindow.ScopedSevenDayPrefix, StringComparison.Ordinal)
            => key[Application.Ports.UsageWindow.ScopedSevenDayPrefix.Length..],

        _ => key,
    };

    /// <summary>
    /// <c>LIONMAIL\youngsun</c> → <c>youngsun</c>。
    /// </summary>
    /// <remarks>
    /// az 的 <c>uniqueName</c> 带域前缀，整列都是同一个域，只有反斜杠后面那截有信息量。
    /// 完整值仍然进 tooltip —— 一个人可能同时有域账号和邮箱两种身份。
    /// </remarks>
    internal static string ShortAccount(string account)
    {
        if (account.Length == 0)
        {
            return "—";
        }

        var slash = account.LastIndexOf('\\');
        return slash >= 0 && slash < account.Length - 1 ? account[(slash + 1)..] : account;
    }
}

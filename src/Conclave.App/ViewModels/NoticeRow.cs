using System.Globalization;
using Conclave.Application;

namespace Conclave.App.ViewModels;

/// <summary>通知页里的一行。</summary>
/// <remarks>
/// 两行一条而不是一行：补充信息里常有异常消息和拒绝理由，挤在一行里必然被截断，
/// 而那恰好是最需要看清的部分。
/// </remarks>
public sealed class NoticeRow
{
    public NoticeRow(Notice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        // 通知是「刚刚发生的事」，看的是几点几分几秒，不是哪一天
        At = notice.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Key = notice.At.UtcTicks.ToString(CultureInfo.InvariantCulture) + '\u241e' + notice.Title;
        Title = notice.Title;
        Detail = notice.Detail ?? string.Empty;
        HasDetail = Detail.Length > 0;
        Kind = notice.Kind switch
        {
            NoticeKind.Ok => new Badge("完成", BadgeTone.Ok),
            NoticeKind.Warn => new Badge("注意", BadgeTone.Warn),
            NoticeKind.Bad => new Badge("出错", BadgeTone.Bad),
            _ => new Badge("消息", BadgeTone.Info),
        };
    }

    /// <summary>
    /// 这一条在列表里的身份，刷新之间不变。
    /// </summary>
    /// <remarks>
    /// 通知本身没有 id。用「发生时刻（到 tick）+ 标题」：<see cref="NodeState.Notify"/>
    /// 已经把一分钟内内容完全相同的一条挡掉了，所以这个组合在列表里唯一。
    /// 撞了也不会出错，只是那一行会白重建一次（见 <see cref="RowSync"/>）。
    /// </remarks>
    public string Key { get; }

    public string At { get; }

    public Badge Kind { get; }

    public string Title { get; }

    public string Detail { get; }

    public bool HasDetail { get; }
}

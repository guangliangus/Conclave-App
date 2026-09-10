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

    public string At { get; }

    public Badge Kind { get; }

    public string Title { get; }

    public string Detail { get; }

    public bool HasDetail { get; }
}

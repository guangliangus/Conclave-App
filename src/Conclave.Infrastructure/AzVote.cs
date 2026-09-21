using Conclave.Domain;

namespace Conclave.Infrastructure;

/// <summary>
/// 结论转成 <c>az repos pr set-vote --vote</c> 接受的字符串。
/// </summary>
/// <remarks>
/// <para>
/// 刻意不放领域层：取值是 <c>az</c> 的命令行词汇，换个 ADO 客户端就得跟着换，
/// 而 <see cref="ReviewDecision"/> 不该跟着动。
/// </para>
/// <para>
/// 从 <c>AzCliPrSource</c> 里提出来单独放，是因为有了第二个消费者：飞书卡片上要显示
/// 这次在 PR 上投的是哪一票。两处各写一份的话，改了投票词汇只会改投票那一处 ——
/// 于是通知上写着 <c>approve-with-suggestions</c>，PR 上留下的却是别的，
/// 而这种不一致没有任何一处会报错。
/// </para>
/// </remarks>
internal static class AzVote
{
    /// <summary><c>none</c> 表示不投票。</summary>
    private const string None = "none";

    /// <summary>
    /// 这个结论对应的 <c>--vote</c> 取值。
    /// </summary>
    /// <remarks>
    /// <see cref="ReviewDecision.Error"/> 是本机跑挂了，不是一个评审意见，
    /// 不该在 PR 上留下任何一票，所以它映射到 <c>none</c>。
    /// </remarks>
    internal static string For(ReviewDecision decision) => decision switch
    {
        ReviewDecision.Approve => "approve",
        ReviewDecision.ApproveWithSuggestions => "approve-with-suggestions",
        ReviewDecision.WaitForAuthor => "wait-for-author",
        ReviewDecision.Reject => "reject",
        _ => None,
    };

    /// <summary>这个结论会不会真的在 PR 上留下一票。</summary>
    internal static bool IsCast(ReviewDecision decision) => For(decision) != None;
}

namespace Conclave.Domain;

/// <summary>评审结论。取值对齐 Azure DevOps 的 vote 语义。</summary>
public enum ReviewDecision
{
    /// <summary>子进程失败/超时。计入 quorum 分母，但不参与多数决。</summary>
    Error = 0,

    Approve = 10,
    ApproveWithSuggestions = 5,
    WaitForAuthor = -5,
    Reject = -10,
}

/// <summary><see cref="ReviewDecision"/> 与 <c>az repos pr set-vote</c> 之间的映射。</summary>
public static class ReviewDecisionExtensions
{
    /// <summary>转成 <c>az repos pr set-vote --vote</c> 接受的字符串。</summary>
    public static string ToAzVote(this ReviewDecision decision) => decision switch
    {
        ReviewDecision.Approve => "approve",
        ReviewDecision.ApproveWithSuggestions => "approve-with-suggestions",
        ReviewDecision.WaitForAuthor => "wait-for-author",
        ReviewDecision.Reject => "reject",
        _ => "none",
    };

    /// <summary>Error 不代表任何评审意见，多数决时必须排除。</summary>
    public static bool CountsTowardMajority(this ReviewDecision decision)
        => decision != ReviewDecision.Error;
}

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

/// <summary>
/// <see cref="ReviewDecision"/> 的领域谓词。
/// </summary>
/// <remarks>
/// 刻意<b>不</b>放「转成 az 的 vote 字符串」那种映射 —— 领域层不该知道
/// <c>az repos pr set-vote</c> 的命令行长什么样。它在
/// <c>Conclave.Infrastructure.AzCliPrSource</c> 里，跟唯一的消费者放在一起。
/// 面向人的中文名同理，在 <c>Conclave.Application.DecisionLabels</c>。
/// </remarks>
public static class ReviewDecisionExtensions
{
    /// <summary>Error 不代表任何评审意见，多数决时必须排除。</summary>
    public static bool CountsTowardMajority(this ReviewDecision decision)
        => decision != ReviewDecision.Error;
}

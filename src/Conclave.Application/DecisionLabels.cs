using Conclave.Domain;

namespace Conclave.Application;

/// <summary>
/// <see cref="ReviewDecision"/> 的人读名字。
/// </summary>
/// <remarks>
/// 放在 Application 而不是各显示层自己写一份：结论现在有三个出口 —— 面板、
/// <c>conclave report</c>、飞书通知，而且分属三个程序集。同一个结论在面板上叫「驳回」、
/// 在飞书里叫「不通过」，收到通知的人会以为是两回事。
/// </remarks>
public static class DecisionLabels
{
    public static string Decision(ReviewDecision decision) => decision switch
    {
        ReviewDecision.Approve => "通过",
        ReviewDecision.ApproveWithSuggestions => "通过·有建议",
        ReviewDecision.WaitForAuthor => "待作者",
        ReviewDecision.Reject => "驳回",
        ReviewDecision.Error => "执行失败",
        _ => decision.ToString(),
    };

    public static string Severity(Severity severity) => severity switch
    {
        Domain.Severity.Critical => "致命",
        Domain.Severity.Major => "严重",
        Domain.Severity.Minor => "次要",
        Domain.Severity.Info => "提示",
        _ => severity.ToString(),
    };
}

namespace Conclave.Application.Ports;

/// <summary>
/// 允许其区块进入本地账本的节点白名单。
/// </summary>
/// <remarks>
/// 这道闸不是「以后再加」的：别人的 Seating 块会让别人的评审任务在你的机器上跑 <c>Bash</c>。
/// 心跳收编与区块接收都必须过它。
/// </remarks>
public interface IElectorAllowList
{
    /// <summary>本节点自己永远在名单内。</summary>
    bool IsAllowed(string electorId);

    /// <summary>当前名单，供 UI 与日志展示。</summary>
    IReadOnlyCollection<string> Allowed { get; }
}

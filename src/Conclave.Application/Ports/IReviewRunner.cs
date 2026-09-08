using Conclave.Domain;

namespace Conclave.Application.Ports;

/// <summary>跑一次评审。实现见 <c>Conclave.Infrastructure.ClaudeReviewRunner</c>。</summary>
public interface IReviewRunner
{
    /// <summary>
    /// 对 <paramref name="pr"/> 跑一次评审并产出这一轮的 Ballot。
    /// </summary>
    /// <remarks>
    /// 实现必须以 collect 模式运行 —— 只产出结论，不投递到 Azure DevOps。
    /// 投递由 round=0 的节点在收齐 quorum 后单独做一次，否则 quorum=3 会灌 3 条重复评论。
    /// </remarks>
    Task<BallotPayload> RunAsync(Revision revision, PrMeta pr, int round, CancellationToken ct);
}

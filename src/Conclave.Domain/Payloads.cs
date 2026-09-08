namespace Conclave.Domain;

/// <summary>Summons 块的载荷：发现了一个待评审的 PR 版本。</summary>
/// <param name="Revision">幂等键，见 <see cref="Conclave.Domain.Revision"/>。</param>
/// <param name="Pr">当时读到的 PR 元数据快照。</param>
/// <param name="Quorum">按 <see cref="SeatAssignment.QuorumSize"/> 算出的应有席位数。</param>
/// <param name="RulesFingerprint">
/// 当时生效的 <see cref="ReservedMatters"/> 指纹。链上留痕，日后回看
/// 「为什么这个 PR 只跑了 1 个节点」时不必猜配置。
/// </param>
public sealed record SummonsPayload(
    Revision Revision,
    PrMeta Pr,
    int Quorum,
    string RulesFingerprint);

/// <summary>Seating 块的载荷：某节点认领了某一轮的席位。</summary>
/// <param name="RevisionId">对应 <see cref="Revision.Id"/>。</param>
/// <param name="Round">席位轮次，0 起。round=0 的节点负责最终投递。</param>
/// <param name="ElectorId">认领者的公钥指纹。</param>
public sealed record SeatingPayload(
    string RevisionId,
    int Round,
    string ElectorId);

/// <summary>Ballot 块的载荷：一个节点的评审结论。</summary>
/// <param name="RevisionId">对应 <see cref="Revision.Id"/>。</param>
/// <param name="Round">席位轮次。</param>
/// <param name="Decision">该节点的结论。<see cref="ReviewDecision.Error"/> 表示子进程失败。</param>
/// <param name="Findings">该节点独立报出的问题。</param>
/// <param name="Model">出这份结论的主模型，便于日后归因。</param>
/// <param name="DurationMs">claude 子进程墙钟耗时。</param>
/// <param name="Usage">token 与折算金额，见 <see cref="ReviewUsage"/>。</param>
/// <param name="ReviewerAz">
/// 评审者的 <c>az</c> 登录身份，人读的「谁」。
/// <para>
/// 区块本身有 <c>ElectorId</c>（公钥指纹），密码学上足够，但审计报表要给人看。
/// 由评审者自己在签名范围内声明，所以不可否认；用的也正是「不评审自己的 PR」
/// 那条硬规则比对的同一个身份。
/// </para>
/// </param>
/// <param name="Error">
/// 子进程失败时的 stderr 摘要。
/// <para>
/// ⚠️ <b>传这个参数必须用命名实参</b>（<c>Error:</c>）。它排在 <see cref="ReviewerAz"/> 之后，
/// 按位置传会静默落到 ReviewerAz 上，然后被编排层的 <c>with { ReviewerAz = ... }</c> 覆盖 ——
/// 错误信息就此消失，链上只留下一张没有原因的 Error 票。这个坑真的踩过。
/// </para>
/// </param>
public sealed record BallotPayload(
    string RevisionId,
    int Round,
    ReviewDecision Decision,
    IReadOnlyList<Finding> Findings,
    string Model,
    long DurationMs,
    ReviewUsage? Usage = null,
    string? ReviewerAz = null,
    string? Error = null)
{
    /// <summary>拿不到计量时退成全 0，免得每个读取点都判空。</summary>
    public ReviewUsage Metering => Usage ?? ReviewUsage.None;
}

/// <summary>Promulgation 块的载荷：合并后的最终结论。</summary>
/// <param name="RevisionId">对应 <see cref="Revision.Id"/>。</param>
/// <param name="Decision">多数决结果。平票取更保守的一方。</param>
/// <param name="Findings">按 Confidence 降序合并后的问题列表。</param>
/// <param name="Degraded">合格节点不足、未跑满 quorum。链上必须留痕，不假装跑满了。</param>
/// <param name="ActualQuorum">实际收到的 Ballot 数。</param>
/// <param name="ExpectedQuorum">Summons 当时期望的席位数。</param>
/// <param name="ThreadId">投递到 Azure DevOps 后拿到的评论 thread ID；collect 模式下为 null。</param>
public sealed record PromulgationPayload(
    string RevisionId,
    ReviewDecision Decision,
    IReadOnlyList<MergedFinding> Findings,
    bool Degraded,
    int ActualQuorum,
    int ExpectedQuorum,
    int? ThreadId = null);

/// <summary>Recess 块的载荷：某一轮超时弃权，席位让给下一轮。</summary>
/// <param name="RevisionId">对应 <see cref="Revision.Id"/>。</param>
/// <param name="Round">被弃权的轮次。下一轮从 <c>Round + 1</c> 重新 HRW。</param>
/// <param name="Reason">弃权原因，例如 <c>seating timeout</c>。</param>
public sealed record RecessPayload(
    string RevisionId,
    int Round,
    string Reason);

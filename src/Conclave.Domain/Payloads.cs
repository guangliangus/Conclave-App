namespace Conclave.Domain;

/// <summary>
/// Summons 块的载荷：发现了一个待评审的 PR 版本。
/// </summary>
/// <remarks>
/// ⚠️ <b>已不再写入链上</b>（2026-09-09）。「队列里有什么」改由实时状态
/// <see cref="QueuedRevision"/> 承载 —— 写进不可变的链只会留下一地永远清不掉的僵尸
/// （实测积到 41 个 revision 里 36 个已经在 ADO 上关闭了）。类型保留是为了能读懂旧区块。
/// </remarks>
/// <param name="Revision">幂等键，见 <see cref="Conclave.Domain.Revision"/>。</param>
/// <param name="Pr">当时读到的 PR 元数据快照。</param>
/// <param name="Quorum">按 <see cref="SeatAssignment.QuorumSize"/> 算出的应有席位数。</param>
/// <param name="RulesFingerprint">
/// 当时生效的规则指纹，形如 <c>&lt;reserved&gt;+&lt;quorum&gt;</c> —— 是
/// <see cref="ReservedMatters.Fingerprint"/> 与 <see cref="QuorumPolicy.Fingerprint"/>
/// 两者拼起来的，拼法见 <c>DiscoveryService.RulesFingerprint</c>。
/// <para>
/// 两个都要留：<see cref="Quorum"/> 只说了「跑几遍」，说不出「为什么是这个数」——
/// 是碰了敏感路径，还是改动大，取决于当时两份配置各是什么。链上留痕，半年后回看
/// 「这个 PR 为什么只跑了 1 个节点」时不必猜。
/// </para>
/// </param>
public sealed record SummonsPayload(
    Revision Revision,
    PrMeta Pr,
    int Quorum,
    string RulesFingerprint);

/// <summary>
/// Seating 块的载荷：某节点认领了某一轮的席位。
/// </summary>
/// <remarks>
/// ⚠️ <b>已不再写入链上</b>（2026-09-09）。「谁正在评」改由实时状态
/// <see cref="ActiveReview"/> 承载，掉线自动释放。类型保留是为了能读懂旧区块。
/// </remarks>
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
    /// <summary>
    /// 评审时的 PR 快照。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 以前这份快照在 <c>Summons</c> 块上，投影表回查那里取 project/repo/标题/作者。
    /// 现在链上只留完成的评审，<c>Summons</c> 不再上链，所以快照必须跟着票走 ——
    /// 否则账单里连「这一票评的是哪个仓库的哪个 PR」都答不上来。
    /// </para>
    /// <para>
    /// quorum=1 时每个 revision 只有一票，所以并不会像原先担心的那样「同一份快照在链上
    /// 存两遍」；quorum≥2 时会有几份副本，那是为了让每一票都自洽可读，值得。
    /// </para>
    /// <para>
    /// 是 init 属性而不是构造参数：既有的构造点和 <c>with</c> 拷贝都不用改，
    /// 而改造之前落链的老 Ballot 没有这个字段，反序列化时留 null 就是正确的表达。
    /// </para>
    /// </remarks>
    public PrMeta? Pr { get; init; }

    /// <summary>
    /// 评审节点<b>起草好的那条评论原文</b>（markdown）；拿不到就是 null。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 投递到 Azure DevOps 的就是这段文本，一个字不改（见
    /// <c>AzCliPrSource.CommentBody</c>）。原先是把 <see cref="Findings"/> 重新拼成一张
    /// 表格再发 —— 那样丢掉的是评审里最有价值的部分：为什么是问题、该怎么改、
    /// 以及那些不该被压成一行标题的上下文。结构化的 finding 仍然留着，
    /// 它们喂的是账单、投影表和界面，跟人看的那条评论各管一头。
    /// </para>
    /// <para>
    /// 上链是必需的而不是顺手：公布那一步读的是
    /// <see cref="ChainState.ValidBallots"/>，未必跑在评审的那个节点上。
    /// 长度由 <c>ClaudeReviewRunner</c> 在出票时截断，因为链是 append-only 的。
    /// </para>
    /// <para>
    /// 是 init 属性，理由同 <see cref="Pr"/>：既有构造点不用改，老 Ballot 反序列化留 null
    /// 也正是「那时候没有这个东西」的正确表达 —— 读取点据此退回旧的渲染。
    /// </para>
    /// </remarks>
    public string? Comment { get; init; }

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
    int? ThreadId = null)
{
    /// <summary>
    /// 要原样投递的评论原文；null 表示没有，投递方退回自己渲染。
    /// </summary>
    /// <remarks>
    /// <b>只在恰好一票有效时才有值</b>（见 <see cref="QuorumEngine.Merge"/>）。
    /// quorum ≥ 2 时有 N 份各自起草的评论，"原样投递"就没有唯一答案了 ——
    /// 那种情况下合并渲染才是对的：它的价值本来就在「几个节点独立提到了同一条」。
    /// 默认策略是全 1（<see cref="QuorumPolicy.Single"/>），所以常态走原文这条路。
    /// </remarks>
    public string? Comment { get; init; }
}

/// <summary>
/// Recess 块的载荷：某一轮超时弃权，席位让给下一轮。
/// </summary>
/// <remarks>
/// ⚠️ <b>已不再写入链上</b>（2026-09-09）。节点掉线后它的 <see cref="ActiveReview"/> 会随
/// 心跳窗口过期而消失，PR 自动回队列 —— 不再需要显式的弃权块与 10 分钟席位超时。
/// 类型保留是为了能读懂旧区块。
/// </remarks>
/// <param name="RevisionId">对应 <see cref="Revision.Id"/>。</param>
/// <param name="Round">被弃权的轮次。下一轮从 <c>Round + 1</c> 重新 HRW。</param>
/// <param name="Reason">弃权原因，例如 <c>seating timeout</c>。</param>
public sealed record RecessPayload(
    string RevisionId,
    int Round,
    string Reason);

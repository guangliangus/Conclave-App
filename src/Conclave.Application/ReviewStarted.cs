namespace Conclave.Application;

/// <summary>
/// 评审开跑的那一刻，用来通知 PR 作者「有人接手了」。
/// </summary>
/// <remarks>
/// <para>
/// 为什么这条通知值得单独存在：从 PR 推上去到结论回来，中间是十几分钟的静默，
/// 而作者在那段时间里分不出「还没轮到我」和「正在评」—— 两者的外部表现完全一样，
/// 都是什么都没有。结论通知回答的是「评完了怎么样」，这条回答的是「开始了没有」。
/// </para>
/// <para>
/// 不上链：它不是一个需要留痕的结论，进程重启后补发也没有意义（那时候要么评完了、
/// 要么席位早就让给别的节点了）。「谁正在评」的权威来源是实时状态里的
/// <see cref="Conclave.Domain.ActiveReview"/>，不是这条通知。
/// </para>
/// </remarks>
/// <param name="RevisionId">哪一版。实时日志按它取，深链也带它。</param>
/// <param name="ReviewerAz">
/// 哪个节点在评，人读的 <c>az</c> 身份。跟盖在票上的
/// <see cref="Conclave.Domain.BallotPayload.ReviewerAz"/> 是同一个值 ——
/// 作者在通知里看到的名字，跟事后去链上查到的必须对得上。
/// </param>
/// <param name="LogEndpoint">
/// 评审节点对外的 HTTP 端点，形如 <c>http://192.168.1.7:47708</c>；mesh 没开时为空。
/// <para>
/// 实时日志在<b>评审那台机器</b>上，不在作者这台 —— 拼日志地址只能用评审者自己的端点。
/// 空的时候通知照发，只是少一个按钮：没有 mesh 就没有别的节点，那种情况下
/// 评审者必然就是本机，作者自己打开面板就看得到。
/// </para>
/// </param>
public sealed record ReviewStarted(string RevisionId, string ReviewerAz, string LogEndpoint);

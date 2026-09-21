using Conclave.Domain;

namespace Conclave.Application.Ports;

/// <summary>
/// 把评审结论推给人。实现见 <c>Conclave.Infrastructure.LarkNotifier</c>。
/// </summary>
/// <remarks>
/// <para>
/// 跟 <see cref="IPrSource.PostResultAsync"/> 是两件事：那个把结论写回 PR（留痕、投票），
/// 这个把结论<b>推到人眼前</b>。没人会盯着 34 个 project 的 PR 列表刷新，
/// 结论躺在 ADO 上等于没出。
/// </para>
/// <para>
/// 通知失败绝不能影响公布 —— 结论已经在链上了，补发的成本远低于让编排循环卡住。
/// 调用方负责吞掉异常，见 <c>ReviewOrchestrator.PromulgateAsync</c>。
/// </para>
/// </remarks>
public interface INotifier
{
    /// <summary>通知 PR 作者：他的这一版评审出结论了。</summary>
    Task NotifyPromulgationAsync(PrMeta pr, PromulgationPayload result, CancellationToken ct);

    /// <summary>
    /// 通知 PR 作者：有节点开始评他这一版了。
    /// </summary>
    /// <remarks>
    /// 跟 <see cref="NotifyPromulgationAsync"/> 一样，失败绝不能影响评审本身 ——
    /// 这条通知连链上都不留痕，没有任何理由让它掀掉一次已经开跑的评审。
    /// 调用方负责吞掉异常，见 <c>ReviewOrchestrator.StartReview</c>。
    /// </remarks>
    Task NotifyReviewStartedAsync(PrMeta pr, ReviewStarted started, CancellationToken ct);

    /// <summary>
    /// 通知指派的对面那个人：指派来了，或者对方答复了。
    /// </summary>
    /// <remarks>
    /// 收件人<b>不是</b> PR 作者，而是 <see cref="AssignmentNotice.Recipient"/> ——
    /// 指派发给被指派者，答复发回给指派者。跟另外两条一样，失败不许影响指派本身。
    /// </remarks>
    Task NotifyAssignmentAsync(PrMeta pr, AssignmentNotice notice, CancellationToken ct);
}

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
}

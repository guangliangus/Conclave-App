namespace Conclave.Application;

/// <summary>
/// 一次指派，或它的答复 —— 用来通知<b>对面那个人</b>。
/// </summary>
/// <remarks>
/// <para>
/// 指派是整套流程里唯一需要人当场做决定的一步：被指派的节点会攒出一条待确认，
/// 而它<b>只在面板上</b>，人不开面板就不知道有活等着自己。反过来，指派出去的人也没有
/// 任何提示 —— 对方接没接受，他得自己去看。两边都在等对方，而两边都不知道要等。
/// </para>
/// <para>
/// 用一个记录表达三件事，靠 <see cref="Accepted"/> 分：<c>null</c> 是指派本身，
/// <c>true</c>/<c>false</c> 是答复。这不是省类型，是沿用既有词汇 ——
/// <c>MainViewModel</c> 判「这条是不是答复」用的就是 <c>Accepted is not null</c>，
/// 两处对同一件事的表达方式一致，比多两个类型更不容易读错。
/// </para>
/// </remarks>
/// <param name="RevisionId">哪一版。</param>
/// <param name="Recipient">
/// 这条通知发给谁，<c>az</c> 身份。
/// <para>
/// 刻意不复用 PR 作者：指派类通知的收件人<b>不是</b>作者，而是指派的两端 ——
/// 指派发给被指派者，答复发回给指派者。
/// </para>
/// </param>
/// <param name="Counterpart">对面是谁，<c>az</c> 身份。</param>
/// <param name="Accepted"><c>null</c> = 这是指派本身；否则是答复的结果。</param>
/// <param name="Note">指派时的留言，或拒绝的理由；没有就是 <c>null</c>。</param>
public sealed record AssignmentNotice(
    string RevisionId,
    string Recipient,
    string Counterpart,
    bool? Accepted,
    string? Note);

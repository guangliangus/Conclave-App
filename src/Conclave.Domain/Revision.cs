namespace Conclave.Domain;

/// <summary>
/// 评审的最小单元：PR 的某一个版本。
/// </summary>
/// <remarks>
/// 幂等键刻意不是 PR ID —— 用 PR ID 的话，作者 push 新 commit 后就不会重新评审；
/// 每轮轮询都重跑，又会重复烧 token。带上 <paramref name="SrcCommit"/> 之后：
/// 链上有该 Revision 的 Promulgation 就跳过，作者 push 了就自然变成一个新 Revision。
/// </remarks>
public sealed record Revision(string Project, int PrId, string SrcCommit)
{
    /// <summary>形如 <c>2721@dc1d1d47</c>。席位分配与 claude 会话 ID 都以此为种子。</summary>
    public string Id => $"{PrId}@{ShortCommit}";

    public string ShortCommit => SrcCommit.Length >= 8 ? SrcCommit[..8] : SrcCommit;

    /// <summary>同一个 PR 的不同版本。用于「每个 PR 只处理最新那个未结论的版本」。</summary>
    public (string Project, int PrId) PullRequest => (Project, PrId);

    /// <summary>
    /// 席位分配的 HRW 种子。
    /// </summary>
    /// <remarks>
    /// 刻意按 <b>PR</b> 而不是 revision 取种子：作者 push 修复之后 <see cref="Id"/> 变了，
    /// 但席位应当落回同一个节点 —— 那个节点已经读过这份代码、提过这些 finding，
    /// 复审同一个 PR 的边际成本远低于换一个节点从头看。
    /// <para>
    /// 带 <c>seat:</c> 前缀跟 <c>discover:</c> 那套分片种子分开，免得两种分配在同一个
    /// 命名空间里相互关联（同一个 project 的轮询者和评审者应当独立抽签）。
    /// </para>
    /// </remarks>
    public string SeatKey => $"seat:{Project}:{PrId}";
}

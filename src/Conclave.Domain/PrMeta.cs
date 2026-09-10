namespace Conclave.Domain;

/// <summary>
/// 一个活跃 PR 的元数据。字段名对齐 <c>az repos pr show</c> 的输出路径。
/// </summary>
public sealed record PrMeta
{
    public required int PrId { get; init; }

    public required string Project { get; init; }

    public required string Repo { get; init; }

    public required string Title { get; init; }

    /// <summary><c>createdBy.uniqueName</c>，形如 <c>LIONMAIL\youngsun</c>。</summary>
    public required string Author { get; init; }

    /// <summary><c>lastMergeSourceCommit.commitId</c>。幂等键的另一半，见 <see cref="Revision"/>。</summary>
    public required string SrcCommit { get; init; }

    /// <summary>
    /// 仓库的 git 克隆地址（<c>repository.remoteUrl</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 评审时按它把代码拉进临时工作区，所以它必须随 PR 快照一起进 Summons 块 ——
    /// 评审的节点是从链上读 <see cref="PrMeta"/> 的，不会再去查一次 Azure DevOps。
    /// </para>
    /// <para>
    /// 刻意<b>不</b>是 <c>required</c>：实测 <c>az repos pr list</c> 把 <c>remoteUrl</c> 返回成
    /// <c>null</c>（只有 <c>az repos pr show</c> 才给真值），而且改造之前落链的老 Summons 块里
    /// 根本没有这个字段 —— 声明成 required 会让 <c>System.Text.Json</c> 在反序列化老区块时直接抛。
    /// 为空时由 <c>IPrSource.GetCloneUrlAsync</c> 按「组织地址/project/_git/repo」兜底拼出来。
    /// </para>
    /// </remarks>
    public string RemoteUrl { get; init; } = string.Empty;

    public bool IsDraft { get; init; }

    public string SourceBranch { get; init; } = string.Empty;

    public string TargetBranch { get; init; } = string.Empty;

    /// <summary>改动的文件数。quorum 自适应的主要输入，见 <see cref="SeatAssignment.QuorumSize"/>。</summary>
    public int FilesChanged { get; init; }

    /// <summary>改动涉及的仓库内路径，用于匹配 <see cref="ReservedMatters"/>。</summary>
    public IReadOnlyList<string> ChangedPaths { get; init; } = [];

    public Revision ToRevision() => new(Project, PrId, SrcCommit);
}

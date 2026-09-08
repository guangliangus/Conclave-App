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

    public bool IsDraft { get; init; }

    public string SourceBranch { get; init; } = string.Empty;

    public string TargetBranch { get; init; } = string.Empty;

    public int FilesChanged { get; init; }

    public int LinesChanged { get; init; }

    /// <summary>改动涉及的仓库内路径，用于匹配 <see cref="ReservedMatters"/>。</summary>
    public IReadOnlyList<string> ChangedPaths { get; init; } = [];

    public Revision ToRevision() => new(Project, PrId, SrcCommit);
}

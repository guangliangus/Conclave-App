using Conclave.Domain;

namespace Conclave.Application.Ports;

/// <summary>Azure DevOps 侧的读写。实现见 <c>Conclave.Infrastructure.AzCliPrSource</c>。</summary>
public interface IPrSource
{
    /// <summary>
    /// 当前 <c>az</c> 登录者的账号名，形如 <c>guangliangli</c>。
    /// </summary>
    /// <remarks>
    /// 「不评审自己的 PR」是硬规则，靠它跟 <c>createdBy.uniqueName</c> 比对。
    /// 实测 ADO Server 上取法是 <c>az devops invoke --area Location --resource connectionData
    /// --api-version 5.0-preview</c>，读 <c>authenticatedUser.properties.Account["$value"]</c>。
    /// </remarks>
    Task<string> GetAuthenticatedIdentityAsync(CancellationToken ct);

    /// <summary>列出当前身份能看到的所有 project。</summary>
    Task<IReadOnlyList<string>> ListProjectsAsync(CancellationToken ct);

    /// <summary>
    /// 列出某个 project 下所有活跃（status=active）的 PR。
    /// </summary>
    /// <remarks>
    /// 一次 <c>az repos pr list</c> 就能拿到 <c>lastMergeSourceCommit</c>、<c>isDraft</c>、
    /// <c>createdBy</c>、<c>repository</c>（实测过），所以发现阶段每个 project 只需一次调用。
    /// 但返回的 <see cref="PrMeta"/> 不含 diff 统计 —— 那要另外算，见
    /// <see cref="EnrichWithDiffStatsAsync"/>。
    /// </remarks>
    Task<IReadOnlyList<PrMeta>> ListActivePullRequestsAsync(string project, CancellationToken ct);

    /// <summary>
    /// 用本地 git 补上 diff 统计与改动路径（quorum 自适应要用）。
    /// </summary>
    /// <remarks>
    /// 刻意用本地 <c>git diff --numstat</c> 而不是 REST 的 iterations/changes API：
    /// 「本机有 clone」本来就是入席硬规则，既然一定有克隆，本地算 diff 更快也不耗 API 配额。
    /// 拿不到时返回统计为 0 的原对象 —— 那样 quorum 会退到 1，是安全的降级方向。
    /// </remarks>
    Task<PrMeta> EnrichWithDiffStatsAsync(PrMeta pr, CancellationToken ct);

    /// <summary>只给一个 PR 号，跨 project 找到它。供「手动插队」用。</summary>
    Task<PrMeta?> FindPullRequestAsync(int prId, CancellationToken ct);

    /// <summary>把合并后的结论投递回 PR：发评论并投票。返回评论 thread ID。</summary>
    Task<int?> PostResultAsync(PrMeta pr, PromulgationPayload result, CancellationToken ct);
}

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
    /// 但返回的 <see cref="PrMeta"/> 不含改动统计，<c>repository.remoteUrl</c> 也是
    /// <c>null</c>（同样实测）—— 前者见 <see cref="EnrichWithChangeStatsAsync"/>，
    /// 后者见 <see cref="GetCloneUrlAsync"/>。
    /// </remarks>
    Task<IReadOnlyList<PrMeta>> ListActivePullRequestsAsync(string project, CancellationToken ct);

    /// <summary>
    /// 一次拿到整个 collection（组织）里所有活跃的 PR，不分 project。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这是发现循环的主路径。</b> 按 project 逐个列举是 N 次 <c>az</c> 调用，
    /// 而每次 az 调用要起一个 Python 进程 —— 实测单次 1.27 秒，34 个 project 串起来
    /// 就是 43 秒，其中 30 个 project 一个活跃 PR 都没有。collection 级的
    /// <c>_apis/git/pullrequests</c> 一次 1.5 秒把 29 个 PR 全拿回来。
    /// </para>
    /// <para>
    /// 返回的字段跟 <see cref="ListActivePullRequestsAsync"/> 完全一致（实测确认，
    /// 包含 <c>repository.project.name</c>，所以跨 project 也分得清谁是谁），
    /// <c>remoteUrl</c> 同样是 <c>null</c>。
    /// </para>
    /// <para>
    /// 失败时由调用方退回逐 project 列举 —— 这个路由在不同 ADO 部署上不保证都开着。
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<PrMeta>> ListAllActivePullRequestsAsync(CancellationToken ct);

    /// <summary>
    /// 补上改动的文件数与路径（quorum 自适应要用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 走 Azure DevOps 的 <c>pullRequestIterations</c> + <c>pullRequestIterationChanges</c>
    /// 两个接口，<b>零克隆</b>。以前是用本机 clone 跑 <c>git diff --numstat</c>，那依赖
    /// 「本机有 clone」这条已经删掉的硬规则；为了给每个新发现的 PR 定 quorum 就先拉一遍仓库，
    /// 成本远大于收益。
    /// </para>
    /// <para>
    /// 代价是 <b>ADO 不提供行数</b>（实测确认），所以 <see cref="PrMeta"/> 里没有 LinesChanged，
    /// quorum 的档位改成按文件数分。拿不到统计时返回文件数为 0 的原对象 ——
    /// 那样 quorum 退到 1，是安全的降级方向。
    /// </para>
    /// </remarks>
    Task<PrMeta> EnrichWithChangeStatsAsync(PrMeta pr, CancellationToken ct);

    /// <summary>
    /// PR 所在仓库的 git 克隆地址。
    /// </summary>
    /// <remarks>
    /// 优先用 <see cref="PrMeta.RemoteUrl"/>（Summons 快照里带的）；为空时按
    /// 「组织地址/project/_git/repo」拼 —— <c>az repos pr list</c> 不给 remoteUrl，
    /// 而且改造之前落链的老 Summons 块里根本没有这个字段。
    /// </remarks>
    Task<string> GetCloneUrlAsync(PrMeta pr, CancellationToken ct);

    /// <summary>
    /// 组织（collection）地址，形如 <c>https://host/Collection</c>；读不到时返回空串。
    /// </summary>
    /// <remarks>
    /// 配置优先，其次读 <c>az devops configure --list</c> 的默认值 —— 也就是
    /// <see cref="GetCloneUrlAsync"/> 兜底时用的那个值。单独暴露出来是给界面拼 PR 链接用：
    /// 账本里的历史记录只有 project / repo / PR 号，没有 <see cref="PrMeta.RemoteUrl"/>，
    /// 而「点 PR 号跳浏览器」这件事不该为了一个地址再去查一次 Azure DevOps。
    /// </remarks>
    Task<string> GetOrgUrlAsync(CancellationToken ct);

    /// <summary>只给一个 PR 号，跨 project 找到它。供「手动插队」用。</summary>
    Task<PrMeta?> FindPullRequestAsync(int prId, CancellationToken ct);

    /// <summary>把合并后的结论投递回 PR：发评论并投票。返回评论 thread ID。</summary>
    Task<int?> PostResultAsync(PrMeta pr, PromulgationPayload result, CancellationToken ct);
}

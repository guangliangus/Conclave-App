using Conclave.Application.Ports;
using Conclave.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conclave.Application;

/// <summary>
/// 轮询 Azure DevOps，把新发现的 PR 版本写成 Summons 块。
/// </summary>
/// <remarks>
/// 去重完全靠 Acta：链上有该 <see cref="Revision"/> 的 Summons 就跳过。
/// 因为 Revision 带 <c>srcCommit</c>，作者 push 新 commit 会自然变成一个新 Revision，
/// 于是「PR 更新了要重评」和「没更新别重复烧 token」这两件事同一套机制解决，
/// 不需要额外的已处理状态表。
/// </remarks>
public sealed class DiscoveryService(
    IPrSource prSource,
    IActaStore acta,
    IMesh mesh,
    IRepoLocator repos,
    NodeState state,
    ConclaveOptions options,
    ILogger<DiscoveryService> logger) : BackgroundService
{
    private readonly ReservedMatters _reserved = ReservedMatters.Default;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 首轮先探一次身份与 project 权限，失败就退化成空闲循环而不是崩掉 UI。
        await RefreshSelfAsync(stoppingToken).ConfigureAwait(false);

        // mesh 冷启动时谁都还没收到别人的心跳，于是每个节点都以为 34 个 project 全归自己 ——
        // 结果同一批 PR 被所有节点各召集一遍，在 index 0 上撞成一堆索引冲突。
        // 等两个心跳周期，让成员表先收敛。
        if (options.Mesh.Enabled)
        {
            var settle = options.Mesh.BeaconInterval * 2;
            logger.LogInformation("等 {Settle} 让 mesh 成员表收敛后再开始轮询", settle);
            try
            {
                await Task.Delay(settle, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        using var timer = new PeriodicTimer(options.PollInterval);
        do
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 轮询失败不能让常驻服务退出：网络抖动、az token 过期都属常态。
                logger.LogError(ex, "发现轮询失败");
                state.SetStatus($"轮询失败：{ex.Message}");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>跑一轮发现。UI 上的「立即轮询」也调它。</summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        await RefreshSelfAsync(ct).ConfigureAwait(false);

        var self = mesh.Self;
        var projects = options.ProjectAllowList.Count > 0
            ? options.ProjectAllowList.ToList()
            : (await prSource.ListProjectsAsync(ct).ConfigureAwait(false)).ToList();

        var share = SeatAssignment.DiscoveryShare(self.Id, projects, mesh.Members, DateTimeOffset.UtcNow);
        state.SetStatus($"轮询 {share.Count}/{projects.Count} 个 project（其余由 mesh 内其他节点负责）");

        var summoned = 0;
        var skippedDraft = 0;
        var seen = 0;

        foreach (var project in share)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<PrMeta> prs;
            try
            {
                prs = await prSource.ListActivePullRequestsAsync(project, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单个 project 无权限或超时不该拖垮整轮。
                logger.LogWarning(ex, "project {Project} 列举失败，跳过", project);
                continue;
            }

            foreach (var pr in prs)
            {
                seen++;

                if (pr.IsDraft)
                {
                    // 草稿在列举结果里就能判定，不必再花一次 git diff。
                    skippedDraft++;
                    continue;
                }

                var revision = pr.ToRevision();
                if (string.IsNullOrEmpty(revision.SrcCommit))
                {
                    logger.LogWarning("PR {PrId} 没有 lastMergeSourceCommit，无法构造幂等键，跳过", pr.PrId);
                    continue;
                }

                var chain = await acta.ReadRevisionAsync(revision.Id, ct).ConfigureAwait(false);
                if (ActaProjection.Project(chain, revision.Id).Summons is not null)
                {
                    continue;
                }

                var enriched = await prSource.EnrichWithDiffStatsAsync(pr, ct).ConfigureAwait(false);
                var quorum = SeatAssignment.QuorumSize(enriched, _reserved);
                if (quorum == 0)
                {
                    skippedDraft++;
                    continue;
                }

                var block = await acta.AppendAsync(
                    revision.Id,
                    BlockKind.Summons,
                    new SummonsPayload(revision, enriched, quorum, _reserved.Fingerprint()),
                    ct).ConfigureAwait(false);

                await mesh.BroadcastAsync(block, ct).ConfigureAwait(false);
                summoned++;

                logger.LogInformation(
                    "召集 {Revision} {Repo} 「{Title}」 quorum={Quorum}（{Files} 文件 / {Lines} 行）",
                    revision.Id, enriched.Repo, enriched.Title, quorum,
                    enriched.FilesChanged, enriched.LinesChanged);
            }
        }

        state.SetStatus(
            $"已轮询 {share.Count} 个 project：看到 {seen} 个活跃 PR，新召集 {summoned} 个，跳过草稿 {skippedDraft} 个");
    }

    /// <summary>
    /// 只给一个 PR 号，跨 project 找到它并召集评议（插队）。
    /// </summary>
    /// <remarks>
    /// 这是 <c>bridge.sh</c> 唯一需要保留的能力：人手上只有一个 4 位 PR 号，
    /// 不想等下一轮轮询。链上已有该版本的 Summons 时直接返回，不重复写。
    /// </remarks>
    public async Task<Revision?> SummonAsync(int prId, CancellationToken ct)
    {
        await RefreshSelfAsync(ct).ConfigureAwait(false);

        var pr = await prSource.FindPullRequestAsync(prId, ct).ConfigureAwait(false);
        if (pr is null)
        {
            logger.LogError("找不到 PR {PrId}", prId);
            return null;
        }

        if (string.IsNullOrEmpty(pr.SrcCommit))
        {
            logger.LogError("PR {PrId} 没有 lastMergeSourceCommit，无法构造幂等键", prId);
            return null;
        }

        var revision = pr.ToRevision();
        var chain = await acta.ReadRevisionAsync(revision.Id, ct).ConfigureAwait(false);
        if (ActaProjection.Project(chain, revision.Id).Summons is not null)
        {
            logger.LogInformation("{Revision} 已在链上，无需重复召集", revision.Id);
            return revision;
        }

        // 插队刻意不看 IsDraft：人明确指名要评的，草稿也评。
        var quorum = Math.Max(1, SeatAssignment.QuorumSize(pr, _reserved));
        var block = await acta.AppendAsync(
            revision.Id,
            BlockKind.Summons,
            new SummonsPayload(revision, pr, quorum, _reserved.Fingerprint()),
            ct).ConfigureAwait(false);

        await mesh.BroadcastAsync(block, ct).ConfigureAwait(false);
        logger.LogInformation(
            "插队召集 {Revision} {Repo}「{Title}」 quorum={Quorum}（{Files} 文件 / {Lines} 行）",
            revision.Id, pr.Repo, pr.Title, quorum, pr.FilesChanged, pr.LinesChanged);

        return revision;
    }

    /// <summary>刷新本节点的动态字段：已 clone 的 repo、有权限的 project、近 24h 票数。</summary>
    private async Task RefreshSelfAsync(CancellationToken ct)
    {
        var located = repos.Locate();
        var reviews = await acta.CountRecentBallotsAsync(mesh.Self.Id, ct).ConfigureAwait(false);

        IReadOnlyList<string> projects;
        try
        {
            projects = options.ProjectAllowList.Count > 0
                ? options.ProjectAllowList.ToList()
                : await prSource.ListProjectsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "读取 project 列表失败，保留上一轮的权限视图");
            projects = mesh.Self.Projects;
        }

        mesh.UpdateSelf(self => self with
        {
            Repos = [.. located.Keys],
            Projects = [.. projects],
            Reviews24h = reviews,
            MaxConcurrent = options.MaxConcurrent,
            LastHeartbeat = DateTimeOffset.UtcNow,
        });
    }
}

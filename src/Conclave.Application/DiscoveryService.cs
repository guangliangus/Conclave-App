using System.Globalization;
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
    IUsageMeter usage,
    IClaudeCli claudeCli,
    NodeState state,
    ConclaveOptions options,
    ILogger<DiscoveryService> logger) : BackgroundService
{
    private readonly ReservedMatters _reserved = ReservedMatters.Default;

    /// <summary>
    /// 写进 Summons 的规则指纹：敏感路径清单 + quorum 策略。
    /// </summary>
    /// <remarks>
    /// 两者一起 —— 半年后回看「这个 PR 为什么只跑了 1 个节点」时，要能分清是
    /// 「没命中敏感路径」还是「当时 quorum 策略本来就是全 1」。
    /// </remarks>
    // 自评开关也要进指纹：否则「这个 PR 怎么是作者自己评的」在实时状态里查不出依据
    private string RulesFingerprint()
        => $"{_reserved.Fingerprint()}+{options.Quorum.Fingerprint()}"
            + (options.AllowSelfReview ? "+self" : string.Empty);

    /// <summary>上次写进日志的额度来源与档位，用来抑制重复日志。</summary>
    private string? _loggedSource;

    private int _loggedBucket = -1;

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

    /// <summary>
    /// 跑一轮发现：把本节点这一片的活跃 PR 上报成实时状态。UI 上的「立即轮询」也调它。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>不再往链上写 Summons。</b> 上报是<b>全量覆盖</b>语义 —— 每轮把这一片当前的活跃 PR
    /// 整个替换掉。PR 在 Azure DevOps 上一被 merge 或 abandon，它就从下一轮的上报里消失，
    /// 队列自动干净；被新 push 取代的旧 revision 同理（活跃列表里只有最新的 srcCommit）。
    /// </para>
    /// <para>
    /// 这正是原先那个 bug 的根治：以前队列是「链上有 Summons、无 Promulgation」，一条纯历史
    /// 查询，没有任何东西会去清它 —— 实测积到 41 个 revision 里 36 个已经在 ADO 上关闭了。
    /// </para>
    /// <para>
    /// 已经评过的不需要在这里过滤：链上有票或结论的，会在
    /// <see cref="QueueProjection.Build"/> 里被摘要挡掉。这一层只负责如实上报「现在还活跃的」。
    /// </para>
    /// </remarks>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        // 耗时要落在日志和状态栏上。一轮发现的成本几乎全是 az 调用次数
        // （每次起一个 Python 进程 ≈ 1.3 秒），没有这个数就只能凭感觉说「慢」。
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await RefreshSelfAsync(ct).ConfigureAwait(false);

        var self = mesh.Self;
        // 白名单非空时直接拿它当候选，省掉一次 az 调用；黑名单再从候选里剔除。
        var projects = options.FilterProjects(
            options.ProjectAllowList.Count > 0
                ? options.ProjectAllowList
                : await prSource.ListProjectsAsync(ct).ConfigureAwait(false));

        // 组织地址给界面拼 PR 链接用。实现侧有缓存，所以每轮都问一次也只是一次字段读取。
        try
        {
            state.SetOrgUrl(await prSource.GetOrgUrlAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 读不到只是界面上的 PR 号点不动，不该拖垮一整轮发现。
            logger.LogDebug(ex, "读不到 Azure DevOps 组织地址，PR 链接会不可点");
        }

        var share = SeatAssignment.DiscoveryShare(self.Id, projects, mesh.Members, DateTimeOffset.UtcNow);
        state.SetStatus($"轮询 {share.Count}/{projects.Count} 个 project（其余由 mesh 内其他节点负责）");

        // ① 列举。
        var candidates = new List<PrMeta>();
        var skippedDraft = 0;
        var mine = share.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var pr in await ListAsync(share, mine, ct).ConfigureAwait(false))
        {
            if (pr.IsDraft)
            {
                // 草稿在列举结果里就能判定，不必再花两次 API 取改动统计。
                skippedDraft++;
                continue;
            }

            if (string.IsNullOrEmpty(pr.SrcCommit))
            {
                logger.LogWarning("PR {PrId} 没有 lastMergeSourceCommit，无法构造幂等键，跳过", pr.PrId);
                continue;
            }

            candidates.Add(pr);
        }

        // ② 已经上报过同一个 revision 的直接沿用上一轮的结果，零 API 调用 ——
        // srcCommit 是幂等键的一半，它没变就说明 PR 内容没变，quorum 也不会变。
        var known = mesh.State.Discovered.ToDictionary(d => d.Revision.Id, StringComparer.Ordinal);
        var carried = new List<QueuedRevision>();
        var fresh = new List<PrMeta>();

        foreach (var pr in candidates)
        {
            if (known.TryGetValue(pr.ToRevision().Id, out var cached))
            {
                carried.Add(cached);
            }
            else
            {
                fresh.Add(pr);
            }
        }

        // ③ 新 PR 的改动统计分批并发取。每批完成就上报一次 —— 冷启动时队列会渐进出现，
        // 而不是整轮跑完（可能几分钟）之前界面上什么都没有。
        var lanes = Math.Max(1, options.DiscoveryConcurrency);
        var batches = 0;

        foreach (var batch in fresh.Chunk(lanes))
        {
            ct.ThrowIfCancellationRequested();

            var enriched = await Task.WhenAll(batch.Select(pr => EnrichAsync(pr, ct))).ConfigureAwait(false);
            carried.AddRange(enriched.Where(q => q is not null).Select(q => q!));
            batches++;

            if (fresh.Count > lanes)
            {
                // 只在真的要跑多批时才做中途上报：一批就搞完的情况下，
                // 中途那次上报会白白让对端多拉一次 /state。
                Publish(carried);
                state.SetStatus(
                    $"轮询中：已处理 {carried.Count}/{candidates.Count} 个 PR（{batches} 批）");
            }
        }

        // ④ 权威的全量覆盖。这一步是「PR 在 ADO 上关闭后自动出队」的全部机制：
        // carried 只由本轮还活跃的 PR 构成，关掉的自然不在里面。
        Publish(carried);

        sw.Stop();

        var summary = $"{candidates.Count} 个活跃 PR，上报 {carried.Count} 个待评"
            + $"（新取 {fresh.Count} 个），跳过草稿 {skippedDraft} 个 · "
            + sw.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " 秒";

        logger.LogInformation("轮询完成：{Summary}", summary);
        state.SetStatus($"已轮询 {share.Count} 个 project：{summary}");
    }

    /// <summary>
    /// 列出本节点这一轮该看的 PR。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 主路径是<b>一次</b> collection 级列举，再按分片过滤。原先是每个 project 一次
    /// <c>az repos pr list</c>：实测单次 1.27 秒 × 34 个 project ≈ 43 秒，而其中 30 个
    /// project 一个活跃 PR 都没有 —— 一整轮发现的时间几乎全花在「问一个空 project 有没有
    /// PR」上。换成 collection 级之后同样的结果 1.5 秒拿到。
    /// </para>
    /// <para>
    /// <b>分片仍然按 project 做</b>（<see cref="SeatAssignment.DiscoveryShare"/>）：
    /// 列举便宜了，但每个 PR 后面还有两次取改动统计的调用，那部分仍然值得分摊到各节点。
    /// 过滤用不分大小写的比较 —— project 名的大小写在 ADO 的不同接口里实测不完全一致。
    /// </para>
    /// <para>
    /// 那个路由不是所有 ADO 部署都开着，所以失败就退回逐 project 列举，只是慢，不会瘫。
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<PrMeta>> ListAsync(
        IReadOnlyList<string> share, IReadOnlySet<string> mine, CancellationToken ct)
    {
        try
        {
            var all = await prSource.ListAllActivePullRequestsAsync(ct).ConfigureAwait(false);
            return [.. all.Where(pr => mine.Contains(pr.Project))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "collection 级列举失败，退回逐 project 列举（会慢很多）");
        }

        var listed = new List<PrMeta>();

        foreach (var project in share)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                listed.AddRange(
                    await prSource.ListActivePullRequestsAsync(project, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单个 project 无权限或超时不该拖垮整轮。
                logger.LogWarning(ex, "project {Project} 列举失败，跳过", project);
            }
        }

        return listed;
    }

    /// <summary>取一个 PR 的改动统计并算出 quorum。失败或草稿返回 null。</summary>
    /// <remarks>
    /// 单独抽出来是为了能并发跑。异常在这里就地咽掉 —— 一个 PR 取不到统计不该让整批
    /// <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/> 炸掉，
    /// 从而连累同批其他 PR。
    /// </remarks>
    private async Task<QueuedRevision?> EnrichAsync(PrMeta pr, CancellationToken ct)
    {
        try
        {
            var enriched = await prSource.EnrichWithChangeStatsAsync(pr, ct).ConfigureAwait(false);
            var quorum = SeatAssignment.QuorumSize(enriched, _reserved, options.Quorum);
            if (quorum == 0)
            {
                return null;
            }

            var revision = enriched.ToRevision();
            logger.LogInformation(
                "发现 {Revision} {Repo}「{Title}」 quorum={Quorum}（{Files} 个文件）",
                revision.Id, enriched.Repo, enriched.Title, quorum, enriched.FilesChanged);

            return new QueuedRevision(revision, enriched, quorum, RulesFingerprint())
            {
                AllowSelfReview = options.AllowSelfReview,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "取 PR {PrId} 的改动统计失败，本轮跳过", pr.PrId);
            return null;
        }
    }

    /// <summary>把当前这批上报出去。</summary>
    /// <remarks>
    /// 全量覆盖语义，不做增量合并 —— 增量就得再想「什么时候删」，而那正是原先出僵尸的地方。
    /// </remarks>
    private void Publish(IReadOnlyList<QueuedRevision> discovered)
        => mesh.UpdateState(s => s with { Discovered = [.. discovered] });

    /// <summary>
    /// 只给一个 PR 号，跨 project 找到它、上报并由本节点认领（插队）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 人手上只有一个 4 位 PR 号、不想等下一轮轮询时用；也是无头入口
    /// <c>conclave review &lt;id&gt;</c> 的实现。
    /// </para>
    /// <para>
    /// 顺手<b>认领</b>它，而不只是上报：明确指名要评的，不该再交给加权 HRW 去抽 ——
    /// 抽到别的节点的话，敲命令的人会看着它一直不动。认领也顺带绕开了「不评审自己的 PR」
    /// 那条硬规则挡不住的场景：作者本人指名要评自己的 PR 时，认领是唯一的入口。
    /// </para>
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

        // 插队刻意不看 IsDraft：人明确指名要评的，草稿也评。
        var quorum = Math.Max(1, SeatAssignment.QuorumSize(pr, _reserved, options.Quorum));
        var item = new QueuedRevision(revision, pr, quorum, RulesFingerprint())
        {
            AllowSelfReview = options.AllowSelfReview,
        };
        var claim = new ReviewClaim(revision.Id, mesh.Self.Id, DateTimeOffset.UtcNow);

        mesh.UpdateState(s => s with
        {
            Discovered = s.Discovered.Any(d => d.Revision.Id == revision.Id)
                ? s.Discovered
                : [.. s.Discovered, item],
            Claims = s.Claims.Any(c => c.RevisionId == revision.Id)
                ? s.Claims
                : [.. s.Claims, claim],
        });

        logger.LogInformation(
            "插队并认领 {Revision} {Repo}「{Title}」 quorum={Quorum}（{Files} 个文件）",
            revision.Id, pr.Repo, pr.Title, quorum, pr.FilesChanged);

        return revision;
    }

    /// <summary>
    /// 刷新本节点的动态字段：有权限的 project、近 24h 票数、Claude 额度用量。
    /// </summary>
    /// <remarks>
    /// 这三个字段都会进签名心跳，别的节点靠它们算席位。刷新失败时刻意保留上一轮的值而不是
    /// 清零 —— 清零会让本节点看起来「又闲又有额度」，把席位全吸过来然后一个都干不了。
    /// </remarks>
    private async Task RefreshSelfAsync(CancellationToken ct)
    {
        var reviews = await acta.CountRecentBallotsAsync(mesh.Self.Id, ct).ConfigureAwait(false);

        var utilization = mesh.Self.Utilization;
        try
        {
            var reading = await usage.ReadAsync(mesh.Self.Id, ct).ConfigureAwait(false);
            utilization = reading.Utilization;

            // UI 的额度面板读这一份，不自己再起一次探针（见 NodeState.Usage）。
            state.SetUsage(reading);

            // 额度读数原先只在过线时才写日志，于是「探针到底有没有在工作、拿到的是真值还是
            // 折算」从日志里根本看不出来 —— 这次排查就卡在这上面。改成来源或档位一变就写一行，
            // 稳态下不刷屏（5 个百分点一档），但任何时候都能从日志确认它是活的。
            var bucket = (int)(reading.Utilization * 20);
            if (reading.Source != _loggedSource || bucket != _loggedBucket)
            {
                _loggedSource = reading.Source;
                _loggedBucket = bucket;
                logger.LogInformation(
                    "Claude 额度 {Used}%（{Detail}），来源 {Source}",
                    (reading.Utilization * 100).ToString("F0", CultureInfo.InvariantCulture),
                    reading.Detail, reading.Source);
            }

            if (reading.Utilization >= Elector.MaxUtilization)
            {
                // 不参与评审是设计行为，但必须说出来 —— 否则看起来就是「PR 卡在已召集不动」。
                // 带上重置时刻：额度什么时候回来是这条提示唯一有用的行动信息。
                var back = reading.ResetsAt is { } at
                    ? $"，{at.ToLocalTime():MM-dd HH:mm} 重置后自动恢复"
                    : string.Empty;

                logger.LogWarning(
                    "Claude 额度已用 {Used}%（{Detail}，来源 {Source}），超过 {Max}% 上限，本节点暂不入席{Back}",
                    (reading.Utilization * 100).ToString("F0", CultureInfo.InvariantCulture),
                    reading.Detail, reading.Source,
                    (Elector.MaxUtilization * 100).ToString("F0", CultureInfo.InvariantCulture), back);
                state.Notify(
                    NoticeKind.Warn,
                    $"Claude 额度已用 {reading.Utilization:P0}，本节点暂不接评审任务",
                    reading.ResetsAt is { } resetAt
                        ? $"{reading.Detail} · {resetAt.ToLocalTime():MM-dd HH:mm} 重置后自动恢复"
                        : reading.Detail);

                state.SetStatus(
                    $"Claude 额度已用 {reading.Utilization:P0}（{reading.Detail}）—— 超过 "
                    + $"{Elector.MaxUtilization:P0} 上限，本节点暂不接评审任务{back}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "读不到 Claude 额度用量，保留上一轮的 {Utilization:P0}", utilization);
            state.Notify(NoticeKind.Warn, "读不到 Claude 额度用量", ex.Message);
        }

        IReadOnlyList<string> projects;
        try
        {
            projects = options.FilterProjects(
                options.ProjectAllowList.Count > 0
                    ? options.ProjectAllowList
                    : await prSource.ListProjectsAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "读取 project 列表失败，保留上一轮的权限视图");
            projects = mesh.Self.Projects;
        }

        // 每轮都问一次而不是启动时取一次：claude 会自己原地更新，而「他明明更新过了」
        // 正是这个字段要回答的问题 —— 显示一个开机时的快照等于把人指回同一个坑。
        // 实现自己带缓存（半小时），所以这里并不会每轮都 fork 一个子进程。
        var claudeVersion = await claudeCli.VersionAsync(ct).ConfigureAwait(false);

        mesh.UpdateSelf(self => self with
        {
            Projects = [.. projects],
            Reviews24h = reviews,
            Utilization = utilization,
            MaxConcurrent = options.MaxConcurrent,
            ClaudeVersion = claudeVersion,
            LastHeartbeat = DateTimeOffset.UtcNow,
        });
    }
}

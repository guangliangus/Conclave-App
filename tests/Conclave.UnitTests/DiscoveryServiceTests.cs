using Conclave.Domain;

namespace Conclave.UnitTests;

/// <summary>
/// 发现循环：把本节点这一片的活跃 PR 上报成实时状态。
/// </summary>
/// <remarks>
/// 原先这套测试断言的是「链上写了 Summons」。那个模型把队列建在链上历史之上，PR 在
/// Azure DevOps 上被 merge 之后没人清，永远留在队列里 —— 实测积到 41 个 revision 里
/// 36 个是僵尸。现在断言的是「上报了什么」，而上报是<b>全量覆盖</b>语义。
/// </remarks>
public class DiscoveryServiceTests
{
    private static PrMeta Pr(int id = 2721, string commit = "aaaaaaaa11111111", bool draft = false)
        => TestElectors.Pr(id: id, author: "LIONMAIL\\youngsun", draft: draft) with { SrcCommit = commit };

    /// <summary>本节点当前上报的队列。</summary>
    private static IReadOnlyList<QueuedRevision> Reported(Harness h) => h.Mesh.State.Discovered;

    [Fact]
    public async Task A_new_pr_is_reported_with_its_quorum_and_rules_fingerprint()
    {
        using var h = new Harness();
        var pr = h.Publish(Pr());

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        var item = Assert.Single(Reported(h));
        Assert.Equal(pr.PrId, item.Pr.PrId);
        Assert.Equal(1, item.Quorum);

        // 指纹是「敏感路径清单 + quorum 策略」两者 —— 半年后回看要能分清
        // 「没命中敏感路径」和「当时策略本来就是全 1」。
        Assert.Equal(
            $"{ReservedMatters.Default.Fingerprint()}+{QuorumPolicy.Single.Fingerprint()}",
            item.RulesFingerprint);
    }

    [Fact]
    public async Task Nothing_is_written_to_the_chain_while_discovering()
    {
        using var h = new Harness();
        _ = h.Publish(Pr());

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // 链只记完成的评审。发现阶段往链上写块正是僵尸队列的成因 ——
        // 那种记录没有任何东西会去清它。
        Assert.Empty(await h.WholeChainAsync());
    }

    [Fact]
    public async Task A_closed_pr_drops_out_of_the_report_on_the_next_poll()
    {
        using var h = new Harness();
        var pr = h.Publish(Pr());
        await h.Discovery.PollOnceAsync(CancellationToken.None);
        Assert.Single(Reported(h));

        // PR 被 merge / abandon → 从 az repos pr list --status active 里消失。
        h.PrSource.Active[pr.Project].Clear();
        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // 这就是那个 bug 的根治：上报是全量覆盖，关掉的 PR 自然不在里面。
        // 不需要任何清理逻辑，也不需要新增区块类型。
        Assert.Empty(Reported(h));
    }

    [Fact]
    public async Task A_second_poll_reuses_the_cached_entry_without_more_api_calls()
    {
        using var h = new Harness();
        _ = h.Publish(Pr());

        await h.Discovery.PollOnceAsync(CancellationToken.None);
        var before = h.PrSource.EnrichCalls;

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // srcCommit 是幂等键的一半，它没变就说明 PR 内容没变，quorum 也不会变 ——
        // 每个新 PR 要两次 az 调用（实测约 3 秒），稳态下不该重复付这个钱。
        Assert.Equal(before, h.PrSource.EnrichCalls);
        Assert.Single(Reported(h));
    }

    [Fact]
    public async Task A_new_src_commit_replaces_the_previous_revision()
    {
        using var h = new Harness();
        var first = h.Publish(Pr(commit: "aaaaaaaa11111111"));
        await h.Discovery.PollOnceAsync(CancellationToken.None);
        Assert.Equal(first.ToRevision().Id, Assert.Single(Reported(h)).Revision.Id);

        // 作者 push 了修复：活跃列表里只有最新的 srcCommit。
        var second = h.Publish(Pr(commit: "bbbbbbbb22222222"));
        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // 被取代的旧版本自动消失 —— 这也是原先会积僵尸的一种情况。
        var item = Assert.Single(Reported(h));
        Assert.Equal(second.ToRevision().Id, item.Revision.Id);
    }

    [Fact]
    public async Task Draft_pr_is_never_reported()
    {
        using var h = new Harness();
        _ = h.Publish(Pr(draft: true));

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // 草稿在列举结果里就能判定，不必再花两次 API 取改动统计。
        Assert.Empty(Reported(h));
        Assert.Equal(0, h.PrSource.EnrichCalls);
    }

    [Fact]
    public async Task Pr_without_a_src_commit_is_skipped_rather_than_crashing()
    {
        using var h = new Harness();
        _ = h.Publish(Pr(commit: string.Empty));
        var ok = h.Publish(Pr(id: 2722));

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // 没有 lastMergeSourceCommit 就构造不出幂等键。跳过它，别连累同一轮的其他 PR。
        var item = Assert.Single(Reported(h));
        Assert.Equal(ok.PrId, item.Pr.PrId);
    }

    [Fact]
    public async Task A_failing_project_does_not_abort_the_round()
    {
        using var h = new Harness();
        _ = h.PrSource.Failing.Add("broken-project");
        var pr = h.Publish(Pr());

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // 一个 project 无权限/超时不该让其余的一起漏掉。
        Assert.Equal(pr.PrId, Assert.Single(Reported(h)).Pr.PrId);
    }

    [Fact]
    public async Task Polling_refreshes_the_self_capability_view()
    {
        using var h = new Harness();
        h.Usage.Utilization = 0.42;
        _ = h.Publish(Pr());

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        var self = h.Mesh.Self;
        Assert.Contains("liontrip-cms", self.Projects);
        Assert.Equal(0.42, self.Utilization);
        Assert.True(self.IsAlive(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_failed_usage_read_keeps_the_previous_value_instead_of_zeroing_it()
    {
        using var h = new Harness();
        h.Usage.Utilization = 0.9;
        _ = h.Publish(Pr());
        await h.Discovery.PollOnceAsync(CancellationToken.None);
        Assert.Equal(0.9, h.Mesh.Self.Utilization);

        // 清零会让本节点看起来「又闲又有额度」，把席位全吸过来然后一个都干不了。
        h.Usage.Throw = new InvalidOperationException("投影表读不了");
        await h.Discovery.PollOnceAsync(CancellationToken.None);

        Assert.Equal(0.9, h.Mesh.Self.Utilization);
    }

    [Fact]
    public async Task Denied_projects_are_neither_polled_nor_reported()
    {
        using var h = new Harness();
        h.Options.ProjectDenyList = ["*test*"];

        var kept = h.Publish(Pr(id: 4000));
        var skipped = h.Publish(Pr(id: 4001) with { Project = "edison-test" });

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        Assert.Contains(Reported(h), q => q.Pr.PrId == kept.PrId);
        Assert.DoesNotContain(Reported(h), q => q.Pr.PrId == skipped.PrId);

        // 连 az repos pr list 都不该为它跑一次 —— 目的就是别去拉那些 PR。
        Assert.DoesNotContain("edison-test", h.PrSource.ListedProjects);
    }

    [Fact]
    public async Task Denied_projects_drop_out_of_the_advertised_permission_view()
    {
        using var h = new Harness();
        h.Options.ProjectDenyList = ["*test*"];
        _ = h.Publish(Pr(id: 4002) with { Project = "edison-test" });
        _ = h.Publish(Pr(id: 4003));

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // 心跳里的 Projects 会喂给 SeatAssignment.Eligible。漏掉这一步的话，本节点虽然
        // 不轮询被排除的 project，却仍会去评审别的节点召集来的那些 PR。
        Assert.DoesNotContain("edison-test", h.Mesh.Self.Projects);
        Assert.Contains("liontrip-cms", h.Mesh.Self.Projects);
    }

    [Fact]
    public async Task Every_pr_is_reported_with_quorum_one_by_default()
    {
        using var h = new Harness();
        var big = h.Publish(Pr(id: 3000) with { FilesChanged = 30 });
        var small = h.Publish(Pr(id: 3001) with { FilesChanged = 1 });

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // 默认只评一次，改动多大都一样 —— 多跑几遍是成倍的额度，要显式配才开。
        Assert.Equal(1, Reported(h).Single(q => q.Pr.PrId == big.PrId).Quorum);
        Assert.Equal(1, Reported(h).Single(q => q.Pr.PrId == small.PrId).Quorum);
    }

    [Fact]
    public async Task A_configured_quorum_policy_shows_up_in_the_report()
    {
        using var h = new Harness();
        h.Options.Quorum = new QuorumPolicy { LargeChange = 3, MediumChange = 2 };
        var big = h.Publish(Pr(id: 3000) with { FilesChanged = 30 });
        var medium = h.Publish(Pr(id: 3001) with { FilesChanged = 8 });
        var small = h.Publish(Pr(id: 3002) with { FilesChanged = 1 });

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        Assert.Equal(3, Reported(h).Single(q => q.Pr.PrId == big.PrId).Quorum);
        Assert.Equal(2, Reported(h).Single(q => q.Pr.PrId == medium.PrId).Quorum);
        Assert.Equal(1, Reported(h).Single(q => q.Pr.PrId == small.PrId).Quorum);
    }

    [Fact]
    public async Task Allow_list_short_circuits_the_project_listing()
    {
        using var h = new Harness();
        h.Options.ProjectAllowList = ["liontrip-cms"];
        _ = h.Publish(Pr());

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // 白名单非空时不该再去问「有哪些 project」—— 省一次 az 调用。
        Assert.Equal(0, h.PrSource.ProjectListCalls);
        Assert.Single(Reported(h));
    }

    [Fact]
    public async Task Summoning_by_pr_id_reports_and_claims_it()
    {
        using var h = new Harness();
        var pr = h.Publish(Pr(id: 2878));

        var revision = await h.Discovery.SummonAsync(2878, CancellationToken.None);

        Assert.NotNull(revision);
        Assert.Contains(Reported(h), q => q.Revision.Id == revision!.Id);

        // 插队顺手认领：明确指名要评的，不该再交给加权 HRW 去抽 ——
        // 抽到别的节点的话，敲命令的人会看着它一直不动。
        Assert.Contains(h.Mesh.State.Claims, c => c.RevisionId == revision!.Id);
        Assert.Equal(h.SelfId, h.Mesh.State.Claims.Single().ElectorId);
    }

    [Fact]
    public async Task Summoning_an_unknown_pr_returns_null()
    {
        using var h = new Harness();

        Assert.Null(await h.Discovery.SummonAsync(999999, CancellationToken.None));
    }
    [Fact]
    public async Task Listing_is_one_collection_wide_call_not_one_per_project()
    {
        using var h = new Harness();
        h.Mesh.UpdateSelf(e => e with { Projects = ["liontrip-cms", "payment-center"] });
        h.PrSource.Active["liontrip-cms"] = [TestElectors.Pr(id: 1)];
        h.PrSource.Active["payment-center"] = [
            TestElectors.Pr(id: 2) with { Project = "payment-center" }];

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // 逐 project 列举时每次 az 调用实测 1.27 秒，34 个 project 就是 43 秒 ——
        // 而其中绝大多数 project 一个活跃 PR 都没有。整轮只该问一次。
        Assert.Equal(1, h.PrSource.CollectionListCalls);
        Assert.Empty(h.PrSource.ListedProjects);
        Assert.Equal(2, h.Mesh.State.Discovered.Count);
    }

    [Fact]
    public async Task Prs_from_projects_i_do_not_poll_are_ignored()
    {
        using var h = new Harness();

        // collection 级列举拿回来的是<b>整个组织</b>的 PR，不再天然只剩自己该看的那些 ——
        // 所以过滤必须自己做。漏了它，白名单/黑名单会静默失效（配了「过滤掉 test
        // project」却照样把它们全评一遍），mesh 分片也会变成每个节点都上报全部。
        h.Options.ProjectAllowList = ["liontrip-cms"];
        h.PrSource.Active["liontrip-cms"] = [TestElectors.Pr(id: 1)];
        h.PrSource.Active["some-test-project"] = [
            TestElectors.Pr(id: 2) with { Project = "some-test-project" }];

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        var reported = h.Mesh.State.Discovered.Single();
        Assert.Equal(1, reported.Pr.PrId);
    }

    [Fact]
    public async Task Project_names_are_matched_case_insensitively()
    {
        using var h = new Harness();
        h.Options.ProjectAllowList = ["LionTrip-CMS"];
        h.PrSource.Active["x"] = [TestElectors.Pr(id: 1) with { Project = "liontrip-cms" }];

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // 实测 ADO 不同接口返回的 project 名大小写不完全一致；按大小写敏感比的话
        // 那些 PR 会被静默丢掉 —— 界面上就是「队列莫名少了一批」。
        Assert.Single(h.Mesh.State.Discovered);
    }

    [Fact]
    public async Task A_missing_collection_route_falls_back_to_per_project()
    {
        using var h = new Harness();
        h.Mesh.UpdateSelf(e => e with { Projects = ["liontrip-cms"] });
        h.PrSource.Active["liontrip-cms"] = [TestElectors.Pr(id: 1)];
        h.PrSource.CollectionListingFails = true;

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // 那个路由不是所有 ADO 部署都开着。退回去只是慢，不能瘫。
        Assert.Single(h.Mesh.State.Discovered);
        Assert.Contains("liontrip-cms", h.PrSource.ListedProjects);
    }

}

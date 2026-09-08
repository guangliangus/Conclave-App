using Conclave.Domain;

namespace Conclave.UnitTests;

public class DiscoveryServiceTests
{
    private static PrMeta Pr(int id = 2721, string commit = "aaaaaaaa11111111", bool draft = false)
        => TestElectors.Pr(id: id, author: "LIONMAIL\\youngsun", draft: draft) with { SrcCommit = commit };

    [Fact]
    public async Task A_new_pr_gets_summoned()
    {
        using var h = new Harness();
        var pr = h.Publish(Pr());

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        var state = await h.StateOfAsync(pr.ToRevision());
        Assert.NotNull(state.Summons);
        Assert.Equal(pr.PrId, state.Summons.Pr.PrId);
        Assert.Equal(ReservedMatters.Default.Fingerprint(), state.Summons.RulesFingerprint);
    }

    [Fact]
    public async Task Second_poll_does_not_summon_the_same_revision_again()
    {
        using var h = new Harness();
        var pr = h.Publish(Pr());

        await h.Discovery.PollOnceAsync(CancellationToken.None);
        await h.Discovery.PollOnceAsync(CancellationToken.None);
        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // P0.5 的核心性质：不重复烧 token。
        var chain = await h.ChainAsync(pr.ToRevision());
        Assert.Single(chain);
    }

    [Fact]
    public async Task A_new_src_commit_becomes_a_separate_revision_on_the_global_chain()
    {
        using var h = new Harness();
        var first = h.Publish(Pr(commit: "aaaaaaaa11111111"));
        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // 作者 push 了新 commit。
        var second = h.Publish(first with { SrcCommit = "bbbbbbbb22222222" });
        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // 两个版本各自一个 revision，各有一块 Summons……
        Assert.Single(await h.ChainAsync(first.ToRevision()));
        Assert.Single(await h.ChainAsync(second.ToRevision()));

        // ……但指向同一个 PR，编排层据此只处理最新那个。
        Assert.Equal(first.ToRevision().PullRequest, second.ToRevision().PullRequest);

        // 全局链上前后相连，是一条时间线。
        var whole = await h.WholeChainAsync();
        Assert.Equal(2, whole.Count);
        Assert.Equal(
            [first.ToRevision(), second.ToRevision()],
            ActaProjection.RevisionsOf(whole));
    }

    [Fact]
    public async Task Draft_pr_is_never_summoned()
    {
        using var h = new Harness();
        var pr = h.Publish(Pr(draft: true));

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        Assert.Empty(await h.ChainAsync(pr.ToRevision()));
    }

    [Fact]
    public async Task Pr_without_a_src_commit_is_skipped_rather_than_crashing()
    {
        using var h = new Harness();
        var pr = h.Publish(Pr(commit: string.Empty));

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // 构不出幂等键就不该落链 —— 否则会得到一条永远重复召集的链。
        Assert.Empty(await h.Acta.ReadOpenRevisionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_failing_project_does_not_abort_the_round()
    {
        using var h = new Harness();
        _ = h.PrSource.Failing.Add("broken-project");
        var pr = h.Publish(Pr());

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // 一个 project 无权限/超时不该让其余 33 个 project 一起漏掉。
        Assert.NotNull((await h.StateOfAsync(pr.ToRevision())).Summons);
    }

    [Fact]
    public async Task Polling_refreshes_the_self_capability_view()
    {
        using var h = new Harness(repos: ["cms-apostrophe", "payment-center"]);
        _ = h.Publish(Pr());

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        var self = h.Mesh.Self;
        Assert.Equal(2, self.Repos.Count);
        Assert.Contains("liontrip-cms", self.Projects);
        Assert.True(self.IsAlive(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Quorum_recorded_on_the_summons_follows_the_diff_size()
    {
        using var h = new Harness();
        var big = h.Publish(Pr(id: 3000) with { FilesChanged = 30, LinesChanged = 900 });
        var small = h.Publish(Pr(id: 3001) with { FilesChanged = 1, LinesChanged = 5 });

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        Assert.Equal(3, (await h.StateOfAsync(big.ToRevision())).Summons!.Quorum);
        Assert.Equal(1, (await h.StateOfAsync(small.ToRevision())).Summons!.Quorum);
    }

    [Fact]
    public async Task Allow_list_short_circuits_the_project_listing()
    {
        using var h = new Harness();
        h.Options.ProjectAllowList.Add("liontrip-cms");
        _ = h.Publish(Pr());

        await h.Discovery.PollOnceAsync(CancellationToken.None);

        // 只列了白名单里那一个 project。
        Assert.Equal(1, h.PrSource.ListCalls);
    }
}

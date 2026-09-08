using Conclave.Domain;

namespace Conclave.UnitTests;

public class ReviewOrchestratorTests
{
    private const string Author = "LIONMAIL\\youngsun";

    private static PrMeta Pr(int id = 2721, int files = 3, int lines = 40)
        => TestElectors.Pr(id: id, author: Author, files: files, lines: lines)
            with { SrcCommit = "aaaaaaaa11111111" };

    private static Elector Peer(string id) => new()
    {
        Id = id,
        PublicKey = "pk-" + id,
        AzIdentity = "peer-" + id,
        Repos = ["cms-apostrophe"],
        Projects = ["liontrip-cms"],
        MaxConcurrent = 2,
        LastHeartbeat = DateTimeOffset.UtcNow,
    };

    private static async Task<Revision> SeedAsync(Harness h, PrMeta pr)
    {
        _ = h.Publish(pr);
        await h.Discovery.PollOnceAsync(CancellationToken.None);
        return pr.ToRevision();
    }

    [Fact]
    public async Task Without_auto_review_the_node_only_watches()
    {
        using var h = new Harness();
        var rev = await SeedAsync(h, Pr());

        await h.TickAsync();
        await h.TickAsync();

        // AutoReview 默认关，所以只该有 Summons，一票不投、一分钱额度不烧。
        var state = await h.StateOfAsync(rev);
        Assert.Empty(state.Seatings);
        Assert.Equal(0, h.Runner.Calls);

        // 但队列视图要能看到它，否则 UI 上根本没法手动点。
        Assert.Single(h.State.Pipeline);
        Assert.Equal("待评审", h.State.Pipeline[0].Stage);
    }

    [Fact]
    public async Task Manual_request_drives_seating_then_ballot_then_promulgation()
    {
        using var h = new Harness();
        var rev = await SeedAsync(h, Pr());

        h.Orchestrator.RequestReview(rev.Id);

        await h.TickAsync();                                  // 认领席位
        var seated = await h.StateOfAsync(rev);
        Assert.Single(seated.Seatings);
        Assert.Equal(h.SelfId, seated.Seatings[0].ElectorId);
        Assert.Empty(seated.Ballots);                         // 刻意分两轮：先让 Seating 落链

        await h.TickAsync();                                  // 跑评审并出票
        var voted = await h.StateOfAsync(rev);
        Assert.Single(voted.Ballots);
        Assert.Equal(ReviewDecision.Reject, voted.Ballots[0].Decision);
        Assert.Equal(1, h.Runner.Calls);

        await h.TickAsync();                                  // 公布
        var done = await h.StateOfAsync(rev);
        Assert.NotNull(done.Promulgation);
        Assert.Equal(ReviewDecision.Reject, done.Promulgation.Decision);
        Assert.False(done.Promulgation.Degraded);
        Assert.True(done.IsFinished);
    }

    [Fact]
    public async Task Auto_review_needs_no_manual_request()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        var rev = await SeedAsync(h, Pr());

        await h.TickAsync();
        await h.TickAsync();
        await h.TickAsync();

        Assert.NotNull((await h.StateOfAsync(rev)).Promulgation);
    }

    [Fact]
    public async Task A_promulgated_chain_is_no_longer_open()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        var rev = await SeedAsync(h, Pr());

        for (var i = 0; i < 3; i++)
        {
            await h.TickAsync();
        }

        Assert.Empty(await h.Acta.ReadOpenChainsAsync(CancellationToken.None));

        // 再多跑几轮也不该重复评审。
        await h.TickAsync();
        Assert.Equal(1, h.Runner.Calls);
    }

    [Fact]
    public async Task Runner_failure_still_produces_an_error_ballot()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Runner.Throw = new InvalidOperationException("claude 挂了");
        var rev = await SeedAsync(h, Pr());

        await h.TickAsync();
        await h.TickAsync();

        // 失败也要出票，否则这个 PR 会永远等下去。
        var state = await h.StateOfAsync(rev);
        Assert.Single(state.Ballots);
        Assert.Equal(ReviewDecision.Error, state.Ballots[0].Decision);
        Assert.Contains("claude 挂了", state.Ballots[0].Error);

        await h.TickAsync();
        var done = await h.StateOfAsync(rev);
        Assert.Equal(ReviewDecision.Error, done.Promulgation!.Decision);
        Assert.True(done.Promulgation.Degraded);
    }

    [Fact]
    public async Task A_non_lowest_seat_holder_does_not_promulgate()
    {
        using var h = new Harness();
        var peers = new[] { Peer("peer-1"), Peer("peer-2"), Peer("peer-3"), Peer("peer-4") };
        List<Elector> intendedMesh = [h.Mesh.Self, .. peers];

        // 先在目标 mesh 上反查一个「本节点坐 round=1」的大 PR（quorum=3）。
        var template = Pr(files: 30, lines: 900);
        var prId = h.FindPrIdSeating(1, template, intendedMesh);

        // 播种时 mesh 里只有自己 —— 否则 project 的轮询责任会按 HRW 分片给某个 peer，
        // 本节点压根不会写 Summons。加 peer 必须在播种之后。
        var rev = await SeedAsync(h, template with { PrId = prId });
        h.Mesh.Peers.AddRange(peers);

        h.Options.AutoReview = true;
        await h.TickAsync();       // 认领 round=1
        await h.TickAsync();       // 出票

        var state = await h.StateOfAsync(rev);
        Assert.True(state.Ballots.ContainsKey(1), "本节点应当坐 round=1 并出票");
        Assert.False(state.Ballots.ContainsKey(0));

        // 只有 1 票、quorum=3 → 还不能公布；而且 round=0 不是自己，本节点无权公布。
        await h.TickAsync();
        Assert.Null((await h.StateOfAsync(rev)).Promulgation);
    }

    [Fact]
    public async Task The_author_never_gets_a_seat_on_their_own_pr()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Mesh.UpdateSelf(self => self with { AzIdentity = "youngsun" });   // 本节点就是作者

        var rev = await SeedAsync(h, Pr());
        await h.TickAsync();
        await h.TickAsync();

        var state = await h.StateOfAsync(rev);
        Assert.Empty(state.Seatings);
        Assert.Equal(0, h.Runner.Calls);
        Assert.Equal(-1, h.State.Pipeline[0].MySeat);   // UI 上显示「旁观」
    }

    [Fact]
    public async Task A_timed_out_seat_is_recessed_and_then_retried()
    {
        using var h = new Harness();
        var rev = await SeedAsync(h, Pr());
        h.Orchestrator.RequestReview(rev.Id);

        await h.TickAsync();                              // 认领 round=0
        Assert.Single((await h.StateOfAsync(rev)).Seatings);

        h.Options.SeatingTimeout = TimeSpan.Zero;         // 让它立刻算超时
        await h.TickAsync();

        var recessed = await h.StateOfAsync(rev);
        Assert.Contains(0, recessed.Recessed);

        // 弃权当轮不该同时开跑评审 —— 那样自相矛盾。
        Assert.Equal(0, h.Runner.Calls);

        // 但必须能重试：弃权追加了一个轮次，单节点 mesh 上又抽回自己。
        h.Options.SeatingTimeout = TimeSpan.FromMinutes(10);
        await h.TickAsync();                              // 认领 round=1
        var retried = await h.StateOfAsync(rev);
        Assert.True(retried.Seatings.ContainsKey(1), "弃权后必须有可重试的席位，否则 PR 永远卡住");

        await h.TickAsync();                              // 出票
        await h.TickAsync();                              // 公布
        Assert.NotNull((await h.StateOfAsync(rev)).Promulgation);
    }

    [Fact]
    public async Task A_seat_the_node_is_actively_running_is_not_recessed()
    {
        using var h = new Harness();
        var rev = await SeedAsync(h, Pr());
        h.Orchestrator.RequestReview(rev.Id);

        await h.TickAsync();                                  // 认领 round=0
        Assert.Single((await h.StateOfAsync(rev)).Seatings);

        // 让评审挂住，造出「本节点正在跑这个席位」的时间窗。
        h.Runner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.Orchestrator.TickAsync(CancellationToken.None);   // 开跑，刻意不等它收尾
        await h.Runner.Started.Task;

        // 现在把超时压到 0：席位「过期」了，但本节点确实在跑。
        h.Options.SeatingTimeout = TimeSpan.Zero;
        await h.Orchestrator.TickAsync(CancellationToken.None);

        // 慢不等于掉线 —— 自己在跑的席位不能被自己判弃权。
        Assert.Empty((await h.StateOfAsync(rev)).Recessed);

        h.Runner.Gate.SetResult();
        await h.Orchestrator.WhenIdleAsync();

        var state = await h.StateOfAsync(rev);
        Assert.Empty(state.Recessed);
        Assert.Single(state.Ballots);
    }

    [Fact]
    public async Task Posting_is_skipped_while_the_toggle_is_off()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        var rev = await SeedAsync(h, Pr());

        for (var i = 0; i < 3; i++)
        {
            await h.TickAsync();
        }

        // 默认不往真实 PR 写东西。
        Assert.Empty(h.PrSource.Posted);
        Assert.Null((await h.StateOfAsync(rev)).Promulgation!.ThreadId);
    }

    [Fact]
    public async Task Posting_records_the_thread_id_when_enabled()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Options.PostToAzureDevOps = true;
        var rev = await SeedAsync(h, Pr());

        for (var i = 0; i < 3; i++)
        {
            await h.TickAsync();
        }

        Assert.Single(h.PrSource.Posted);
        Assert.Equal(8821, (await h.StateOfAsync(rev)).Promulgation!.ThreadId);
    }

    [Fact]
    public async Task A_posting_failure_does_not_block_the_verdict_from_being_recorded()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Options.PostToAzureDevOps = true;
        h.PrSource.NextThreadId = null;
        var rev = await SeedAsync(h, Pr());

        for (var i = 0; i < 3; i++)
        {
            await h.TickAsync();
        }

        // 投递没拿到 thread id，结论仍然要落链 —— 投递可以人工补，结论不能丢。
        var promulgation = (await h.StateOfAsync(rev)).Promulgation;
        Assert.NotNull(promulgation);
        Assert.Null(promulgation.ThreadId);
    }

    [Fact]
    public async Task A_new_revision_after_promulgation_reopens_the_chain()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        var first = Pr();
        var rev1 = await SeedAsync(h, first);

        for (var i = 0; i < 3; i++)
        {
            await h.TickAsync();
        }

        Assert.NotNull((await h.StateOfAsync(rev1)).Promulgation);

        // 作者 push 了新 commit。
        var second = first with { SrcCommit = "bbbbbbbb22222222" };
        var rev2 = await SeedAsync(h, second);

        for (var i = 0; i < 3; i++)
        {
            await h.TickAsync();
        }

        Assert.NotNull((await h.StateOfAsync(rev2)).Promulgation);
        Assert.Equal(2, h.Runner.Calls);                       // 两个版本各评一次
        Assert.Equal(rev1.ChainId, rev2.ChainId);              // 同一条链
    }

    [Fact]
    public async Task Merged_findings_carry_confidence_onto_the_chain()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        var rev = await SeedAsync(h, Pr());

        for (var i = 0; i < 3; i++)
        {
            await h.TickAsync();
        }

        var findings = (await h.StateOfAsync(rev)).Promulgation!.Findings;

        Assert.Single(findings);
        Assert.Equal(1.0, findings[0].Confidence, 3);          // quorum=1，唯一一票提到 → 1.0
        Assert.Equal("src/A.cs", findings[0].Best.File);
    }

    [Fact]
    public async Task The_load_gate_throttles_but_never_drops_work()
    {
        using var h = new Harness(maxConcurrent: 1);
        h.Options.AutoReview = true;

        for (var id = 3000; id < 3005; id++)
        {
            _ = h.Publish(Pr(id: id));
        }

        await h.Discovery.PollOnceAsync(CancellationToken.None);
        Assert.Equal(5, (await h.Acta.ReadOpenChainsAsync(CancellationToken.None)).Count);

        // 单个 tick 能开跑几个是不确定的 —— 节点一旦饱和，按 SeatAssignment.Eligible
        // 就不再有入席资格，所以后面的链这一轮直接旁观。要断言的是最终收敛，不是单轮吞吐。
        for (var i = 0; i < 40 && (await h.Acta.ReadOpenChainsAsync(CancellationToken.None)).Count > 0; i++)
        {
            await h.TickAsync();
        }

        Assert.Empty(await h.Acta.ReadOpenChainsAsync(CancellationToken.None));
        Assert.Equal(5, h.Runner.Calls);                       // 一个不漏，一个不重
        Assert.Equal(0, h.Mesh.Self.RunningJobs);              // 负载计数归零
    }

    [Fact]
    public async Task Every_block_written_is_broadcast_and_signed()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        var rev = await SeedAsync(h, Pr());

        for (var i = 0; i < 3; i++)
        {
            await h.TickAsync();
        }

        var chain = await h.ChainAsync(rev);

        Assert.All(chain, b => Assert.True(b.VerifySignature()));
        Assert.Equal(
            [BlockKind.Summons, BlockKind.Seating, BlockKind.Ballot, BlockKind.Promulgation],
            chain.Select(b => b.Kind));

        // 每一块都要广播出去，P1 接上 mesh 时才不会有块只留在本地。
        Assert.Equal(chain.Count, h.Mesh.Broadcast.Count);
    }
}

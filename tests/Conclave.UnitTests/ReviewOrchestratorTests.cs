using Conclave.Domain;

namespace Conclave.UnitTests;

/// <summary>
/// 编排循环：从实时状态里的队列拿活，评完把票写上链。
/// </summary>
/// <remarks>
/// 队列不再来自链上历史，所以这里先 <see cref="Harness.ReportAsync"/> 让发现循环上报，
/// 再驱动编排。原先还有一批测席位超时（<c>Seating</c> + <c>Recess</c>）的用例 ——
/// 那套机制已经换成心跳驱动，节点掉线后它的 <see cref="ActiveReview"/> 随心跳窗口过期
/// 而消失、PR 自动回队列，见 <see cref="QueueProjectionTests"/>。
/// </remarks>
public class ReviewOrchestratorTests
{
    private const string Author = "LIONMAIL\\youngsun";

    private static PrMeta Pr(int id = 2721, int files = 3)
        => TestElectors.Pr(id: id, author: Author, files: files)
            with { SrcCommit = "aaaaaaaa11111111" };

    private static Elector Peer(string id) => new()
    {
        Id = id,
        PublicKey = "pk-" + id,
        AzIdentity = "peer-" + id,
        Projects = ["liontrip-cms"],
        MaxConcurrent = 2,
        LastHeartbeat = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Without_auto_review_the_node_only_watches()
    {
        using var h = new Harness();
        var rev = await h.ReportAsync(Pr());

        await h.TickAsync();

        // 一开机就把所有活跃 PR 全评一遍会烧掉可观额度，所以默认只看不做。
        Assert.Equal(0, h.Runner.Calls);
        Assert.Empty(await h.ChainAsync(rev));
        Assert.Equal("待评审", h.State.Pipeline.Single().Stage);
    }

    [Fact]
    public async Task A_manual_request_drives_ballot_then_verdict()
    {
        using var h = new Harness();
        var rev = await h.ReportAsync(Pr());

        h.Orchestrator.RequestReview(rev.Id);
        await h.TickAsync();          // 跑评审、出票

        var voted = await h.StateOfAsync(rev);
        Assert.Single(voted.Ballots);
        Assert.Equal(ReviewDecision.Reject, voted.Ballots[0].Decision);

        await h.TickAsync();          // 公布

        var done = await h.StateOfAsync(rev);
        Assert.True(done.IsFinished);
        Assert.Equal(ReviewDecision.Reject, done.Promulgation!.Decision);
    }

    [Fact]
    public async Task Auto_review_needs_no_manual_request()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        var rev = await h.ReportAsync(Pr());

        await h.TickAsync();

        Assert.Single((await h.StateOfAsync(rev)).Ballots);
    }

    [Fact]
    public async Task A_claim_alone_is_enough_to_start_without_auto_review()
    {
        using var h = new Harness();
        var rev = await h.ReportAsync(Pr());

        // 认领就是「明确要评」的信号，不必再打开 AutoReview。
        h.Mesh.UpdateState(s => s with
        {
            Claims = [new ReviewClaim(rev.Id, h.SelfId, DateTimeOffset.UtcNow)],
        });

        await h.TickAsync();

        Assert.Single((await h.StateOfAsync(rev)).Ballots);
    }

    [Fact]
    public async Task Reviewing_is_published_while_running_and_withdrawn_after()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        var rev = await h.ReportAsync(Pr());
        h.Runner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tick = h.Orchestrator.TickAsync(CancellationToken.None);
        await h.Runner.Started.Task;

        // 跑的期间要广播「我在评这个」—— 别的节点据此不插手。
        var running = h.Mesh.State.Reviewing.Single();
        Assert.Equal(rev.Id, running.RevisionId);
        Assert.Equal(0, running.Round);

        h.Runner.Gate.SetResult();
        await tick;
        await h.Orchestrator.WhenIdleAsync();

        // 跑完就撤下，否则这个 PR 会永远显示「评审中」。
        Assert.Empty(h.Mesh.State.Reviewing);
    }

    /// <summary>
    /// 「在评」状态泄漏一条，那个 PR 就对整个 mesh 永久隐身 —— 每轮编排都要核对。
    /// </summary>
    /// <remarks>
    /// <c>StartReview</c> 的 finally 先清 <c>Reviewing</c> 再摘 <c>_inFlight</c>，
    /// 所以正常情况下 <c>Reviewing ⊆ _inFlight</c> 恒成立，这条用例造的是那个不该出现的
    /// 违例状态。不核对的话别的节点看到「有人在评」就不插手，而本节点只要不重启
    /// 就一直这么广播下去。
    /// </remarks>
    [Fact]
    public async Task An_active_review_with_no_task_behind_it_is_taken_back_down()
    {
        using var h = new Harness();
        var rev = await h.ReportAsync(Pr());

        // 没有任何 task，只有状态 —— 泄漏后的样子。
        h.Mesh.UpdateState(s => s with
        {
            Reviewing = [new ActiveReview(rev.Id, 0, DateTimeOffset.UtcNow)],
        });

        await h.Orchestrator.TickAsync(CancellationToken.None);

        Assert.Empty(h.Mesh.State.Reviewing);
    }

    /// <summary>
    /// 超时之后取消也拉不回来的评审，要把「在评」撤下来让别的节点接管。
    /// </summary>
    /// <remarks>
    /// 正常超时的收尾是「CTS 到点 → 杀子进程 → 补一张 Error 票」，几秒就走完。
    /// 这条造的是卡在取消拉不回来的地方那种：task 还在（<c>_inFlight</c> 有它），
    /// 但已经跑了远超 ReviewTimeout 的时间。
    /// </remarks>
    [Fact]
    public async Task A_review_stuck_far_past_its_timeout_stops_being_advertised()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Options.ReviewTimeout = TimeSpan.FromMinutes(15);
        var rev = await h.ReportAsync(Pr());
        h.Runner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tick = h.Orchestrator.TickAsync(CancellationToken.None);
        await h.Runner.Started.Task;
        await tick;

        // task 还挂在 Gate 上（_inFlight 里有它），把开始时刻挪到很久以前。
        var running = h.Mesh.State.Reviewing.Single();
        h.Mesh.UpdateState(s => s with
        {
            Reviewing = [running with { StartedAt = DateTimeOffset.UtcNow.AddHours(-1) }],
        });

        await h.Orchestrator.TickAsync(CancellationToken.None);

        Assert.Empty(h.Mesh.State.Reviewing);
        Assert.Equal(rev.Id, running.RevisionId);

        h.Runner.Gate.SetResult();
        await h.Orchestrator.WhenIdleAsync();
    }

    /// <summary>还在预算之内的评审绝不能被撤 —— 误撤等于两个节点同时评同一轮。</summary>
    [Fact]
    public async Task A_review_still_within_its_budget_keeps_being_advertised()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        _ = await h.ReportAsync(Pr());
        h.Runner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tick = h.Orchestrator.TickAsync(CancellationToken.None);
        await h.Runner.Started.Task;
        await tick;

        await h.Orchestrator.TickAsync(CancellationToken.None);

        Assert.Single(h.Mesh.State.Reviewing);

        h.Runner.Gate.SetResult();
        await h.Orchestrator.WhenIdleAsync();
    }

    /// <summary>
    /// 什么都没改的 UpdateState 不该让版本号动。
    /// </summary>
    /// <remarks>
    /// 版本号是对端「要不要拉一次 GET /state」的唯一依据。空操作也递增的话，
    /// 一次重复的撤销认领就能让 mesh 里每个节点白拉一遍状态 ——
    /// 实测日志里同一个 revision 隔几秒「撤销认领」两次，第二次正是这种空操作。
    /// </remarks>
    [Fact]
    public async Task Releasing_a_claim_twice_does_not_bump_the_state_version()
    {
        using var h = new Harness();
        var rev = await h.ReportAsync(Pr());

        h.Orchestrator.Claim(rev.Id);
        h.Orchestrator.Release(rev.Id);
        var settled = h.Mesh.State.Version;

        h.Orchestrator.Release(rev.Id);

        Assert.Equal(settled, h.Mesh.State.Version);
    }

    /// <summary>
    /// 停机时正在跑的评审，必须在链上留下一张写明原因的 Error 票。
    /// </summary>
    /// <remarks>
    /// 实测的坑：<c>dev-cluster.sh down</c> 之后进程直接退出，在跑的 claude 子进程被连根
    /// 砍掉，链上一个字都没留 —— 那次评审烧掉的 token 无处可查，别的节点还得等心跳窗口
    /// 过期才知道席位空了。根因是评审在 <c>Task.Run</c> 里 fire-and-forget 跑着，
    /// 只认 ReviewTimeout 那个 CTS，停机信号传不进去。
    /// </remarks>
    [Fact]
    public async Task Shutting_down_mid_review_leaves_an_error_ballot_that_says_why()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        var rev = await h.ReportAsync(Pr());
        h.Runner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tick = h.Orchestrator.TickAsync(CancellationToken.None);
        await h.Runner.Started.Task;
        await tick;

        // 闸没放，评审还挂在里面 —— 正是 down 打断它的那一刻。
        await h.Orchestrator.StopAsync(CancellationToken.None);

        var ballot = (await h.ChainAsync(rev))
            .Where(b => b.Kind == BlockKind.Ballot)
            .Select(b => b.Payload<BallotPayload>())
            .Single();

        Assert.NotNull(ballot);
        Assert.Equal(ReviewDecision.Error, ballot.Decision);

        // 原因必须是人话。OperationCanceledException 的 "The operation was canceled."
        // 半年后回看链上这张票时什么也说明不了。
        Assert.NotNull(ballot.Error);
        Assert.Contains("停机", ballot.Error, StringComparison.Ordinal);

        // 席位也得让出来，否则别的节点会一直以为这台还在评。
        Assert.Empty(h.Mesh.State.Reviewing);
    }

    /// <summary>停机时排在并发闸后面、一个 token 都没烧的评审，不该被记一张 Error 票。</summary>
    /// <remarks>
    /// 它跟「跑到一半被打断」是两回事：给它补票会白烧掉一次
    /// <c>ConclaveOptions.MaxReviewAttempts</c> 的重试额度。
    /// </remarks>
    [Fact]
    public async Task A_review_that_never_started_gets_no_ballot_on_shutdown()
    {
        using var h = new Harness(maxConcurrent: 1);
        h.Options.AutoReview = true;
        _ = await h.ReportAsync(Pr(id: 3101));
        _ = await h.ReportAsync(Pr(id: 3102), append: true);
        h.Runner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tick = h.Orchestrator.TickAsync(CancellationToken.None);
        await h.Runner.Started.Task;
        await tick;

        await h.Orchestrator.StopAsync(CancellationToken.None);

        // 两个 PR 里只有一个真的起跑（并发上限 1），所以整条链上只该有一张票。
        // 刻意不断言「哪一个」—— 席位是按 HRW 抽的，跟上报顺序无关，
        // 假定第一个上报的先跑会让这个用例在种子变化时莫名其妙地红。
        Assert.Equal(1, h.Runner.Calls);
        Assert.Single(await h.WholeChainAsync(), b => b.Kind == BlockKind.Ballot);
    }

    [Fact]
    public async Task A_finished_revision_shows_its_verdict_not_just_that_it_finished()
    {
        using var h = new Harness();
        var rev = await h.ReportAsync(Pr());

        h.Orchestrator.RequestReview(rev.Id);
        await h.TickAsync();   // 评审 + 出票
        await h.TickAsync();   // 公布
        await h.TickAsync();   // 重新投影出队列

        var view = h.State.Pipeline.Single(v => v.Revision.Id == rev.Id);

        // 早先这里是「阶段=已公布，结论=null」：界面上那一行写着已完成，结论列却是「—」，
        // 还挂着一条收票进度条，而且因为 Decision 为空它看起来仍然可操作。
        Assert.Equal("已评审", view.Stage);
        Assert.Equal(ReviewDecision.Reject, view.Decision);
        Assert.Equal(1, view.Findings);
    }

    [Fact]
    public async Task Only_one_review_runs_at_a_time()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;

        // 两个 PR，两个都归本节点（单节点 mesh 里席位没别人可去）。
        _ = await h.ReportAsync(Pr(id: 3001));
        _ = await h.ReportAsync(Pr(id: 3002), append: true);
        h.Runner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tick = h.Orchestrator.TickAsync(CancellationToken.None);
        await h.Runner.Started.Task;
        await tick;

        // 一个节点一次只评一个 PR。并发起跑的话，两个 PR 都会被标成「我在评」，
        // 而实际上第二个在信号量后面排队 —— 别的空闲节点看到有人接手就不插手，
        // 活全堆在这一台上。所以这一轮只能起一个，另一个留在队列里。
        Assert.Equal(1, h.Runner.Calls);
        Assert.Single(h.Mesh.State.Reviewing);

        // 手上有活时再转一轮也不许再接。
        // 这里必须直接调 Orchestrator.TickAsync —— Harness.TickAsync 末尾会等所有在跑的
        // 评审收尾，而此刻那一个正卡在闸门上，等它就是死等自己。
        await h.Orchestrator.TickAsync(CancellationToken.None);
        Assert.Equal(1, h.Runner.Calls);

        // 放闸：置 null 是没用的，跑着的那次已经 await 在这个 TCS 上了。
        h.Runner.Gate.SetResult();
        await h.Orchestrator.WhenIdleAsync();

        // 评完了才轮到下一个 —— 席位是按那一刻的队列现状重新取的。
        await h.TickAsync();
        Assert.Equal(2, h.Runner.Calls);
    }

    [Fact]
    public async Task Starting_a_review_consumes_the_claim()
    {
        using var h = new Harness();
        var rev = await h.ReportAsync(Pr());
        h.Mesh.UpdateState(s => s with
        {
            Claims = [new ReviewClaim(rev.Id, h.SelfId, DateTimeOffset.UtcNow)],
        });

        await h.TickAsync();

        // 认领是「打算评」，兑现成实际开跑之后那个意向状态就没意义了。
        Assert.Empty(h.Mesh.State.Claims);
    }

    [Fact]
    public async Task Runner_failure_produces_an_error_ballot_that_is_not_a_verdict()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Runner.Throw = new InvalidOperationException("claude 挂了");
        var rev = await h.ReportAsync(Pr());

        await h.TickAsync();

        var state = await h.StateOfAsync(rev);
        Assert.Single(state.Ballots);
        Assert.Equal(ReviewDecision.Error, state.Ballots[0].Decision);
        Assert.Contains("claude 挂了", state.Ballots[0].Error);

        // 一次偶发的子进程失败不该变成这个 PR 的最终结论 —— 它让出席位、由别人重试。
        Assert.Null(state.Promulgation);
        Assert.Empty(state.ValidBallots);
    }

    [Fact]
    public async Task A_failed_round_is_retried_and_a_later_success_wins()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Runner.Throw = new InvalidOperationException("claude 挂了");
        var rev = await h.ReportAsync(Pr());

        await h.TickAsync();
        Assert.Equal([0], (await h.StateOfAsync(rev)).SpentRounds);

        // 单节点上重试会再抽到自己 —— 这次让它成功。
        h.Runner.Throw = null;
        await h.TickAsync();
        await h.TickAsync();

        var done = await h.StateOfAsync(rev);
        Assert.Equal(ReviewDecision.Reject, done.Promulgation!.Decision);
        Assert.Single(done.ValidBallots);
        Assert.False(done.Promulgation.Degraded);
    }

    [Fact]
    public async Task Retries_stop_at_the_configured_ceiling()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Options.MaxReviewAttempts = 2;
        h.Runner.Throw = new InvalidOperationException("claude 每次都起不来");
        var rev = await h.ReportAsync(Pr());

        for (var i = 0; i < 8; i++)
        {
            await h.TickAsync();
        }

        var state = await h.StateOfAsync(rev);

        // 到上限就收尾，否则同一个 PR 会无限重试下去。
        Assert.Equal(2, state.SpentRounds.Count);
        Assert.Equal(ReviewDecision.Error, state.Promulgation!.Decision);
        Assert.True(state.Promulgation.Degraded);
    }

    [Fact]
    public void Quorum_one_seats_exactly_one_node_however_big_the_mesh()
    {
        var mesh = Enumerable.Range(1, 8).Select(i => Peer($"peer-{i}")).ToList();
        var pr = Pr();

        var seats = SeatAssignment.Seats(
            pr.ToRevision(), pr, mesh, quorum: 1, DateTimeOffset.UtcNow);

        // 这就是「一个 node 开始 review 后其他 node 不再 review」的机制本身：
        // 席位表里只有一个人，别人根本没有席位可认。
        Assert.Single(seats);
    }

    [Fact]
    public async Task A_node_without_the_seat_neither_reviews_nor_writes()
    {
        using var h = new Harness();
        var peers = new[] { Peer("peer-1"), Peer("peer-2"), Peer("peer-3") };
        List<Elector> intendedMesh = [h.Mesh.Self, .. peers];

        var template = Pr();
        var prId = 0;
        for (var candidate = 1; candidate < 20000 && prId == 0; candidate++)
        {
            var trial = template with { PrId = candidate };
            var seats = SeatAssignment.Seats(
                trial.ToRevision(), trial, intendedMesh, quorum: 1, DateTimeOffset.UtcNow);
            if (seats.Count == 1 && seats[0] != h.SelfId)
            {
                prId = candidate;
            }
        }

        Assert.NotEqual(0, prId);

        var rev = await h.ReportAsync(template with { PrId = prId });
        h.Mesh.Peers.AddRange(peers);
        h.Options.AutoReview = true;

        await h.TickAsync();

        // 席位归别人，本节点就完全不插手。
        Assert.Equal(0, h.Runner.Calls);
        Assert.Empty(await h.ChainAsync(rev));
    }

    [Fact]
    public async Task Another_node_already_reviewing_keeps_this_one_out()
    {
        using var h = new Harness();
        var peer = Peer("peer-1");
        var rev = await h.ReportAsync(Pr());
        h.Mesh.Peers.Add(peer);
        h.Options.AutoReview = true;

        // 对端上报「我在评这个」。
        h.Mesh.Remote[peer.Id] = new LiveState
        {
            Version = 1,
            Reviewing = [new ActiveReview(rev.Id, 0, DateTimeOffset.UtcNow)],
        };

        await h.TickAsync();

        Assert.Equal(0, h.Runner.Calls);
        Assert.Equal("评审中", h.State.Pipeline.Single().Stage);
        Assert.Equal(peer.Id, h.State.Pipeline.Single().ReviewingBy);
    }

    [Fact]
    public async Task A_dead_node_s_review_is_taken_over()
    {
        using var h = new Harness();
        var dead = Peer("peer-dead") with
        {
            // 心跳早已过期。
            LastHeartbeat = DateTimeOffset.UtcNow - Elector.HeartbeatWindow - TimeSpan.FromMinutes(1),
        };
        var rev = await h.ReportAsync(Pr());
        h.Mesh.Peers.Add(dead);
        h.Options.AutoReview = true;

        h.Mesh.Remote[dead.Id] = new LiveState
        {
            Version = 1,
            Reviewing = [new ActiveReview(rev.Id, 0, DateTimeOffset.UtcNow.AddMinutes(-30))],
        };

        await h.TickAsync();

        // 掉线节点的上报被整体忽略，PR 自动回到「需要人评」——
        // 这一条就是接管机制的全部，不需要写 Recess 也不需要 10 分钟席位超时。
        Assert.Single((await h.StateOfAsync(rev)).Ballots);
    }

    [Fact]
    public async Task A_fix_is_reviewed_by_the_node_that_reviewed_the_previous_revision()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;

        var pr = Pr();
        var first = await h.ReportAsync(pr);
        await h.TickAsync();
        await h.TickAsync();
        Assert.True((await h.StateOfAsync(first)).IsFinished);

        var fixedUp = pr with { SrcCommit = "ffffffff22222222" };
        var second = await h.ReportAsync(fixedUp);
        Assert.NotEqual(first.Id, second.Id);

        var incumbent = await h.ReviewLog.ReadLastReviewerAsync(
            second.Project, second.PrId, CancellationToken.None);

        // 复审要落回同一个节点：它已经读过这份代码、提过这些 finding。
        Assert.Equal(h.SelfId, incumbent);
    }

    [Fact]
    public async Task An_error_ballot_does_not_become_the_incumbent()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Runner.Throw = new InvalidOperationException("claude 挂了");
        var rev = await h.ReportAsync(Pr());

        await h.TickAsync();

        // 上一版是 Error 票说明那个节点当时根本没跑成，没有「读过这份代码」的优势 ——
        // 把它请回来只会重复同一个失败。
        Assert.Null(await h.ReviewLog.ReadLastReviewerAsync(
            rev.Project, rev.PrId, CancellationToken.None));
    }

    [Fact]
    public async Task The_author_never_gets_a_seat_on_their_own_pr()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Mesh.UpdateSelf(self => self with { AzIdentity = "youngsun" });

        var rev = await h.ReportAsync(Pr());
        await h.TickAsync();

        // createdBy.uniqueName 实测形如 LIONMAIL\youngsun，az 身份是裸 youngsun ——
        // AzIdentity.Normalize 要把两种形态认成同一个人，否则会出现自己批准自己的 PR。
        Assert.Equal(0, h.Runner.Calls);

        // 单节点上这意味着永远没人评。界面上必须跟「待评审」区分开，
        // 否则人会一直等一件不会发生的事。
        var view = h.State.Pipeline.Single();
        Assert.True(view.NobodyEligible);
        Assert.Equal("无人可评", view.Stage);
    }

    [Fact]
    public async Task Posting_is_skipped_while_the_toggle_is_off()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        var rev = await h.ReportAsync(Pr());

        await h.TickAsync();
        await h.TickAsync();

        Assert.True((await h.StateOfAsync(rev)).IsFinished);
        Assert.Empty(h.PrSource.Posted);
    }

    [Fact]
    public async Task Posting_records_the_thread_id_when_enabled()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Options.PostToAzureDevOps = true;
        var rev = await h.ReportAsync(Pr());

        await h.TickAsync();
        await h.TickAsync();

        Assert.Single(h.PrSource.Posted);
        Assert.Equal(8821, (await h.StateOfAsync(rev)).Promulgation!.ThreadId);
    }

    [Fact]
    public async Task A_posting_failure_does_not_block_the_verdict()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Options.PostToAzureDevOps = true;
        h.PrSource.PostThrows = new InvalidOperationException("az 炸了");
        var rev = await h.ReportAsync(Pr());

        await h.TickAsync();
        await h.TickAsync();

        // 结论已经算出来了，链上先记下，投递可以人工补。
        var done = await h.StateOfAsync(rev);
        Assert.True(done.IsFinished);
        Assert.Null(done.Promulgation!.ThreadId);
    }

    [Fact]
    public async Task Merged_findings_carry_confidence_onto_the_chain()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        var rev = await h.ReportAsync(Pr());

        await h.TickAsync();
        await h.TickAsync();

        var merged = (await h.StateOfAsync(rev)).Promulgation!;
        var finding = Assert.Single(merged.Findings);

        // quorum=1 时每条 finding 的 Confidence 都是 1.0，合并器等于直通。
        Assert.Equal(1.0, finding.Confidence);
        Assert.Equal(1, finding.Mentions);
    }

    [Fact]
    public async Task The_load_gate_throttles_but_never_drops_work()
    {
        using var h = new Harness(maxConcurrent: 1);
        h.Options.AutoReview = true;

        _ = await h.ReportAsync(Pr(id: 2721));
        _ = await h.ReportAsync(Pr(id: 2722), append: true);

        await h.TickAsync();
        await h.TickAsync();

        // 并发 1 意味着排队，但两个都得跑到 —— 丢活比慢更糟。
        Assert.Equal(2, h.Runner.Calls);
    }

    [Fact]
    public async Task Every_block_written_is_broadcast_and_signed()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        _ = await h.ReportAsync(Pr());

        await h.TickAsync();
        await h.TickAsync();

        Assert.NotEmpty(h.Mesh.Broadcast);
        Assert.All(h.Mesh.Broadcast, b => Assert.True(b.VerifySignature()));

        // 链上只该有完成的评审这两种块。
        Assert.All(
            h.Mesh.Broadcast,
            b => Assert.Contains(b.Kind, new[] { BlockKind.Ballot, BlockKind.Promulgation }));
    }
}

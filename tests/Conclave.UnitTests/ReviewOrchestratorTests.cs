using Conclave.Application;
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
        h.Options.RetryOnSameNode = true;
        h.Runner.Throw = new InvalidOperationException("claude 挂了");
        var rev = await h.ReportAsync(Pr());

        await h.TickAsync();
        Assert.Equal([0], (await h.StateOfAsync(rev)).SpentRounds);

        // 开着开关，重试才会再抽到自己 —— 这次让它成功。多节点上这条路径是换台机器
        // 接手，判定完全相同（见 ActaProjectionTests 里那条覆盖暂定结论的用例）。
        h.Runner.Throw = null;
        await h.TickAsync();
        await h.TickAsync();

        var done = await h.StateOfAsync(rev);
        Assert.Equal(ReviewDecision.Reject, done.Promulgation!.Decision);
        Assert.Single(done.ValidBallots);
        Assert.False(done.Promulgation.Degraded);
    }

    /// <summary>
    /// 公布块自己说得出「哪个 PR、谁评的」。
    /// </summary>
    /// <remarks>
    /// 原先它只有 revision id：账本浏览器里一条公布行，作者和评审者都得回头去翻同一
    /// revision 的出票行，而那两块未必在同一屏里 —— 一次只列最近 60 块，中间还插着别的 PR。
    /// </remarks>
    [Fact]
    public async Task The_verdict_carries_the_pr_snapshot_and_who_reviewed_it()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        var rev = await h.ReportAsync(Pr());

        await h.TickAsync();          // 出票
        await h.TickAsync();          // 公布

        var verdict = (await h.StateOfAsync(rev)).Promulgation!;

        Assert.Equal(Author, verdict.Pr!.Author);
        Assert.Equal(2721, verdict.Pr.PrId);
        Assert.Equal(["alan"], verdict.Reviewers);
    }

    /// <summary>
    /// 一张有效票都没有的降级结论，照样说得出是谁跑的。
    /// </summary>
    /// <remarks>
    /// 这条是那两个字段<b>不在 <see cref="QuorumEngine.Merge"/> 里填</b>的理由：合并器
    /// 收到的是有效票，这种块的有效票是空列表（<c>ActualQuorum</c> 就是 0），它手上
    /// 什么都没有。而这恰恰是最需要说清楚的一种块 —— 烧了额度、产出为零，
    /// 人第一个要问的就是「哪个 PR、哪台机器」。
    /// </remarks>
    [Fact]
    public async Task A_degraded_verdict_with_no_valid_ballot_still_names_the_pr_and_the_node()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Runner.FatalError = "未找到 collect 契约的 JSON 块";
        var rev = await h.ReportAsync(Pr());

        for (var i = 0; i < 4; i++)
        {
            await h.TickAsync();
        }

        var verdict = (await h.StateOfAsync(rev)).Promulgation!;

        Assert.Equal(0, verdict.ActualQuorum);      // 有效票一张都没有
        Assert.Equal(Author, verdict.Pr!.Author);   // 但这两样仍然填得出来
        Assert.Equal(["alan"], verdict.Reviewers);
    }

    /// <summary>
    /// 确定性失败一轮就收尾，不再把重试预算烧完。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 实测那次：PR 2912 连着两轮都是「没输出 collect 契约的 JSON 块」，两轮 claude 都把
    /// 评审跑完了（原文里结论、finding、行号都在），只是最后那个围栏没出来。
    /// 两轮 121 万 token、$2.79，产出为零，而按老规则它还会再跑第三轮。
    /// </para>
    /// <para>
    /// 这条跟 <see cref="Retries_stop_at_the_configured_ceiling"/> 的区别就是预算还剩着 ——
    /// 剩着也不再花，因为同一份输入跑出来的是同一个结果。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_deterministic_failure_stops_after_one_round()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Options.MaxReviewAttempts = 3;
        h.Runner.FatalError = "未找到 collect 契约的 JSON 块";
        var rev = await h.ReportAsync(Pr());

        for (var i = 0; i < 8; i++)
        {
            await h.TickAsync();
        }

        var state = await h.StateOfAsync(rev);

        // 预算是 3，只烧了 1 —— 剩下两轮省下来了。
        Assert.Single(state.SpentRounds);
        Assert.Equal(1, h.Runner.Calls);

        // 但 PR 不能就这么挂着：收一个降级的 Error 结论，人看到原因去修。
        Assert.Equal(ReviewDecision.Error, state.Promulgation!.Decision);
        Assert.True(state.Promulgation.Degraded);
    }

    /// <summary>
    /// 不重试这件事必须说出来，而且要带上原因。
    /// </summary>
    /// <remarks>
    /// 「执行失败」后面没有下一轮了。不写清楚的话，人看到的就是一个 PR 无声无息地停住 ——
    /// 而这次的原因（评审跑完了、只是取不出结论）恰恰是指向该修什么的那句话。
    /// </remarks>
    [Fact]
    public async Task A_deterministic_failure_says_it_will_not_retry_and_why()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Runner.FatalError = "未找到 collect 契约的 JSON 块（回复末尾要有且只有一个 ```json 围栏）";
        _ = await h.ReportAsync(Pr());

        await h.TickAsync();

        var notice = Assert.Single(h.State.Notices, n => n.Title.Contains("不会重试", StringComparison.Ordinal));

        Assert.Equal(NoticeKind.Bad, notice.Kind);
        Assert.Contains("collect 契约", notice.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// 偶发失败也不在同一台机器上重试 —— 一次就够了，剩下的交给别的节点。
    /// </summary>
    /// <remarks>
    /// 预算是 3 却只烧了 1：这台已经试过，而 mesh 里没有别人。多烧的那两轮除了账单
    /// 什么都不会带来 —— claude 没额度、用户退出登录、az 连不上、token 到期，
    /// 这些在同一台机器上一分钟内不会自己好。
    /// </remarks>
    [Fact]
    public async Task An_ordinary_failure_does_not_retry_on_the_same_node()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Options.MaxReviewAttempts = 3;
        h.Runner.Throw = new InvalidOperationException("claude 挂了");
        var rev = await h.ReportAsync(Pr());

        for (var i = 0; i < 8; i++)
        {
            await h.TickAsync();
        }

        var state = await h.StateOfAsync(rev);

        Assert.Single(state.SpentRounds);
        Assert.Equal(1, h.Runner.Calls);

        // 收了个暂定结论，但这一版<b>没有</b>评完：换台机器上线还能接手。
        Assert.Equal(ReviewDecision.Error, state.Promulgation!.Decision);
        Assert.True(state.IsProvisional);
        Assert.False(state.IsFinished);
    }

    /// <summary>
    /// 打开开关就回到老行为，而上限仍然拦得住无限重试。
    /// </summary>
    [Fact]
    public async Task Retries_stop_at_the_configured_ceiling()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Options.RetryOnSameNode = true;
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

    /// <summary>
    /// 停下来的时候要发通知，而且要说清楚试了几次、错在哪。
    /// </summary>
    /// <remarks>
    /// 通知里那个次数原先取的是 <c>ActualQuorum</c> —— 而它只数有效票，全失败时恒为 0，
    /// 于是面板上永远是「0 轮都失败了」。
    /// </remarks>
    [Fact]
    public async Task Giving_up_says_how_many_times_and_why()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Runner.Throw = new InvalidOperationException("claude 没额度了");
        var rev = await h.ReportAsync(Pr());

        for (var i = 0; i < 4; i++)
        {
            await h.TickAsync();
        }

        var notice = Assert.Single(
            h.State.Notices,
            n => n.Title.Contains("已停止分配席位", StringComparison.Ordinal));

        Assert.Equal(NoticeKind.Bad, notice.Kind);
        Assert.Contains(rev.Id, notice.Title, StringComparison.Ordinal);
        Assert.Contains("失败 1 次", notice.Title, StringComparison.Ordinal);
        Assert.Contains("claude 没额度了", notice.Detail, StringComparison.Ordinal);
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

    /// <summary>
    /// 执行失败不往真 PR 上发。
    /// </summary>
    /// <remarks>
    /// 一个 degraded 的 Error 结论对作者没有任何可行动信息 —— 「评审没跑出结论」是这边的
    /// 运维问题，不是他代码的问题，而且投票本来就是 none。链上和本地通知照常留痕。
    /// </remarks>
    [Fact]
    public async Task A_failed_review_is_not_posted_to_the_pull_request()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Options.PostToAzureDevOps = true;
        h.Runner.FatalError = "未找到 collect 契约的 JSON 块";
        var rev = await h.ReportAsync(Pr());

        for (var i = 0; i < 4; i++)
        {
            await h.TickAsync();
        }

        var done = await h.StateOfAsync(rev);

        // 结论上链了 —— 不投递不等于不留痕。
        Assert.Equal(ReviewDecision.Error, done.Promulgation!.Decision);
        Assert.Empty(h.PrSource.Posted);

        // 本地必须看得见，否则这个 PR 就真的无声无息了。
        Assert.Contains(
            h.State.Notices,
            n => n.Title.Contains("已停止分配席位", StringComparison.Ordinal));
    }

    /// <summary>正常结论照旧投递 —— 上一条不能把投递整个关掉。</summary>
    [Fact]
    public async Task A_real_verdict_is_still_posted()
    {
        using var h = new Harness();
        h.Options.AutoReview = true;
        h.Options.PostToAzureDevOps = true;
        var rev = await h.ReportAsync(Pr());

        await h.TickAsync();
        await h.TickAsync();

        Assert.Equal(ReviewDecision.Reject, (await h.StateOfAsync(rev)).Promulgation!.Decision);
        Assert.Single(h.PrSource.Posted);
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

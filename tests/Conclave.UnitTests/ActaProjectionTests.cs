using Conclave.Domain;

namespace Conclave.UnitTests;

/// <summary>
/// 把链折叠成 <see cref="ChainState"/>。
/// </summary>
/// <remarks>
/// 链现在只记<b>已完成</b>的评审（<c>Ballot</c> + <c>Promulgation</c>）。原先这套测试还覆盖
/// <c>Summons</c>/<c>Seating</c>/<c>Recess</c> 的折叠，那三种块已停写 —— 「队列里有什么、
/// 谁正在评」改由实时状态承载，见 <see cref="QueueProjectionTests"/>。
/// </remarks>
public class ActaProjectionTests
{
    private static readonly Revision V1 = new("liontrip-cms", 2721, "aaaaaaaa11111111");
    private static readonly Revision V2 = new("liontrip-cms", 2721, "bbbbbbbb22222222");

    private static Block Block<T>(BlockKind kind, T payload, long index)
        => new()
        {
            ChainId = Domain.Acta.ChainId,
            Index = index,
            PrevHash = Domain.Block.GenesisPrevHash,
            At = TestElectors.Now.AddMinutes(index),
            Kind = kind,
            PayloadJson = ActaJson.Serialize(payload),
            ElectorId = "e1",
            PublicKey = "pk",
            Signature = "sig",
        };

    private static BallotPayload Ballot(
        Revision rev,
        int round,
        ReviewDecision decision,
        string? error = null,
        bool retryable = true) => new(
            rev.Id, round, decision,
            decision == ReviewDecision.Error ? [] : [new Finding("a.cs", 1, Severity.Major, "t", "d")],
            "claude-opus-5", 1234, ReviewUsage.None, "alan", error)
        {
            Pr = TestElectors.Pr(),
            Retryable = retryable,
        };

    [Fact]
    public void Projection_folds_ballots_and_the_verdict()
    {
        var blocks = new[]
        {
            Block(BlockKind.Ballot, Ballot(V1, 0, ReviewDecision.Reject), 0),
            Block(BlockKind.Ballot, Ballot(V1, 1, ReviewDecision.Reject), 1),
            Block(
                BlockKind.Promulgation,
                new PromulgationPayload(V1.Id, ReviewDecision.Reject, [], false, 2, 2),
                2),
        };

        var state = ActaProjection.Project(blocks, V1.Id);

        Assert.Equal(2, state.Ballots.Count);
        Assert.Equal(2, state.ValidBallots.Count);
        Assert.True(state.IsFinished);
        Assert.Equal(ReviewDecision.Reject, state.Promulgation!.Decision);
    }

    [Fact]
    public void Retired_block_kinds_are_ignored()
    {
        // Summons / Seating / Recess 已停写。老链上还有它们，投影必须一律跳过 ——
        // 否则会出现两个真相来源：界面上一个 PR 同时「在队列里」和「已召集」。
        var blocks = new[]
        {
            Block(BlockKind.Summons, new SummonsPayload(V1, TestElectors.Pr(), 3, "r"), 0),
            Block(BlockKind.Seating, new SeatingPayload(V1.Id, 0, "e1"), 1),
            Block(BlockKind.Recess, new RecessPayload(V1.Id, 0, "seating timeout"), 2),
        };

        var state = ActaProjection.Project(blocks, V1.Id);

        Assert.Empty(state.Ballots);
        Assert.Null(state.Promulgation);
        Assert.False(state.IsFinished);
    }

    [Fact]
    public void An_older_revision_s_verdict_does_not_mark_the_new_one_finished()
    {
        // 全 mesh 一条链，上面叠着同一个 PR 的多个版本。漏了按 revisionId 过滤，
        // 上一版的结论会让作者 push 的修复被当成「已评审」，再也不会被评。
        var blocks = new[]
        {
            Block(BlockKind.Ballot, Ballot(V1, 0, ReviewDecision.Reject), 0),
            Block(
                BlockKind.Promulgation,
                new PromulgationPayload(V1.Id, ReviewDecision.Reject, [], false, 1, 1),
                1),
            Block(BlockKind.Ballot, Ballot(V2, 0, ReviewDecision.Approve), 2),
        };

        var older = ActaProjection.Project(blocks, V1.Id);
        var newer = ActaProjection.Project(blocks, V2.Id);

        Assert.True(older.IsFinished);
        Assert.False(newer.IsFinished);
        Assert.Single(newer.Ballots);
    }

    [Fact]
    public void An_error_ballot_spends_the_round_but_is_not_a_verdict()
    {
        var blocks = new[]
        {
            Block(BlockKind.Ballot, Ballot(V1, 0, ReviewDecision.Error, "claude 挂了"), 0),
        };

        var state = ActaProjection.Project(blocks, V1.Id);

        // 以前 Error 票照样计入 quorum 分母，于是 quorum=1 时「子进程挂了」会直接被公布成
        // Error 结论 —— 明明只是这台机器上的一次偶发失败，换个节点重跑就好。
        Assert.Empty(state.ValidBallots);
        Assert.Equal([0], state.SpentRounds);
        Assert.True(state.IsRoundSettled(0));
        Assert.False(state.CanPromulgate(quorum: 1, maxAttempts: 3));
    }

    [Fact]
    public void Enough_valid_ballots_allow_promulgation()
    {
        var blocks = new[]
        {
            Block(BlockKind.Ballot, Ballot(V1, 0, ReviewDecision.Reject), 0),
        };

        var state = ActaProjection.Project(blocks, V1.Id);

        Assert.True(state.CanPromulgate(quorum: 1, maxAttempts: 3));
        Assert.False(state.CanPromulgate(quorum: 2, maxAttempts: 3));
    }

    [Fact]
    public void Exhausting_the_retry_budget_finally_allows_promulgation()
    {
        var blocks = new[]
        {
            Block(BlockKind.Ballot, Ballot(V1, 0, ReviewDecision.Error), 0),
            Block(BlockKind.Ballot, Ballot(V1, 1, ReviewDecision.Error), 1),
            Block(BlockKind.Ballot, Ballot(V1, 2, ReviewDecision.Error), 2),
        };

        var state = ActaProjection.Project(blocks, V1.Id);

        // 「claude 每次都起不来」不能让同一个 PR 无限重试下去：到上限就拿手上的票收尾，
        // 哪怕一张有效票都没有（那就是个 degraded 的 Error 结论）。
        Assert.Equal(3, state.SpentRounds.Count);
        Assert.True(state.CanPromulgate(quorum: 1, maxAttempts: 3));
        Assert.False(state.CanPromulgate(quorum: 1, maxAttempts: 5));
    }

    /// <summary>
    /// 没机器可换了就收尾，预算剩着也不再排。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这条曾经是靠 <see cref="ChainState.HasFatalError"/> 提前收尾的 —— 一张确定性失败票
    /// 一票否决整个预算。现在换了个判据：出错的节点本来就不会被再抽到，所以「还有没有
    /// 没试过的机器」才是该不该继续排的那个问题，跟错得确不确定无关。
    /// </para>
    /// <para>
    /// 单节点 mesh 上这就是「一次失败立刻收尾」：唯一那台已经试过了。
    /// </para>
    /// </remarks>
    [Fact]
    public void A_failure_with_nobody_left_to_try_ends_the_review()
    {
        var blocks = new[]
        {
            Block(
                BlockKind.Ballot,
                Ballot(V1, 0, ReviewDecision.Error, "未找到 collect 契约的 JSON 块", retryable: false),
                0),
        };

        var state = ActaProjection.Project(blocks, V1.Id);

        // 预算还剩两轮，但已经没有没试过的机器了。
        Assert.Single(state.SpentRounds);
        Assert.True(state.CanPromulgate(quorum: 1, maxAttempts: 3, nobodyLeftToTry: true));

        // 收的是个 degraded 的 Error 结论：一张有效票都没有，这一点不能被掩盖。
        Assert.Empty(state.ValidBallots);
    }

    [Fact]
    public void A_failure_waits_while_there_is_still_a_machine_to_try()
    {
        // 工作区拉不下来、子进程挂了、服务端 5xx、claude 没额度、token 到期 ——
        // 换一台机器很可能就过了，所以还有没试过的节点时不收尾，回队列等它接手。
        var blocks = new[]
        {
            Block(BlockKind.Ballot, Ballot(V1, 0, ReviewDecision.Error, "claude 挂了"), 0),
        };

        var state = ActaProjection.Project(blocks, V1.Id);

        Assert.False(state.CanPromulgate(quorum: 1, maxAttempts: 3));
    }

    /// <summary>
    /// Error 结论不是终局 —— 那一版一个字都还没被评过。
    /// </summary>
    [Fact]
    public void A_failure_verdict_is_provisional_not_finished()
    {
        var blocks = new[]
        {
            Block(BlockKind.Ballot, Ballot(V1, 0, ReviewDecision.Error, "claude 没额度"), 0),
            Block(
                BlockKind.Promulgation,
                new PromulgationPayload(V1.Id, ReviewDecision.Error, [], true, 0, 1),
                1),
        };

        var state = ActaProjection.Project(blocks, V1.Id);

        Assert.True(state.IsProvisional);
        Assert.False(state.IsFinished);
    }

    /// <summary>
    /// 有效票一出就覆盖掉暂定结论 —— 链上是第二个 Promulgation 块，投影取最后一个。
    /// </summary>
    [Fact]
    public void A_real_ballot_supersedes_a_provisional_failure_verdict()
    {
        var failed = new[]
        {
            Block(BlockKind.Ballot, Ballot(V1, 0, ReviewDecision.Error, "az 连不上"), 0),
            Block(
                BlockKind.Promulgation,
                new PromulgationPayload(V1.Id, ReviewDecision.Error, [], true, 0, 1),
                1),
        };

        // 还没有有效票时不许重写 —— 否则「预算到顶」那条每 15 秒往链上拍一个一样的块。
        Assert.False(ActaProjection
            .Project(failed, V1.Id)
            .CanPromulgate(quorum: 1, maxAttempts: 3, nobodyLeftToTry: true));

        // 换了台机器评出来了。
        var rescued = failed
            .Append(Block(BlockKind.Ballot, Ballot(V1, 1, ReviewDecision.Reject), 2))
            .ToArray();

        var state = ActaProjection.Project(rescued, V1.Id);

        Assert.True(state.CanPromulgate(quorum: 1, maxAttempts: 3));

        var settled = ActaProjection.Project(
            [.. rescued, Block(
                BlockKind.Promulgation,
                new PromulgationPayload(V1.Id, ReviewDecision.Reject, [], false, 1, 1),
                3)],
            V1.Id);

        Assert.True(settled.IsFinished);
        Assert.False(settled.IsProvisional);
        Assert.False(settled.CanPromulgate(quorum: 1, maxAttempts: 3));
    }

    /// <summary>
    /// 老 Ballot 反序列化成「可重试」。
    /// </summary>
    /// <remarks>
    /// 链是 append-only 的，这个字段之前的票上根本没有。默认必须是 true ——
    /// 反过来的话，升级那一刻链上所有历史 Error 票会突然变成「确定性失败」。
    /// </remarks>
    [Fact]
    public void A_ballot_written_before_this_field_existed_stays_retryable()
    {
        var legacy = ActaJson.Serialize(new
        {
            RevisionId = V1.Id,
            Round = 0,
            Decision = "Error",
            Findings = Array.Empty<Finding>(),
            Model = "claude-opus-5",
            DurationMs = 1234,
        });

        var ballot = ActaJson.Deserialize<BallotPayload>(legacy);

        Assert.NotNull(ballot);
        Assert.True(ballot.Retryable);
        Assert.False(ballot.IsFatal);
    }

    /// <summary>这个标记要过得了链 —— 公布跑在哪个节点上不一定。</summary>
    [Fact]
    public void The_non_retryable_mark_survives_the_round_trip_through_the_chain()
    {
        var json = ActaJson.Serialize(
            Ballot(V1, 0, ReviewDecision.Error, "没有围栏", retryable: false));

        var back = ActaJson.Deserialize<BallotPayload>(json);

        Assert.NotNull(back);
        Assert.True(back.IsFatal);
    }

    [Fact]
    public void A_verdict_already_on_the_chain_stops_further_promulgation()
    {
        var blocks = new[]
        {
            Block(BlockKind.Ballot, Ballot(V1, 0, ReviewDecision.Reject), 0),
            Block(
                BlockKind.Promulgation,
                new PromulgationPayload(V1.Id, ReviewDecision.Reject, [], false, 1, 1),
                1),
        };

        var state = ActaProjection.Project(blocks, V1.Id);

        // 幂等：两个节点同时判定「票齐了」时，第二个不该再公布一遍。
        Assert.False(state.CanPromulgate(quorum: 1, maxAttempts: 3));
    }

    [Fact]
    public void The_ballot_carries_its_own_pr_snapshot()
    {
        // Summons 停写之后，PR 快照必须跟着票走 —— 否则账单里连「这一票评的是哪个仓库的
        // 哪个 PR」都答不上来。
        var blocks = new[] { Block(BlockKind.Ballot, Ballot(V1, 0, ReviewDecision.Reject), 0) };

        var state = ActaProjection.Project(blocks, V1.Id);

        Assert.NotNull(state.Ballots[0].Pr);
        Assert.Equal("cms-apostrophe", state.Ballots[0].Pr!.Repo);
    }

    /// <summary>
    /// 「谁评的」要数上出了 Error 票的那几轮。
    /// </summary>
    /// <remarks>
    /// 跟 <see cref="ChainState.ValidBallots"/> 刻意不是同一个集合：那个答的是
    /// 「谁的意见进了结论」，这个答的是「谁跑过这个 PR」。三轮全挂的时候前者是空的，
    /// 而那三台确实都烧了一轮额度 —— 公布块上要说得出是谁。
    /// </remarks>
    [Fact]
    public void Reviewers_count_the_rounds_that_failed_too()
    {
        var blocks = new[]
        {
            Block(BlockKind.Ballot, Ballot(V1, 0, ReviewDecision.Error) with { ReviewerAz = "bob" }, 0),
            Block(BlockKind.Ballot, Ballot(V1, 1, ReviewDecision.Reject) with { ReviewerAz = "alan" }, 1),
        };

        var state = ActaProjection.Project(blocks, V1.Id);

        Assert.Equal(["bob", "alan"], state.Reviewers);
        Assert.Single(state.ValidBallots);
    }

    /// <summary>没有 az 身份的老票宁可漏掉，也不往名单里塞一个空串。</summary>
    [Fact]
    public void A_ballot_without_an_az_identity_is_left_out_of_the_reviewers()
    {
        var blocks = new[]
        {
            Block(BlockKind.Ballot, Ballot(V1, 0, ReviewDecision.Reject) with { ReviewerAz = null }, 0),
            Block(BlockKind.Ballot, Ballot(V1, 1, ReviewDecision.Reject) with { ReviewerAz = " " }, 1),
            Block(BlockKind.Ballot, Ballot(V1, 2, ReviewDecision.Reject) with { ReviewerAz = "alan" }, 2),
        };

        var state = ActaProjection.Project(blocks, V1.Id);

        Assert.Equal(["alan"], state.Reviewers);
    }

    /// <summary>
    /// 老公布块反序列化留 null，而不是空列表。
    /// </summary>
    /// <remarks>
    /// 链是 append-only 的，这两个字段之前的公布块上根本没有。留 null 表达的是
    /// 「那时候没有这个东西」，空列表会被读成「确认过，没有人评」—— 界面上一个该退回
    /// 出票行去找、一个该显示「没人评」，不能混。
    /// </remarks>
    [Fact]
    public void A_verdict_written_before_these_fields_existed_carries_neither()
    {
        var legacy = ActaJson.Serialize(new
        {
            RevisionId = V1.Id,
            Decision = "Reject",
            Findings = Array.Empty<MergedFinding>(),
            Degraded = false,
            ActualQuorum = 1,
            ExpectedQuorum = 1,
        });

        var verdict = ActaJson.Deserialize<PromulgationPayload>(legacy);

        Assert.NotNull(verdict);
        Assert.Null(verdict.Pr);
        Assert.Null(verdict.Reviewers);
    }

    [Fact]
    public void Empty_chain_projects_to_empty_state()
    {
        var state = ActaProjection.Project([], V1.Id);

        Assert.Empty(state.Ballots);
        Assert.Null(state.Promulgation);
        Assert.False(state.CanPromulgate(quorum: 1, maxAttempts: 3));
    }
}

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
        Revision rev, int round, ReviewDecision decision, string? error = null) => new(
            rev.Id, round, decision,
            decision == ReviewDecision.Error ? [] : [new Finding("a.cs", 1, Severity.Major, "t", "d")],
            "claude-opus-5", 1234, ReviewUsage.None, "alan", error)
        {
            Pr = TestElectors.Pr(),
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

    [Fact]
    public void Empty_chain_projects_to_empty_state()
    {
        var state = ActaProjection.Project([], V1.Id);

        Assert.Empty(state.Ballots);
        Assert.Null(state.Promulgation);
        Assert.False(state.CanPromulgate(quorum: 1, maxAttempts: 3));
    }
}

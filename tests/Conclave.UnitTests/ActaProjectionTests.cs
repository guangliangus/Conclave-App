using Conclave.Domain;

namespace Conclave.UnitTests;

public class ActaProjectionTests
{
    private static readonly Revision V1 = new("liontrip-cms", 2721, "aaaaaaaa11111111");
    private static readonly Revision V2 = new("liontrip-cms", 2721, "bbbbbbbb22222222");

    private static Block Block<T>(BlockKind kind, T payload, long index)
        => new()
        {
            ChainId = V1.ChainId,
            Index = index,
            PrevHash = Domain.Block.GenesisPrevHash,
            At = TestElectors.Now.AddMinutes(index),
            Kind = kind,
            PayloadJson = ActaJson.Serialize(payload),
            ElectorId = "e1",
            PublicKey = "pk",
            Signature = "sig",
        };

    [Fact]
    public void Projection_folds_a_full_conclave_into_state()
    {
        var blocks = new[]
        {
            Block(BlockKind.Summons, new SummonsPayload(V1, TestElectors.Pr(), 2, "abc123"), 0),
            Block(BlockKind.Seating, new SeatingPayload(V1.Id, 0, "e1"), 1),
            Block(BlockKind.Seating, new SeatingPayload(V1.Id, 1, "e2"), 2),
            Block(BlockKind.Ballot, new BallotPayload(V1.Id, 0, ReviewDecision.Reject, [], "m", 1), 3),
            Block(BlockKind.Ballot, new BallotPayload(V1.Id, 1, ReviewDecision.Reject, [], "m", 1), 4),
        };

        var state = ActaProjection.Project(blocks, V1.Id);

        Assert.NotNull(state.Summons);
        Assert.Equal(2, state.Quorum);
        Assert.Equal(2, state.Seatings.Count);
        Assert.Equal(2, state.Ballots.Count);
        Assert.True(state.CanPromulgate);
        Assert.False(state.IsFinished);
    }

    [Fact]
    public void An_older_revision_s_promulgation_does_not_mark_the_new_one_finished()
    {
        var blocks = new[]
        {
            Block(BlockKind.Summons, new SummonsPayload(V1, TestElectors.Pr(), 1, "r"), 0),
            Block(BlockKind.Ballot, new BallotPayload(V1.Id, 0, ReviewDecision.Approve, [], "m", 1), 1),
            Block(BlockKind.Promulgation, new PromulgationPayload(V1.Id, ReviewDecision.Approve, [], false, 1, 1), 2),
            Block(BlockKind.Summons, new SummonsPayload(V2, TestElectors.Pr(), 1, "r"), 3),
        };

        // 这是幂等键设计的关键回归点：作者 push 之后新版本必须重新评审。
        Assert.True(ActaProjection.Project(blocks, V1.Id).IsFinished);
        Assert.False(ActaProjection.Project(blocks, V2.Id).IsFinished);
    }

    [Fact]
    public void Duplicate_seating_for_the_same_round_keeps_the_first()
    {
        var blocks = new[]
        {
            Block(BlockKind.Summons, new SummonsPayload(V1, TestElectors.Pr(), 1, "r"), 0),
            Block(BlockKind.Seating, new SeatingPayload(V1.Id, 0, "first"), 1),
            Block(BlockKind.Seating, new SeatingPayload(V1.Id, 0, "second"), 2),
        };

        var state = ActaProjection.Project(blocks, V1.Id);

        Assert.Single(state.Seatings);
        Assert.Equal("first", state.Seatings[0].ElectorId);
        Assert.Equal(TestElectors.Now.AddMinutes(1), state.SeatedAt[0]);
    }

    [Fact]
    public void Recess_counts_toward_the_promulgation_threshold()
    {
        var blocks = new[]
        {
            Block(BlockKind.Summons, new SummonsPayload(V1, TestElectors.Pr(), 2, "r"), 0),
            Block(BlockKind.Ballot, new BallotPayload(V1.Id, 0, ReviewDecision.Approve, [], "m", 1), 1),
            Block(BlockKind.Recess, new RecessPayload(V1.Id, 1, "seating timeout"), 2),
        };

        var state = ActaProjection.Project(blocks, V1.Id);

        // 一票 + 一次弃权 = 席位用尽，可以公布（会标 degraded），否则这个 PR 会永远挂着。
        Assert.True(state.CanPromulgate);
        Assert.Single(state.Recessed);
    }

    [Fact]
    public void RevisionsOf_returns_revisions_in_chain_order()
    {
        var blocks = new[]
        {
            Block(BlockKind.Summons, new SummonsPayload(V1, TestElectors.Pr(), 1, "r"), 0),
            Block(BlockKind.Summons, new SummonsPayload(V2, TestElectors.Pr(), 1, "r"), 1),
        };

        Assert.Equal([V1, V2], ActaProjection.RevisionsOf(blocks));
    }

    [Fact]
    public void Empty_chain_projects_to_empty_state()
    {
        var state = ActaProjection.Project([], V1.Id);

        Assert.Null(state.Summons);
        Assert.False(state.CanPromulgate);
    }
}

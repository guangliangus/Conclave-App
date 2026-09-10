using Conclave.Domain;

namespace Conclave.UnitTests;

/// <summary>
/// 主动认领（P3）与指派需同意（P4）。
/// </summary>
/// <remarks>
/// 两条都是<b>实时状态</b>上的操作，不上链 —— 链只记完成的评审。所以断言看的是
/// <c>mesh.State</c> 与发出去的消息，而不是区块。
/// </remarks>
public class ClaimAndAssignmentTests
{
    private const string Author = "LIONMAIL\\youngsun";

    private static PrMeta Pr(int id = 2721)
        => TestElectors.Pr(id: id, author: Author) with { SrcCommit = "aaaaaaaa11111111" };

    private static Elector Peer(string id) => new()
    {
        Id = id,
        PublicKey = "pk-" + id,
        AzIdentity = "peer-" + id,
        Projects = ["liontrip-cms"],
        MaxConcurrent = 2,
        LastHeartbeat = DateTimeOffset.UtcNow,
    };

    // ── P3 认领 ────────────────────────────────────────────────────

    [Fact]
    public async Task Claiming_lets_this_node_review_without_auto_review()
    {
        using var h = new Harness();
        var rev = await h.ReportAsync(Pr());

        h.Orchestrator.Claim(rev.Id);
        await h.TickAsync();

        // 认领就是「明确要评」的信号，不必再打开 AutoReview。
        Assert.Single((await h.StateOfAsync(rev)).Ballots);
    }

    [Fact]
    public async Task A_claim_beats_the_hrw_draw_even_on_a_pr_hrw_gave_to_someone_else()
    {
        using var h = new Harness();
        var peers = new[] { Peer("peer-1"), Peer("peer-2"), Peer("peer-3") };
        List<Elector> intendedMesh = [h.Mesh.Self, .. peers];

        // 找一个 HRW 抽给别人的 PR。
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

        var rev = await h.ReportAsync(template with { PrId = prId });
        h.Mesh.Peers.AddRange(peers);

        h.Orchestrator.Claim(rev.Id);
        await h.TickAsync();

        // 人明确要评的，规则不该抢走 —— 这正是「主动认领非自己的 PR」那条需求。
        Assert.Single((await h.StateOfAsync(rev)).Ballots);
    }

    [Fact]
    public async Task Releasing_a_claim_hands_the_pr_back()
    {
        using var h = new Harness();
        var rev = await h.ReportAsync(Pr());

        h.Orchestrator.Claim(rev.Id);
        Assert.Single(h.Mesh.State.Claims);

        h.Orchestrator.Release(rev.Id);
        await h.TickAsync();

        Assert.Empty(h.Mesh.State.Claims);
        Assert.Equal(0, h.Runner.Calls);   // 没认领、也没开 AutoReview
    }

    [Fact]
    public async Task Losing_a_claim_race_drops_this_node_s_claim()
    {
        using var h = new Harness();
        var peer = Peer("aaaa-earlier");
        var rev = await h.ReportAsync(Pr());
        h.Mesh.Peers.Add(peer);

        h.Orchestrator.Claim(rev.Id);

        // 对端更早认领 → 按 ReviewClaim.Winner 它胜出。
        h.Mesh.Remote[peer.Id] = new LiveState
        {
            Version = 1,
            Claims = [new ReviewClaim(rev.Id, peer.Id, DateTimeOffset.UtcNow.AddMinutes(-5))],
        };

        await h.TickAsync();

        // 输家必须自己撤销：不撤的话它会一直挂在实时状态里随心跳广播出去，
        // 界面上看是「我认领了却永远不动」。
        Assert.Empty(h.Mesh.State.Claims);
        Assert.Equal(0, h.Runner.Calls);
    }

    [Fact]
    public async Task A_claim_on_a_closed_pr_is_dropped()
    {
        using var h = new Harness();
        var pr = Pr();
        var rev = await h.ReportAsync(pr);
        h.Orchestrator.Claim(rev.Id);

        // PR 在 Azure DevOps 上被 merge → 下一轮上报里没有它了。
        h.PrSource.Active[pr.Project].Clear();
        await h.Discovery.PollOnceAsync(CancellationToken.None);
        await h.TickAsync();

        Assert.Empty(h.Mesh.State.Claims);
    }

    // ── P4 指派 ────────────────────────────────────────────────────

    [Fact]
    public async Task Assigning_sends_a_request_to_the_named_node()
    {
        using var h = new Harness();
        var peer = Peer("peer-1");
        var rev = await h.ReportAsync(Pr());
        h.Mesh.Peers.Add(peer);

        var sent = await h.Orchestrator.AssignAsync(
            rev.Id, peer.Id, "这个我自己写的，麻烦你看一下", CancellationToken.None);

        Assert.True(sent);
        var request = Assert.Single(h.Mesh.SentAssignments);
        Assert.Equal(rev.Id, request.RevisionId);
        Assert.Equal(h.SelfId, request.From);
        Assert.Equal(peer.Id, request.To);
        Assert.Contains("麻烦你看一下", request.Note);
    }

    [Fact]
    public async Task Assigning_to_an_unknown_node_fails_without_throwing()
    {
        using var h = new Harness();
        var rev = await h.ReportAsync(Pr());

        // 界面上不该给出这个选项，但走到这里要如实说「没送到」，而不是崩一个对话框。
        Assert.False(await h.Orchestrator.AssignAsync(
            rev.Id, "not-in-mesh", null, CancellationToken.None));
        Assert.Empty(h.Mesh.SentAssignments);
    }

    [Fact]
    public async Task Accepting_an_assignment_turns_it_into_a_claim_and_starts_the_review()
    {
        using var h = new Harness();
        var requester = Peer("peer-1");

        // 先上报再加 peer：轮询责任按 HRW 分片，播种时 mesh 里有别人的话
        // 这个 project 可能被分给对端，本节点就什么都不会上报。
        var rev = await h.ReportAsync(Pr());
        h.Mesh.Peers.Add(requester);

        // 模拟收到请求（真实路径是 POST /assignments）。
        var request = new AssignmentRequest(
            "req-1", rev.Id, requester.Id, h.SelfId, DateTimeOffset.UtcNow, "帮我看下");
        h.Mesh.UpdateState(s => s with { Pending = [request] });

        await h.Orchestrator.RespondToAssignmentAsync("req-1", accepted: true, null, CancellationToken.None);

        // 同意 → 转成认领，pending 清掉，并回一条答复。
        Assert.Empty(h.Mesh.State.Pending);
        Assert.Equal(rev.Id, h.Mesh.State.Claims.Single().RevisionId);
        Assert.True(Assert.Single(h.Mesh.SentReplies).Accepted);

        await h.TickAsync();
        Assert.Single((await h.StateOfAsync(rev)).Ballots);
    }

    [Fact]
    public async Task Declining_an_assignment_clears_it_without_claiming()
    {
        using var h = new Harness();
        var requester = Peer("peer-1");
        var rev = await h.ReportAsync(Pr());
        h.Mesh.Peers.Add(requester);

        h.Mesh.UpdateState(s => s with
        {
            Pending = [new AssignmentRequest(
                "req-2", rev.Id, requester.Id, h.SelfId, DateTimeOffset.UtcNow, null)],
        });

        await h.Orchestrator.RespondToAssignmentAsync(
            "req-2", accepted: false, "额度快用完了", CancellationToken.None);

        Assert.Empty(h.Mesh.State.Pending);
        Assert.Empty(h.Mesh.State.Claims);

        var reply = Assert.Single(h.Mesh.SentReplies);
        Assert.False(reply.Accepted);
        Assert.Equal("额度快用完了", reply.Reason);
    }

    [Fact]
    public async Task Responding_to_an_unknown_request_is_a_no_op()
    {
        using var h = new Harness();
        _ = await h.ReportAsync(Pr());

        await h.Orchestrator.RespondToAssignmentAsync(
            "nope", accepted: true, null, CancellationToken.None);

        Assert.Empty(h.Mesh.State.Claims);
        Assert.Empty(h.Mesh.SentReplies);
    }

    [Fact]
    public async Task An_assignment_gets_the_author_s_own_pr_reviewed()
    {
        using var h = new Harness();
        // 本节点就是作者 —— 硬规则把它排除。
        h.Mesh.UpdateSelf(self => self with { AzIdentity = "youngsun" });
        var rev = await h.ReportAsync(Pr());
        h.Options.AutoReview = true;

        // 此刻 mesh 里只有自己，所以没有任何合格节点。那不是「在等」，
        // 是永远不会有人评 —— 界面上必须跟「待评审」区分开。
        await h.TickAsync();
        Assert.Equal(0, h.Runner.Calls);
        Assert.True(h.State.Pipeline.Single().NobodyEligible);

        // 来了个别的节点，指派给它正是这个 PR 的出路。
        var peer = Peer("peer-1");
        h.Mesh.Peers.Add(peer);

        Assert.True(await h.Orchestrator.AssignAsync(rev.Id, peer.Id, null, CancellationToken.None));
        Assert.Equal(peer.Id, Assert.Single(h.Mesh.SentAssignments).To);

        // 有了合格节点之后就不再是「无人可评」了。
        await h.TickAsync();
        Assert.False(h.State.Pipeline.Single().NobodyEligible);
    }

    // ── 签名封装 ───────────────────────────────────────────────────

    [Fact]
    public void A_signed_assignment_round_trips_and_detects_tampering()
    {
        using var identity = ElectorIdentity.Create();
        var request = new AssignmentRequest(
            "req-3", "2721@aaaaaaaa", identity.Id, "peer-1", TestElectors.Now, "看一下");

        var signed = SignedAssignment.Sign(identity, request);

        Assert.True(signed.VerifySignature());
        Assert.Equal(request, signed.Body<AssignmentRequest>());

        // 没有签名的话，同网段任何人都能伪造一条「某某请你评这个」，
        // 或者伪造一条「我同意了」把活推给别人。
        var forged = signed with { BodyJson = signed.BodyJson.Replace("peer-1", "peer-9") };
        Assert.False(forged.VerifySignature());
    }
}

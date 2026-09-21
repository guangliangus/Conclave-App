using Conclave.Domain;

namespace Conclave.UnitTests;

/// <summary>
/// 指派与答复的飞书通知。
/// </summary>
/// <remarks>
/// 指派是整套流程里唯一要人当场做决定的一步，而那条待确认只在面板上 —— 不开面板就
/// 不知道有活等着自己；指派出去的人同样没有任何提示，对方接没接受得自己去看。
/// 两边都在等对方，而两边都不知道要等。这组用例钉的就是「两边都收得到」。
/// </remarks>
public class AssignmentNoticeTests
{
    private static PrMeta Pr()
        => TestElectors.Pr(id: 2721, author: "LIONMAIL\\youngsun") with { SrcCommit = "aaaaaaaa11111111" };

    private static Elector Peer(string id) => new()
    {
        Id = id,
        PublicKey = "pk-" + id,
        AzIdentity = "peer-" + id,
        Projects = ["liontrip-cms"],
        MaxConcurrent = 2,
        LastHeartbeat = DateTimeOffset.UtcNow,
    };

    /// <summary>收件人是<b>被指派者</b>，不是 PR 作者。</summary>
    [Fact]
    public async Task Assigning_tells_the_node_that_got_the_work()
    {
        using var h = new Harness();
        var peer = Peer("peer-1");
        var rev = await h.ReportAsync(Pr());
        h.Mesh.Peers.Add(peer);

        await h.Orchestrator.AssignAsync(rev.Id, peer.Id, "这个我自己写的，麻烦你看一下", CancellationToken.None);

        var (pr, notice) = Assert.Single(h.Notifier.Assignments);
        Assert.Equal(2721, pr.PrId);
        Assert.Equal(rev.Id, notice.RevisionId);
        Assert.Equal("peer-peer-1", notice.Recipient);
        Assert.Equal("alan", notice.Counterpart);
        Assert.Null(notice.Accepted);
        Assert.Equal("这个我自己写的，麻烦你看一下", notice.Note);
    }

    /// <summary>
    /// 送不到就不通知。
    /// </summary>
    /// <remarks>
    /// 发一条「指派给你了」，对方会去面板上找一条根本不存在的待确认。
    /// </remarks>
    [Fact]
    public async Task An_undelivered_assignment_notifies_nobody()
    {
        using var h = new Harness();
        var rev = await h.ReportAsync(Pr());

        Assert.False(await h.Orchestrator.AssignAsync(rev.Id, "not-in-mesh", null, CancellationToken.None));
        Assert.Empty(h.Notifier.Assignments);
    }

    /// <summary>答复反向发回给<b>指派者</b>。</summary>
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "这台机器额度快满了")]
    public async Task A_reply_goes_back_to_whoever_asked(bool accepted, string? reason)
    {
        using var h = new Harness();
        var requester = Peer("peer-1");
        var rev = await h.ReportAsync(Pr());
        h.Mesh.Peers.Add(requester);

        h.Mesh.UpdateState(s => s with
        {
            Pending = [new AssignmentRequest(
                "req-1", rev.Id, requester.Id, h.SelfId, DateTimeOffset.UtcNow, null)],
        });

        await h.Orchestrator.RespondToAssignmentAsync("req-1", accepted, reason, CancellationToken.None);

        var (_, notice) = Assert.Single(h.Notifier.Assignments);
        Assert.Equal("peer-peer-1", notice.Recipient);
        Assert.Equal("alan", notice.Counterpart);
        Assert.Equal(accepted, notice.Accepted);
        Assert.Equal(reason, notice.Note);
    }

    /// <summary>
    /// 处理完，菜单栏那枚角标就该消失。
    /// </summary>
    /// <remarks>
    /// 角标不恢复比不提示更糟：它会让人反复去开面板找一条已经处理掉的待确认。
    /// </remarks>
    [Fact]
    public async Task Responding_clears_the_tray_badge()
    {
        using var h = new Harness();
        var requester = Peer("peer-1");
        var rev = await h.ReportAsync(Pr());
        h.Mesh.Peers.Add(requester);

        h.Mesh.UpdateState(s => s with
        {
            Pending = [new AssignmentRequest(
                "req-1", rev.Id, requester.Id, h.SelfId, DateTimeOffset.UtcNow, null)],
        });
        // 不手工同步：角标该由 IMesh.StateChanged 自己带起来，这里正是在验那条线。
        Assert.Equal(1, h.State.PendingAssignments);

        await h.Orchestrator.RespondToAssignmentAsync("req-1", accepted: true, null, CancellationToken.None);

        Assert.Equal(0, h.State.PendingAssignments);
    }

    /// <summary>
    /// 通知发不出去，指派照样生效。
    /// </summary>
    /// <remarks>
    /// 认领已经落进实时状态、答复也已经送到对端了。为一条通知把它回滚或者抛出去都说不通。
    /// </remarks>
    [Fact]
    public async Task A_failing_notification_does_not_undo_the_assignment()
    {
        using var h = new Harness();
        var requester = Peer("peer-1");
        var rev = await h.ReportAsync(Pr());
        h.Mesh.Peers.Add(requester);
        h.Notifier.ThrowOnAssignment = new InvalidOperationException("飞书挂了");

        h.Mesh.UpdateState(s => s with
        {
            Pending = [new AssignmentRequest(
                "req-1", rev.Id, requester.Id, h.SelfId, DateTimeOffset.UtcNow, null)],
        });

        await h.Orchestrator.RespondToAssignmentAsync("req-1", accepted: true, null, CancellationToken.None);

        Assert.Empty(h.Mesh.State.Pending);
        Assert.Equal(rev.Id, h.Mesh.State.Claims.Single().RevisionId);
        Assert.True(Assert.Single(h.Mesh.SentReplies).Accepted);
        Assert.Empty(h.Notifier.Assignments);
    }
}

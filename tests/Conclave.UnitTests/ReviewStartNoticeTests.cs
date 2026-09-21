using Conclave.Domain;

namespace Conclave.UnitTests;

/// <summary>
/// 「已开始评审」通知：开跑时发，一个 revision 只发一条，发不出去也不许影响评审。
/// </summary>
/// <remarks>
/// 这三件事的失败方式各不相同但都很隐蔽：不发等于作者继续干等；发重了是骚扰，
/// 而 quorum≥2 和失败重试都会让同一个 revision 反复开跑；发崩了则会掀掉一次
/// 已经开跑的评审 —— 那是拿一条锦上添花的通知去换一次真正烧了额度的评审。
/// </remarks>
public class ReviewStartNoticeTests
{
    private static PrMeta Pr()
        => TestElectors.Pr(id: 2721, author: "LIONMAIL\\youngsun", files: 3)
            with { SrcCommit = "aaaaaaaa11111111" };

    [Fact]
    public async Task The_author_is_told_as_soon_as_a_node_picks_the_pr_up()
    {
        using var h = new Harness();
        var rev = await h.ReportAsync(Pr());

        h.Orchestrator.RequestReview(rev.Id);
        await h.TickAsync();

        var (pr, started) = Assert.Single(h.Notifier.Started);
        Assert.Equal(2721, pr.PrId);
        Assert.Equal(rev.Id, started.RevisionId);

        // 通知里的评审人，跟盖在票上的 ReviewerAz 必须是同一个值 ——
        // 作者在通知里看到的名字，事后去链上查得对得上。
        Assert.Equal("alan", started.ReviewerAz);
    }

    /// <summary>
    /// 失败换一轮重跑时不能再发一条。
    /// </summary>
    /// <remarks>
    /// 作者要的信息是「有人接手了」，而那件事只会发生一次。重试是我们内部怎么安排的，
    /// 每换一台机器就通知他一次，只会让他以为出了什么事。
    /// </remarks>
    [Fact]
    public async Task A_retry_does_not_announce_the_same_revision_twice()
    {
        using var h = new Harness();
        h.Options.RetryOnSameNode = true;
        h.Runner.Throw = new InvalidOperationException("claude 起不来");

        var rev = await h.ReportAsync(Pr());

        h.Orchestrator.RequestReview(rev.Id);
        await h.TickAsync();

        h.Orchestrator.RequestReview(rev.Id);
        await h.TickAsync();

        Assert.True(h.Runner.Calls >= 2, $"应当重试过，实际只跑了 {h.Runner.Calls} 次");
        Assert.Single(h.Notifier.Started);
    }

    /// <summary>
    /// 通知发不出去，评审照跑、票照出。
    /// </summary>
    /// <remarks>
    /// 它连链上都不留痕，没有任何理由让它掀掉一次已经开跑的评审。
    /// </remarks>
    [Fact]
    public async Task A_failing_notification_does_not_stop_the_review()
    {
        using var h = new Harness();
        h.Notifier.ThrowOnStart = new InvalidOperationException("飞书挂了");

        var rev = await h.ReportAsync(Pr());

        h.Orchestrator.RequestReview(rev.Id);
        await h.TickAsync();

        var voted = await h.StateOfAsync(rev);
        Assert.Single(voted.Ballots);
        Assert.Equal(ReviewDecision.Reject, voted.Ballots[0].Decision);
        Assert.Empty(h.Notifier.Started);
    }
}

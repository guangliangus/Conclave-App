using Conclave.Domain;

namespace Conclave.UnitTests;

/// <summary>
/// 把各节点上报的实时状态合并成一份全局队列视图。
/// </summary>
/// <remarks>
/// 这是 P1 的核心：队列从「链上历史」换成「实时状态的并集」。纯函数，所以每个节点合出
/// 同一份视图 —— 跟席位表一个道理，「队列里有什么」不需要协商。
/// <para>
/// 掉线节点的上报被整体忽略，这一条就是自动接管的全部机制 —— 原先要靠链上的
/// <c>Seating</c> + 10 分钟 <c>SeatingTimeout</c> + <c>Recess</c> 三个东西配合。
/// </para>
/// </remarks>
public class QueueProjectionTests
{
    private static readonly DateTimeOffset Now = TestElectors.Now;

    private static readonly Revision Rev = new("liontrip-cms", 2721, "aaaaaaaa11111111");

    private static QueuedRevision Item(Revision? rev = null, int quorum = 1)
    {
        var r = rev ?? Rev;
        return new QueuedRevision(r, TestElectors.Pr(id: r.PrId), quorum, "rules");
    }

    private static IReadOnlySet<string> Alive(params string[] ids)
        => ids.ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, int> NoBallots
        => new Dictionary<string, int>(StringComparer.Ordinal);

    private static IReadOnlySet<string> NoneFinished
        => new HashSet<string>(StringComparer.Ordinal);

    [Fact]
    public void A_reported_pr_shows_up_as_needing_a_reviewer()
    {
        var states = new Dictionary<string, LiveState>(StringComparer.Ordinal)
        {
            ["n1"] = new() { Discovered = [Item()] },
        };

        var entry = Assert.Single(
            QueueProjection.Build(states, Alive("n1"), NoBallots, NoneFinished));

        Assert.Equal(Rev.Id, entry.Revision.Id);
        Assert.True(entry.NeedsReviewer);
        Assert.Null(entry.ReviewingBy);
    }

    [Fact]
    public void A_dead_node_s_report_is_ignored_entirely()
    {
        var states = new Dictionary<string, LiveState>(StringComparer.Ordinal)
        {
            ["dead"] = new() { Discovered = [Item()] },
        };

        // 掉线节点上报的队列项也一起消失。它可能上报的是几小时前的世界，
        // 而那些 PR 早就 merge 了 —— 留着就又变成僵尸。
        Assert.Empty(QueueProjection.Build(states, Alive(), NoBallots, NoneFinished));
    }

    [Fact]
    public void A_dead_node_s_in_flight_review_frees_the_pr()
    {
        var states = new Dictionary<string, LiveState>(StringComparer.Ordinal)
        {
            ["n1"] = new() { Discovered = [Item()] },
            ["dead"] = new() { Reviewing = [new ActiveReview(Rev.Id, 0, Now)] },
        };

        var entry = Assert.Single(
            QueueProjection.Build(states, Alive("n1"), NoBallots, NoneFinished));

        // 这就是接管：崩掉的节点不再发心跳，它「正在评」的声明随之从视图里消失。
        Assert.Null(entry.ReviewingBy);
        Assert.True(entry.NeedsReviewer);
    }

    [Fact]
    public void An_active_review_marks_the_entry_as_taken()
    {
        var started = Now.AddMinutes(-3);
        var states = new Dictionary<string, LiveState>(StringComparer.Ordinal)
        {
            ["n1"] = new() { Discovered = [Item()] },
            ["n2"] = new() { Reviewing = [new ActiveReview(Rev.Id, 0, started)] },
        };

        var entry = Assert.Single(
            QueueProjection.Build(states, Alive("n1", "n2"), NoBallots, NoneFinished));

        Assert.Equal("n2", entry.ReviewingBy);
        Assert.Equal(started, entry.StartedAt);
        Assert.False(entry.NeedsReviewer);
    }

    [Fact]
    public void The_earliest_claim_wins_and_ties_break_on_elector_id()
    {
        var states = new Dictionary<string, LiveState>(StringComparer.Ordinal)
        {
            ["n1"] = new()
            {
                Discovered = [Item()],
                Claims = [new ReviewClaim(Rev.Id, "n1", Now.AddSeconds(1))],
            },
            ["n2"] = new() { Claims = [new ReviewClaim(Rev.Id, "n2", Now)] },
        };

        var entry = Assert.Single(
            QueueProjection.Build(states, Alive("n1", "n2"), NoBallots, NoneFinished));

        // 先到者胜。纯函数，各节点独立算出同一结果，输的一方自己撤销。
        Assert.Equal("n2", entry.ClaimedBy);

        var sameInstant = new Dictionary<string, LiveState>(StringComparer.Ordinal)
        {
            ["n1"] = new()
            {
                Discovered = [Item()],
                Claims = [new ReviewClaim(Rev.Id, "n1", Now)],
            },
            ["n2"] = new() { Claims = [new ReviewClaim(Rev.Id, "n2", Now)] },
        };

        // 同刻必须再比 id：只按时间会让两个节点都以为自己赢了，同一个 PR 被评两遍。
        Assert.Equal(
            "n1",
            QueueProjection.Build(sameInstant, Alive("n1", "n2"), NoBallots, NoneFinished)
                .Single().ClaimedBy);
    }

    [Fact]
    public void Once_running_the_claim_stops_being_shown()
    {
        var states = new Dictionary<string, LiveState>(StringComparer.Ordinal)
        {
            ["n1"] = new()
            {
                Discovered = [Item()],
                Claims = [new ReviewClaim(Rev.Id, "n1", Now)],
                Reviewing = [new ActiveReview(Rev.Id, 0, Now)],
            },
        };

        var entry = Assert.Single(
            QueueProjection.Build(states, Alive("n1"), NoBallots, NoneFinished));

        // 认领只是「打算评」，开跑之后那个状态没意义了。
        Assert.Equal("n1", entry.ReviewingBy);
        Assert.Null(entry.ClaimedBy);
    }

    [Fact]
    public void The_chain_summary_marks_entries_finished_and_counts_ballots()
    {
        var states = new Dictionary<string, LiveState>(StringComparer.Ordinal)
        {
            ["n1"] = new() { Discovered = [Item(quorum: 2)] },
        };

        var counted = QueueProjection.Build(
            states,
            Alive("n1"),
            new Dictionary<string, int>(StringComparer.Ordinal) { [Rev.Id] = 1 },
            NoneFinished).Single();

        Assert.Equal(1, counted.Ballots);
        Assert.True(counted.NeedsReviewer);   // quorum=2，还差一票

        var done = QueueProjection.Build(
            states, Alive("n1"), NoBallots, Alive(Rev.Id)).Single();

        Assert.True(done.Finished);
        Assert.False(done.NeedsReviewer);
    }

    [Fact]
    public void The_same_pr_reported_by_two_nodes_is_deduped_deterministically()
    {
        // 轮询分片短暂重叠时会出现。取哪一份必须确定，否则各节点算出的 quorum 不一致。
        var states = new Dictionary<string, LiveState>(StringComparer.Ordinal)
        {
            ["n2"] = new() { Discovered = [Item(quorum: 3)] },
            ["n1"] = new() { Discovered = [Item(quorum: 1)] },
        };

        var entry = Assert.Single(
            QueueProjection.Build(states, Alive("n1", "n2"), NoBallots, NoneFinished));

        // 按 electorId 排序后先到先得 → n1 那一份。
        Assert.Equal(1, entry.Quorum);
    }

    [Fact]
    public void Entries_are_ordered_newest_pr_first()
    {
        var older = new Revision("liontrip-cms", 2700, "cccccccc33333333");
        var states = new Dictionary<string, LiveState>(StringComparer.Ordinal)
        {
            ["n1"] = new() { Discovered = [Item(older), Item()] },
        };

        var entries = QueueProjection.Build(states, Alive("n1"), NoBallots, NoneFinished);

        Assert.Equal([2721, 2700], entries.Select(e => e.Revision.PrId));
    }

    [Fact]
    public void A_manual_claim_beats_the_hrw_draw()
    {
        var mesh = Enumerable.Range(1, 5)
            .Select(i => TestElectors.Make($"n{i}"))
            .ToList();

        var entry = new QueueEntry(Item(), null, 0, null, ClaimedBy: "n4", 0, false);

        // 人明确要评的，规则不该抢走。
        Assert.Equal(0, QueueProjection.SeatFor(entry, "n4", mesh, Now));
        Assert.Equal(-1, QueueProjection.SeatFor(entry, "n1", mesh, Now));
    }

    [Fact]
    public void Nobody_is_seated_on_an_entry_someone_is_already_reviewing()
    {
        var mesh = new[] { TestElectors.Make("n1") };
        var entry = new QueueEntry(Item(), ReviewingBy: "n2", 0, Now, null, 0, false);

        Assert.Equal(-1, QueueProjection.SeatFor(entry, "n1", mesh, Now));
    }

    [Fact]
    public void The_seat_offered_is_indexed_by_rounds_already_used()
    {
        var mesh = Enumerable.Range(1, 4).Select(i => TestElectors.Make($"n{i}")).ToList();
        var item = Item(quorum: 3);

        var seats = SeatAssignment.Seats(item.Revision, item.Pr, mesh, quorum: 3, Now);
        var entry = new QueueEntry(item, null, 0, null, null, Ballots: 1, false);

        // 烧掉 1 轮 → 下一个待填的是 round 1，只有坐那一席的人该动。
        Assert.Equal(1, QueueProjection.SeatFor(entry, seats[1], mesh, Now, roundsUsed: 1));
        Assert.Equal(-1, QueueProjection.SeatFor(entry, seats[0], mesh, Now, roundsUsed: 1));
    }

    [Fact]
    public void An_error_ballot_still_advances_the_seat_index()
    {
        // 这是踩过的坑：Error 票不计入有效票，所以拿 Ballots（有效票数）当下标会一直指向
        // round 0 —— 而 round 0 已经出过 Error 票、被判定为已定局，于是编排层既不重试
        // 也不公布，PR 永久卡住。下标必须按「已烧掉的轮次」算。
        var mesh = new[] { TestElectors.Make("n1") };
        var item = Item();

        // 有效票 0、已烧掉 1 轮（那一票是 Error）。
        var entry = new QueueEntry(item, null, 0, null, null, Ballots: 0, false);

        Assert.Equal(1, QueueProjection.SeatFor(entry, "n1", mesh, Now, roundsUsed: 1));
    }

    [Fact]
    public void A_finished_entry_offers_no_seat()
    {
        var mesh = new[] { TestElectors.Make("n1") };
        var entry = new QueueEntry(Item(), null, 0, null, null, 1, Finished: true);

        Assert.Equal(-1, QueueProjection.SeatFor(entry, "n1", mesh, Now));
    }
    [Fact]
    public void A_claim_still_has_to_pass_the_hard_rules()
    {
        // 认领优先于抽签，但不能优先于硬规则 —— 否则作者点一下「认领」就评了自己的 PR，
        // 而「不评审自己的 PR」是这套东西的地基。
        var author = TestElectors.Make("self", azIdentity: "youngsun");
        var other = TestElectors.Make("other", azIdentity: "edisonwei");
        var item = Item();

        var claimedByAuthor = new QueueEntry(
            item, ReviewingBy: null, Round: 0, StartedAt: null,
            ClaimedBy: author.Id, Ballots: 0, Finished: false);
        Assert.Equal(-1, QueueProjection.SeatFor(claimedByAuthor, author.Id, [author, other], Now));

        // 同一个 PR 换个人认领就成立。
        var claimedByOther = new QueueEntry(
            item, ReviewingBy: null, Round: 0, StartedAt: null,
            ClaimedBy: other.Id, Ballots: 0, Finished: false);
        Assert.Equal(0, QueueProjection.SeatFor(claimedByOther, other.Id, [author, other], Now));
    }


    [Fact]
    public void Claiming_your_own_pr_is_refused_unless_the_entry_allows_self_review()
    {
        // 认领优先于 HRW，但不该绕过硬规则 —— 除非这一项本身就带着「允许自评」。
        var mesh = new[] { TestElectors.Make("n1", azIdentity: "youngsun") };
        var pr = TestElectors.Pr(author: "LIONMAIL\\youngsun");
        var strict = new QueuedRevision(Rev, pr, 1, "rules");
        var lenient = strict with { AllowSelfReview = true };

        var refused = new QueueEntry(strict, null, 0, null, ClaimedBy: "n1", 0, false);
        var allowed = new QueueEntry(lenient, null, 0, null, ClaimedBy: "n1", 0, false);

        Assert.Equal(-1, QueueProjection.SeatFor(refused, "n1", mesh, Now));
        Assert.Equal(0, QueueProjection.SeatFor(allowed, "n1", mesh, Now));
    }

    [Fact]
    public void The_self_review_flag_travels_with_the_entry_not_with_the_node()
    {
        // 席位表必须各节点算出同一份，所以这个开关只能来自队列项。两个节点读同一项，
        // 就算它们本机配置不同也会得出同一个结论 —— 这条测试守的就是这件事。
        var mesh = new[] { TestElectors.Make("n1", azIdentity: "youngsun") };
        var pr = TestElectors.Pr(author: "LIONMAIL\\youngsun");
        var item = new QueuedRevision(Rev, pr, 1, "rules") { AllowSelfReview = true };
        var entry = new QueueEntry(item, null, 0, null, null, 0, false);

        Assert.Equal(0, QueueProjection.SeatFor(entry, "n1", mesh, Now));
    }
    private static QueueEntry Open(int prId, string? reviewingBy = null, string? claimedBy = null)
        => new(
            new QueuedRevision(
                new Revision("liontrip-cms", prId, "aaaaaaaa11111111"),
                TestElectors.Pr(id: prId), 1, "rules"),
            ReviewingBy: reviewingBy,
            Round: 0,
            StartedAt: reviewingBy is null ? null : Now,
            ClaimedBy: claimedBy,
            Ballots: 0,
            Finished: false);

    [Fact]
    public void A_node_holds_at_most_one_seat()
    {
        var mesh = new[] { TestElectors.Make("n1"), TestElectors.Make("n2") };
        var queue = new[] { Open(2001), Open(2002), Open(2003), Open(2004) }
            .Select(e => new QueueProjection.SeatCandidate(e));

        var seats = QueueProjection.AssignSeats(queue, mesh, Now);

        // 两个节点、四个 PR：只该分出两个席位，一人一个。
        // 逐个 PR 独立算的话每个节点都会是两个 PR 的赢家，而它一次只能跑一个 ——
        // 界面上四行都写「归本节点」，四行的按钮都能点，全是假的。
        Assert.Equal(2, seats.Count);
        Assert.Equal(2, seats.Values.Distinct().Count());
    }

    [Fact]
    public void Seats_go_to_the_oldest_prs_first()
    {
        var mesh = new[] { TestElectors.Make("n1") };
        var queue = new[] { Open(2009), Open(2001), Open(2005) }
            .Select(e => new QueueProjection.SeatCandidate(e));

        var seats = QueueProjection.AssignSeats(queue, mesh, Now);

        // FIFO：分配顺序必须稳定，否则新来的 PR 一到就把已经分好的席位顶掉，
        // 界面上「归本节点」会在几行之间跳。
        Assert.Equal("2001@aaaaaaaa", Assert.Single(seats).Key);
    }

    [Fact]
    public void A_node_that_is_already_reviewing_gets_no_new_seat()
    {
        var mesh = new[] { TestElectors.Make("n1"), TestElectors.Make("n2") };

        // n1 正在评 2001；剩下两个 PR 只能落到 n2 手上，而且只落一个。
        var queue = new[] { Open(2001, reviewingBy: "n1"), Open(2002), Open(2003) }
            .Select(e => new QueueProjection.SeatCandidate(e));

        var seats = QueueProjection.AssignSeats(queue, mesh, Now);

        Assert.Equal("n2", Assert.Single(seats).Value);
        Assert.DoesNotContain("n1", seats.Values);
    }

    [Fact]
    public void Finishing_a_review_frees_the_node_for_the_next_pr()
    {
        var mesh = new[] { TestElectors.Make("n1") };
        var busy = new[] { Open(2001, reviewingBy: "n1"), Open(2002) }
            .Select(e => new QueueProjection.SeatCandidate(e));

        Assert.Empty(QueueProjection.AssignSeats(busy, mesh, Now));

        // 评完之后 ActiveReview 撤下，同一份纯函数就把它算回空闲 ——
        // 「跑完才拿新席位」不需要任何额外机制。
        var idle = new[] { Open(2002) }.Select(e => new QueueProjection.SeatCandidate(e));

        Assert.Equal("n1", Assert.Single(QueueProjection.AssignSeats(idle, mesh, Now)).Value);
    }

    [Fact]
    public void A_claim_occupies_the_claimer_too()
    {
        var mesh = new[] { TestElectors.Make("n1"), TestElectors.Make("n2") };
        var queue = new[] { Open(2001, claimedBy: "n1"), Open(2002), Open(2003) }
            .Select(e => new QueueProjection.SeatCandidate(e));

        var seats = QueueProjection.AssignSeats(queue, mesh, Now);

        // 认领是「打算评」，一样占着这个节点 —— 否则认领完还能再被分一个，
        // 而它一次只能跑一个。
        Assert.Equal("n1", seats["2001@aaaaaaaa"]);
        Assert.Single(seats, kv => kv.Value == "n2");
        Assert.Equal(2, seats.Count);
    }

    [Fact]
    public void A_pr_waiting_to_be_promulgated_does_not_hold_a_seat()
    {
        var mesh = new[] { TestElectors.Make("n1") };
        var queue = new[]
        {
            // 2001 票收够了，等公布 —— 它不需要评审者。
            new QueueProjection.SeatCandidate(Open(2001), RoundsUsed: 1, NeedsReviewer: false),
            new QueueProjection.SeatCandidate(Open(2002)),
        };

        var seats = QueueProjection.AssignSeats(queue, mesh, Now);

        // 席位是稀缺的（一个节点一个），给一个不需要评审的 PR 就等于把唯一空闲的节点
        // 挂在那儿，后面真正等着评的一个都动不了。实测踩过。
        Assert.Equal("2002@aaaaaaaa", Assert.Single(seats).Key);
    }

}

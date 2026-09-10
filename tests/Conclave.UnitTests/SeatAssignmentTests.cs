using Conclave.Domain;

namespace Conclave.UnitTests;

public class SeatAssignmentTests
{
    private static readonly ReservedMatters Reserved = ReservedMatters.Default;

    /// <summary>把三档都拉到 3 的策略 —— 也就是改造之前的写死行为。</summary>
    private static readonly QuorumPolicy Triple = new()
    {
        ReservedMatters = 3,
        MediumChange = 2,
        LargeChange = 3,
    };

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(30)]
    public void The_default_policy_reviews_every_pr_exactly_once(int files)
        // 默认只评一次。多跑几遍能压掉 LLM 的方差，但那是成倍的额度 —— 要显式配才开。
        => Assert.Equal(1, SeatAssignment.QuorumSize(
            TestElectors.Pr(files: files), Reserved, QuorumPolicy.Single));

    [Fact]
    public void The_default_policy_still_skips_drafts()
        => Assert.Equal(0, SeatAssignment.QuorumSize(
            TestElectors.Pr(draft: true), Reserved, QuorumPolicy.Single));

    [Fact]
    public void A_configured_policy_restores_the_three_way_review()
    {
        var reserved = TestElectors.Pr(files: 1, paths: ["src/Auth/TokenService.cs"]);
        var large = TestElectors.Pr(files: 30);
        var medium = TestElectors.Pr(files: 8);

        Assert.Equal(3, SeatAssignment.QuorumSize(reserved, Reserved, Triple));
        Assert.Equal(3, SeatAssignment.QuorumSize(large, Reserved, Triple));
        Assert.Equal(2, SeatAssignment.QuorumSize(medium, Reserved, Triple));
    }

    [Fact]
    public void Overlapping_tiers_take_the_strictest_one()
    {
        // 25 个文件 + 碰了支付路径：该按两者里更严的那个跑，
        // 而不是取决于 if 的书写顺序。
        var policy = new QuorumPolicy { ReservedMatters = 2, LargeChange = 3, MediumChange = 2 };
        var pr = TestElectors.Pr(files: 25, paths: ["src/payment-center/Charge.cs"]);

        Assert.Equal(3, SeatAssignment.QuorumSize(pr, Reserved, policy));
    }

    [Fact]
    public void The_policy_fingerprint_changes_with_the_policy()
    {
        // 指纹跟敏感路径清单一起写进 Summons —— 半年后要能分清「没命中敏感路径」
        // 和「当时 quorum 策略本来就是全 1」。
        Assert.NotEqual(QuorumPolicy.Single.Fingerprint(), Triple.Fingerprint());
        Assert.Equal(QuorumPolicy.Single.Fingerprint(), new QuorumPolicy().Fingerprint());
        Assert.True(QuorumPolicy.Single.IsSingleReview);
        Assert.False(Triple.IsSingleReview);
    }

    [Fact]
    public void The_seat_key_is_stable_across_revisions_of_the_same_pr()
    {
        // 作者 push 修复后 revision 变了，席位种子不能变 —— 否则复审会换人。
        var first = new Revision("liontrip-cms", 2721, "dc1d1d474a470955");
        var second = new Revision("liontrip-cms", 2721, "ffffffff11111111");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.SeatKey, second.SeatKey);
    }

    [Fact]
    public void A_fix_goes_back_to_the_node_that_reviewed_the_previous_revision()
    {
        var mesh = Enumerable.Range(1, 5).Select(i => TestElectors.Make($"n{i}")).ToList();
        var pr = TestElectors.Pr();
        var fixedUp = pr with { SrcCommit = "ffffffff11111111" };

        var seats = SeatAssignment.Seats(
            fixedUp.ToRevision(), fixedUp, mesh, quorum: 1, TestElectors.Now, incumbent: "n4");

        // 那个节点已经读过这份代码、提过这些 finding。
        Assert.Equal(["n4"], seats);
    }

    [Fact]
    public void The_incumbent_is_ignored_when_it_is_no_longer_eligible()
    {
        var mesh = new[]
        {
            TestElectors.Make("n1"),
            TestElectors.Make("n2", utilization: 0.95),   // 额度过线，已出局
        };
        var pr = TestElectors.Pr();

        var seats = SeatAssignment.Seats(
            pr.ToRevision(), pr, mesh, quorum: 1, TestElectors.Now, incumbent: "n2");

        // 硬规则优先于归属：额度用尽的节点请回来只会换到一张 Error 票。
        Assert.Equal(["n1"], seats);
    }

    [Fact]
    public void The_incumbent_only_holds_round_zero()
    {
        var mesh = Enumerable.Range(1, 4).Select(i => TestElectors.Make($"n{i}")).ToList();
        var pr = TestElectors.Pr();

        var seats = SeatAssignment.Seats(
            pr.ToRevision(), pr, mesh, quorum: 1, TestElectors.Now,
            extraRounds: 2, incumbent: "n3");

        // 重试轮次必须换人 —— 上一个已经失败过了，再派给它只会重复同一个失败。
        Assert.Equal("n3", seats[0]);
        Assert.Equal(3, seats.Count);
        Assert.DoesNotContain("n3", seats.Skip(1));
    }

    [Fact]
    public void Draft_pr_gets_no_quorum()
        => Assert.Equal(0, SeatAssignment.QuorumSize(TestElectors.Pr(draft: true), Reserved, Triple));

    [Theory]
    [InlineData(1, 1)]       // 单文件改动单跑
    [InlineData(5, 1)]       // 正好在中档边界上，仍是 1
    [InlineData(6, 2)]       // 过了中档就跑两遍
    [InlineData(20, 2)]      // 正好在大改动边界上，仍是 2
    [InlineData(21, 3)]      // 过了就跑三遍
    public void Quorum_scales_with_the_number_of_changed_files(int files, int expected)
        => Assert.Equal(expected, SeatAssignment.QuorumSize(
            TestElectors.Pr(files: files), Reserved, Triple));

    [Fact]
    public void Quorum_falls_back_to_one_when_change_stats_are_unavailable()
        // ADO 读不到改动统计时文件数留 0 —— 必须退到 1（少跑几遍），
        // 不能因为读不到就把 PR 漏掉或反过来拉满 quorum 白烧额度。
        => Assert.Equal(1, SeatAssignment.QuorumSize(
            TestElectors.Pr(files: 0, paths: []), Reserved, Triple));

    [Theory]
    [InlineData("src/payment-center/Charge.cs")]
    [InlineData("src/Auth/TokenService.cs")]
    [InlineData("db/migrations/001_init.sql")]
    [InlineData("api/openapi.yaml")]
    [InlineData("src/Api/appsettings.Production.json")]
    public void Reserved_matters_force_full_quorum_even_for_tiny_diffs(string path)
        => Assert.Equal(3, SeatAssignment.QuorumSize(
            TestElectors.Pr(files: 1, paths: [path]), Reserved, Triple));

    [Fact]
    public void Ordinary_path_does_not_trip_reserved_matters()
        => Assert.Equal(1, SeatAssignment.QuorumSize(
            TestElectors.Pr(files: 1, paths: ["src/Web/HomeController.cs"]), Reserved, Triple));

    [Fact]
    public void An_author_can_never_review_their_own_pr()
    {
        var pr = TestElectors.Pr(author: "LIONMAIL\\youngsun");
        var author = TestElectors.Make("n1", azIdentity: "youngsun");

        Assert.False(SeatAssignment.Eligible(author, pr, TestElectors.Now));
        Assert.Empty(SeatAssignment.Seats(pr.ToRevision(), pr, [author], quorum: 1, TestElectors.Now));
    }

    [Fact]
    public void Not_having_the_repo_cloned_no_longer_matters()
    {
        // 代码是评审时拉进临时工作区的，所以「本机预先 clone 了什么」与入席资格无关 ——
        // 任何节点都能评任何仓库。这条曾经是硬规则，现在刻意断言它已经不是。
        var pr = TestElectors.Pr();
        var anyNode = TestElectors.Make("n1");

        Assert.True(SeatAssignment.Eligible(anyNode, pr, TestElectors.Now));
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.79, true)]
    [InlineData(0.8, false)]     // 正好到上限就出局
    [InlineData(0.95, false)]
    public void A_node_over_the_claude_usage_ceiling_is_not_eligible(double utilization, bool eligible)
        => Assert.Equal(eligible, SeatAssignment.Eligible(
            TestElectors.Make("n1", utilization: utilization), TestElectors.Pr(), TestElectors.Now));

    [Fact]
    public void Nobody_is_seated_when_every_node_is_out_of_claude_headroom()
    {
        var pr = TestElectors.Pr(files: 30);   // quorum = 3
        var spent = new[]
        {
            TestElectors.Make("n1", utilization: 0.81),
            TestElectors.Make("n2", utilization: 0.90),
            TestElectors.Make("n3", utilization: 1.00),
        };

        // 空席位表是设计行为而不是故障：额度回落后下一轮编排会把这个 PR 捡起来。
        Assert.Empty(SeatAssignment.Seats(
            pr.ToRevision(), pr, spent, quorum: 3, TestElectors.Now));
    }

    [Fact]
    public void Claude_usage_tapers_the_weight_before_the_ceiling_bites()
    {
        var fresh = TestElectors.Make("n1");
        var nearly = TestElectors.Make("n1", utilization: 0.75);

        // 到上限之前权重要平滑下降，而不是一直满权重然后突然出局 ——
        // 否则额度快用完的节点仍会均分席位，然后集体在同一时刻掉出 mesh。
        Assert.True(nearly.Weight < fresh.Weight);
        Assert.True(nearly.Weight > 0);
    }

    [Fact]
    public void Recent_review_count_lowers_the_weight_too()
    {
        // 「当前 node 的 review 次数」也要计入公平性：刚干了很多活的少拿。
        var idle = TestElectors.Make("n1");
        var busy = TestElectors.Make("n1", reviews24h: 20);

        Assert.True(busy.Weight < idle.Weight);
    }

    [Fact]
    public void Node_without_project_access_is_not_eligible()
        => Assert.False(SeatAssignment.Eligible(
            TestElectors.Make("n1", projects: ["another-project"]), TestElectors.Pr(), TestElectors.Now));

    [Fact]
    public void Saturated_node_is_not_eligible()
        => Assert.False(SeatAssignment.Eligible(
            TestElectors.Make("n1", running: 2, maxConcurrent: 2), TestElectors.Pr(), TestElectors.Now));

    [Fact]
    public void Stale_heartbeat_makes_a_node_ineligible()
        => Assert.False(SeatAssignment.Eligible(
            TestElectors.Make("n1", heartbeat: TestElectors.Now.AddMinutes(-5)),
            TestElectors.Pr(),
            TestElectors.Now));

    [Fact]
    public void Seats_are_distinct_nodes()
    {
        var pr = TestElectors.Pr(files: 30);   // quorum = 3
        var mesh = Enumerable.Range(1, 5).Select(i => TestElectors.Make($"n{i}")).ToList();

        var seats = SeatAssignment.Seats(pr.ToRevision(), pr, mesh, quorum: 3, TestElectors.Now);

        Assert.Equal(3, seats.Count);
        Assert.Equal(3, seats.Distinct().Count());
    }

    [Fact]
    public void Every_node_computes_the_same_seat_table()
    {
        var pr = TestElectors.Pr(files: 30);
        var mesh = Enumerable.Range(1, 5).Select(i => TestElectors.Make($"n{i}")).ToList();

        var a = SeatAssignment.Seats(pr.ToRevision(), pr, mesh, quorum: 3, TestElectors.Now);
        var b = SeatAssignment.Seats(pr.ToRevision(), pr, mesh.AsEnumerable().Reverse(), quorum: 3, TestElectors.Now);

        // 席位表必须与成员枚举顺序无关，否则各节点会算出不同结果。
        Assert.Equal(a, b);
    }

    [Fact]
    public void Seats_degrade_when_there_are_not_enough_eligible_nodes()
    {
        var pr = TestElectors.Pr(files: 30);   // 要 3 个
        var mesh = new[] { TestElectors.Make("n1"), TestElectors.Make("n2") };

        var seats = SeatAssignment.Seats(pr.ToRevision(), pr, mesh, quorum: 3, TestElectors.Now);

        // 返回的席位少于 quorum —— 调用方据此在 Promulgation 上标 degraded。
        Assert.Equal(2, seats.Count);
    }

    [Fact]
    public void Extra_rounds_are_retries_and_may_redraw_the_same_node()
    {
        var pr = TestElectors.Pr();                        // quorum = 1
        var only = new[] { TestElectors.Make("n1") };

        var seats = SeatAssignment.Seats(
            pr.ToRevision(), pr, only, quorum: 1, TestElectors.Now, extraRounds: 2);

        // 单节点 mesh 上弃权后必须还能重试，否则这个 PR 永远到不了终态：
        // 没有票不能公布，也没有别人可以接管。
        Assert.Equal(["n1", "n1", "n1"], seats);
    }

    [Fact]
    public void Extra_rounds_never_pad_the_formal_quorum_seats()
    {
        var pr = TestElectors.Pr(files: 30);    // quorum = 3
        var two = new[] { TestElectors.Make("n1"), TestElectors.Make("n2") };

        var seats = SeatAssignment.Seats(pr.ToRevision(), pr, two, quorum: 3, TestElectors.Now);

        // 正式席位互不重复：只有 2 个合格节点就只给 2 席（降级），不能拿重复节点凑满 3。
        Assert.Equal(2, seats.Count);
        Assert.Equal(2, seats.Distinct().Count());
    }

    [Fact]
    public void Discovery_only_shards_a_project_to_nodes_that_can_see_it()
    {
        // A 能看 secret-project，B 看不见。分片候选人必须只有 A ——
        // 拿全体存活节点当候选时，HRW 可能把它判给 B，而 B 的清单里根本没有它、
        // 永远不会去轮，于是这个 project 的 PR 谁都不发现，还没有任何报错。
        var a = TestElectors.Make("a", projects: ["shared", "secret-project"]);
        var b = TestElectors.Make("b", projects: ["shared"]);
        var mesh = new[] { a, b };

        var mine = SeatAssignment.DiscoveryShare(
            a.Id, ["shared", "secret-project"], mesh, TestElectors.Now);
        var theirs = SeatAssignment.DiscoveryShare(
            b.Id, ["shared"], mesh, TestElectors.Now);

        Assert.Contains("secret-project", mine);
        Assert.DoesNotContain("secret-project", theirs);

        // 两边都看得见的那个仍然只归一个人，不重不漏。
        Assert.Single(new[] { mine, theirs }, s => s.Contains("shared"));
    }

    [Fact]
    public void A_project_nobody_claims_still_gets_exactly_one_owner()
    {
        // 对端刚起来、心跳里的 Projects 还是空的：此时退回「全体 HRW」而不是
        // 「那就自己轮」—— 后者会让每个节点都把它算进自己的份额，分片退化成人人全轮。
        var mesh = Enumerable.Range(1, 3).Select(i => TestElectors.Make($"n{i}", projects: [])).ToList();
        var projects = Enumerable.Range(1, 9).Select(i => $"p{i}").ToList();

        var owned = mesh.SelectMany(
            n => SeatAssignment.DiscoveryShare(n.Id, projects, mesh, TestElectors.Now)).ToList();

        Assert.Equal(9, owned.Count);
        Assert.Equal(9, owned.Distinct().Count());
    }

    [Fact]
    public void Discovery_share_partitions_projects_without_overlap_or_gaps()
    {
        var projects = Enumerable.Range(1, 34).Select(i => $"project-{i}").ToList();
        var mesh = Enumerable.Range(1, 5).Select(i => TestElectors.Make($"n{i}")).ToList();

        var shares = mesh.ToDictionary(
            n => n.Id,
            n => SeatAssignment.DiscoveryShare(n.Id, projects, mesh, TestElectors.Now));

        var all = shares.Values.SelectMany(s => s).ToList();

        Assert.Equal(34, all.Count);                       // 不漏
        Assert.Equal(34, all.Distinct().Count());          // 不重
        Assert.All(shares.Values, s => Assert.NotEmpty(s));
    }

    [Fact]
    public void Discovery_share_falls_back_to_nothing_when_the_mesh_is_empty()
        => Assert.Empty(SeatAssignment.DiscoveryShare("n1", ["p"], [], TestElectors.Now));

    [Fact]
    public void The_author_is_not_eligible_by_default()
    {
        var author = TestElectors.Make("n1", azIdentity: "youngsun");
        var pr = TestElectors.Pr(author: "LIONMAIL\\youngsun");

        Assert.False(SeatAssignment.Eligible(author, pr, TestElectors.Now));
    }

    [Fact]
    public void The_author_becomes_eligible_when_self_review_is_allowed()
    {
        var author = TestElectors.Make("n1", azIdentity: "youngsun");
        var pr = TestElectors.Pr(author: "LIONMAIL\\youngsun");

        Assert.True(
            SeatAssignment.Eligible(author, pr, TestElectors.Now, allowSelfReview: true));
    }

    [Fact]
    public void Self_review_does_not_bypass_the_other_hard_rules()
    {
        // 这个开关只放开「作者」这一条。额度用满、没有 project 权限、并发打满
        // 仍然出局 —— 否则联调开关会顺手把三条硬规则一起关掉。
        var pr = TestElectors.Pr(author: "LIONMAIL\\youngsun");
        var overBudget = TestElectors.Make("n1", azIdentity: "youngsun", utilization: 0.95);
        var wrongProject = TestElectors.Make(
            "n2", azIdentity: "youngsun", projects: ["another-project"]);
        var busy = TestElectors.Make(
            "n3", azIdentity: "youngsun", running: 2, maxConcurrent: 2);

        foreach (var elector in new[] { overBudget, wrongProject, busy })
        {
            Assert.False(
                SeatAssignment.Eligible(elector, pr, TestElectors.Now, allowSelfReview: true));
        }
    }

    [Fact]
    public void A_single_node_mesh_seats_the_author_only_when_self_review_is_allowed()
    {
        // 本机联调的实际场景：只有一个（或几个 az 身份相同的）节点，而 PR 是自己提的。
        var mesh = new[] { TestElectors.Make("n1", azIdentity: "youngsun") };
        var revision = new Revision("liontrip-cms", 2721, "aaaaaaaa11111111");
        var pr = TestElectors.Pr(author: "LIONMAIL\\youngsun");

        Assert.Empty(SeatAssignment.Seats(revision, pr, mesh, quorum: 1, TestElectors.Now));
        Assert.Equal(
            ["n1"],
            SeatAssignment.Seats(
                revision, pr, mesh, quorum: 1, TestElectors.Now, allowSelfReview: true));
    }
}

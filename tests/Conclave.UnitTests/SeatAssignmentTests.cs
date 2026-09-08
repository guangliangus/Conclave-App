using Conclave.Domain;

namespace Conclave.UnitTests;

public class SeatAssignmentTests
{
    private static readonly ReservedMatters Reserved = ReservedMatters.Default;

    [Fact]
    public void Draft_pr_gets_no_quorum()
        => Assert.Equal(0, SeatAssignment.QuorumSize(TestElectors.Pr(draft: true), Reserved));

    [Theory]
    [InlineData(3, 40, 1)]      // 小改动单跑
    [InlineData(5, 120, 2)]     // 中等改动跑两遍
    [InlineData(5, 600, 3)]     // 大改动跑三遍
    [InlineData(25, 40, 3)]     // 文件多也算大
    public void Quorum_scales_with_diff_size(int files, int lines, int expected)
        => Assert.Equal(expected, SeatAssignment.QuorumSize(
            TestElectors.Pr(files: files, lines: lines), Reserved));

    [Theory]
    [InlineData("src/payment-center/Charge.cs")]
    [InlineData("src/Auth/TokenService.cs")]
    [InlineData("db/migrations/001_init.sql")]
    [InlineData("api/openapi.yaml")]
    [InlineData("src/Api/appsettings.Production.json")]
    public void Reserved_matters_force_full_quorum_even_for_tiny_diffs(string path)
        => Assert.Equal(3, SeatAssignment.QuorumSize(
            TestElectors.Pr(files: 1, lines: 2, paths: [path]), Reserved));

    [Fact]
    public void Ordinary_path_does_not_trip_reserved_matters()
        => Assert.Equal(1, SeatAssignment.QuorumSize(
            TestElectors.Pr(files: 1, lines: 2, paths: ["src/Web/HomeController.cs"]), Reserved));

    [Fact]
    public void An_author_can_never_review_their_own_pr()
    {
        var pr = TestElectors.Pr(author: "LIONMAIL\\youngsun");
        var author = TestElectors.Make("n1", azIdentity: "youngsun");

        Assert.False(SeatAssignment.Eligible(author, pr, TestElectors.Now));
        Assert.Empty(SeatAssignment.Seats(pr.ToRevision(), pr, [author], Reserved, TestElectors.Now));
    }

    [Fact]
    public void Node_without_the_repo_cloned_is_not_eligible()
    {
        var pr = TestElectors.Pr();
        var noClone = TestElectors.Make("n1", repos: ["some-other-repo"]);

        Assert.False(SeatAssignment.Eligible(noClone, pr, TestElectors.Now));
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
        var pr = TestElectors.Pr(files: 30, lines: 900);   // quorum = 3
        var mesh = Enumerable.Range(1, 5).Select(i => TestElectors.Make($"n{i}")).ToList();

        var seats = SeatAssignment.Seats(pr.ToRevision(), pr, mesh, Reserved, TestElectors.Now);

        Assert.Equal(3, seats.Count);
        Assert.Equal(3, seats.Distinct().Count());
    }

    [Fact]
    public void Every_node_computes_the_same_seat_table()
    {
        var pr = TestElectors.Pr(files: 30, lines: 900);
        var mesh = Enumerable.Range(1, 5).Select(i => TestElectors.Make($"n{i}")).ToList();

        var a = SeatAssignment.Seats(pr.ToRevision(), pr, mesh, Reserved, TestElectors.Now);
        var b = SeatAssignment.Seats(pr.ToRevision(), pr, mesh.AsEnumerable().Reverse(), Reserved, TestElectors.Now);

        // 席位表必须与成员枚举顺序无关，否则各节点会算出不同结果。
        Assert.Equal(a, b);
    }

    [Fact]
    public void Seats_degrade_when_there_are_not_enough_eligible_nodes()
    {
        var pr = TestElectors.Pr(files: 30, lines: 900);   // 要 3 个
        var mesh = new[] { TestElectors.Make("n1"), TestElectors.Make("n2") };

        var seats = SeatAssignment.Seats(pr.ToRevision(), pr, mesh, Reserved, TestElectors.Now);

        // 返回的席位少于 quorum —— 调用方据此在 Promulgation 上标 degraded。
        Assert.Equal(2, seats.Count);
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
}

using Conclave.Domain;

namespace Conclave.UnitTests;

/// <summary>
/// 评审积分的计分函数。
/// </summary>
/// <remarks>
/// <para>
/// 这一组钉的不是某个具体分数，而是几条<b>不能被后来的调参破坏</b>的性质：
/// 没评完不得分、难度单调但很快变平、抓到问题的加成有界且严格单调。
/// 权重改了这些用例仍然该过；过不了说明改的不是权重而是性质。
/// </para>
/// </remarks>
public sealed class PointsProjectionTests
{
    [Fact]
    public void An_error_ballot_scores_nothing()
    {
        // 没评成就是没评成 —— 它让出席位由别人重试，那是调度的事，不是贡献。
        Assert.Equal(0, PointsProjection.Score(ReviewDecision.Error, 20, 3, 5, 5));
    }

    [Fact]
    public void Reviewing_a_clean_pr_still_earns_the_base()
    {
        // 读完 diff 确认没问题也是活。基础分不看结论，只看有没有真的评完。
        var clean = PointsProjection.Score(ReviewDecision.Approve, 1, 0, 0, 0);
        Assert.True(clean > 0, "干净的 PR 也该有基础分");
        Assert.Equal(PointsProjection.Base * PointsProjection.Difficulty(1), clean, 6);
    }

    [Theory]
    [InlineData(1, 1.25)]
    [InlineData(3, 1.50)]
    [InlineData(7, 1.75)]
    [InlineData(15, 2.00)]
    public void Difficulty_matches_the_documented_anchors(int files, double expected)
        => Assert.Equal(expected, PointsProjection.Difficulty(files), 6);

    [Fact]
    public void Difficulty_rises_but_flattens_fast()
    {
        // 单调上升：大改动该值更多分。
        Assert.True(PointsProjection.Difficulty(100) > PointsProjection.Difficulty(20));
        Assert.True(PointsProjection.Difficulty(20) > PointsProjection.Difficulty(1));

        // 但远不是线性：100 个文件不该顶 100 个单文件 PR，否则没人认领小 PR。
        Assert.True(PointsProjection.Difficulty(100) < 3 * PointsProjection.Difficulty(1));
    }

    [Fact]
    public void Difficulty_tolerates_a_missing_file_count()
    {
        // 老 Ballot 没带 PR 快照时 FilesChanged 是 0，不能因此算出负分或 NaN。
        Assert.Equal(1.0, PointsProjection.Difficulty(0), 6);
        Assert.Equal(1.0, PointsProjection.Difficulty(-5), 6);
    }

    [Fact]
    public void The_catch_bonus_never_reaches_the_base()
    {
        // 有界：堆到荒谬的数量也不可能靠 finding 让一次评审翻倍以上。
        var huge = PointsProjection.Catch(critical: 50, major: 50, minor: 50, ceiling: 12.5);
        Assert.True(huge < 12.5, $"实际 {huge}");
    }

    [Fact]
    public void The_catch_bonus_is_strictly_monotonic()
    {
        const double ceiling = 12.5;

        // 多抓一条一定更高。
        var one = PointsProjection.Catch(0, 1, 0, ceiling);
        var two = PointsProjection.Catch(0, 2, 0, ceiling);
        Assert.True(two > one);

        // 把一条 minor 换成 critical 也一定更高。
        Assert.True(PointsProjection.Catch(1, 0, 0, ceiling) > PointsProjection.Catch(0, 0, 1, ceiling));
    }

    [Fact]
    public void No_findings_means_no_bonus()
        => Assert.Equal(0, PointsProjection.Catch(0, 0, 0, ceiling: 12.5));

    /// <summary>
    /// 回归：硬截断会把不同的评审压成同一个分。
    /// </summary>
    /// <remarks>
    /// 第一版写的是 <c>Math.Min(raw, base)</c>，拿真实链数据一跑，2879（2 个 critical）、
    /// 2880（4 个 major）、2878（1 critical + 2 major）三次评审都顶到上限，
    /// 得分一模一样 25.0。封顶一旦经常触发就不再是护栏，而是把最该区分的那段信息剪平。
    /// </remarks>
    [Fact]
    public void Three_different_finding_mixes_do_not_collapse_to_one_score()
    {
        var a = PointsProjection.Score(ReviewDecision.Reject, 1, critical: 2, major: 2, minor: 1);
        var b = PointsProjection.Score(ReviewDecision.Reject, 1, critical: 1, major: 2, minor: 2);
        var c = PointsProjection.Score(ReviewDecision.WaitForAuthor, 1, critical: 0, major: 4, minor: 3);

        Assert.Equal(3, new[] { a, b, c }.Select(x => Math.Round(x, 1)).Distinct().Count());

        // 而且顺序要跟直觉一致：critical 更多的那次更高。
        Assert.True(a > b, $"a={a} b={b}");
        Assert.True(b > c, $"b={b} c={c}");
    }

    [Fact]
    public void A_harder_pr_outscores_an_easier_one_with_the_same_findings()
    {
        var big = PointsProjection.Score(ReviewDecision.Reject, 30, 1, 1, 1);
        var small = PointsProjection.Score(ReviewDecision.Reject, 1, 1, 1, 1);
        Assert.True(big > small, $"big={big} small={small}");
    }
}

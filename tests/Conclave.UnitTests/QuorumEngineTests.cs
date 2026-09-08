using Conclave.Domain;

namespace Conclave.UnitTests;

public class QuorumEngineTests
{
    private static BallotPayload Ballot(int round, ReviewDecision decision, params Finding[] findings)
        => new("2721@dc1d1d47", round, decision, findings, "claude-opus-5", 1000);

    private static Finding F(string file, int line, Severity sev = Severity.Major, string title = "t")
        => new(file, line, sev, title, "detail");

    [Fact]
    public void Majority_wins()
    {
        var result = QuorumEngine.Merge("r", [
            Ballot(0, ReviewDecision.Reject),
            Ballot(1, ReviewDecision.Reject),
            Ballot(2, ReviewDecision.Approve),
        ], 3);

        Assert.Equal(ReviewDecision.Reject, result.Decision);
        Assert.False(result.Degraded);
    }

    [Fact]
    public void Tie_breaks_toward_the_more_conservative_decision()
    {
        var result = QuorumEngine.Merge("r", [
            Ballot(0, ReviewDecision.Approve),
            Ballot(1, ReviewDecision.Reject),
        ], 2);

        // 平票时宁可拦下来让人看一眼，也不要放过。
        Assert.Equal(ReviewDecision.Reject, result.Decision);
    }

    [Fact]
    public void Findings_mentioned_by_every_node_get_full_confidence()
    {
        var result = QuorumEngine.Merge("r", [
            Ballot(0, ReviewDecision.Reject, F("src/A.cs", 12), F("src/B.cs", 30)),
            Ballot(1, ReviewDecision.Reject, F("src/A.cs", 14)),
            Ballot(2, ReviewDecision.Reject, F("src/A.cs", 11)),
        ], 3);

        var top = result.Findings[0];

        // A.cs 三个节点都提到了（行号 11/12/14 落在同一个 10 行分桶）→ 置信度 1.0，排最前。
        Assert.Equal("src/A.cs", top.Best.File);
        Assert.Equal(3, top.Mentions);
        Assert.Equal(1.0, top.Confidence, 3);

        // B.cs 只有一个节点提到 → 置信度 1/3，排后面。
        var lonely = result.Findings.Single(f => f.Best.File == "src/B.cs");
        Assert.Equal(1, lonely.Mentions);
        Assert.Equal(1.0 / 3, lonely.Confidence, 3);
        Assert.Same(top, result.Findings[0]);
        Assert.Same(lonely, result.Findings[^1]);
    }

    [Fact]
    public void Line_bucketing_does_not_merge_findings_that_are_far_apart()
    {
        var result = QuorumEngine.Merge("r", [
            Ballot(0, ReviewDecision.Reject, F("src/A.cs", 12)),
            Ballot(1, ReviewDecision.Reject, F("src/A.cs", 480)),
        ], 2);

        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(1, f.Mentions));
    }

    [Fact]
    public void Highest_severity_wins_within_a_merged_group()
    {
        var result = QuorumEngine.Merge("r", [
            Ballot(0, ReviewDecision.Reject, F("src/A.cs", 10, Severity.Minor, "minor take")),
            Ballot(1, ReviewDecision.Reject, F("src/A.cs", 11, Severity.Critical, "critical take")),
        ], 2);

        Assert.Equal(Severity.Critical, result.Findings[0].Best.Severity);
        Assert.Equal("critical take", result.Findings[0].Best.Title);
    }

    [Fact]
    public void Error_ballots_count_toward_the_denominator_but_not_the_majority()
    {
        var result = QuorumEngine.Merge("r", [
            Ballot(0, ReviewDecision.Approve),
            Ballot(1, ReviewDecision.Error),
            Ballot(2, ReviewDecision.Error),
        ], 3);

        // 两票执行失败，唯一有效票是 approve —— 结论就是 approve，但票数记 3。
        Assert.Equal(ReviewDecision.Approve, result.Decision);
        Assert.Equal(3, result.ActualQuorum);
        Assert.False(result.Degraded);
    }

    [Fact]
    public void All_errors_yield_an_error_verdict_marked_degraded()
    {
        var result = QuorumEngine.Merge("r", [Ballot(0, ReviewDecision.Error)], 3);

        Assert.Equal(ReviewDecision.Error, result.Decision);
        Assert.True(result.Degraded);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Missing_ballots_mark_the_result_degraded()
    {
        var result = QuorumEngine.Merge("r", [Ballot(0, ReviewDecision.Approve)], expectedQuorum: 3);

        // 只跑了 1 个节点却期望 3 个 —— 链上必须留痕，不能假装跑满了。
        Assert.True(result.Degraded);
        Assert.Equal(1, result.ActualQuorum);
        Assert.Equal(3, result.ExpectedQuorum);
    }

    [Fact]
    public void File_paths_are_compared_case_and_separator_insensitively()
    {
        var result = QuorumEngine.Merge("r", [
            Ballot(0, ReviewDecision.Reject, F("src/A.cs", 10)),
            Ballot(1, ReviewDecision.Reject, F("src\\a.cs", 12)),
        ], 2);

        Assert.Single(result.Findings);
        Assert.Equal(2, result.Findings[0].Mentions);
    }
}

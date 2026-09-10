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
    public void Findings_far_apart_in_the_same_file_are_not_merged()
    {
        var result = QuorumEngine.Merge("r", [
            Ballot(0, ReviewDecision.Reject, F("src/A.cs", 12)),
            Ballot(1, ReviewDecision.Reject, F("src/A.cs", 480)),
        ], 2);

        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(1, f.Mentions));
    }

    [Fact]
    public void Two_findings_from_the_same_ballot_are_never_merged()
    {
        // 首次真实评审的原始数据：一票 4 条 finding，行号 9 / 13 / 17 / 13。
        // 旧的 line/10 分桶把 13、17、13 全塞进同一个桶，4 条真实问题只剩 2 条。
        var result = QuorumEngine.Merge("r", [
            Ballot(0, ReviewDecision.Reject,
                F("scripts/retry.sh", 9, Severity.Critical, "只传次数不传命令时返回 0"),
                F("scripts/retry.sh", 13, Severity.Major, "最后一次失败后仍白等一轮退避"),
                F("scripts/retry.sh", 17, Severity.Major, "退出码被压成 1"),
                F("scripts/retry.sh", 13, Severity.Minor, "退避无上限且无 jitter")),
        ], 1);

        // 同一个节点报的两条 finding 必然是两个不同的问题，哪怕挨得很近。
        Assert.Equal(4, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(1, f.Mentions));
        Assert.All(result.Findings, f => Assert.Equal(1.0, f.Confidence, 3));
        Assert.Equal(Severity.Critical, result.Findings[0].Best.Severity);
    }

    [Fact]
    public void Adjacent_findings_from_different_ballots_still_merge()
    {
        var result = QuorumEngine.Merge("r", [
            Ballot(0, ReviewDecision.Reject, F("scripts/retry.sh", 13), F("scripts/retry.sh", 17)),
            Ballot(1, ReviewDecision.Reject, F("scripts/retry.sh", 14), F("scripts/retry.sh", 18)),
        ], 2);

        // 两票各报两条、两两对应 → 合成两条，各 2 次提及。
        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(2, f.Mentions));
    }

    [Fact]
    public void Tolerance_is_relative_to_the_anchor_not_an_arbitrary_bucket()
    {
        // 9 和 13 差 4，在容差内 → 该合并。旧的 line/10 分桶会把它们分到 0 和 1 桶。
        var merged = QuorumEngine.Merge("r", [
            Ballot(0, ReviewDecision.Reject, F("a.cs", 9)),
            Ballot(1, ReviewDecision.Reject, F("a.cs", 13)),
        ], 2);
        Assert.Single(merged.Findings);
        Assert.Equal(2, merged.Findings[0].Mentions);

        // 10 和 19 差 9，超出容差 → 不该合并。旧分桶里 10 和 19 同属 1 桶。
        var split = QuorumEngine.Merge("r", [
            Ballot(0, ReviewDecision.Reject, F("a.cs", 10)),
            Ballot(1, ReviewDecision.Reject, F("a.cs", 19)),
        ], 2);
        Assert.Equal(2, split.Findings.Count);
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

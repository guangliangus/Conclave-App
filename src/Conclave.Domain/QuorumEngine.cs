namespace Conclave.Domain;

/// <summary>
/// 把多个节点的 Ballot 合并成一个结论。
/// </summary>
/// <remarks>
/// 这是引入 quorum 的全部回报：单跑一次 Claude，findings 真假参半；三跑取交集，
/// 被全体独立提到的那些基本不用人工复核。
/// </remarks>
public static class QuorumEngine
{
    /// <summary>行号分桶宽度。两个节点对同一问题报的行号常有几行偏差，硬按行号分组会拆开。</summary>
    private const int LineBucket = 10;

    public static PromulgationPayload Merge(
        string revisionId,
        IReadOnlyList<BallotPayload> ballots,
        int expectedQuorum)
    {
        ArgumentNullException.ThrowIfNull(revisionId);
        ArgumentNullException.ThrowIfNull(ballots);

        // Error 票计入 quorum 分母（说明这一轮确实跑过），但不参与多数决也不贡献 finding。
        var valid = ballots.Where(b => b.Decision.CountsTowardMajority()).ToList();

        if (valid.Count == 0)
        {
            return new PromulgationPayload(
                revisionId,
                ReviewDecision.Error,
                [],
                Degraded: true,
                ActualQuorum: ballots.Count,
                ExpectedQuorum: expectedQuorum);
        }

        var decision = valid
            .GroupBy(b => b.Decision)
            .OrderByDescending(g => g.Count())
            // 平票取更保守的一方：Reject(-10) < WaitForAuthor(-5) < Approve(10)
            .ThenBy(g => (int)g.Key)
            .First()
            .Key;

        var findings = valid
            .SelectMany(b => b.Findings.Select(f => (Ballot: b, Finding: f)))
            .GroupBy(x => (
                File: x.Finding.File.Replace('\\', '/').ToLowerInvariant(),
                Bucket: x.Finding.Line / LineBucket))
            .Select(g =>
            {
                var mentions = g.Select(x => x.Ballot.Round).Distinct().Count();
                var best = g.Select(x => x.Finding)
                    .OrderByDescending(f => f.Severity)
                    .ThenBy(f => f.Line)
                    .First();
                return new MergedFinding(best, mentions, mentions / (double)valid.Count);
            })
            .OrderByDescending(f => f.Confidence)
            .ThenByDescending(f => f.Best.Severity)
            .ThenBy(f => f.Best.File, StringComparer.Ordinal)
            .ThenBy(f => f.Best.Line)
            .ToList();

        return new PromulgationPayload(
            revisionId,
            decision,
            findings,
            Degraded: ballots.Count < expectedQuorum,
            ActualQuorum: ballots.Count,
            ExpectedQuorum: expectedQuorum);
    }
}

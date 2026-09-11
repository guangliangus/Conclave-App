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
    /// <summary>
    /// 认定「两个节点在说同一个问题」的行号容差。
    /// </summary>
    /// <remarks>
    /// 不同节点对同一处问题报的行号常有几行偏差，硬按行号分组会把一条拆成两条低置信度的。
    /// 用相对锚点的对称容差而不是 <c>line / 10</c> 分桶：分桶的边界是任意的 ——
    /// 实测里 9 和 13 差 4 却分属不同桶，13 和 17 差 4 却同桶。
    /// </remarks>
    private const int LineTolerance = 5;

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

        var findings = Cluster(valid)
            .Select(c => new MergedFinding(
                c.Findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Line).First(),
                c.Rounds.Count,
                c.Rounds.Count / (double)valid.Count))
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
            ExpectedQuorum: expectedQuorum)
        {
            // 恰好一票才把原文带出去。N 票时「原样投递」没有唯一答案，而且合并渲染
            // 在那种情况下本来就更有价值（它说的是「几个节点独立提到了同一条」）。
            //
            // 空白也归成 null：出票那侧已经归一过（ClaudeReviewRunner.CapComment），
            // 这里再挡一次是因为<b>这个值要上链</b> —— 让链上存一串空白，读的人就得
            // 自己判「这是没有评论，还是评论真的是空白」。不变量就一条：
            // Comment 要么是 null，要么是真有内容。
            Comment = valid.Count == 1 && !string.IsNullOrWhiteSpace(valid[0].Comment)
                ? valid[0].Comment
                : null,
        };
    }

    /// <summary>
    /// 把各节点报的 finding 贪心聚成簇：同文件、行号在容差内、且该簇尚未收过这一轮的 finding。
    /// </summary>
    /// <remarks>
    /// 「尚未收过这一轮」是关键约束。同一个节点报的两条 finding 必然是两个不同的问题，
    /// 哪怕挨得很近 —— 实测一票里 <c>retry.sh:13</c> 和 <c>retry.sh:17</c> 是两回事，
    /// 少了这条约束会把 4 条真实问题吞成 2 条。
    /// </remarks>
    private static List<FindingCluster> Cluster(IReadOnlyList<BallotPayload> valid)
    {
        var clusters = new List<FindingCluster>();

        foreach (var ballot in valid.OrderBy(b => b.Round))
        {
            foreach (var finding in ballot.Findings)
            {
                var file = NormalizePath(finding.File);
                var match = clusters.FirstOrDefault(c =>
                    c.File == file
                    && Math.Abs(c.AnchorLine - finding.Line) <= LineTolerance
                    && !c.Rounds.Contains(ballot.Round));

                if (match is null)
                {
                    clusters.Add(new FindingCluster(file, finding.Line, ballot.Round, finding));
                }
                else
                {
                    match.Add(ballot.Round, finding);
                }
            }
        }

        return clusters;
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').ToLowerInvariant();

    private sealed class FindingCluster
    {
        internal FindingCluster(string file, int anchorLine, int round, Finding first)
        {
            File = file;
            AnchorLine = anchorLine;
            Rounds = [round];
            Findings = [first];
        }

        internal string File { get; }

        /// <summary>簇的锚点行 = 第一条落进来的 finding 的行号。</summary>
        internal int AnchorLine { get; }

        internal HashSet<int> Rounds { get; }

        internal List<Finding> Findings { get; }

        internal void Add(int round, Finding finding)
        {
            _ = Rounds.Add(round);
            Findings.Add(finding);
        }
    }
}

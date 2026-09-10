namespace Conclave.Domain;

/// <summary>
/// 把一条链的区块序列折叠成 <see cref="ChainState"/>。纯函数，方便单测。
/// </summary>
/// <remarks>
/// 全局单链上叠着所有 PR 所有版本的记录，所以投影必须按 <c>revisionId</c> 过滤 ——
/// 否则上一版的结论会让新版本被当成「已评审」。
/// </remarks>
public static class ActaProjection
{
    public static ChainState Project(IEnumerable<Block> blocks, string revisionId)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(revisionId);

        PromulgationPayload? promulgation = null;
        var ballots = new Dictionary<int, BallotPayload>();

        foreach (var block in blocks.OrderBy(b => b.Index))
        {
            switch (block.Kind)
            {
                case BlockKind.Ballot:
                    var ballot = block.Payload<BallotPayload>();
                    if (ballot?.RevisionId == revisionId)
                    {
                        ballots[ballot.Round] = ballot;
                    }

                    break;

                case BlockKind.Promulgation:
                    var p = block.Payload<PromulgationPayload>();
                    if (p?.RevisionId == revisionId)
                    {
                        promulgation = p;
                    }

                    break;

                default:
                    // Summons / Seating / Recess 已不再写入，老区块读到就跳过。
                    break;
            }
        }

        return new ChainState(ballots, promulgation);
    }
}

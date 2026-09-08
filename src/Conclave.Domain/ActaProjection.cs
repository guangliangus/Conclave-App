namespace Conclave.Domain;

/// <summary>
/// 一条链上某个 Revision 的当前状态。
/// </summary>
/// <param name="Summons">该 Revision 的召集块；null 表示链上还没有它。</param>
/// <param name="Seatings">已认领的席位，按 round 索引。</param>
/// <param name="SeatedAt">各 round 的认领时刻，按 round 索引。用于判定席位超时。</param>
/// <param name="Ballots">已投出的票，按 round 索引。</param>
/// <param name="Recessed">已弃权的 round。</param>
/// <param name="Promulgation">最终结论；非 null 表示这个 Revision 评审完毕。</param>
public sealed record ChainState(
    SummonsPayload? Summons,
    IReadOnlyDictionary<int, SeatingPayload> Seatings,
    IReadOnlyDictionary<int, DateTimeOffset> SeatedAt,
    IReadOnlyDictionary<int, BallotPayload> Ballots,
    IReadOnlyList<int> Recessed,
    PromulgationPayload? Promulgation)
{
    public static ChainState Empty { get; } =
        new(
            null,
            new Dictionary<int, SeatingPayload>(),
            new Dictionary<int, DateTimeOffset>(),
            new Dictionary<int, BallotPayload>(),
            [],
            null);

    public bool IsFinished => Promulgation is not null;

    public int Quorum => Summons?.Quorum ?? 0;

    /// <summary>票数够了（或席位已用尽）可以公布。</summary>
    public bool CanPromulgate => Summons is not null
        && Promulgation is null
        && Ballots.Count > 0
        && Ballots.Count + Recessed.Count >= Quorum;
}

/// <summary>
/// 把一条链的区块序列折叠成 <see cref="ChainState"/>。纯函数，方便单测。
/// </summary>
/// <remarks>
/// 一条链（一个 PR）上可能叠着同一个 PR 多个版本的评审记录，所以投影必须按
/// <c>revisionId</c> 过滤 —— 否则上一版的 Promulgation 会让新版本被当成「已评审」。
/// </remarks>
public static class ActaProjection
{
    public static ChainState Project(IEnumerable<Block> blocks, string revisionId)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(revisionId);

        SummonsPayload? summons = null;
        PromulgationPayload? promulgation = null;
        var seatings = new Dictionary<int, SeatingPayload>();
        var seatedAt = new Dictionary<int, DateTimeOffset>();
        var ballots = new Dictionary<int, BallotPayload>();
        var recessed = new List<int>();

        foreach (var block in blocks.OrderBy(b => b.Index))
        {
            switch (block.Kind)
            {
                case BlockKind.Summons:
                    var s = block.Payload<SummonsPayload>();
                    if (s?.Revision.Id == revisionId)
                    {
                        summons = s;
                    }

                    break;

                case BlockKind.Seating:
                    var seat = block.Payload<SeatingPayload>();
                    if (seat?.RevisionId == revisionId)
                    {
                        // 先到先得：同 round 的重复认领以链上第一个为准。
                        if (seatings.TryAdd(seat.Round, seat))
                        {
                            seatedAt[seat.Round] = block.At;
                        }
                    }

                    break;

                case BlockKind.Ballot:
                    var ballot = block.Payload<BallotPayload>();
                    if (ballot?.RevisionId == revisionId)
                    {
                        ballots[ballot.Round] = ballot;
                    }

                    break;

                case BlockKind.Recess:
                    var recess = block.Payload<RecessPayload>();
                    if (recess?.RevisionId == revisionId && !recessed.Contains(recess.Round))
                    {
                        recessed.Add(recess.Round);
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
                    break;
            }
        }

        return new ChainState(summons, seatings, seatedAt, ballots, recessed, promulgation);
    }

    /// <summary>取出链上出现过的所有 Revision，按链上顺序。</summary>
    public static IReadOnlyList<Revision> RevisionsOf(IEnumerable<Block> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        return blocks
            .Where(b => b.Kind == BlockKind.Summons)
            .OrderBy(b => b.Index)
            .Select(b => b.Payload<SummonsPayload>()?.Revision)
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();
    }
}

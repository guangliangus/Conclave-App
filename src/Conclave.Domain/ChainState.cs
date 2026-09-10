namespace Conclave.Domain;

/// <summary>
/// 链上某个 Revision 的已完成部分。
/// </summary>
/// <remarks>
/// 链只记<b>已经完成</b>的评审，所以这里只有票和最终结论 —— 「队列里有什么、谁正在评」
/// 是实时状态（<see cref="LiveState"/>），不上链。
/// <para>
/// 原先这个类型还带 <c>Summons</c> / <c>Seatings</c> / <c>SeatedAt</c> / <c>Recessed</c>
/// 四组字段，随那三种块停写一起去掉了：席位与弃权改由心跳驱动，节点掉线后它的
/// <see cref="ActiveReview"/> 随心跳窗口过期而消失，PR 自动回队列。
/// </para>
/// </remarks>
/// <param name="Ballots">已投出的票，按 round 索引。</param>
/// <param name="Promulgation">最终结论；非 null 表示这个 Revision 评审完毕。</param>
public sealed record ChainState(
    IReadOnlyDictionary<int, BallotPayload> Ballots,
    PromulgationPayload? Promulgation)
{
    public static ChainState Empty { get; } = new(new Dictionary<int, BallotPayload>(), null);

    public bool IsFinished => Promulgation is not null;

    /// <summary>真正表达了评审意见的票。Error 票不算。</summary>
    public IReadOnlyList<BallotPayload> ValidBallots =>
        [.. Ballots.OrderBy(kv => kv.Key)
                   .Select(kv => kv.Value)
                   .Where(b => b.Decision.CountsTowardMajority())];

    /// <summary>
    /// 已经烧掉的轮次 —— 出过票就算，无论是有效票还是 Error 票。
    /// </summary>
    /// <remarks>
    /// Error 票只说明那一轮跑失败了，不能把一次偶发的子进程失败变成这个 PR 的最终结论；
    /// 它让出席位、由别的节点重试，但那一轮确实烧掉了，要计入重试预算。
    /// </remarks>
    public IReadOnlyList<int> SpentRounds => [.. Ballots.Keys.Order()];

    /// <summary>某一轮已经有结果了，不该再有人去坐。</summary>
    public bool IsRoundSettled(int round) => Ballots.ContainsKey(round);

    /// <summary>
    /// 票收够了可以公布。
    /// </summary>
    /// <param name="quorum">应有席位数，取自实时状态里发现节点带来的那个数。</param>
    /// <param name="maxAttempts">
    /// 一个 revision 最多烧掉几个席位。到顶之后就拿手上的票收尾（可能一张有效票都没有，
    /// 那就是个 degraded 的 Error 结论）—— 否则「claude 每次都起不来」会无限重试下去。
    /// </param>
    public bool CanPromulgate(int quorum, int maxAttempts)
        => Promulgation is null
        && (ValidBallots.Count >= Math.Max(1, quorum)
            || (SpentRounds.Count >= Math.Max(1, maxAttempts) && Ballots.Count > 0));
}

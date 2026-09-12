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

    /// <summary>
    /// 真的评完了 —— 有结论，而且不是失败收尾。
    /// </summary>
    /// <remarks>
    /// <see cref="IsProvisional"/> 那种不算：它只是「试过 N 个节点都没跑起来」的留痕，
    /// PR 本身一个字都还没被评过。
    /// </remarks>
    public bool IsFinished => Promulgation is not null && !IsProvisional;

    /// <summary>
    /// 暂定结论 —— 一张有效票都没有，纯粹是执行失败的收尾。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Decision == Error</c> 等价于「没有有效票」：Error 票不参与多数决
    /// （<c>ReviewDecision.CountsTowardMajority</c>），所以合并器只有在
    /// <c>valid.Count == 0</c> 那条早返回里才会给出 Error 结论。
    /// </para>
    /// <para>
    /// 它<b>不终局</b>：这一版回到队列，等还没试过的节点接手，有效票一出就把它覆盖掉
    /// （链上是第二个 Promulgation 块，投影取最后一个）。失败的原因大多不在这份代码上 ——
    /// claude 没额度、用户退出登录、az 连不上、token 到期 —— 换台机器就好了。
    /// </para>
    /// </remarks>
    public bool IsProvisional => Promulgation is { Decision: ReviewDecision.Error };

    /// <summary>真正表达了评审意见的票。Error 票不算。</summary>
    public IReadOnlyList<BallotPayload> ValidBallots =>
        [.. Ballots.OrderBy(kv => kv.Key)
                   .Select(kv => kv.Value)
                   .Where(b => b.Decision.CountsTowardMajority())];

    /// <summary>
    /// 出过票的评审者，az 身份，按 round 排。
    /// </summary>
    /// <remarks>
    /// <b>含 Error 票的出票者</b> —— 那一轮确实有人跑过，只是没产出，而「谁评的」
    /// 问的正是这个。<see cref="ValidBallots"/> 答的是另一个问题（谁的意见进了结论）。
    /// 没有 az 身份的老票跳过：填不出名字就别填，公布块上少一个人比多一个空串强。
    /// </remarks>
    public IReadOnlyList<string> Reviewers =>
        [.. Ballots.OrderBy(kv => kv.Key)
                   .Select(kv => kv.Value.ReviewerAz)
                   .Where(az => !string.IsNullOrWhiteSpace(az))
                   .Select(az => az!)];

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
    /// 已经出现过确定性失败。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>它不再砍掉重试预算</b>（2026-09-12）。原先一张确定性失败票就一票否决整个
    /// 预算，理由是「同一份输入在同一台机器上再跑一遍只会得到同一个结果」—— 而现在
    /// 出错的节点本来就不会被再抽到（<c>ConclaveOptions.RetryOnSameNode</c>），
    /// 每一轮都换一台机器，skill 又是各机器各自装的。也就是说原注释里那句
    /// 「理论上换一台可能就有能出契约块的版本」现在是重试的常态路径，不再是空谈。
    /// 代价照旧要认：每多一个没试过的节点就多烧一轮完整额度，上限由
    /// <c>MaxReviewAttempts</c> 管。
    /// </remarks>
    /// <remarks>
    /// <para>
    /// 见 <see cref="BallotPayload.Retryable"/>：claude 把评审跑完了、token 也烧光了，
    /// 只是最后那个机器可读的契约块没出来。再抽一个席位跑同样的输入，只会得到同样的结果，
    /// 而每一轮都是完整的账单 —— 实测一个 PR 两轮 121 万 token、$2.79，产出为零。
    /// </para>
    /// <para>
    /// ⚠️ <b>取舍写在这里。</b> skill 是各机器各自装的，所以理论上换一台可能就有能出契约块
    /// 的版本。但这里仍然一票否决整个重试预算：契约是 runner 与 skill 这一对的约定，
    /// 整个 mesh 本来就该保持同步（版本不齐在节点表和通知里是单独说出来的），
    /// 而赌「下一台可能不一样」的代价是每个 PR 多烧两轮完整额度。
    /// 判错的代价则只是提前拿到一个 degraded 的 Error 结论 —— 人看到原因后重跑即可。
    /// </para>
    /// </remarks>
    public bool HasFatalError => Ballots.Values.Any(b => b.IsFatal);

    /// <summary>
    /// 票收够了可以公布。
    /// </summary>
    /// <param name="quorum">应有席位数，取自实时状态里发现节点带来的那个数。</param>
    /// <param name="maxAttempts">
    /// 一个 revision 最多让<b>几个不同节点</b>试。到顶之后就拿手上的票收尾（可能一张
    /// 有效票都没有，那就是个 degraded 的 Error 结论，见 <see cref="IsProvisional"/>），
    /// 并且不再分配席位 —— 否则「claude 在谁那儿都起不来」会一直排下去。
    /// <para>
    /// 「几个节点」而不是「几轮」：出错的节点不再被抽到第二次
    /// （<c>ConclaveOptions.RetryOnSameNode</c>），所以每一轮都落在一台新机器上。
    /// </para>
    /// </param>
    /// <param name="nobodyLeftToTry">
    /// 已经没有「没试过这一版的合格节点」了（见 <see cref="SeatAssignment.CouldEverReview"/>）。
    /// 由调用方从实时成员表算好传进来 —— 领域层不认识 mesh。
    /// </param>
    /// <remarks>
    /// 已经有暂定结论时<b>只有真的收够票才覆盖</b>。少了这个条件，「预算到顶」那条会在
    /// 每一轮编排里再算一次真，于是同一个 Error 结论每 15 秒往链上写一块。
    /// </remarks>
    public bool CanPromulgate(int quorum, int maxAttempts, bool nobodyLeftToTry = false)
    {
        var enough = ValidBallots.Count >= Math.Max(1, quorum);

        if (Promulgation is not null)
        {
            return IsProvisional && enough;
        }

        if (enough)
        {
            return true;
        }

        if (Ballots.Count == 0)
        {
            return false;
        }

        // 失败收尾的两条路：试满了上限，或者已经没有没试过的机器了
        // （单节点 mesh 上一次失败就是后者 —— 少了它这个 PR 会永远挂在队列上）。
        //
        // 后一条<b>只在一张有效票都没有时</b>才算数：quorum≥2 时第二个节点临时掉线，
        // 不该把手上那一张有效票提前收成降级结论 —— 它回来还能补票。
        return SpentRounds.Count >= Math.Max(1, maxAttempts)
            || (nobodyLeftToTry && ValidBallots.Count == 0);
    }
}

namespace Conclave.Domain;

/// <summary>
/// 队列里一个 PR 版本的当前处境：谁在评、谁认领了、链上有几票。
/// </summary>
/// <param name="Item">发现节点上报的那一项（含 PR 快照与 quorum）。</param>
/// <param name="ReviewingBy">正在评它的节点；null 表示没人在评。</param>
/// <param name="Round">正在评的是第几轮。</param>
/// <param name="StartedAt">开始评的时刻，用于在界面上显示「评了多久」。</param>
/// <param name="ClaimedBy">认领了但还没开跑的节点。</param>
/// <param name="Ballots">链上已有的有效票数。</param>
/// <param name="Finished">链上已有最终结论。</param>
public sealed record QueueEntry(
    QueuedRevision Item,
    string? ReviewingBy,
    int Round,
    DateTimeOffset? StartedAt,
    string? ClaimedBy,
    int Ballots,
    bool Finished)
{
    public Revision Revision => Item.Revision;

    public PrMeta Pr => Item.Pr;

    public int Quorum => Item.Quorum;

    /// <summary>见 <see cref="QueuedRevision.AllowSelfReview"/>。</summary>
    public bool AllowSelfReview => Item.AllowSelfReview;

    /// <summary>还需要人来评：没人正在评、没人认领、票也没收够。</summary>
    public bool NeedsReviewer
        => !Finished && ReviewingBy is null && ClaimedBy is null && Ballots < Quorum;
}

/// <summary>
/// 把各节点上报的实时状态合并成一份全局队列视图。
/// </summary>
/// <remarks>
/// <para>
/// 纯函数，所以每个节点合出同一份视图 —— 跟席位表一个道理，「队列里有什么」不需要协商。
/// </para>
/// <para>
/// <b>掉线节点的上报被整体忽略</b>，这一条就是自动接管的全部机制：节点崩了不再发心跳，
/// <see cref="Elector.HeartbeatWindow"/> 一过它的 <see cref="LiveState.Reviewing"/> 就从视图里
/// 消失，那个 PR 自动回到「需要人评」。以前这件事要靠链上的 <c>Seating</c> + 10 分钟
/// <c>SeatingTimeout</c> + <c>Recess</c> 三个东西配合，现在不用写任何块。
/// </para>
/// </remarks>
public static class QueueProjection
{
    /// <summary>
    /// <see cref="Build"/> 会考虑到的那些 revision id。
    /// </summary>
    /// <remarks>
    /// 存在的理由只有一个：<c>ballots</c> 与 <c>finished</c> 是调用方从链上读来传进来的，
    /// 而链上「已经评过的」会一直积累下去。先问清楚「这一轮到底要查哪几个」，
    /// 读账本那一步的开销就跟队列长度挂钩，而不是跟链长挂钩
    /// （见 <c>IActaStore.ReadSummaryAsync</c>）。
    /// <para>
    /// 筛选口径必须跟 <see cref="Build"/> 里那一段<b>逐字一致</b>：只看存活节点上报的
    /// <see cref="LiveState.Discovered"/>。少算一个就会让那个 PR 的票数凭空变成 0、
    /// 于是被重复评审。
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string> RevisionIds(
        IReadOnlyDictionary<string, LiveState> states, IReadOnlySet<string> alive)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(alive);

        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (electorId, state) in states)
        {
            if (!alive.Contains(electorId))
            {
                continue;
            }

            foreach (var item in state.Discovered)
            {
                _ = ids.Add(item.Revision.Id);
            }
        }

        return ids;
    }

    /// <summary>
    /// 合并视图。
    /// </summary>
    /// <param name="states">electorId → 该节点上报的实时状态（含自己）。</param>
    /// <param name="alive">仍然存活的 electorId。掉线节点的上报会被忽略。</param>
    /// <param name="ballots">revisionId → 链上已有的有效票数。</param>
    /// <param name="finished">链上已有最终结论的 revisionId。</param>
    /// <remarks>
    /// <paramref name="ballots"/> 与 <paramref name="finished"/> 由调用方从链上读出来传进来，
    /// 而不是在这里查库 —— 领域层要保持无 I/O，才能让这份合并逻辑在单测里毫无环境依赖地验证。
    /// </remarks>
    public static IReadOnlyList<QueueEntry> Build(
        IReadOnlyDictionary<string, LiveState> states,
        IReadOnlySet<string> alive,
        IReadOnlyDictionary<string, int> ballots,
        IReadOnlySet<string> finished)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(alive);
        ArgumentNullException.ThrowIfNull(ballots);
        ArgumentNullException.ThrowIfNull(finished);

        // 只看存活节点的上报。顺序按 electorId 排：同一个 revision 被多个节点上报时
        // （轮询分片短暂重叠），取哪一份必须是确定的，否则各节点的 quorum 可能不一致。
        var live = states
            .Where(kv => alive.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        var items = new Dictionary<string, QueuedRevision>(StringComparer.Ordinal);
        var reviewing = new Dictionary<string, (string By, ActiveReview R)>(StringComparer.Ordinal);
        var claims = new Dictionary<string, List<ReviewClaim>>(StringComparer.Ordinal);

        foreach (var (electorId, state) in live)
        {
            foreach (var item in state.Discovered)
            {
                _ = items.TryAdd(item.Revision.Id, item);
            }

            foreach (var r in state.Reviewing)
            {
                // 同一轮被两个节点同时声称在评（分区恢复后可能出现）：取 electorId 小者。
                // live 已按 id 排序，所以先到先得就是这个语义。
                _ = reviewing.TryAdd(r.RevisionId, (electorId, r));
            }

            foreach (var c in state.Claims)
            {
                if (!claims.TryGetValue(c.RevisionId, out var list))
                {
                    list = [];
                    claims[c.RevisionId] = list;
                }

                list.Add(c);
            }
        }

        var entries = new List<QueueEntry>(items.Count);
        foreach (var (revisionId, item) in items)
        {
            var active = reviewing.TryGetValue(revisionId, out var r) ? r : default;
            var winner = claims.TryGetValue(revisionId, out var cs) ? ReviewClaim.Winner(cs) : null;

            entries.Add(new QueueEntry(
                item,
                active.By,
                active.R?.Round ?? 0,
                active.R?.StartedAt,
                // 已经在评了就不再显示认领 —— 认领只是「打算评」，开跑之后那个状态没意义了。
                active.By is null ? winner?.ElectorId : null,
                ballots.TryGetValue(revisionId, out var n) ? n : 0,
                finished.Contains(revisionId)));
        }

        // 按 PR 号倒序：新的在上面。跟界面的排序一致，省得两处各排一次。
        return [.. entries.OrderByDescending(e => e.Revision.PrId).ThenBy(e => e.Revision.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// 一个待分配的队列项，连同分配要用的链上上下文。
    /// </summary>
    /// <param name="Entry">队列里的这一项。</param>
    /// <param name="Incumbent">同一个 PR 上一版的评审者；非空且仍空闲时优先落回它。</param>
    /// <param name="RoundsUsed">这个 revision 上已经烧掉的轮次数（链上 SpentRounds 的个数）。</param>
    /// <param name="Voted">
    /// 已经在这个 revision 上出过票的节点。它们不该再坐后续轮次 ——
    /// 正式席位必须互不重复，否则 quorum=2 会变成同一个节点评两遍、方差一点没压掉。
    /// </param>
    /// <param name="NeedsReviewer">
    /// 这一版<b>还需要有人评</b>。
    /// <para>
    /// 票收够了（等公布）、这一轮已经出过票、或者重试次数用尽的，都不该再占一个席位 ——
    /// 席位是稀缺的（一个节点只有一个），给一个不需要评审的 PR 就等于把唯一空闲的节点
    /// 挂在那儿，后面真正等着评的 PR 一个都动不了。实测过：两个 PR、一个节点，
    /// 第一个评完等公布时仍占着席位，第二个就永远排不上。
    /// </para>
    /// <para>
    /// 判定要读链（收够没有、这一轮settled 没有、重试到顶没有），所以由调用方算好传进来 ——
    /// 领域层保持无 I/O。
    /// </para>
    /// </param>
    public sealed record SeatCandidate(
        QueueEntry Entry,
        string? Incumbent = null,
        int RoundsUsed = 0,
        IReadOnlyCollection<string>? Voted = null,
        bool NeedsReviewer = true);

    /// <summary>
    /// 对整个队列做<b>一次</b>席位分配：每个节点最多拿一个席位。
    /// </summary>
    /// <param name="queue">当前队列（含链上上下文）。</param>
    /// <param name="members">当前已知的成员（含自己）。</param>
    /// <param name="now">存活判定用的「现在」，由调用方传入以保证各节点一致。</param>
    /// <returns>revisionId → 该评它的节点；没分到人的不出现在结果里。</returns>
    /// <remarks>
    /// <para>
    /// <b>为什么必须整队一起分，而不是逐个 PR 独立算。</b> 一个节点一次只跑一个评审
    /// （见 <c>ConclaveOptions.MaxConcurrent</c>），所以「同时持有 6 个席位」这件事本身
    /// 没有意义：界面上 6 行都写着「归本节点」，而它一次只能动一个，人看不出真实的顺序；
    /// 更实际的是那 6 行的「评审」「认领」按钮全都能点，点第二个只是排进本机的队列。
    /// 改成一次分配之后，每个节点最多一行是「归我」的，其余是「等有人腾出手」。
    /// </para>
    /// <para>
    /// <b>为什么这仍然不需要协商。</b> 分配的输入只有三样：队列、成员表、以及「谁被占着」——
    /// 而占用状态（<see cref="LiveState.Reviewing"/> 与 <see cref="LiveState.Claims"/>）
    /// 本来就在 mesh 里 gossip。三样输入各节点都一样，所以这仍然是纯函数、各节点算出同一份。
    /// <b>占用状态必须是可观测的</b>，这是整个改动唯一的硬要求：如果 A 只在自己内存里
    /// 记「我占着了」，B 算出来的席位表就跟 A 不一致，PR 会没人评或被评两遍。
    /// </para>
    /// <para>
    /// <b>为什么按 PR 号升序（FIFO）分。</b> 分配顺序决定了谁先被挑走，所以它必须稳定：
    /// 按升序时新来的 PR（号更大）只会排到末尾，不会把已经分好的席位抢走。
    /// 倒序就会出现「新 PR 一来，老 PR 的席位被顶掉」的抖动。
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, string> AssignSeats(
        IEnumerable<SeatCandidate> queue,
        IEnumerable<Elector> members,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(members);

        var all = queue.ToList();
        var live = members.Where(m => m.IsAlive(now)).ToList();
        var assigned = new Dictionary<string, string>(StringComparer.Ordinal);

        if (live.Count == 0 || all.Count == 0)
        {
            return assigned;
        }

        var busy = new HashSet<string>(StringComparer.Ordinal);

        // ① 正在评的：那个节点被占着，这一轮不再拿新席位。
        foreach (var c in all.Where(x => x.Entry.ReviewingBy is not null))
        {
            _ = busy.Add(c.Entry.ReviewingBy!);
        }

        // ② 认领即分配 —— 人明确要评的，抽签结果不该抢走。但认领同样要过硬规则：
        // 不加这一层的话，作者点一下「认领」就能评自己的 PR。
        foreach (var c in all.Where(Open).Where(x => x.Entry.ClaimedBy is not null))
        {
            var claimer = live.FirstOrDefault(m => m.Id == c.Entry.ClaimedBy);
            if (claimer is null
                || !SeatAssignment.Eligible(claimer, c.Entry.Pr, now, c.Entry.AllowSelfReview))
            {
                continue;
            }

            assigned[c.Entry.Revision.Id] = claimer.Id;
            _ = busy.Add(claimer.Id);
        }

        // ③ 剩下的按 FIFO 分给还空闲的节点。
        var free = live.Where(m => !busy.Contains(m.Id)).ToList();

        foreach (var c in all
            .Where(Open)
            .Where(x => x.Entry.ClaimedBy is null)
            .OrderBy(x => x.Entry.Revision.PrId)
            .ThenBy(x => x.Entry.Revision.Id, StringComparer.Ordinal))
        {
            if (free.Count == 0)
            {
                break;
            }

            // 已经出过票的节点排除掉，正式席位互不重复 —— 但<b>只在填正式席位时</b>。
            // 重试轮次（roundsUsed ≥ quorum）必须允许再抽到同一个节点，否则单节点 mesh 上
            // 一次失败就让这个 PR 永远卡住：唯一的节点已经出过（Error）票，池子空了。
            // 这条跟 SeatAssignment.Seats 里「重试轮次才重新蓄池」是同一个规则。
            var formalRound = c.RoundsUsed < c.Entry.Quorum;
            var pool = formalRound && c.Voted is { Count: > 0 } voted
                ? free.Where(m => !voted.Contains(m.Id)).ToList()
                : free;

            var seats = SeatAssignment.Seats(
                c.Entry.Revision, c.Entry.Pr, pool, c.Entry.Quorum, now,
                extraRounds: c.RoundsUsed, incumbent: c.Incumbent,
                allowSelfReview: c.Entry.AllowSelfReview);

            if (seats.Count <= c.RoundsUsed)
            {
                // 这一版没有合格且空闲的节点：留在队列里等，而不是硬塞给谁。
                continue;
            }

            var winner = seats[c.RoundsUsed];
            assigned[c.Entry.Revision.Id] = winner;
            _ = free.RemoveAll(m => m.Id == winner);
        }

        return assigned;

        static bool Open(SeatCandidate c)
            => !c.Entry.Finished && c.Entry.ReviewingBy is null && c.NeedsReviewer;
    }

    /// <summary>
    /// 本节点该不该评这个 PR，该评第几轮；不该评返回 -1。
    /// </summary>
    /// <param name="entry">队列里的这一项。</param>
    /// <param name="selfId">本节点指纹。</param>
    /// <param name="members">当前已知的成员（含自己）。</param>
    /// <param name="now">存活判定用的「现在」，由调用方传入以保证各节点一致。</param>
    /// <param name="incumbent">
    /// 同一个 PR 上一版的评审者。非空且仍合格时直接坐第一个待填席位。
    /// </param>
    /// <param name="roundsUsed">
    /// 这个 revision 上已经烧掉的轮次数（链上 <see cref="ChainState.SpentRounds"/> 的个数）。
    /// <para>
    /// <b>必须是「已烧掉」而不是「有效票数」。</b> Error 票不计入有效票，所以拿有效票数
    /// 当下标会一直指向 round 0 —— 而 round 0 已经出过（Error）票、被判定为已定局，
    /// 于是编排层既不重试也不公布，PR 永久卡住。实测踩过。
    /// </para>
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>手动认领优先于加权 HRW，但不越过硬规则。</b> 人明确要评的，抽签结果不该抢走；
    /// 而 <see cref="SeatAssignment.Eligible"/> 那几条（不评自己的 PR、有 project 权限、
    /// 额度没满、当前不在评别的 PR）认领也一样得满足。
    /// </para>
    /// <para>
    /// 没有认领时才回到 <see cref="SeatAssignment.Seats"/>：席位表是纯函数，各节点算出同一份，
    /// 于是「谁评哪个」不需要协商。<paramref name="incumbent"/> 让作者 push 修复之后仍落回
    /// 上一版的评审者。
    /// </para>
    /// </remarks>
    public static int SeatFor(
        QueueEntry entry,
        string selfId,
        IEnumerable<Elector> members,
        DateTimeOffset now,
        string? incumbent = null,
        int roundsUsed = 0)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // 只是 AssignSeats 的单项包装。两份实现必然漂移，而这条路径决定「谁评什么」，
        // 漂移的后果是 PR 没人评或被评两遍 —— 所以只留一份算法。
        // 注意：单项调用看不到「本节点正被别的 PR 占着」，那要整队一起分才知道。
        // 编排循环走的是 AssignSeats；这个重载给单个 PR 的推理与测试用。
        var assigned = AssignSeats(
            [new SeatCandidate(entry, incumbent, roundsUsed)], members, now);

        return assigned.TryGetValue(entry.Revision.Id, out var who) && who == selfId
            ? roundsUsed
            : -1;
    }
}

namespace Conclave.Domain;

/// <summary>
/// 席位分配规则：硬规则做过滤，软规则做权重。见 docs/DESIGN.md §7。
/// </summary>
/// <remarks>
/// 这里每一个方法都必须是纯函数。任何一处引入本机状态（文件、时钟以外的环境），
/// 各节点就会算出不同的席位表，进而重复评审或集体旁观。
/// </remarks>
public static class SeatAssignment
{
    /// <summary>
    /// quorum 自适应：按 <see cref="QuorumPolicy"/> 定这个 PR 跑几遍。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 默认策略（<see cref="QuorumPolicy.Single"/>）下所有档位都是 1，即每个 PR 只评一次。
    /// 多跑几遍能压掉 LLM 的方差，但那是成倍的额度，所以要显式配才开。
    /// </para>
    /// <para>
    /// 阈值只看<b>文件数</b>而不看行数。行数要靠本机 clone 跑一次
    /// <c>git diff --numstat</c> 才有，而评审用的代码现在是评审时才临时拉的 ——
    /// 为了给每个新发现的 PR 定 quorum 就先拉一遍仓库，成本远大于收益。
    /// Azure DevOps 的 <c>pullRequestIterationChanges</c> 接口能零克隆拿到改动路径与文件数，
    /// 但<b>不提供行数</b>（实测确认）。
    /// </para>
    /// <para>
    /// 各档取<b>最大</b>而不是「命中就返回」：一个 25 个文件、又碰了支付路径的 PR，
    /// 应该按两者里更严的那个跑，而不是取决于 if 的书写顺序。
    /// </para>
    /// </remarks>
    public static int QuorumSize(PrMeta pr, ReservedMatters reserved, QuorumPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(reserved);
        ArgumentNullException.ThrowIfNull(policy);

        if (pr.IsDraft)
        {
            return 0;
        }

        var quorum = policy.Default;

        if (reserved.Matches(pr.ChangedPaths))
        {
            quorum = Math.Max(quorum, policy.ReservedMatters);
        }

        if (pr.FilesChanged > policy.MediumChangeFiles)
        {
            quorum = Math.Max(quorum, policy.MediumChange);
        }

        if (pr.FilesChanged > policy.LargeChangeFiles)
        {
            quorum = Math.Max(quorum, policy.LargeChange);
        }

        return Math.Max(1, quorum);
    }

    /// <summary>
    /// 硬规则。任一条不满足即无资格入席。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「本机有 clone」这条已经删掉：代码是评审时拉进临时工作区、结束即删的，
    /// 任何节点都能评任何仓库。<see cref="Elector.HasProject"/> 保留 ——
    /// 它是 <c>az</c> 身份的读权限，没权限连克隆和发评论都做不到。
    /// </para>
    /// <para>
    /// <see cref="Elector.HasHeadroom"/> 是新加的一条：Claude 额度用过
    /// <see cref="Elector.MaxUtilization"/> 的节点不再接评审任务。它必须是硬规则而不只是
    /// 权重 —— 权重再低也仍有概率中签，而额度用尽的节点中签只会换来一张 Error 票。
    /// </para>
    /// </remarks>
    /// <param name="elector">候选节点。</param>
    /// <param name="pr">待评审的 PR 快照。</param>
    /// <param name="now">存活判定用的「现在」，由调用方传入以保证各节点一致。</param>
    /// <param name="allowSelfReview">
    /// 允许作者评自己的 PR。
    /// <para>
    /// <b>这个值必须由调用方从队列项里读出来传进来，不能各节点各读自己的配置</b> ——
    /// 一台允许、一台不允许，两边的合格节点集就不同，席位表随之分叉，表现为同一个 PR
    /// 被两个节点同时评、或者所有节点都以为该别人干，而且不会报错。发现节点把它写在
    /// <see cref="QueuedRevision.AllowSelfReview"/> 上随条目广播，理由跟 quorum 一样。
    /// </para>
    /// </param>
    public static bool Eligible(
        Elector elector, PrMeta pr, DateTimeOffset now, bool allowSelfReview = false)
    {
        ArgumentNullException.ThrowIfNull(elector);
        ArgumentNullException.ThrowIfNull(pr);

        return CouldEverReview(elector, pr, now, allowSelfReview)
            && elector.RunningJobs < elector.MaxConcurrent
            && elector.HasHeadroom;
    }

    /// <summary>
    /// 这个节点<b>原则上</b>能评这个 PR —— 不看它此刻忙不忙、额度还剩多少。
    /// </summary>
    /// <remarks>
    /// 拿来回答「还有没有没试过的机器」。<see cref="Eligible"/> 不行：它把「正在评别的
    /// PR」和「额度过线」也算进去了，而那两样几分钟后就会变 —— 用它判断会得出
    /// 「没人可试了」然后把一个其实还有救的 PR 提前收成失败。
    /// </remarks>
    public static bool CouldEverReview(
        Elector elector, PrMeta pr, DateTimeOffset now, bool allowSelfReview = false)
    {
        ArgumentNullException.ThrowIfNull(elector);
        ArgumentNullException.ThrowIfNull(pr);

        return elector.HasProject(pr.Project)
            && (allowSelfReview || !AzIdentity.SamePerson(elector.AzIdentity, pr.Author))
            && elector.IsAlive(now);
    }

    /// <summary>
    /// 算出席位表：第 N 个元素就是 round=N 的评审节点 ID。
    /// </summary>
    /// <param name="revision">评审的 PR 版本。HRW 的种子取自它的 <see cref="Revision.SeatKey"/>。</param>
    /// <param name="pr">Summons 快照里的 PR 元数据。</param>
    /// <param name="mesh">当前已知的成员（含自己）。</param>
    /// <param name="quorum">应有席位数，取自 Summons 块 —— 不在这里重算。</param>
    /// <param name="now">存活判定用的「现在」，由调用方传入以保证各节点一致。</param>
    /// <param name="extraRounds">失败/超时后追加的重试轮次数。</param>
    /// <param name="retryOnSameNode">
    /// 候选人用完时重新蓄池，允许把后续轮次再给一个已经坐过的节点。
    /// 默认关 —— 执行失败要的是换一台机器，见 <c>ConclaveOptions.RetryOnSameNode</c>。
    /// </param>
    /// <param name="allowSelfReview">
    /// 允许作者评自己的 PR，取自队列项（<see cref="QueuedRevision.AllowSelfReview"/>）——
    /// 不能各节点各读本机配置，否则合格节点集不同、席位表分叉。
    /// </param>
    /// <param name="incumbent">
    /// 同一个 PR 上一版的评审者。非空且仍合格时直接坐 round=0。
    /// </param>
    /// <remarks>
    /// <para>
    /// 前 <c>quorum</c> 轮从「尚未入席的合格节点」里按加权 HRW 取，所以正式席位互不重复。
    /// 合格节点不足时返回的列表短于 quorum —— 调用方据此在 Promulgation 上标 degraded。
    /// </para>
    /// <para>
    /// <b>HRW 的种子是 PR 而不是 revision</b>（<see cref="Revision.SeatKey"/>）。作者 push
    /// 修复之后 revision 变了，但席位应当落回同一个节点 —— 那个节点已经读过这份代码、
    /// 提过这些 finding，复审同一个 PR 的边际成本远低于换一个节点从头看。
    /// 种子换成 PR 之后，即使 <paramref name="incumbent"/> 拿不到，抽签结果天然也是稳定的。
    /// </para>
    /// <para>
    /// <paramref name="incumbent"/> 是在此之上的<b>确定性</b>保证：光靠 HRW 还不够，
    /// 因为权重会随负载和额度漂移，两个节点分数接近时结果会翻。
    /// 它只作用于 round=0 —— 重试轮次必须换人，上一个已经失败过了。
    /// </para>
    /// <para>
    /// <paramref name="extraRounds"/> 在候选池用尽时会<b>重新蓄池</b>，允许再次抽到同一个
    /// 节点 —— 否则单节点 mesh 上一次超时就会让这个 PR 永远卡住：没有票不能公布，
    /// 也没有下一轮可以接管。
    /// </para>
    /// <para>
    /// 各节点是按自己的链视图算 <paramref name="extraRounds"/> 与 <paramref name="incumbent"/>
    /// 的，gossip 有延迟时可能短暂不一致；收敛后自愈，因此不需要为它引入协商。
    /// </para>
    /// <para>
    /// 全员额度都过线时返回空席位表，这个 PR 就停在「已召集」不动 —— 这是
    /// <see cref="Elector.MaxUtilization"/> 想要的行为而不是故障：额度回落后下一轮编排
    /// 自己会把它捡起来。调用方要把这种情况在状态栏上讲清楚，别让它看起来像卡住了。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Seats(
        Revision revision,
        PrMeta pr,
        IEnumerable<Elector> mesh,
        int quorum,
        DateTimeOffset now,
        int extraRounds = 0,
        string? incumbent = null,
        bool allowSelfReview = false,
        bool retryOnSameNode = false)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(mesh);

        if (quorum <= 0)
        {
            return [];
        }

        var eligible = mesh.Where(e => Eligible(e, pr, now, allowSelfReview)).ToList();
        if (eligible.Count == 0)
        {
            return [];
        }

        var totalRounds = quorum + Math.Max(0, extraRounds);
        var pool = new List<Elector>(eligible);
        var seats = new List<string>(totalRounds);

        // 复审优先落回上一版的评审者。只对 round=0 生效 —— 重试轮次要换人。
        if (!string.IsNullOrEmpty(incumbent))
        {
            var sitting = pool.FirstOrDefault(e => e.Id == incumbent);
            if (sitting is not null)
            {
                seats.Add(sitting.Id);
                _ = pool.Remove(sitting);
            }
        }

        for (var round = seats.Count; round < totalRounds; round++)
        {
            if (pool.Count == 0)
            {
                if (round < quorum)
                {
                    // 正式席位必须互不重复：合格节点不够就降级，返回短于 quorum 的席位表，
                    // 由调用方在 Promulgation 上标 degraded。
                    break;
                }

                // 重试轮次重新蓄池，允许再抽到同一个节点。
                //
                // 默认<b>关着</b>：出错的原因大多不在这份代码上 —— claude 没额度、
                // 用户退出登录、az 连不上、token 到期 —— 在同一台机器上再跑一遍
                // 只会原样再错一次，而每一轮都是完整的账单。换台机器才有意义，
                // 所以池子空了就让这一版留在队列里等新节点，而不是塞回给刚失败的那台。
                if (!retryOnSameNode)
                {
                    break;
                }

                pool.AddRange(eligible);
            }

            var picked = Hrw.Pick(revision.SeatKey, round, pool);
            if (picked is null)
            {
                break;
            }

            seats.Add(picked.Id);
            _ = pool.Remove(picked);
        }

        return seats;
    }

    /// <summary>
    /// 把 project 的轮询责任分片到 mesh，返回该节点应当负责轮询的 project。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 34 个 project 让每个节点都把改动统计取一遍是浪费（每个新 PR 两次 API 调用）。
    /// 分片后每节点约 <c>34/N</c> 个；节点掉线时 HRW 自动把它的份额重分给别人，
    /// 没有故障切换代码。
    /// </para>
    /// <para>
    /// <b>候选人只能是真的看得见这个 project 的节点。</b> 分片用的是「本节点有权限的
    /// project 清单」，而各节点的 <c>az</c> 权限并不相同 —— 拿全体存活节点当候选的话，
    /// 只有 A 有权限的 project 可能被判给 B，而 B 的清单里根本没有它、永远不会去轮，
    /// 于是那个 project 的 PR <b>谁都不发现</b>，而且没有任何报错。
    /// 权限视图就在心跳里（<see cref="Elector.Projects"/>），先按它筛一遍候选。
    /// </para>
    /// <para>
    /// 没有任何存活节点声称有权限时（对端的权限视图还没广播过来 —— 心跳里的
    /// <c>Projects</c> 要等它自己轮完一轮才填上），退回「在全体存活节点里 HRW」。
    /// <b>不能改成「那就自己轮」</b>：那样每个节点都会把它算进自己的份额，
    /// 分片退化成人人全轮，改动统计的调用量翻 N 倍。
    /// 这个退化只是暂时的 —— 对端一轮之后清单就填上了。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> DiscoveryShare(
        string selfId,
        IEnumerable<string> allProjects,
        IEnumerable<Elector> mesh,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(allProjects);
        ArgumentNullException.ThrowIfNull(mesh);

        var alive = mesh.Where(e => e.IsAlive(now)).ToList();
        if (alive.Count == 0)
        {
            return [];
        }

        var share = new List<string>();

        foreach (var project in allProjects)
        {
            var candidates = alive.Where(e => e.HasProject(project)).Select(e => e.Id).ToList();
            if (candidates.Count == 0)
            {
                // 谁都没声称有权限：退回全体，保住「不重不漏」。见上面第三段。
                candidates = [.. alive.Select(e => e.Id)];
            }

            if (Hrw.PickId($"discover:{project}", 0, candidates) == selfId)
            {
                share.Add(project);
            }
        }

        return share;
    }
}

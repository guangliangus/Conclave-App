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
    /// <summary>quorum 自适应：不是所有 PR 都值得跑 3 遍。</summary>
    public static int QuorumSize(PrMeta pr, ReservedMatters reserved)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(reserved);

        if (pr.IsDraft)
        {
            return 0;
        }

        if (reserved.Matches(pr.ChangedPaths))
        {
            return 3;
        }

        if (pr.LinesChanged > 500 || pr.FilesChanged > 20)
        {
            return 3;
        }

        return pr.LinesChanged > 80 ? 2 : 1;
    }

    /// <summary>硬规则。任一条不满足即无资格入席。</summary>
    public static bool Eligible(Elector elector, PrMeta pr, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(elector);
        ArgumentNullException.ThrowIfNull(pr);

        return elector.HasRepo(pr.Repo)
            && elector.HasProject(pr.Project)
            && !AzIdentity.SamePerson(elector.AzIdentity, pr.Author)
            && elector.RunningJobs < elector.MaxConcurrent
            && elector.IsAlive(now);
    }

    /// <summary>
    /// 算出席位表：第 N 个元素就是 round=N 的评审节点 ID。
    /// </summary>
    /// <remarks>
    /// 逐轮从「尚未入席的合格节点」里按加权 HRW 取一个，因此同一节点不会占两个席位。
    /// 合格节点不足时返回的列表短于 quorum —— 调用方据此在 Promulgation 上标 degraded。
    /// </remarks>
    public static IReadOnlyList<string> Seats(
        Revision revision,
        PrMeta pr,
        IEnumerable<Elector> mesh,
        ReservedMatters reserved,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(mesh);

        var quorum = QuorumSize(pr, reserved);
        if (quorum == 0)
        {
            return [];
        }

        var pool = mesh.Where(e => Eligible(e, pr, now)).ToList();
        var seats = new List<string>(quorum);

        for (var round = 0; round < quorum && pool.Count > 0; round++)
        {
            var picked = Hrw.Pick(revision.Id, round, pool);
            if (picked is null)
            {
                break;
            }

            seats.Add(picked.Id);
            pool.Remove(picked);
        }

        return seats;
    }

    /// <summary>
    /// 把 project 的轮询责任分片到 mesh，返回该节点应当负责轮询的 project。
    /// </summary>
    /// <remarks>
    /// 34 个 project 让每个节点全轮一遍是浪费。分片后每节点约 <c>34/N</c> 个；
    /// 节点掉线时 HRW 自动把它的份额重分给别人，没有故障切换代码。
    /// </remarks>
    public static IReadOnlyList<string> DiscoveryShare(
        string selfId,
        IEnumerable<string> allProjects,
        IEnumerable<Elector> mesh,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(allProjects);
        ArgumentNullException.ThrowIfNull(mesh);

        var alive = mesh.Where(e => e.IsAlive(now)).Select(e => e.Id).ToList();
        if (alive.Count == 0)
        {
            return [];
        }

        return allProjects
            .Where(p => Hrw.PickId($"discover:{p}", 0, alive) == selfId)
            .ToList();
    }
}

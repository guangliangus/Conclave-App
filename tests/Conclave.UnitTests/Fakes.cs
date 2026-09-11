using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;

namespace Conclave.UnitTests;

internal sealed class FakePrSource : IPrSource
{
    /// <summary>project → 该 project 下的活跃 PR。</summary>
    internal Dictionary<string, List<PrMeta>> Active { get; } = new(StringComparer.Ordinal);

    /// <summary>这些 project 的列举会抛异常，用来验证单个 project 失败不拖垮整轮。</summary>
    internal HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

    internal List<(PrMeta Pr, PromulgationPayload Result)> Posted { get; } = [];

    internal int ListCalls { get; private set; }

    /// <summary>被真正列举过的 project。用来验证「被排除的 project 连 API 都不调」。</summary>
    internal List<string> ListedProjects { get; } = [];

    /// <summary>取改动统计的次数。每次要两个 az 调用（约 3 秒），稳态下不该重复付。</summary>
    internal int EnrichCalls { get; private set; }

    /// <summary>问过几次「有哪些 project」。白名单非空时应当为 0。</summary>
    internal int ProjectListCalls { get; private set; }

    internal int? NextThreadId { get; set; } = 8821;

    /// <summary>非 null 时投递会抛，用来验证投递失败不阻止结论落链。</summary>
    internal Exception? PostThrows { get; set; }

    public Task<string> GetAuthenticatedIdentityAsync(CancellationToken ct) => Task.FromResult("alan");

    public Task<IReadOnlyList<string>> ListProjectsAsync(CancellationToken ct)
    {
        ProjectListCalls++;
        return Task.FromResult<IReadOnlyList<string>>([.. Active.Keys, .. Failing]);
    }

    public Task<IReadOnlyList<PrMeta>> ListActivePullRequestsAsync(string project, CancellationToken ct)
    {
        ListCalls++;
        ListedProjects.Add(project);
        if (Failing.Contains(project))
        {
            throw new InvalidOperationException($"az 炸了：{project}");
        }

        return Task.FromResult<IReadOnlyList<PrMeta>>(
            Active.TryGetValue(project, out var prs) ? prs : []);
    }

    /// <summary>fake 不查 ADO，原样返回 —— PrMeta 里的统计由测试直接给定。</summary>
    public Task<PrMeta> EnrichWithChangeStatsAsync(PrMeta pr, CancellationToken ct)
    {
        EnrichCalls++;
        return Task.FromResult(pr);
    }

    /// <summary>collection 级列举：把所有 project 的都摊平给出去。</summary>
    /// <remarks>
    /// 置 <see cref="CollectionListingFails"/> 可以模拟「这个路由没开」，
    /// 验证发现循环会退回逐 project 列举。
    /// </remarks>
    internal bool CollectionListingFails { get; set; }

    internal int CollectionListCalls { get; private set; }

    public Task<IReadOnlyList<PrMeta>> ListAllActivePullRequestsAsync(CancellationToken ct)
    {
        CollectionListCalls++;

        if (CollectionListingFails)
        {
            throw new InvalidOperationException("collection 级列举没开");
        }

        return Task.FromResult<IReadOnlyList<PrMeta>>(
            [.. Active.Values.SelectMany(x => x)]);
    }

    public Task<string> GetCloneUrlAsync(PrMeta pr, CancellationToken ct)
        => Task.FromResult(pr.RemoteUrl.Length > 0 ? pr.RemoteUrl : $"https://az.invalid/_git/{pr.Repo}");

    /// <summary>组织地址。置空可以模拟「读不到，PR 链接不可点」。</summary>
    public string OrgUrl { get; set; } = "https://az.invalid/Collection";

    public Task<string> GetOrgUrlAsync(CancellationToken ct) => Task.FromResult(OrgUrl);

    public Task<PrMeta?> FindPullRequestAsync(int prId, CancellationToken ct)
        => Task.FromResult(Active.Values.SelectMany(x => x).FirstOrDefault(p => p.PrId == prId));

    public Task<int?> PostResultAsync(PrMeta pr, PromulgationPayload result, CancellationToken ct)
    {
        if (PostThrows is not null)
        {
            throw PostThrows;
        }

        Posted.Add((pr, result));
        return Task.FromResult(NextThreadId);
    }
}

internal sealed class FakeNotifier : INotifier
{
    internal List<(PrMeta Pr, PromulgationPayload Result)> Sent { get; } = [];

    /// <summary>非 null 时直接抛出，用来验证通知失败不能把公布带崩。</summary>
    internal Exception? Throw { get; set; }

    public Task NotifyPromulgationAsync(PrMeta pr, PromulgationPayload result, CancellationToken ct)
    {
        if (Throw is not null)
        {
            throw Throw;
        }

        Sent.Add((pr, result));
        return Task.CompletedTask;
    }
}

internal sealed class FakeReviewRunner : IReviewRunner
{
    private readonly Func<Revision, int, BallotPayload> _produce;

    internal FakeReviewRunner(Func<Revision, int, BallotPayload>? produce = null)
        => _produce = produce ?? ((rev, round) => new BallotPayload(
            rev.Id, round, ReviewDecision.Reject,
            [new Finding("src/A.cs", 12, Severity.Major, "问题", "细节")],
            "claude-opus-5", 1234));

    internal int Calls { get; private set; }

    /// <summary>非 null 时直接抛出，用来验证失败也必须出 Error 票。</summary>
    internal Exception? Throw { get; set; }

    /// <summary>
    /// 非 null 时评审会挂在这里，直到测试放闸。
    /// </summary>
    /// <remarks>
    /// 用来造出「本节点正在跑这个席位」的时间窗 —— 席位超时的豁免逻辑只在那个窗口里成立。
    /// </remarks>
    internal TaskCompletionSource? Gate { get; set; }

    /// <summary>评审已经开跑（Gate 模式下用它等到真正进入执行）。</summary>
    internal TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<BallotPayload> RunAsync(Revision revision, PrMeta pr, int round, CancellationToken ct)
    {
        Calls++;
        Started.TrySetResult();

        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(ct);
        }

        return Throw is not null ? throw Throw : _produce(revision, round);
    }
}

internal sealed class FakeMesh : IMesh
{
    private readonly Lock _gate = new();
    private Elector _self;
    private LiveState _state = LiveState.Empty;

    internal FakeMesh(Elector self, IEnumerable<Elector>? peers = null)
    {
        _self = self;
        Peers = [.. peers ?? []];
    }

    internal List<Elector> Peers { get; }

    internal List<Block> Broadcast { get; } = [];

    public Elector Self
    {
        get { lock (_gate) { return _self; } }
    }

    public IReadOnlyList<Elector> Members => [Self, .. Peers];

    /// <summary>本节点的实时状态。</summary>
    public LiveState State
    {
        get { lock (_gate) { return _state; } }
    }

    /// <summary>
    /// peer 的实时状态。测试直接往这里塞，模拟「别的节点上报了什么」。
    /// </summary>
    /// <remarks>
    /// 真实实现里这些是从 <c>GET /state</c> 拉回来的；测试不需要跑 HTTP，
    /// 直接给状态就能验队列合并与席位分配。
    /// </remarks>
    internal Dictionary<string, LiveState> Remote { get; } = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, LiveState> PeerStates
    {
        get
        {
            var all = new Dictionary<string, LiveState>(Remote, StringComparer.Ordinal);
            all[Self.Id] = State;
            return all;
        }
    }

    /// <summary>发出去的指派请求与答复，测试拿它断言「送到了什么」。</summary>
    internal List<AssignmentRequest> SentAssignments { get; } = [];

    internal List<AssignmentReply> SentReplies { get; } = [];

    /// <summary>false 表示对端不可达，用来验证送不到时的处理。</summary>
    internal bool AssignmentDelivers { get; set; } = true;

    public Task<bool> SendAssignmentAsync(Elector peer, AssignmentRequest request, CancellationToken ct)
    {
        if (AssignmentDelivers)
        {
            SentAssignments.Add(request);
        }

        return Task.FromResult(AssignmentDelivers);
    }

    /// <summary>假的对端日志。键是 revisionId，测试直接往里塞。</summary>
    internal Dictionary<string, LogChunk> PeerLogs { get; } = new(StringComparer.Ordinal);

    public Task<LogChunk?> FetchLogAsync(
        Elector peer, string revisionId, long from, CancellationToken ct)
        => Task.FromResult(PeerLogs.TryGetValue(revisionId, out var chunk) ? chunk : null);

    public Task<bool> SendAssignmentReplyAsync(Elector peer, AssignmentReply reply, CancellationToken ct)
    {
        if (AssignmentDelivers)
        {
            SentReplies.Add(reply);
        }

        return Task.FromResult(AssignmentDelivers);
    }

    public void UpdateState(Func<LiveState, LiveState> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            var next = mutate(_state);

            // 跟 HttpMesh / LocalMesh 同语义：空操作不递增。假货在这一点上偷懒的话，
            // 「守卫没生效」这类 bug 在单测里永远看不见。
            if (ReferenceEquals(next, _state))
            {
                return;
            }

            _state = next with { Version = _state.Version + 1 };
        }
    }

    public Task BroadcastAsync(Block block, CancellationToken ct)
    {
        Broadcast.Add(block);
        return Task.CompletedTask;
    }

    public void UpdateSelf(Func<Elector, Elector> mutate)
    {
        lock (_gate)
        {
            _self = mutate(_self);
        }
    }
}

/// <summary>用量读数由测试直接给定，不去碰投影表也不读覆盖文件。</summary>
internal sealed class FakeUsageMeter(double utilization = 0) : IUsageMeter
{
    internal double Utilization { get; set; } = utilization;

    /// <summary>非 null 时直接抛，用来验证读不到用量必须保留上一轮的值而不是清零。</summary>
    internal Exception? Throw { get; set; }

    internal int Calls { get; private set; }

    public Task<UsageReading> ReadAsync(string electorId, CancellationToken ct)
    {
        Calls++;
        return Throw is not null
            ? throw Throw
            : Task.FromResult(new UsageReading(Utilization, "fake", "测试给定"));
    }
}

/// <summary>测试用的可变白名单。</summary>
internal sealed class MutableAllowList(string selfId) : IElectorAllowList
{
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal) { selfId };

    public IReadOnlyCollection<string> Allowed => _ids;

    public bool IsAllowed(string electorId) => _ids.Contains(electorId);

    internal void Allow(string electorId) => _ids.Add(electorId);
}

/// <summary>假的更新源：测试直接塞「最新版是哪个」，或者让它抛。</summary>
/// <summary>不起子进程的 claude：心跳要能在一台没装 claude 的机器上照常发（CI 就是）。</summary>
internal sealed class FakeClaudeCli(string version = "2.1.268") : IClaudeCli
{
    public Task<string> VersionAsync(CancellationToken ct) => Task.FromResult(version);
}

internal sealed class FakeUpdateInstaller : IUpdateInstaller
{
    internal bool Installable { get; set; } = true;

    internal Exception? Throw { get; set; }

    internal List<ReleaseInfo> Installed { get; } = [];

    public bool CanInstall => Installable;

    public Task InstallAsync(ReleaseInfo release, CancellationToken ct)
    {
        Installed.Add(release);
        return Throw is null ? Task.CompletedTask : Task.FromException(Throw);
    }
}

internal sealed class FakeUpdateSource : IUpdateSource
{
    internal ReleaseInfo? Latest { get; set; }

    internal Exception? Throw { get; set; }

    internal int Calls { get; private set; }

    public Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        Calls++;
        return Throw is null ? Task.FromResult(Latest) : Task.FromException<ReleaseInfo?>(Throw);
    }
}

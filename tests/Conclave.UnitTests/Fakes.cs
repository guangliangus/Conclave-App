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

    internal int? NextThreadId { get; set; } = 8821;

    public Task<string> GetAuthenticatedIdentityAsync(CancellationToken ct) => Task.FromResult("alan");

    public Task<IReadOnlyList<string>> ListProjectsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>([.. Active.Keys, .. Failing]);

    public Task<IReadOnlyList<PrMeta>> ListActivePullRequestsAsync(string project, CancellationToken ct)
    {
        ListCalls++;
        if (Failing.Contains(project))
        {
            throw new InvalidOperationException($"az 炸了：{project}");
        }

        return Task.FromResult<IReadOnlyList<PrMeta>>(
            Active.TryGetValue(project, out var prs) ? prs : []);
    }

    /// <summary>fake 不算 git diff，原样返回 —— PrMeta 里的统计由测试直接给定。</summary>
    public Task<PrMeta> EnrichWithDiffStatsAsync(PrMeta pr, CancellationToken ct) => Task.FromResult(pr);

    public Task<PrMeta?> FindPullRequestAsync(int prId, CancellationToken ct)
        => Task.FromResult(Active.Values.SelectMany(x => x).FirstOrDefault(p => p.PrId == prId));

    public Task<int?> PostResultAsync(PrMeta pr, PromulgationPayload result, CancellationToken ct)
    {
        Posted.Add((pr, result));
        return Task.FromResult(NextThreadId);
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

internal sealed class FakeRepoLocator(params string[] repos) : IRepoLocator
{
    public IReadOnlyDictionary<string, string> Locate()
        => repos.ToDictionary(r => r, r => "/tmp/" + r, StringComparer.OrdinalIgnoreCase);
}

/// <summary>测试用的可变白名单。</summary>
internal sealed class MutableAllowList(string selfId) : IElectorAllowList
{
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal) { selfId };

    public IReadOnlyCollection<string> Allowed => _ids;

    public bool IsAllowed(string electorId) => _ids.Contains(electorId);

    internal void Allow(string electorId) => _ids.Add(electorId);
}

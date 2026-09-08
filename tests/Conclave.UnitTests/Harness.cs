using Conclave.Application;
using Conclave.Domain;
using Conclave.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

/// <summary>
/// 编排测试用的装配：真的 <see cref="SqliteActa"/>（临时目录）+ fake 的外部依赖。
/// </summary>
/// <remarks>
/// 账本刻意用真实实现 —— 验签、PrevHash 链接、开放链查询这些正是编排逻辑依赖的行为，
/// 换成内存假货就把最容易出错的部分测空了。
/// </remarks>
internal sealed class Harness : IDisposable
{
    private readonly string _home;

    internal Harness(int maxConcurrent = 2, params string[] repos)
    {
        _home = Path.Combine(Path.GetTempPath(), "conclave-orch-" + Guid.NewGuid().ToString("N"));
        Options = new ConclaveOptions
        {
            HomeDirectory = _home,
            MaxConcurrent = maxConcurrent,
            AutoReview = false,
            PostToAzureDevOps = false,
        };

        Identity = ElectorIdentity.Create();
        AllowList = new MutableAllowList(Identity.Id);
        Acta = new SqliteActa(Options, Identity, AllowList, NullLogger<SqliteActa>.Instance);

        Mesh = new FakeMesh(new Elector
        {
            Id = Identity.Id,
            PublicKey = Identity.PublicKey,
            AzIdentity = "alan",
            Repos = repos.Length > 0 ? repos : ["cms-apostrophe"],
            Projects = ["liontrip-cms"],
            MaxConcurrent = maxConcurrent,
            LastHeartbeat = DateTimeOffset.UtcNow,
        });

        PrSource = new FakePrSource();
        Runner = new FakeReviewRunner();
        State = new NodeState();
        Repos = new FakeRepoLocator(repos.Length > 0 ? repos : ["cms-apostrophe"]);

        Discovery = new DiscoveryService(
            PrSource, Acta, Mesh, Repos, State, Options, NullLogger<DiscoveryService>.Instance);
        Orchestrator = new ReviewOrchestrator(
            Acta, Mesh, Runner, PrSource, State, Options, NullLogger<ReviewOrchestrator>.Instance);
    }

    internal ConclaveOptions Options { get; }

    internal ElectorIdentity Identity { get; }

    internal MutableAllowList AllowList { get; }

    internal SqliteActa Acta { get; }

    internal FakeMesh Mesh { get; }

    internal FakePrSource PrSource { get; }

    internal FakeReviewRunner Runner { get; }

    internal FakeRepoLocator Repos { get; }

    internal NodeState State { get; }

    internal DiscoveryService Discovery { get; }

    internal ReviewOrchestrator Orchestrator { get; }

    internal string SelfId => Identity.Id;

    /// <summary>把一个 PR 放进某个 project 的活跃列表。</summary>
    internal PrMeta Publish(PrMeta pr)
    {
        if (!PrSource.Active.TryGetValue(pr.Project, out var list))
        {
            list = [];
            PrSource.Active[pr.Project] = list;
        }

        list.RemoveAll(p => p.PrId == pr.PrId);
        list.Add(pr);
        return pr;
    }

    /// <summary>跑一轮编排，并等到本轮开跑的评审收尾。</summary>
    internal async Task TickAsync()
    {
        await Orchestrator.TickAsync(CancellationToken.None);
        await Orchestrator.WhenIdleAsync();
    }

    internal Task<IReadOnlyList<Block>> ChainAsync(Revision rev)
        => Acta.ReadRevisionAsync(rev.Id, CancellationToken.None);

    /// <summary>整条全局链，用来验证索引连续与哈希链完整。</summary>
    internal Task<IReadOnlyList<Block>> WholeChainAsync()
        => Acta.ReadChainAsync(0, CancellationToken.None);

    internal async Task<ChainState> StateOfAsync(Revision rev)
        => ActaProjection.Project(await ChainAsync(rev), rev.Id);

    /// <summary>
    /// 找一个「本节点恰好坐在 <paramref name="wantedRound"/> 席」的 PR 号。
    /// </summary>
    /// <remarks>
    /// HRW 是内容哈希决定的，没法直接指定谁坐哪一席。测试要覆盖「只有 round=0 负责公布」，
    /// 就得反过来搜一个满足条件的 PR 号 —— 这本身也顺带验证了席位分配是纯函数。
    /// </remarks>
    internal int FindPrIdSeating(int wantedRound, PrMeta template, IEnumerable<Elector> mesh)
    {
        for (var prId = 1; prId < 20000; prId++)
        {
            var candidate = template with { PrId = prId };
            var seats = SeatAssignment.Seats(
                candidate.ToRevision(), candidate, mesh, ReservedMatters.Default, DateTimeOffset.UtcNow);

            if (seats.Count > wantedRound && seats[wantedRound] == SelfId)
            {
                return prId;
            }
        }

        throw new InvalidOperationException($"找不到让本节点坐 round={wantedRound} 的 PR 号");
    }

    public void Dispose()
    {
        Identity.Dispose();
        Orchestrator.Dispose();
        Discovery.Dispose();
        // 刻意不调 SqliteConnection.ClearAllPools()：那是进程全局的，会把并行跑的
        // 其他测试的连接池一起清掉，制造出难查的偶发失败。
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响测试结论。
        }
    }
}

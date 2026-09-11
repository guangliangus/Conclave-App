using Conclave.Application;
using Conclave.Domain;
using Conclave.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

/// <summary>
/// 索引冲突的让位与重挂。
/// </summary>
/// <remarks>
/// 这是账本唯一会删块的地方 —— 「append-only」的准确说法是「无冲突时只追加」。
/// 让位规则是纯函数（blockHash 字典序小者胜出），所以所有节点算出同一个结果，
/// 不需要协商就能收敛。
/// </remarks>
public sealed class RebaseTests : IDisposable
{
    private sealed class Node : IDisposable
    {
        private readonly string _home;

        internal Node(string tag)
        {
            _home = Path.Combine(Path.GetTempPath(), $"conclave-rebase-{tag}-{Guid.NewGuid():N}");
            Identity = ElectorIdentity.Create();
            AllowList = new MutableAllowList(Identity.Id);
            var options = new ConclaveOptions { HomeDirectory = _home };
            Acta = new SqliteActa(options, Identity, AllowList, NullLogger<SqliteActa>.Instance);
            Log = new SqliteReviewLog(options);
        }

        internal ElectorIdentity Identity { get; }

        internal MutableAllowList AllowList { get; }

        internal SqliteActa Acta { get; }

        /// <summary>跟 <see cref="Acta"/> 同一个 home，所以读的是同一份投影表。</summary>
        internal SqliteReviewLog Log { get; }

        internal Task<IReadOnlyList<Block>> ChainAsync()
            => Acta.ReadChainAsync(0, int.MaxValue, CancellationToken.None);

        public void Dispose()
        {
            Identity.Dispose();
            try
            {
                Directory.Delete(_home, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清不掉不影响结论
            }
        }
    }

    private static readonly Revision Rev = new("edison-test", 2878, "aaaaaaaa11111111");

    private readonly Node _a = new("a");
    private readonly Node _b = new("b");

    public RebaseTests()
    {
        _a.AllowList.Allow(_b.Identity.Id);
        _b.AllowList.Allow(_a.Identity.Id);
    }

    private static Block SignAt(ElectorIdentity identity, long index, string prevHash, BlockKind kind, object payload)
    {
        var unsigned = new Block
        {
            ChainId = Domain.Acta.ChainId,
            Index = index,
            PrevHash = prevHash,
            At = DateTimeOffset.UtcNow,
            Kind = kind,
            PayloadJson = ActaJson.Serialize(payload),
            ElectorId = identity.Id,
            PublicKey = identity.PublicKey,
            Signature = string.Empty,
        };

        return unsigned with { Signature = identity.Sign(unsigned.SigningPayload()) };
    }

    private static SummonsPayload Summons() => new(Rev, TestElectors.Pr(), 2, "rules1");

    private static void AssertValidChain(IReadOnlyList<Block> chain)
    {
        var prev = Block.GenesisPrevHash;
        for (var i = 0; i < chain.Count; i++)
        {
            Assert.Equal(i, chain[i].Index);
            Assert.Equal(prev, chain[i].PrevHash);
            Assert.True(chain[i].VerifySignature(), $"第 {i} 块签名无效");
            prev = chain[i].Hash();
        }
    }

    [Fact]
    public async Task The_lower_hash_wins_and_the_loser_is_reattached()
    {
        var ct = CancellationToken.None;

        // 双方先同步一个共同的起点
        var genesis = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(), ct);
        Assert.True((await _b.Acta.TryApplyAsync(genesis, ct)).Applied);

        // 同时在 index 1 上各写一块：分区合并后的典型冲突
        var mine = await _a.Acta.AppendAsync(
            Rev.Id, BlockKind.Seating, new SeatingPayload(Rev.Id, 0, _a.Identity.Id), ct);
        var theirs = await _b.Acta.AppendAsync(
            Rev.Id, BlockKind.Ballot,
            new BallotPayload(Rev.Id, 0, ReviewDecision.Reject, [], "m", 1), ct);

        Assert.Equal(1, mine.Index);
        Assert.Equal(1, theirs.Index);

        var remoteWins = string.CompareOrdinal(theirs.Hash(), mine.Hash()) < 0;
        var result = await _a.Acta.TryApplyAsync(theirs, ct);
        var chain = await _a.ChainAsync();

        if (remoteWins)
        {
            Assert.True(result.Applied);
            Assert.Equal(3, chain.Count);
            Assert.Equal(theirs.Hash(), chain[1].Hash());          // 远端块占住 index 1
            Assert.Single(result.Rebased);                          // 自己那块被重挂
            Assert.Equal(2, result.Rebased[0].Index);
            Assert.Equal(mine.PayloadJson, chain[2].PayloadJson);   // 内容不变
            Assert.NotEqual(mine.Signature, chain[2].Signature);    // 但换了索引，必须重签
        }
        else
        {
            Assert.False(result.Applied);
            Assert.Equal(2, chain.Count);
            Assert.Equal(mine.Hash(), chain[1].Hash());
            Assert.Empty(result.Rebased);
        }

        AssertValidChain(chain);
    }

    [Fact]
    public async Task Two_nodes_converge_on_the_same_chain()
    {
        var ct = CancellationToken.None;

        var genesis = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(), ct);
        Assert.True((await _b.Acta.TryApplyAsync(genesis, ct)).Applied);

        var fromA = await _a.Acta.AppendAsync(
            Rev.Id, BlockKind.Seating, new SeatingPayload(Rev.Id, 0, _a.Identity.Id), ct);
        var fromB = await _b.Acta.AppendAsync(
            Rev.Id, BlockKind.Seating, new SeatingPayload(Rev.Id, 1, _b.Identity.Id), ct);

        // 互相推送，并把 rebase 重挂出来的块也推出去 —— MeshService 就是这么做的，
        // 少了这一步两边永远不会一致。
        var toB = await _a.Acta.TryApplyAsync(fromB, ct);
        var toA = await _b.Acta.TryApplyAsync(fromA, ct);

        foreach (var moved in toB.Rebased)
        {
            _ = await _b.Acta.TryApplyAsync(moved, ct);
        }

        foreach (var moved in toA.Rebased)
        {
            _ = await _a.Acta.TryApplyAsync(moved, ct);
        }

        var chainA = await _a.ChainAsync();
        var chainB = await _b.ChainAsync();

        AssertValidChain(chainA);
        AssertValidChain(chainB);

        Assert.Equal(
            chainA.Select(x => x.Hash()),
            chainB.Select(x => x.Hash()));

        // 两块都还在（一块让位重挂，但内容没丢）
        Assert.Equal(3, chainA.Count);
        var seats = chainA
            .Where(x => x.Kind == BlockKind.Seating)
            .Select(x => x.Payload<SeatingPayload>()!.Round)
            .OrderBy(r => r)
            .ToList();
        Assert.Equal([0, 1], seats);
    }

    [Fact]
    public async Task A_displaced_block_we_did_not_author_is_dropped_for_its_author_to_repush()
    {
        var ct = CancellationToken.None;
        using var third = new Node("c");
        _a.AllowList.Allow(third.Identity.Id);

        var genesis = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(), ct);

        // index 1 上放一块第三方签的
        var foreign = SignAt(
            third.Identity, 1, genesis.Hash(), BlockKind.Seating,
            new SeatingPayload(Rev.Id, 0, third.Identity.Id));
        Assert.True((await _a.Acta.TryApplyAsync(foreign, ct)).Applied);

        // 造一块哈希更小的来抢 index 1
        Block winner;
        var attempt = 0;
        do
        {
            winner = SignAt(
                _b.Identity, 1, genesis.Hash(), BlockKind.Ballot,
                new BallotPayload(Rev.Id, attempt, ReviewDecision.Reject, [], "m", attempt));
            attempt++;
        }
        while (string.CompareOrdinal(winner.Hash(), foreign.Hash()) >= 0 && attempt < 200);

        Assert.True(string.CompareOrdinal(winner.Hash(), foreign.Hash()) < 0, "没造出哈希更小的块");

        var result = await _a.Acta.TryApplyAsync(winner, ct);
        var chain = await _a.ChainAsync();

        Assert.True(result.Applied);
        Assert.Empty(result.Rebased);        // 别人的块我们没私钥，重签不了
        Assert.Equal(2, chain.Count);        // 第三方那块被丢弃，等其作者重推
        Assert.Equal(winner.Hash(), chain[1].Hash());
        AssertValidChain(chain);
    }

    [Fact]
    public async Task Rebase_survives_a_deeper_tail()
    {
        var ct = CancellationToken.None;

        var genesis = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(), ct);
        _ = await _a.Acta.AppendAsync(
            Rev.Id, BlockKind.Seating, new SeatingPayload(Rev.Id, 0, _a.Identity.Id), ct);
        _ = await _a.Acta.AppendAsync(
            Rev.Id, BlockKind.Ballot,
            new BallotPayload(Rev.Id, 0, ReviewDecision.Approve, [], "m", 1), ct);

        var before = await _a.ChainAsync();
        Assert.Equal(3, before.Count);

        // 造一块抢 index 1，把后面两块整段挤下去
        Block winner;
        var attempt = 0;
        do
        {
            winner = SignAt(
                _b.Identity, 1, genesis.Hash(), BlockKind.Recess,
                new RecessPayload(Rev.Id, attempt, "conflict"));
            attempt++;
        }
        while (string.CompareOrdinal(winner.Hash(), before[1].Hash()) >= 0 && attempt < 200);

        var result = await _a.Acta.TryApplyAsync(winner, ct);
        var after = await _a.ChainAsync();

        Assert.True(result.Applied);
        Assert.Equal(4, after.Count);
        Assert.Equal(2, result.Rebased.Count);   // 两块自己的都重挂上去了
        Assert.Equal([2L, 3L], result.Rebased.Select(x => x.Index));

        // 删掉一块会让它后面每一块的 PrevHash 断链，所以必须整段重挂 —— 这里验证链仍然完整。
        AssertValidChain(after);
    }

    [Fact]
    public async Task Rebasing_moves_the_projection_instead_of_duplicating_it()
    {
        var ct = CancellationToken.None;

        var genesis = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(), ct);

        // 本地这一票已经投影进 reviews 了。
        var ballot = await _a.Acta.AppendAsync(
            Rev.Id, BlockKind.Ballot,
            new BallotPayload(
                Rev.Id, 0, ReviewDecision.Reject, [], "m", 1,
                new ReviewUsage(10, 20, 0, 0, 0, 0.5m, "list", 1, [])) with
            {
                Pr = TestElectors.Pr(),
            },
            ct);

        Assert.Equal(ballot.Hash(), Assert.Single(await _a.Log.ReadRecentAsync(10, ct)).BlockHash);

        // 造一个哈希一定更小的远端块顶掉它 —— 这条用例要的正是「本地让位」那一支。
        var intruder = Smaller(genesis.Hash(), ballot.Hash());
        var result = await _a.Acta.TryApplyAsync(intruder, ct);

        Assert.True(result.Applied);
        var moved = Assert.Single(result.Rebased);
        Assert.Equal(ballot.PayloadJson, moved.PayloadJson);
        Assert.NotEqual(ballot.Hash(), moved.Hash());   // 换了索引就得重签，哈希跟着变

        // 投影必须<b>跟着搬</b>：旧哈希那行是幽灵（它指向的块已经不在链上了），
        // 新哈希那行必须在。多一行就是金额重复计，少一行就是账单凭空少一次评审。
        var rows = await _a.Log.ReadRecentAsync(10, ct);
        Assert.Equal(moved.Hash(), Assert.Single(rows).BlockHash);

        var total = await _a.Log.ReadTotalAsync(null, null, ct);
        Assert.Equal(1, total.Reviews);
        Assert.Equal(0.5m, total.CostUsd);
    }

    /// <summary>造一个哈希比 <paramref name="loser"/> 小、挂在同一个前驱上的 index 1 区块。</summary>
    private Block Smaller(string prevHash, string loser)
    {
        for (var salt = 0; salt < 1000; salt++)
        {
            var candidate = SignAt(
                _b.Identity, 1, prevHash, BlockKind.Seating,
                new SeatingPayload(Rev.Id, salt, _b.Identity.Id));

            if (string.CompareOrdinal(candidate.Hash(), loser) < 0)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("造不出哈希更小的块 —— 一千次都没撞上，该怀疑哈希实现");
    }

    [Fact]
    public async Task A_block_from_outside_the_whitelist_never_triggers_a_rebase()
    {
        var ct = CancellationToken.None;
        using var stranger = new Node("x");

        var genesis = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(), ct);
        var mine = await _a.Acta.AppendAsync(
            Rev.Id, BlockKind.Seating, new SeatingPayload(Rev.Id, 0, _a.Identity.Id), ct);

        var hostile = SignAt(
            stranger.Identity, 1, genesis.Hash(), BlockKind.Ballot,
            new BallotPayload(Rev.Id, 0, ReviewDecision.Approve, [], "m", 1));

        // 白名单在冲突判定之前 —— 否则外人只要造个小哈希的块就能改写别人的链。
        Assert.False((await _a.Acta.TryApplyAsync(hostile, ct)).Applied);

        var chain = await _a.ChainAsync();
        Assert.Equal(2, chain.Count);
        Assert.Equal(mine.Hash(), chain[1].Hash());
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }
}

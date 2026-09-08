using Conclave.Application;
using Conclave.Domain;
using Conclave.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

public sealed class SqliteActaTests : IDisposable
{
    private readonly string _home;
    private readonly ConclaveOptions _options;
    private readonly ElectorIdentity _identity;
    private readonly SqliteActa _acta;

    public SqliteActaTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "conclave-test-" + Guid.NewGuid().ToString("N"));
        _options = new ConclaveOptions { HomeDirectory = _home };
        _identity = ElectorIdentity.CreateEphemeral();
        _acta = new SqliteActa(_options, _identity, NullLogger<SqliteActa>.Instance);
    }

    private static readonly Revision Rev = new("liontrip-cms", 2721, "aaaaaaaa11111111");

    private static SummonsPayload Summons(int quorum = 1)
        => new(Rev, TestElectors.Pr(), quorum, "rules1");

    [Fact]
    public async Task First_block_on_a_chain_is_a_genesis_block()
    {
        var block = await _acta.AppendAsync(Rev.ChainId, BlockKind.Summons, Summons(), CancellationToken.None);

        Assert.Equal(0, block.Index);
        Assert.Equal(Block.GenesisPrevHash, block.PrevHash);
        Assert.True(block.VerifySignature());
    }

    [Fact]
    public async Task Appended_blocks_form_an_unbroken_hash_chain()
    {
        var ct = CancellationToken.None;
        var a = await _acta.AppendAsync(Rev.ChainId, BlockKind.Summons, Summons(), ct);
        var b = await _acta.AppendAsync(Rev.ChainId, BlockKind.Seating, new SeatingPayload(Rev.Id, 0, _identity.Id), ct);
        var c = await _acta.AppendAsync(
            Rev.ChainId, BlockKind.Ballot,
            new BallotPayload(Rev.Id, 0, ReviewDecision.Approve, [], "m", 1), ct);

        var chain = await _acta.ReadChainAsync(Rev.ChainId, ct);

        Assert.Equal(3, chain.Count);
        Assert.Equal([0L, 1L, 2L], chain.Select(x => x.Index));
        Assert.Equal(a.Hash(), chain[1].PrevHash);
        Assert.Equal(b.Hash(), chain[2].PrevHash);
        Assert.All(chain, x => Assert.True(x.VerifySignature()));
        Assert.Equal(c.Hash(), chain[2].Hash());
    }

    [Fact]
    public async Task Chains_are_independent_so_each_pr_starts_at_index_zero()
    {
        var ct = CancellationToken.None;
        var one = await _acta.AppendAsync("pr:a:1", BlockKind.Summons, Summons(), ct);
        var two = await _acta.AppendAsync("pr:b:2", BlockKind.Summons, Summons(), ct);

        // 每个 PR 一条独立链 —— 这是不需要全局排序（也就不需要共识算法）的根本原因。
        Assert.Equal(0, one.Index);
        Assert.Equal(0, two.Index);
    }

    [Fact]
    public async Task Round_trip_preserves_payload_and_timestamp()
    {
        var ct = CancellationToken.None;
        var written = await _acta.AppendAsync(Rev.ChainId, BlockKind.Summons, Summons(3), ct);

        var read = (await _acta.ReadChainAsync(Rev.ChainId, ct))[0];
        var payload = read.Payload<SummonsPayload>();

        Assert.Equal(written.Hash(), read.Hash());
        Assert.NotNull(payload);
        Assert.Equal(3, payload.Quorum);
        Assert.Equal(Rev.Id, payload.Revision.Id);
        Assert.Equal("rules1", payload.RulesFingerprint);
    }

    [Fact]
    public async Task Open_chains_exclude_promulgated_revisions()
    {
        var ct = CancellationToken.None;
        _ = await _acta.AppendAsync(Rev.ChainId, BlockKind.Summons, Summons(), ct);
        Assert.Contains(Rev.ChainId, await _acta.ReadOpenChainsAsync(ct));

        _ = await _acta.AppendAsync(
            Rev.ChainId, BlockKind.Promulgation,
            new PromulgationPayload(Rev.Id, ReviewDecision.Approve, [], false, 1, 1), ct);

        Assert.DoesNotContain(Rev.ChainId, await _acta.ReadOpenChainsAsync(ct));
    }

    [Fact]
    public async Task A_new_revision_reopens_the_chain()
    {
        var ct = CancellationToken.None;
        _ = await _acta.AppendAsync(Rev.ChainId, BlockKind.Summons, Summons(), ct);
        _ = await _acta.AppendAsync(
            Rev.ChainId, BlockKind.Promulgation,
            new PromulgationPayload(Rev.Id, ReviewDecision.Approve, [], false, 1, 1), ct);

        var next = new Revision(Rev.Project, Rev.PrId, "bbbbbbbb22222222");
        _ = await _acta.AppendAsync(
            Rev.ChainId, BlockKind.Summons,
            new SummonsPayload(next, TestElectors.Pr(), 1, "rules1"), ct);

        // 作者 push 了新 commit → Summons 数 > Promulgation 数 → 这条链重新变成待办。
        Assert.Contains(Rev.ChainId, await _acta.ReadOpenChainsAsync(ct));
    }

    [Fact]
    public async Task Ballot_count_only_counts_this_elector_s_ballots()
    {
        var ct = CancellationToken.None;
        _ = await _acta.AppendAsync(
            Rev.ChainId, BlockKind.Ballot,
            new BallotPayload(Rev.Id, 0, ReviewDecision.Approve, [], "m", 1), ct);
        _ = await _acta.AppendAsync(Rev.ChainId, BlockKind.Summons, Summons(), ct);

        Assert.Equal(1, await _acta.CountRecentBallotsAsync(_identity.Id, ct));
        Assert.Equal(0, await _acta.CountRecentBallotsAsync("someone-else", ct));
    }

    [Fact]
    public async Task Foreign_block_is_rejected_when_the_elector_is_not_whitelisted()
    {
        var ct = CancellationToken.None;
        using var stranger = ElectorIdentity.CreateEphemeral();
        var block = SignAs(stranger, Rev.ChainId, 0, Block.GenesisPrevHash);

        // 白名单不是「以后再加」：别人的块会让别人的评审任务落到本机跑 Bash。
        Assert.False(await _acta.TryApplyAsync(block, ct));
        Assert.Empty(await _acta.ReadChainAsync(Rev.ChainId, ct));
    }

    [Fact]
    public async Task Whitelisted_peer_block_is_applied()
    {
        var ct = CancellationToken.None;
        using var peer = ElectorIdentity.CreateEphemeral();
        _acta.Allow(peer.Id);

        var block = SignAs(peer, Rev.ChainId, 0, Block.GenesisPrevHash);

        Assert.True(await _acta.TryApplyAsync(block, ct));
        Assert.Single(await _acta.ReadChainAsync(Rev.ChainId, ct));
    }

    [Fact]
    public async Task Tampered_block_is_rejected_even_from_a_whitelisted_peer()
    {
        var ct = CancellationToken.None;
        using var peer = ElectorIdentity.CreateEphemeral();
        _acta.Allow(peer.Id);

        var block = SignAs(peer, Rev.ChainId, 0, Block.GenesisPrevHash);
        var tampered = block with { PayloadJson = """{"evil":true}""" };

        Assert.False(await _acta.TryApplyAsync(tampered, ct));
    }

    [Fact]
    public async Task Re_applying_the_same_block_is_idempotent()
    {
        var ct = CancellationToken.None;
        using var peer = ElectorIdentity.CreateEphemeral();
        _acta.Allow(peer.Id);

        var block = SignAs(peer, Rev.ChainId, 0, Block.GenesisPrevHash);

        Assert.True(await _acta.TryApplyAsync(block, ct));
        Assert.True(await _acta.TryApplyAsync(block, ct));   // gossip 送了两遍
        Assert.Single(await _acta.ReadChainAsync(Rev.ChainId, ct));
    }

    [Fact]
    public async Task A_gap_in_the_chain_is_refused_rather_than_silently_accepted()
    {
        var ct = CancellationToken.None;
        using var peer = ElectorIdentity.CreateEphemeral();
        _acta.Allow(peer.Id);

        // index 5 但本地链是空的 —— 需要先补链（P2 的 PullChain），不能直接落。
        var block = SignAs(peer, Rev.ChainId, 5, Block.GenesisPrevHash);

        Assert.False(await _acta.TryApplyAsync(block, ct));
    }

    [Fact]
    public async Task A_wrong_prev_hash_is_refused()
    {
        var ct = CancellationToken.None;
        using var peer = ElectorIdentity.CreateEphemeral();
        _acta.Allow(peer.Id);

        _ = await _acta.AppendAsync(Rev.ChainId, BlockKind.Summons, Summons(), ct);
        var block = SignAs(peer, Rev.ChainId, 1, prevHash: new string('9', 64));

        Assert.False(await _acta.TryApplyAsync(block, ct));
    }

    [Fact]
    public async Task Recent_blocks_come_back_newest_first()
    {
        var ct = CancellationToken.None;
        _ = await _acta.AppendAsync(Rev.ChainId, BlockKind.Summons, Summons(), ct);
        _ = await _acta.AppendAsync(Rev.ChainId, BlockKind.Seating, new SeatingPayload(Rev.Id, 0, _identity.Id), ct);

        var recent = await _acta.ReadRecentAsync(10, ct);

        Assert.Equal(BlockKind.Seating, recent[0].Kind);
        Assert.Equal(BlockKind.Summons, recent[1].Kind);
    }

    [Fact]
    public async Task Ledger_survives_reopening_the_database()
    {
        var ct = CancellationToken.None;
        _ = await _acta.AppendAsync(Rev.ChainId, BlockKind.Summons, Summons(), ct);

        var reopened = new SqliteActa(_options, _identity, NullLogger<SqliteActa>.Instance);
        var chain = await reopened.ReadChainAsync(Rev.ChainId, ct);

        Assert.Single(chain);
        Assert.True(chain[0].VerifySignature());
    }

    private static Block SignAs(ElectorIdentity id, string chainId, long index, string prevHash)
    {
        var unsigned = new Block
        {
            ChainId = chainId,
            Index = index,
            PrevHash = prevHash,
            At = DateTimeOffset.UtcNow,
            Kind = BlockKind.Summons,
            PayloadJson = ActaJson.Serialize(Summons()),
            ElectorId = id.Id,
            PublicKey = id.PublicKey,
            Signature = string.Empty,
        };

        return unsigned with { Signature = id.Sign(unsigned.SigningPayload()) };
    }

    public void Dispose()
    {
        _identity.Dispose();
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

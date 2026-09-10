using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
using Conclave.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

public sealed class SqliteActaTests : IDisposable
{
    private readonly string _home;
    private readonly ConclaveOptions _options;
    private readonly ElectorIdentity _identity;
    private readonly MutableAllowList _allowList;
    private readonly SqliteActa _acta;

    public SqliteActaTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "conclave-test-" + Guid.NewGuid().ToString("N"));
        _options = new ConclaveOptions { HomeDirectory = _home };
        _identity = ElectorIdentity.Create();
        _allowList = new MutableAllowList(_identity.Id);
        _acta = new SqliteActa(_options, _identity, _allowList, NullLogger<SqliteActa>.Instance);
    }

    private static readonly Revision Rev = new("liontrip-cms", 2721, "aaaaaaaa11111111");

    private static SummonsPayload Summons(int quorum = 1)
        => new(Rev, TestElectors.Pr(), quorum, "rules1");

    [Fact]
    public async Task First_block_on_a_chain_is_a_genesis_block()
    {
        var block = await _acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(), CancellationToken.None);

        Assert.Equal(0, block.Index);
        Assert.Equal(Block.GenesisPrevHash, block.PrevHash);
        Assert.True(block.VerifySignature());
    }

    [Fact]
    public async Task Appended_blocks_form_an_unbroken_hash_chain()
    {
        var ct = CancellationToken.None;
        var a = await _acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(), ct);
        var b = await _acta.AppendAsync(Rev.Id, BlockKind.Seating, new SeatingPayload(Rev.Id, 0, _identity.Id), ct);
        var c = await _acta.AppendAsync(
            Rev.Id, BlockKind.Ballot,
            new BallotPayload(Rev.Id, 0, ReviewDecision.Approve, [], "m", 1), ct);

        var chain = await _acta.ReadRevisionAsync(Rev.Id, ct);

        Assert.Equal(3, chain.Count);
        Assert.Equal([0L, 1L, 2L], chain.Select(x => x.Index));
        Assert.Equal(a.Hash(), chain[1].PrevHash);
        Assert.Equal(b.Hash(), chain[2].PrevHash);
        Assert.All(chain, x => Assert.True(x.VerifySignature()));
        Assert.Equal(c.Hash(), chain[2].Hash());
    }

    [Fact]
    public async Task All_revisions_share_one_index_space_but_stay_queryable_apart()
    {
        var ct = CancellationToken.None;
        var one = await _acta.AppendAsync("1@aaaaaaaa", BlockKind.Summons, Summons(), ct);
        var two = await _acta.AppendAsync("2@bbbbbbbb", BlockKind.Summons, Summons(), ct);

        // 全局单链：索引是共享的一条时间线，不同 PR 的块前后相连。
        Assert.Equal(0, one.Index);
        Assert.Equal(1, two.Index);
        Assert.Equal(one.Hash(), two.PrevHash);
        Assert.Equal(Domain.Acta.ChainId, one.ChainId);

        // 「按 PR 查」不再靠 ChainId，靠 revision_id 那一列。
        Assert.Single(await _acta.ReadRevisionAsync("1@aaaaaaaa", ct));
        Assert.Single(await _acta.ReadRevisionAsync("2@bbbbbbbb", ct));
        Assert.Equal(2, (await _acta.ReadChainAsync(0, ct)).Count);
    }

    [Fact]
    public async Task Round_trip_preserves_payload_and_timestamp()
    {
        var ct = CancellationToken.None;
        var written = await _acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(3), ct);

        var read = (await _acta.ReadRevisionAsync(Rev.Id, ct))[0];
        var payload = read.Payload<SummonsPayload>();

        Assert.Equal(written.Hash(), read.Hash());
        Assert.NotNull(payload);
        Assert.Equal(3, payload.Quorum);
        Assert.Equal(Rev.Id, payload.Revision.Id);
        Assert.Equal("rules1", payload.RulesFingerprint);
    }

    [Fact]
    public async Task The_summary_counts_valid_ballots_and_marks_finished_revisions()
    {
        var ct = CancellationToken.None;

        // 链上只记完成的评审了。「队列里有什么」是实时状态的事，链只回答「哪些评过了」——
        // 原先那个「Summons 数 > Promulgation 数」的查询把队列建在历史上，
        // PR 在 ADO 上关掉之后没人清，永远留在队列里（实测 41 个里 36 个是僵尸）。
        Assert.Empty((await _acta.ReadSummaryAsync(ct)).ValidBallots);
        Assert.Empty((await _acta.ReadSummaryAsync(ct)).Finished);

        _ = await _acta.AppendAsync(Rev.Id, BlockKind.Ballot, Ballot(0, ReviewDecision.Reject), ct);

        var afterBallot = await _acta.ReadSummaryAsync(ct);
        Assert.Equal(1, afterBallot.ValidBallots[Rev.Id]);
        Assert.Empty(afterBallot.Finished);

        _ = await _acta.AppendAsync(
            Rev.Id, BlockKind.Promulgation,
            new PromulgationPayload(Rev.Id, ReviewDecision.Reject, [], false, 1, 1), ct);

        Assert.Contains(Rev.Id, (await _acta.ReadSummaryAsync(ct)).Finished);
    }

    [Fact]
    public async Task The_summary_does_not_count_error_ballots()
    {
        var ct = CancellationToken.None;
        _ = await _acta.AppendAsync(Rev.Id, BlockKind.Ballot, Ballot(0, ReviewDecision.Error), ct);

        // Error 票只说明那一轮跑失败了，要换个节点重试 —— 计入的话 quorum=1 时
        // 一次偶发的子进程失败就会被当成「已经评过了」，这个 PR 再也不会被评。
        var summary = await _acta.ReadSummaryAsync(ct);
        Assert.False(summary.ValidBallots.ContainsKey(Rev.Id));
        Assert.Empty(summary.Finished);
    }

    private static BallotPayload Ballot(int round, ReviewDecision decision) => new(
        Rev.Id, round, decision, [], "claude-opus-5", 1234, ReviewUsage.None, "alan")
    {
        Pr = TestElectors.Pr(),
    };

    [Fact]
    public async Task Ballot_count_only_counts_this_elector_s_ballots()
    {
        var ct = CancellationToken.None;
        _ = await _acta.AppendAsync(
            Rev.Id, BlockKind.Ballot,
            new BallotPayload(Rev.Id, 0, ReviewDecision.Approve, [], "m", 1), ct);
        _ = await _acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(), ct);

        Assert.Equal(1, await _acta.CountRecentBallotsAsync(_identity.Id, ct));
        Assert.Equal(0, await _acta.CountRecentBallotsAsync("someone-else", ct));
    }

    [Fact]
    public async Task Foreign_block_is_rejected_when_the_elector_is_not_whitelisted()
    {
        var ct = CancellationToken.None;
        using var stranger = ElectorIdentity.Create();
        var block = SignAs(stranger, 0, Block.GenesisPrevHash);

        // 白名单不是「以后再加」：别人的块会让别人的评审任务落到本机跑 Bash。
        Assert.False((await _acta.TryApplyAsync(block, ct)).Applied);
        Assert.Empty(await _acta.ReadRevisionAsync(Rev.Id, ct));
    }

    [Fact]
    public async Task Whitelisted_peer_block_is_applied()
    {
        var ct = CancellationToken.None;
        using var peer = ElectorIdentity.Create();
        _allowList.Allow(peer.Id);

        var block = SignAs(peer, 0, Block.GenesisPrevHash);

        Assert.True((await _acta.TryApplyAsync(block, ct)).Applied);
        Assert.Single(await _acta.ReadRevisionAsync(Rev.Id, ct));
    }

    [Fact]
    public async Task Tampered_block_is_rejected_even_from_a_whitelisted_peer()
    {
        var ct = CancellationToken.None;
        using var peer = ElectorIdentity.Create();
        _allowList.Allow(peer.Id);

        var block = SignAs(peer, 0, Block.GenesisPrevHash);
        var tampered = block with { PayloadJson = """{"evil":true}""" };

        Assert.False((await _acta.TryApplyAsync(tampered, ct)).Applied);
    }

    [Fact]
    public async Task Re_applying_the_same_block_is_idempotent_but_not_novel()
    {
        var ct = CancellationToken.None;
        using var peer = ElectorIdentity.Create();
        _allowList.Allow(peer.Id);

        var block = SignAs(peer, 0, Block.GenesisPrevHash);

        var first = await _acta.TryApplyAsync(block, ct);
        var second = await _acta.TryApplyAsync(block, ct);   // gossip 送了两遍

        Assert.Equal(ApplyOutcome.Applied, first.Outcome);
        Assert.True(first.Novel);

        // 第二遍仍算「在账本里」，但**不是新块** —— gossip 只转发新块，
        // 否则区块会在两个节点之间无限回弹，实测把进程 OOM 掉过。
        Assert.Equal(ApplyOutcome.AlreadyPresent, second.Outcome);
        Assert.True(second.Applied);
        Assert.False(second.Novel);

        Assert.Single(await _acta.ReadRevisionAsync(Rev.Id, ct));
    }

    [Fact]
    public async Task A_gap_in_the_chain_is_refused_rather_than_silently_accepted()
    {
        var ct = CancellationToken.None;
        using var peer = ElectorIdentity.Create();
        _allowList.Allow(peer.Id);

        // index 5 但本地链是空的 —— 需要先补链（P2 的 PullChain），不能直接落。
        var block = SignAs(peer, 5, Block.GenesisPrevHash);

        Assert.False((await _acta.TryApplyAsync(block, ct)).Applied);
    }

    [Fact]
    public async Task A_wrong_prev_hash_is_refused()
    {
        var ct = CancellationToken.None;
        using var peer = ElectorIdentity.Create();
        _allowList.Allow(peer.Id);

        _ = await _acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(), ct);
        var block = SignAs(peer, 1, prevHash: new string('9', 64));

        Assert.False((await _acta.TryApplyAsync(block, ct)).Applied);
    }

    [Fact]
    public async Task A_block_claiming_a_different_chain_is_rejected()
    {
        var ct = CancellationToken.None;
        using var peer = ElectorIdentity.Create();
        _allowList.Allow(peer.Id);

        var unsigned = new Block
        {
            ChainId = "pr:edison-test:2878",      // 旧的「每 PR 一条链」写法
            Index = 0,
            PrevHash = Block.GenesisPrevHash,
            At = DateTimeOffset.UtcNow,
            Kind = BlockKind.Summons,
            PayloadJson = ActaJson.Serialize(Summons()),
            ElectorId = peer.Id,
            PublicKey = peer.PublicKey,
            Signature = string.Empty,
        };
        var foreign = unsigned with { Signature = peer.Sign(unsigned.SigningPayload()) };

        // 唯一索引是 (chain_id, block_index)，不校验链 ID 的话这块能占住 index 0
        // 而不触发冲突判定 —— 账本里就会并存两条互不相干的序列。
        Assert.True(foreign.VerifySignature());
        Assert.Equal(ApplyOutcome.Rejected, (await _acta.TryApplyAsync(foreign, ct)).Outcome);
        Assert.Empty(await _acta.ReadChainAsync(0, ct));
    }

    [Fact]
    public async Task Recent_blocks_come_back_newest_first()
    {
        var ct = CancellationToken.None;
        _ = await _acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(), ct);
        _ = await _acta.AppendAsync(Rev.Id, BlockKind.Seating, new SeatingPayload(Rev.Id, 0, _identity.Id), ct);

        var recent = await _acta.ReadRecentAsync(10, ct);

        Assert.Equal(BlockKind.Seating, recent[0].Kind);
        Assert.Equal(BlockKind.Summons, recent[1].Kind);
    }

    [Fact]
    public async Task Ledger_survives_reopening_the_database()
    {
        var ct = CancellationToken.None;
        _ = await _acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(), ct);

        var reopened = new SqliteActa(_options, _identity, _allowList, NullLogger<SqliteActa>.Instance);
        var chain = await reopened.ReadRevisionAsync(Rev.Id, ct);

        Assert.Single(chain);
        Assert.True(chain[0].VerifySignature());
    }

    private static Block SignAs(ElectorIdentity id, long index, string prevHash)
    {
        var unsigned = new Block
        {
            // 全局单链：链 ID 只有一个，且 TryApplyAsync 会校验它。
            ChainId = Domain.Acta.ChainId,
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

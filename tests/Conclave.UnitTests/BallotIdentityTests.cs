using System.Globalization;
using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
using Conclave.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

/// <summary>
/// 票在链上的身份 —— 重挂改不动的那一个。
/// </summary>
/// <remarks>
/// <para>
/// 这一组钉的是一个实测出来的事故：账单报出 134 次评审、$77.43，而链上只有 17 张真票、
/// $11.38，放大 8 倍；同一张票在链上挂了 41 遍；单节点一天打出 622 次索引冲突。
/// </para>
/// <para>
/// 病根是拿 <see cref="Block.Hash"/> 当「这块我是不是已经有了」的判据。它含
/// <c>Index</c> 与 <c>PrevHash</c>，而让位重挂改的正好是这两个 —— 判据认不出重挂过的
/// 自己，于是同一份内容被反复入链，每入一次又多一次冲突、多一次重挂。
/// </para>
/// </remarks>
public sealed class BallotIdentityTests : IDisposable
{
    private sealed class Node : IDisposable
    {
        internal Node(string tag)
        {
            Home = Path.Combine(Path.GetTempPath(), $"conclave-identity-{tag}-{Guid.NewGuid():N}");
            Identity = ElectorIdentity.Create();
            AllowList = new MutableAllowList(Identity.Id);
            Options = new ConclaveOptions { HomeDirectory = Home };
            Acta = new SqliteActa(Options, Identity, AllowList, NullLogger<SqliteActa>.Instance);
            Log = new SqliteReviewLog(Options);
        }

        internal string Home { get; }

        internal ConclaveOptions Options { get; }

        internal ElectorIdentity Identity { get; }

        internal MutableAllowList AllowList { get; }

        internal SqliteActa Acta { get; }

        internal SqliteReviewLog Log { get; }

        internal Task<IReadOnlyList<Block>> ChainAsync()
            => Acta.ReadChainAsync(0, int.MaxValue, CancellationToken.None);

        public void Dispose()
        {
            Identity.Dispose();
            try
            {
                Directory.Delete(Home, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清不掉不影响结论
            }
        }
    }

    private static readonly Revision Rev = new("edison-test", 2880, "a6478fa5cafe0001");

    private readonly Node _a = new("a");
    private readonly Node _b = new("b");

    public BallotIdentityTests()
    {
        _a.AllowList.Allow(_b.Identity.Id);
        _b.AllowList.Allow(_a.Identity.Id);
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    private static SummonsPayload Summons() => new(Rev, TestElectors.Pr(), 2, "rules1");

    private static BallotPayload Ballot() => new(
        Rev.Id, 0, ReviewDecision.WaitForAuthor, [], "claude-opus-5", 8);

    /// <summary>
    /// 照 <c>SqliteActa.RebaseAsync</c> 的做法把一块重挂到新索引：改 Index / PrevHash、重签，
    /// <b>其余字段一个不动</b>（尤其是 <c>At</c>）。
    /// </summary>
    private static Block Reattach(ElectorIdentity identity, Block block, long index, string prevHash)
    {
        var moved = block with { Index = index, PrevHash = prevHash, Signature = string.Empty };
        return moved with { Signature = identity.Sign(moved.SigningPayload()) };
    }

    /// <summary>
    /// 把一块的若干份副本挂到链尾，造出改造之前那个现场。
    /// </summary>
    private async Task SeedCopiesAsync(Block block, int copies)
    {
        var tail = (await _a.ChainAsync())[^1];
        for (var i = 0; i < copies; i++)
        {
            var copy = Reattach(_a.Identity, block, tail.Index + 1, tail.Hash());
            await InsertRawAsync(copy);
            tail = copy;
        }
    }

    /// <summary>对端（<c>_b</c>）签的一张票。</summary>
    private Block SignAsPeer(long index, string prevHash)
    {
        var unsigned = new Block
        {
            ChainId = Domain.Acta.ChainId,
            Index = index,
            PrevHash = prevHash,
            At = DateTimeOffset.UtcNow,
            Kind = BlockKind.Ballot,
            PayloadJson = ActaJson.Serialize(Ballot() with { Round = 9 }),
            ElectorId = _b.Identity.Id,
            PublicKey = _b.Identity.PublicKey,
            Signature = string.Empty,
        };

        return unsigned with { Signature = _b.Identity.Sign(unsigned.SigningPayload()) };
    }

    /// <summary>
    /// 绕开写入路径直接塞一块进库。
    /// </summary>
    /// <remarks>
    /// 只给压实的用例用：<c>TryApplyAsync</c> 现在会挡住重复内容，而这些用例要测的恰恰是
    /// 「挡住之前已经写脏的库怎么收拾」，只能自己造出那个现场。
    /// </remarks>
    private async Task InsertRawAsync(Block block)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _a.Options.ActaPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString();

        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO blocks
                (chain_id, block_index, revision_id, prev_hash, block_hash, created_at, kind,
                 payload, elector_id, public_key, signature, content_id)
            VALUES
                ($chain, $index, $revision, $prev, $hash, $at, $kind,
                 $payload, $elector, $pubkey, $sig, $content)
            """;
        _ = cmd.Parameters.AddWithValue("$chain", block.ChainId);
        _ = cmd.Parameters.AddWithValue("$index", block.Index);
        _ = cmd.Parameters.AddWithValue("$revision", Domain.Acta.RevisionIdOf(block) ?? string.Empty);
        _ = cmd.Parameters.AddWithValue("$prev", block.PrevHash);
        _ = cmd.Parameters.AddWithValue("$hash", block.Hash());
        _ = cmd.Parameters.AddWithValue(
            "$at", block.At.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
        _ = cmd.Parameters.AddWithValue("$kind", block.Kind.ToString());
        _ = cmd.Parameters.AddWithValue("$payload", block.PayloadJson);
        _ = cmd.Parameters.AddWithValue("$elector", block.ElectorId);
        _ = cmd.Parameters.AddWithValue("$pubkey", block.PublicKey);
        _ = cmd.Parameters.AddWithValue("$sig", block.Signature);
        _ = cmd.Parameters.AddWithValue("$content", block.ContentId());
        _ = await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public void Reattaching_changes_the_block_hash_but_not_the_content_id()
    {
        using var identity = ElectorIdentity.Create();

        var original = new Block
        {
            ChainId = Domain.Acta.ChainId,
            Index = 1,
            PrevHash = Block.GenesisPrevHash,
            At = DateTimeOffset.UtcNow,
            Kind = BlockKind.Ballot,
            PayloadJson = ActaJson.Serialize(Ballot()),
            ElectorId = identity.Id,
            PublicKey = identity.PublicKey,
            Signature = string.Empty,
        };

        var signed = original with { Signature = identity.Sign(original.SigningPayload()) };
        var moved = Reattach(identity, signed, 7, new string('a', 64));

        // 块哈希必须变 —— 它是「链上这个位置」的哈希，重挂就是换位置。
        Assert.NotEqual(signed.Hash(), moved.Hash());
        Assert.NotEqual(signed.Signature, moved.Signature);

        // 而票还是那张票。
        Assert.Equal(signed.ContentId(), moved.ContentId());
        Assert.True(moved.VerifySignature());
    }

    [Fact]
    public async Task Content_id_distinguishes_two_genuinely_different_ballots()
    {
        var ct = CancellationToken.None;

        var first = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Ballot, Ballot(), ct);
        var second = await _a.Acta.AppendAsync(
            Rev.Id, BlockKind.Ballot,
            Ballot() with { Round = 1, Decision = ReviewDecision.Reject }, ct);

        Assert.NotEqual(first.ContentId(), second.ContentId());
    }

    [Fact]
    public async Task A_reattached_copy_of_a_ballot_we_already_have_is_not_appended_again()
    {
        var ct = CancellationToken.None;

        // 双方同步到同一个起点，再同步一张票。
        var genesis = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(), ct);
        Assert.True((await _b.Acta.TryApplyAsync(genesis, ct)).Applied);

        var ballot = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Ballot, Ballot(), ct);
        Assert.True((await _b.Acta.TryApplyAsync(ballot, ct)).Applied);

        var before = await _b.ChainAsync();
        Assert.Equal(2, before.Count);

        // A 那边发生了一次让位，把这张票重挂到了新索引，然后 gossip 推了过来。
        // 这个副本的 index 正好接在 B 的链尾、PrevHash 也对得上 —— 旧代码在这里
        // 一路走到 INSERT，于是同一张票在 B 上有了两份。
        var reattached = Reattach(_a.Identity, ballot, before[^1].Index + 1, before[^1].Hash());
        Assert.NotEqual(ballot.Hash(), reattached.Hash());

        var result = await _b.Acta.TryApplyAsync(reattached, ct);

        Assert.Equal(ApplyOutcome.AlreadyPresent, result.Outcome);

        // Novel 为假才不会被 gossip 再转发出去 —— 转发出去就是让整个 mesh 一起放大。
        Assert.False(result.Novel);
        Assert.Equal(2, (await _b.ChainAsync()).Count);

        // 账单才是人真正会看的地方：一张票就是一次。
        var total = await _b.Log.ReadTotalAsync(null, null, ct);
        Assert.Equal(1, total.Reviews);
    }

    [Fact]
    public async Task A_rebase_does_not_double_count_the_ballot_in_the_ledger()
    {
        var ct = CancellationToken.None;

        var genesis = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(), ct);
        Assert.True((await _b.Acta.TryApplyAsync(genesis, ct)).Applied);

        // 双方在同一个索引上各写一块，必然有一方让位重挂。
        var mine = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Ballot, Ballot(), ct);
        var theirs = await _b.Acta.AppendAsync(
            Rev.Id, BlockKind.Seating, new SeatingPayload(Rev.Id, 0, _b.Identity.Id), ct);

        var result = await _a.Acta.TryApplyAsync(theirs, ct);

        // 不管谁赢，A 手上都只有一张票。
        var total = await _a.Log.ReadTotalAsync(null, null, ct);
        Assert.Equal(1, total.Reviews);

        // 重挂出来的副本按 MeshService 的做法推回给自己，也不该再多一份。
        foreach (var moved in result.Rebased)
        {
            _ = await _a.Acta.TryApplyAsync(moved, ct);
        }

        Assert.Equal(1, (await _a.Log.ReadTotalAsync(null, null, ct)).Reviews);
        Assert.Equal(mine.ContentId(), result.Rebased.Count > 0
            ? result.Rebased[0].ContentId()
            : mine.ContentId());
    }

    [Fact]
    public async Task Duplicate_ballots_on_a_dirty_chain_do_not_count_toward_quorum()
    {
        var ct = CancellationToken.None;

        _ = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(), ct);
        var ballot = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Ballot, Ballot(), ct);

        var one = await _a.Acta.ReadSummaryAsync([Rev.Id], ct);
        Assert.Equal(1, one.ValidBallots[Rev.Id]);

        // 直接往库里塞两份同内容的块 —— 改造之前的链就是这个样子。
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _a.Options.ActaPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString();

        using (var conn = new SqliteConnection(connectionString))
        {
            conn.Open();
            for (var i = 0; i < 2; i++)
            {
                using var dup = conn.CreateCommand();
                dup.CommandText = $"""
                    INSERT INTO blocks
                        (chain_id, block_index, revision_id, prev_hash, block_hash, created_at,
                         kind, payload, elector_id, public_key, signature, content_id)
                    SELECT chain_id, 2000 + {i}, revision_id, prev_hash, block_hash || '-dup{i}',
                           created_at, kind, payload, elector_id, public_key, signature, content_id
                    FROM blocks WHERE block_hash = $hash
                    """;
                _ = dup.Parameters.AddWithValue("$hash", ballot.Hash());
                _ = dup.ExecuteNonQuery();
            }
        }

        // 三份块，但仍然只是一张票。数成三票的话，法定票数会被<b>同一个人的同一票</b>凑够。
        var after = await _a.Acta.ReadSummaryAsync([Rev.Id], ct);
        Assert.Equal(1, after.ValidBallots[Rev.Id]);
    }

    [Fact]
    public async Task The_display_view_shows_one_row_per_ballot_however_many_copies_exist()
    {
        var ct = CancellationToken.None;

        _ = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(), ct);
        var ballot = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Ballot, Ballot(), ct);

        await SeedCopiesAsync(ballot, 20);

        // 「Acta 会议录」那张表读的就是这个。
        var recent = await _a.Acta.ReadRecentAsync(100, ct);
        Assert.Equal(2, recent.Count);

        // 状态栏那行「链 N 块」也是去重后的数。
        var health = await _a.Acta.ReadHealthAsync(ct);
        Assert.Equal(2, health.Blocks);

        // 近 24H 出票数是 Elector.Weight 的输入 —— 数成 21 票会让这台机器被系统性地少派活。
        Assert.Equal(1, await _a.Acta.CountRecentBallotsAsync(_a.Identity.Id, ct));
    }

    [Fact]
    public async Task The_catch_up_view_stays_literal_so_peers_can_still_verify_the_links()
    {
        var ct = CancellationToken.None;

        _ = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(), ct);
        var ballot = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Ballot, Ballot(), ct);

        await SeedCopiesAsync(ballot, 5);

        // ReadChainAsync 是补链用的（MeshHttpServer 拿它回答对端「我缺 N 之后的块」）。
        // 它<b>必须</b>原样返回：对端收到后要逐块验 PrevHash 咬不咬得上，
        // 少给一块，补过去的链就断在那里，对端从此再也追不上。
        var chain = await _a.Acta.ReadChainAsync(0, int.MaxValue, ct);
        Assert.Equal(7, chain.Count);

        var prev = Block.GenesisPrevHash;
        for (var i = 0; i < chain.Count; i++)
        {
            Assert.Equal(i, chain[i].Index);
            Assert.Equal(prev, chain[i].PrevHash);
            prev = chain[i].Hash();
        }
    }

    [Fact]
    public async Task Peer_blocks_are_never_rewritten_so_the_mesh_can_still_converge()
    {
        var ct = CancellationToken.None;

        var genesis = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Summons, Summons(), ct);
        var peerBlock = SignAsPeer(genesis.Index + 1, genesis.Hash());
        Assert.True((await _a.Acta.TryApplyAsync(peerBlock, ct)).Novel);

        await SeedCopiesAsync(genesis, 3);

        // 链上没有任何一块被改写过 —— 收敛规则的前提就是各节点从同一段链头长出来，
        // 谁单方面重写历史，谁就再也跟别人对不上。对端签的块尤其动不得：我们没有它的私钥。
        var chain = await _a.Acta.ReadChainAsync(0, int.MaxValue, ct);
        var peerOnChain = Assert.Single(chain, b => b.ElectorId == _b.Identity.Id);

        Assert.Equal(peerBlock.Hash(), peerOnChain.Hash());
        Assert.Equal(peerBlock.Signature, peerOnChain.Signature);
        Assert.True(peerOnChain.VerifySignature());
    }

    [Fact]
    public async Task Opening_a_ledger_that_already_has_duplicates_cleans_it_up()
    {
        var ct = CancellationToken.None;

        var ballot = await _a.Acta.AppendAsync(Rev.Id, BlockKind.Ballot, Ballot(), ct);
        var one = await _a.Log.ReadTotalAsync(null, null, ct);
        Assert.Equal(1, one.Reviews);

        // 把库退回改造之前的样子：没有 content_id、同一票存着 11 份（各带不同的块哈希，
        // 正是重挂反复入链留下的形态）。实测那个库里最狠的一张票有 41 份。
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _a.Options.ActaPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString();

        using (var conn = new SqliteConnection(connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DROP INDEX IF EXISTS ux_reviews_content;
                DROP INDEX IF EXISTS ux_reviews_block_hash;
                UPDATE reviews SET content_id = '';
                """;
            _ = cmd.ExecuteNonQuery();

            for (var i = 0; i < 10; i++)
            {
                using var dup = conn.CreateCommand();

                // 同一块内容挂在不同索引上、块哈希各不相同 —— 重挂反复入链留下的正是这个形态。
                dup.CommandText = $"""
                    INSERT INTO blocks
                        (chain_id, block_index, revision_id, prev_hash, block_hash, created_at,
                         kind, payload, elector_id, public_key, signature, content_id)
                    SELECT chain_id, 1000 + {i}, revision_id, prev_hash, block_hash || '-dup{i}',
                           created_at, kind, payload, elector_id, public_key, signature, ''
                    FROM blocks WHERE block_hash = $hash;

                    INSERT INTO reviews
                        (content_id, block_hash, revision_id, project, repo, pr_id, pr_title,
                         pr_author, reviewer_id, reviewer_az, seat_round, status, findings,
                         reviewed_at, duration_ms, model, turns, input_tokens, output_tokens,
                         cache_read_tokens, cache_write_tokens, thinking_tokens, cost_usd, cost_basis)
                    SELECT '', block_hash || '-dup{i}', revision_id, project, repo, pr_id, pr_title,
                           pr_author, reviewer_id, reviewer_az, seat_round, status, findings,
                           reviewed_at, duration_ms, model, turns, input_tokens, output_tokens,
                           cache_read_tokens, cache_write_tokens, thinking_tokens, cost_usd, cost_basis
                    FROM reviews WHERE block_hash = $hash
                    """;
                _ = dup.Parameters.AddWithValue("$hash", ballot.Hash());
                _ = dup.ExecuteNonQuery();
            }
        }

        Assert.Equal(11, (await _a.Log.ReadTotalAsync(null, null, ct)).Reviews);

        // 重开一次账本就会跑迁移：补身份、去重、建唯一索引。
        using var identity = ElectorIdentity.Create();
        var reopened = new SqliteActa(
            _a.Options, identity, _a.AllowList, NullLogger<SqliteActa>.Instance);
        GC.KeepAlive(reopened);

        var after = await _a.Log.ReadTotalAsync(null, null, ct);
        Assert.Equal(1, after.Reviews);
        Assert.Equal(one.CostUsd, after.CostUsd);
    }
}

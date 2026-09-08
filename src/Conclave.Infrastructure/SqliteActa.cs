using System.Globalization;
using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure;

/// <summary>
/// Acta 账本的 SQLite 实现：一条全局链 + 一层可查的评审投影。
/// </summary>
/// <remarks>
/// <para>
/// <b>链是唯一真相，<c>reviews</c> 表只是可查缓存。</b> 表里每一行都指回它来源的
/// <c>block_hash</c>，任何时候都能从链上重建。所以让位重挂（会改块哈希）之后
/// 必须重建投影，否则报表里会留下指向已不存在区块的幽灵行。
/// </para>
/// <para>
/// 刻意不上 EF Core：两张 append-only 表加一层投影，手写 SQL 比 ORM 直白，
/// 也不必为它把 EF 整栈拖进 Infrastructure。
/// </para>
/// </remarks>
public sealed class SqliteActa : IActaStore
{
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";
    private const string ConflictsSeen = "index_conflicts";
    private const string ConflictsLost = "index_conflicts_lost";

    private readonly string _connectionString;
    private readonly ElectorIdentity _identity;
    private readonly IElectorAllowList _allowList;
    private readonly ILogger<SqliteActa> _logger;

    /// <summary>SQLite 单写者。并发写会撞 SQLITE_BUSY，这里直接串行化。</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SqliteActa(
        ConclaveOptions options,
        ElectorIdentity identity,
        IElectorAllowList allowList,
        ILogger<SqliteActa> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(identity);

        _identity = identity;
        _allowList = allowList;
        _logger = logger;

        _ = Directory.CreateDirectory(options.HomeDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.ActaPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        ActaSchema.EnsureCreated(_connectionString);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public async Task<Block> AppendAsync<TPayload>(
        string revisionId, BlockKind kind, TPayload payload, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            var (tailIndex, tailHash) = await ReadTailAsync(conn, ct).ConfigureAwait(false);

            var unsigned = new Block
            {
                ChainId = Acta.ChainId,
                Index = tailIndex + 1,
                PrevHash = tailHash ?? Block.GenesisPrevHash,
                At = DateTimeOffset.UtcNow,
                Kind = kind,
                PayloadJson = ActaJson.Serialize(payload),
                ElectorId = _identity.Id,
                PublicKey = _identity.PublicKey,
                Signature = string.Empty,
            };

            var block = unsigned with { Signature = _identity.Sign(unsigned.SigningPayload()) };
            await InsertAsync(conn, block, revisionId, ct).ConfigureAwait(false);
            await ProjectBallotAsync(conn, block, ct).ConfigureAwait(false);
            return block;
        }
        finally
        {
            _ = _writeGate.Release();
        }
    }

    public async Task<ApplyResult> TryApplyAsync(Block block, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(block);

        // 必须先校验它属于本链。唯一索引是 (chain_id, block_index)，所以一个声称别的
        // ChainId 的块可以占用同一个索引而不触发冲突判定 —— 那样「全局单链」的
        // 不变式就破了，链上会并存两条互不相干的序列。
        if (block.ChainId != Acta.ChainId)
        {
            _logger.LogWarning(
                "拒收区块 #{Index}：链 ID 是 {Chain}，本节点只认 {Expected}",
                block.Index, block.ChainId, Acta.ChainId);
            return ApplyResult.Rejected;
        }

        if (!block.VerifySignature())
        {
            _logger.LogWarning("拒收区块 {Chain}#{Index}：验签失败", block.ChainId, block.Index);
            return ApplyResult.Rejected;
        }

        // 白名单检查刻意在冲突判定之前：否则外人只要造一个哈希更小的块就能改写别人的链。
        if (!_allowList.IsAllowed(block.ElectorId))
        {
            _logger.LogDebug(
                "拒收区块 {Chain}#{Index}：节点 {Elector} 不在白名单",
                block.ChainId, block.Index, block.ElectorId);
            return ApplyResult.Rejected;
        }

        var revisionId = Acta.RevisionIdOf(block);
        if (string.IsNullOrEmpty(revisionId))
        {
            _logger.LogWarning(
                "拒收区块 {Chain}#{Index}：载荷里读不出 revision id", block.ChainId, block.Index);
            return ApplyResult.Rejected;
        }

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = Open();

            var existing = await ReadAtAsync(conn, block.Index, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.Hash() == block.Hash())
                {
                    return ApplyResult.AlreadyPresent;   // 同一个块被 gossip 送重了
                }

                await BumpAsync(conn, ConflictsSeen, ct).ConfigureAwait(false);

                // 同 index 两个不同块，blockHash 字典序小者胜出。双方各自执行同一规则，
                // 不需要协商就会收敛到同一条链。
                if (string.CompareOrdinal(existing.Hash(), block.Hash()) < 0)
                {
                    _logger.LogInformation(
                        "索引冲突 #{Index}：本地块胜出，拒收远端块", block.Index);
                    return ApplyResult.Rejected;
                }

                await BumpAsync(conn, ConflictsLost, ct).ConfigureAwait(false);
                return await RebaseAsync(conn, block, revisionId, ct).ConfigureAwait(false);
            }

            var (tailIndex, tailHash) = await ReadTailAsync(conn, ct).ConfigureAwait(false);
            var expectedPrev = tailHash ?? Block.GenesisPrevHash;

            if (block.Index != tailIndex + 1)
            {
                _logger.LogInformation(
                    "区块 #{Index} 与本地链尾 #{Tail} 不连续，需要补链", block.Index, tailIndex);
                return ApplyResult.Rejected;
            }

            if (block.PrevHash != expectedPrev)
            {
                _logger.LogWarning("区块 #{Index} 的 PrevHash 与本地链尾不符", block.Index);
                return ApplyResult.Rejected;
            }

            await InsertAsync(conn, block, revisionId, ct).ConfigureAwait(false);
            await ProjectBallotAsync(conn, block, ct).ConfigureAwait(false);
            return ApplyResult.Accepted;
        }
        finally
        {
            _ = _writeGate.Release();
        }
    }

    /// <summary>
    /// 让远端块占据它的索引，把本地从该索引起的整段重挂到新链尾。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这是账本唯一会删块的操作。</b>「append-only」的准确说法是「无冲突时只追加」：
    /// 索引冲突必须有一方让位，否则两条链永远不可能一致。让位规则是纯函数
    /// （blockHash 字典序小者胜出），所以所有节点会算出同一个结果。
    /// </para>
    /// <para>
    /// 让位不能只删冲突那一块 —— 它后面每一块的 <c>PrevHash</c> 都指向它，删掉就断链了。
    /// 所以整段摘下来重挂。而重挂要改 <c>Index</c> 和 <c>PrevHash</c>，这两个字段在签名范围内，
    /// 于是<b>只有本节点自己写的块能重签</b>；别人签的块我们没有它的私钥，只能丢弃，
    /// 等其作者在自己那边做同样的 rebase 后重推。
    /// </para>
    /// <para>
    /// 改成全局单链之后这条路径从异常变成常态，所以它必须便宜且正确：整个过程在一个事务里，
    /// 完事后重建评审投影（块哈希变了，旧投影行会变成指向不存在区块的幽灵行）。
    /// </para>
    /// </remarks>
    private async Task<ApplyResult> RebaseAsync(
        SqliteConnection conn, Block winner, string winnerRevisionId, CancellationToken ct)
    {
        var displaced = await ReadFromAsync(conn, winner.Index, ct).ConfigureAwait(false);

        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await DeleteFromAsync(conn, tx, winner.Index, ct).ConfigureAwait(false);
        await InsertAsync(conn, winner, winnerRevisionId, ct, tx).ConfigureAwait(false);

        var reattached = new List<Block>();
        var dropped = 0;
        var index = winner.Index;
        var prevHash = winner.Hash();

        foreach (var block in displaced)
        {
            if (block.ElectorId != _identity.Id)
            {
                // 别人的块改了 Index/PrevHash 就签不回去了，只能丢，等其作者重推。
                dropped++;
                continue;
            }

            index++;
            var moved = block with { Index = index, PrevHash = prevHash, Signature = string.Empty };
            moved = moved with { Signature = _identity.Sign(moved.SigningPayload()) };

            await InsertAsync(
                conn, moved, Acta.RevisionIdOf(moved) ?? string.Empty, ct, tx).ConfigureAwait(false);
            prevHash = moved.Hash();
            reattached.Add(moved);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);

        // 块哈希变了，投影必须重建，否则报表里留下幽灵行。
        await ReprojectAsync(conn, ct).ConfigureAwait(false);

        _logger.LogWarning(
            "索引冲突 #{Index}：远端块胜出，本地重挂 {Reattached} 块、丢弃 {Dropped} 块（待其作者重推）",
            winner.Index, reattached.Count, dropped);

        return new ApplyResult(ApplyOutcome.Applied, reattached);
    }

    public async Task<IReadOnlyList<Block>> ReadRevisionAsync(string revisionId, CancellationToken ct)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{BlockColumns} WHERE revision_id = $revision ORDER BY block_index";
        _ = cmd.Parameters.AddWithValue("$revision", revisionId);
        return await ReadAllAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ReadOpenRevisionsAsync(CancellationToken ct)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();

        // 每个 revision 恰好一个 Summons、最多一个 Promulgation，
        // 所以 Summons 数 > Promulgation 数 就说明它还没结论。
        // 按最早那块的链序排，让调用方能按 PR 取最新的那个版本。
        cmd.CommandText = """
            SELECT revision_id
            FROM blocks
            GROUP BY revision_id
            HAVING SUM(CASE WHEN kind = 'Summons' THEN 1 ELSE 0 END)
                 > SUM(CASE WHEN kind = 'Promulgation' THEN 1 ELSE 0 END)
            ORDER BY MIN(block_index)
            """;

        var open = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            open.Add(reader.GetString(0));
        }

        return open;
    }

    public async Task<IReadOnlyList<Block>> ReadChainAsync(long fromIndex, CancellationToken ct)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{BlockColumns} WHERE block_index >= $from ORDER BY block_index";
        _ = cmd.Parameters.AddWithValue("$from", fromIndex);
        return await ReadAllAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Block>> ReadRecentAsync(int limit, CancellationToken ct)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{BlockColumns} ORDER BY block_index DESC LIMIT $limit";
        _ = cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        return await ReadAllAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<int> CountRecentBallotsAsync(string electorId, CancellationToken ct)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM blocks
            WHERE kind = 'Ballot' AND elector_id = $elector AND created_at >= $since
            """;
        _ = cmd.Parameters.AddWithValue("$elector", electorId);
        _ = cmd.Parameters.AddWithValue(
            "$since",
            DateTimeOffset.UtcNow.AddHours(-24).UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));

        return Convert.ToInt32(
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<ActaHealth> ReadHealthAsync(CancellationToken ct)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM blocks),
              (SELECT COUNT(DISTINCT revision_id) FROM blocks),
              (SELECT COALESCE(value, 0) FROM acta_counters WHERE name = $seen),
              (SELECT COALESCE(value, 0) FROM acta_counters WHERE name = $lost)
            """;
        _ = cmd.Parameters.AddWithValue("$seen", ConflictsSeen);
        _ = cmd.Parameters.AddWithValue("$lost", ConflictsLost);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new ActaHealth(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt64(3));
        }

        return new ActaHealth(0, 0, 0, 0);
    }

    /// <summary>
    /// 一张 Ballot 落链后写进 <c>reviews</c> 投影。
    /// </summary>
    /// <remarks>
    /// PR 的元数据不从 Ballot 里读 —— 那会让同一份快照在链上存两遍。改为回查同一个
    /// revision 的 Summons 块，它才是 PR 快照的权威来源。
    /// </remarks>
    private static async Task ProjectBallotAsync(
        SqliteConnection conn, Block block, CancellationToken ct)
    {
        if (block.Kind != BlockKind.Ballot)
        {
            return;
        }

        BallotPayload? ballot;
        try
        {
            ballot = block.Payload<BallotPayload>();
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        if (ballot is null)
        {
            return;
        }

        var pr = await ReadSummonsPrAsync(conn, ballot.RevisionId, ct).ConfigureAwait(false);
        var usage = ballot.Metering;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO reviews
                (block_hash, revision_id, project, repo, pr_id, pr_title, pr_author,
                 reviewer_id, reviewer_az, seat_round, status, findings, reviewed_at,
                 duration_ms, model, turns, input_tokens, output_tokens,
                 cache_read_tokens, cache_write_tokens, thinking_tokens, cost_usd, cost_basis)
            VALUES
                ($hash, $revision, $project, $repo, $pr, $title, $author,
                 $reviewer, $az, $round, $status, $findings, $at,
                 $duration, $model, $turns, $in, $out,
                 $cacheRead, $cacheWrite, $thinking, $cost, $basis)
            RETURNING id
            """;
        _ = cmd.Parameters.AddWithValue("$hash", block.Hash());
        _ = cmd.Parameters.AddWithValue("$revision", ballot.RevisionId);
        _ = cmd.Parameters.AddWithValue("$project", pr?.Project ?? string.Empty);
        _ = cmd.Parameters.AddWithValue("$repo", pr?.Repo ?? string.Empty);
        _ = cmd.Parameters.AddWithValue("$pr", pr?.PrId ?? 0);
        _ = cmd.Parameters.AddWithValue("$title", pr?.Title ?? string.Empty);
        _ = cmd.Parameters.AddWithValue("$author", pr?.Author ?? string.Empty);
        _ = cmd.Parameters.AddWithValue("$reviewer", block.ElectorId);
        _ = cmd.Parameters.AddWithValue("$az", ballot.ReviewerAz ?? string.Empty);
        _ = cmd.Parameters.AddWithValue("$round", ballot.Round);
        _ = cmd.Parameters.AddWithValue("$status", ballot.Decision.ToString());
        _ = cmd.Parameters.AddWithValue("$findings", ballot.Findings.Count);
        _ = cmd.Parameters.AddWithValue(
            "$at", block.At.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
        _ = cmd.Parameters.AddWithValue("$duration", ballot.DurationMs);
        _ = cmd.Parameters.AddWithValue("$model", ballot.Model);
        _ = cmd.Parameters.AddWithValue("$turns", usage.Turns);
        _ = cmd.Parameters.AddWithValue("$in", usage.InputTokens);
        _ = cmd.Parameters.AddWithValue("$out", usage.OutputTokens);
        _ = cmd.Parameters.AddWithValue("$cacheRead", usage.CacheReadTokens);
        _ = cmd.Parameters.AddWithValue("$cacheWrite", usage.CacheWriteTokens);
        _ = cmd.Parameters.AddWithValue("$thinking", usage.ThinkingTokens);
        _ = cmd.Parameters.AddWithValue("$cost", (double)usage.CostUsd);
        _ = cmd.Parameters.AddWithValue("$basis", usage.CostBasis);

        var reviewId = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (reviewId is null)
        {
            return;   // OR IGNORE 命中，这一票已经投影过
        }

        foreach (var model in usage.Models)
        {
            await using var detail = conn.CreateCommand();
            detail.CommandText = """
                INSERT INTO review_model_usages
                    (review_id, model, canonical_model, input_tokens, output_tokens,
                     cache_read_tokens, cache_write_tokens, thinking_tokens, cost_usd)
                VALUES ($review, $model, $canonical, $in, $out, $cacheRead, $cacheWrite, $thinking, $cost)
                """;
            _ = detail.Parameters.AddWithValue("$review", Convert.ToInt64(reviewId, CultureInfo.InvariantCulture));
            _ = detail.Parameters.AddWithValue("$model", model.Model);
            _ = detail.Parameters.AddWithValue("$canonical", model.CanonicalModel);
            _ = detail.Parameters.AddWithValue("$in", model.InputTokens);
            _ = detail.Parameters.AddWithValue("$out", model.OutputTokens);
            _ = detail.Parameters.AddWithValue("$cacheRead", model.CacheReadTokens);
            _ = detail.Parameters.AddWithValue("$cacheWrite", model.CacheWriteTokens);
            _ = detail.Parameters.AddWithValue("$thinking", model.ThinkingTokens);
            _ = detail.Parameters.AddWithValue("$cost", (double)model.CostUsd);
            _ = await detail.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<PrMeta?> ReadSummonsPrAsync(
        SqliteConnection conn, string revisionId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT payload FROM blocks
            WHERE revision_id = $revision AND kind = 'Summons'
            ORDER BY block_index LIMIT 1
            """;
        _ = cmd.Parameters.AddWithValue("$revision", revisionId);

        var payload = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        try
        {
            return ActaJson.Deserialize<SummonsPayload>(payload)?.Pr;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从链上整体重建评审投影。
    /// </summary>
    /// <remarks>
    /// 让位重挂会改块哈希，而投影行是按 <c>block_hash</c> 唯一的 —— 不重建就会留下
    /// 指向已不存在区块的幽灵行，报表里的金额会重复计。链是唯一真相，重建总是安全的。
    /// </remarks>
    private static async Task ReprojectAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using (var wipe = conn.CreateCommand())
        {
            wipe.CommandText = "DELETE FROM review_model_usages; DELETE FROM reviews;";
            _ = await wipe.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{BlockColumns} WHERE kind = 'Ballot' ORDER BY block_index";
        var ballots = await ReadAllAsync(cmd, ct).ConfigureAwait(false);

        foreach (var block in ballots)
        {
            await ProjectBallotAsync(conn, block, ct).ConfigureAwait(false);
        }
    }

    private const string BlockColumns = """
        SELECT chain_id, block_index, prev_hash, created_at, kind, payload,
               elector_id, public_key, signature
        FROM blocks
        """;

    private static async Task BumpAsync(SqliteConnection conn, string name, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO acta_counters (name, value) VALUES ($name, 1)
            ON CONFLICT (name) DO UPDATE SET value = value + 1
            """;
        _ = cmd.Parameters.AddWithValue("$name", name);
        _ = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertAsync(
        SqliteConnection conn, Block block, string revisionId, CancellationToken ct,
        System.Data.Common.DbTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx as SqliteTransaction;
        cmd.CommandText = """
            INSERT INTO blocks
                (chain_id, block_index, revision_id, prev_hash, block_hash, created_at, kind,
                 payload, elector_id, public_key, signature)
            VALUES
                ($chain, $index, $revision, $prev, $hash, $at, $kind,
                 $payload, $elector, $pubkey, $sig)
            """;
        _ = cmd.Parameters.AddWithValue("$chain", block.ChainId);
        _ = cmd.Parameters.AddWithValue("$index", block.Index);
        _ = cmd.Parameters.AddWithValue("$revision", revisionId);
        _ = cmd.Parameters.AddWithValue("$prev", block.PrevHash);
        _ = cmd.Parameters.AddWithValue("$hash", block.Hash());
        _ = cmd.Parameters.AddWithValue(
            "$at", block.At.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
        _ = cmd.Parameters.AddWithValue("$kind", block.Kind.ToString());
        _ = cmd.Parameters.AddWithValue("$payload", block.PayloadJson);
        _ = cmd.Parameters.AddWithValue("$elector", block.ElectorId);
        _ = cmd.Parameters.AddWithValue("$pubkey", block.PublicKey);
        _ = cmd.Parameters.AddWithValue("$sig", block.Signature);
        _ = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<(long Index, string? Hash)> ReadTailAsync(
        SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT block_index, block_hash FROM blocks ORDER BY block_index DESC LIMIT 1";

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? (reader.GetInt64(0), reader.GetString(1))
            : (-1, null);
    }

    private static async Task<Block?> ReadAtAsync(
        SqliteConnection conn, long index, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{BlockColumns} WHERE block_index = $index";
        _ = cmd.Parameters.AddWithValue("$index", index);

        var blocks = await ReadAllAsync(cmd, ct).ConfigureAwait(false);
        return blocks.Count > 0 ? blocks[0] : null;
    }

    private static async Task<IReadOnlyList<Block>> ReadFromAsync(
        SqliteConnection conn, long fromIndex, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{BlockColumns} WHERE block_index >= $from ORDER BY block_index";
        _ = cmd.Parameters.AddWithValue("$from", fromIndex);
        return await ReadAllAsync(cmd, ct).ConfigureAwait(false);
    }

    private static async Task DeleteFromAsync(
        SqliteConnection conn, System.Data.Common.DbTransaction tx,
        long fromIndex, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx as SqliteTransaction;
        cmd.CommandText = "DELETE FROM blocks WHERE block_index >= $from";
        _ = cmd.Parameters.AddWithValue("$from", fromIndex);
        _ = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Block>> ReadAllAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var blocks = new List<Block>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            blocks.Add(new Block
            {
                ChainId = reader.GetString(0),
                Index = reader.GetInt64(1),
                PrevHash = reader.GetString(2),
                At = DateTimeOffset.ParseExact(
                    reader.GetString(3), TimestampFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                Kind = Enum.Parse<BlockKind>(reader.GetString(4)),
                PayloadJson = reader.GetString(5),
                ElectorId = reader.GetString(6),
                PublicKey = reader.GetString(7),
                Signature = reader.GetString(8),
            });
        }

        return blocks;
    }
}

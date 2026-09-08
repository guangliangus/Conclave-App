using System.Globalization;
using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure;

/// <summary>
/// Acta 账本的 SQLite 实现。
/// </summary>
/// <remarks>
/// <para>
/// 账本是 append-only 的：没有 UPDATE，没有 DELETE。冲突（同一 <c>(chain_id, block_index)</c>
/// 收到两个不同区块）按 blockHash 字典序小者胜出 —— 确定性规则，两侧独立执行会得到同一结果，
/// 不需要协商。
/// </para>
/// <para>
/// 刻意不上 EF Core：一张 append-only 的表，手写 SQL 比 ORM 直白，
/// 也不必为它把 EF 整栈拖进 Infrastructure。
/// </para>
/// </remarks>
public sealed class SqliteActa : IActaStore
{
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    private readonly string _connectionString;
    private readonly ElectorIdentity _identity;
    private readonly ILogger<SqliteActa> _logger;

    /// <summary>
    /// 允许其区块进入本地账本的节点。
    /// </summary>
    /// <remarks>
    /// P0 只含自己。P1 接入 mesh 时这里必须换成真正的白名单 —— 别人的 Seating 块会让
    /// 别人的评审任务落到你机器上跑 Bash，这道闸不是「以后再加」的。
    /// </remarks>
    private readonly HashSet<string> _allowedElectors;

    /// <summary>SQLite 单写者。并发写会撞 SQLITE_BUSY，这里直接串行化。</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SqliteActa(ConclaveOptions options, ElectorIdentity identity, ILogger<SqliteActa> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(identity);

        _identity = identity;
        _logger = logger;
        _allowedElectors = new HashSet<string>(StringComparer.Ordinal) { identity.Id };

        Directory.CreateDirectory(options.HomeDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.ActaPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        Initialize();
    }

    /// <summary>把一个节点加进白名单。P1 由 mesh 的成员管理调用。</summary>
    public void Allow(string electorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(electorId);
        lock (_allowedElectors)
        {
            _ = _allowedElectors.Add(electorId);
        }
    }

    private bool IsAllowed(string electorId)
    {
        lock (_allowedElectors)
        {
            return _allowedElectors.Contains(electorId);
        }
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        // block_index 而不是 index：index 是保留字。
        // 业务键 (chain_id, block_index) 用唯一索引表达，主键仍是自增代理键。
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS blocks (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                chain_id     TEXT    NOT NULL,
                block_index  INTEGER NOT NULL,
                prev_hash    TEXT    NOT NULL,
                block_hash   TEXT    NOT NULL,
                created_at   TEXT    NOT NULL,
                kind         TEXT    NOT NULL,
                payload      TEXT    NOT NULL,
                elector_id   TEXT    NOT NULL,
                public_key   TEXT    NOT NULL,
                signature    TEXT    NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_blocks_chain_block_index
                ON blocks (chain_id, block_index);
            CREATE INDEX IF NOT EXISTS ix_blocks_created_at ON blocks (created_at);
            CREATE INDEX IF NOT EXISTS ix_blocks_kind       ON blocks (kind);
            CREATE INDEX IF NOT EXISTS ix_blocks_elector_id ON blocks (elector_id);
            """;
        _ = cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public async Task<Block> AppendAsync<TPayload>(
        string chainId, BlockKind kind, TPayload payload, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chainId);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            var (tailIndex, tailHash) = await ReadTailAsync(conn, chainId, ct).ConfigureAwait(false);

            var unsigned = new Block
            {
                ChainId = chainId,
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
            await InsertAsync(conn, block, ct).ConfigureAwait(false);
            return block;
        }
        finally
        {
            _ = _writeGate.Release();
        }
    }

    public async Task<bool> TryApplyAsync(Block block, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(block);

        if (!block.VerifySignature())
        {
            _logger.LogWarning("拒收区块 {Chain}#{Index}：验签失败", block.ChainId, block.Index);
            return false;
        }

        if (!IsAllowed(block.ElectorId))
        {
            _logger.LogWarning(
                "拒收区块 {Chain}#{Index}：节点 {Elector} 不在白名单",
                block.ChainId, block.Index, block.ElectorId);
            return false;
        }

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = Open();

            var existing = await ReadAtAsync(conn, block.ChainId, block.Index, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.Hash() == block.Hash())
                {
                    return true;   // 同一个块被 gossip 送了两遍，幂等。
                }

                // 分区合并：同 index 两个不同块，blockHash 字典序小者胜出。
                // 己方更小则保留己方；对方更小则本地这一块要让位 —— P2 实现 rebase，
                // P0 阶段先记日志，因为单节点不会走到这里。
                var keepMine = string.CompareOrdinal(existing.Hash(), block.Hash()) < 0;
                _logger.LogWarning(
                    "索引冲突 {Chain}#{Index}：{Winner} 胜出（rebase 待 P2 实现）",
                    block.ChainId, block.Index, keepMine ? "本地" : "远端");
                return false;
            }

            var (tailIndex, tailHash) = await ReadTailAsync(conn, block.ChainId, ct).ConfigureAwait(false);
            var expectedPrev = tailHash ?? Block.GenesisPrevHash;

            if (block.Index != tailIndex + 1)
            {
                _logger.LogInformation(
                    "区块 {Chain}#{Index} 与本地链尾 {Tail} 不连续，需要补链（P2 的 PullChain）",
                    block.ChainId, block.Index, tailIndex);
                return false;
            }

            if (block.PrevHash != expectedPrev)
            {
                _logger.LogWarning("区块 {Chain}#{Index} 的 PrevHash 与本地链尾不符", block.ChainId, block.Index);
                return false;
            }

            await InsertAsync(conn, block, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _ = _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<Block>> ReadChainAsync(string chainId, CancellationToken ct)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT chain_id, block_index, prev_hash, created_at, kind, payload,
                   elector_id, public_key, signature
            FROM blocks
            WHERE chain_id = $chain
            ORDER BY block_index
            """;
        _ = cmd.Parameters.AddWithValue("$chain", chainId);
        return await ReadAllAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Block>> ReadRecentAsync(int limit, CancellationToken ct)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT chain_id, block_index, prev_hash, created_at, kind, payload,
                   elector_id, public_key, signature
            FROM blocks
            ORDER BY id DESC
            LIMIT $limit
            """;
        _ = cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        return await ReadAllAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ReadOpenChainsAsync(CancellationToken ct)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();

        // 每个 Revision 恰好一个 Summons、最多一个 Promulgation，
        // 所以 Summons 数 > Promulgation 数 就说明这条链上还有没结论的版本。
        cmd.CommandText = """
            SELECT chain_id
            FROM blocks
            GROUP BY chain_id
            HAVING SUM(CASE WHEN kind = 'Summons' THEN 1 ELSE 0 END)
                 > SUM(CASE WHEN kind = 'Promulgation' THEN 1 ELSE 0 END)
            """;

        var chains = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            chains.Add(reader.GetString(0));
        }

        return chains;
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

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task InsertAsync(SqliteConnection conn, Block block, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO blocks
                (chain_id, block_index, prev_hash, block_hash, created_at, kind, payload,
                 elector_id, public_key, signature)
            VALUES
                ($chain, $index, $prev, $hash, $at, $kind, $payload, $elector, $pubkey, $sig)
            """;
        _ = cmd.Parameters.AddWithValue("$chain", block.ChainId);
        _ = cmd.Parameters.AddWithValue("$index", block.Index);
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
        SqliteConnection conn, string chainId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT block_index, block_hash FROM blocks
            WHERE chain_id = $chain
            ORDER BY block_index DESC
            LIMIT 1
            """;
        _ = cmd.Parameters.AddWithValue("$chain", chainId);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return (reader.GetInt64(0), reader.GetString(1));
        }

        return (-1, null);
    }

    private static async Task<Block?> ReadAtAsync(
        SqliteConnection conn, string chainId, long index, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT chain_id, block_index, prev_hash, created_at, kind, payload,
                   elector_id, public_key, signature
            FROM blocks
            WHERE chain_id = $chain AND block_index = $index
            """;
        _ = cmd.Parameters.AddWithValue("$chain", chainId);
        _ = cmd.Parameters.AddWithValue("$index", index);

        var blocks = await ReadAllAsync(cmd, ct).ConfigureAwait(false);
        return blocks.Count > 0 ? blocks[0] : null;
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

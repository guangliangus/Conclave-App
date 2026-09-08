using System.Globalization;
using System.Text;
using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
using Microsoft.Data.Sqlite;

namespace Conclave.Infrastructure;

/// <summary>
/// 读 <c>reviews</c> / <c>review_model_usages</c> 投影表。
/// </summary>
/// <remarks>
/// 表结构由 <see cref="SqliteActa"/> 建立并维护 —— 这里只读，一个字段都不写。
/// 报表不能成为第二个真相来源：任何时候都能从链上重建这两张表。
/// </remarks>
public sealed class SqliteReviewLog : IReviewLog
{
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    private readonly string _connectionString;

    public SqliteReviewLog(ConclaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.ActaPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        // 只读，但表可能还不存在：conclave report 不会构造 SqliteActa。幂等操作。
        ActaSchema.EnsureCreated(_connectionString);
    }

    private const string RecordColumns = """
        SELECT block_hash, revision_id, project, repo, pr_id, pr_title, pr_author,
               reviewer_id, reviewer_az, seat_round, status, findings, reviewed_at,
               duration_ms, model, turns, input_tokens, output_tokens,
               cache_read_tokens, cache_write_tokens, thinking_tokens, cost_usd, cost_basis
        FROM reviews
        """;

    public async Task<IReadOnlyList<ReviewRecord>> ReadRecentAsync(int limit, CancellationToken ct)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{RecordColumns} ORDER BY reviewed_at DESC, id DESC LIMIT $limit";
        _ = cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        return await ReadRecordsAsync(conn, cmd, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReviewRecord>> ReadByRevisionAsync(
        string revisionId, CancellationToken ct)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{RecordColumns} WHERE revision_id = $revision ORDER BY seat_round";
        _ = cmd.Parameters.AddWithValue("$revision", revisionId);
        return await ReadRecordsAsync(conn, cmd, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<UsageSummary>> SummariseByReviewerAsync(
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
        // 按人读的身份分组而不是公钥指纹：报表是给人看的。同一个人换了机器（新密钥）
        // 仍应汇总在一起。
        => SummariseAsync("reviews", "reviewer_az", from, to, ct);

    public Task<IReadOnlyList<UsageSummary>> SummariseByRepoAsync(
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
        => SummariseAsync("reviews", "repo", from, to, ct);

    public Task<IReadOnlyList<UsageSummary>> SummariseByMonthAsync(CancellationToken ct)
        => SummariseAsync("reviews", "substr(reviewed_at, 1, 7)", null, null, ct);

    public Task<IReadOnlyList<UsageSummary>> SummariseByModelAsync(
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
        => SummariseAsync(
            "review_model_usages u JOIN reviews r ON r.id = u.review_id",
            "u.canonical_model", from, to, ct, prefix: "u.", timeColumn: "r.reviewed_at");

    public async Task<UsageSummary> ReadTotalAsync(
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var rows = await SummariseAsync("reviews", "'总计'", from, to, ct).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : new UsageSummary("总计", 0, 0, 0, 0, 0, 0m);
    }

    /// <summary>
    /// 一个通用的分组汇总。
    /// </summary>
    /// <remarks>
    /// <paramref name="groupBy"/> 与 <paramref name="source"/> 都是代码里写死的字面量，
    /// 不接受外部输入 —— 唯一的可变量（时间范围）走参数化。
    /// </remarks>
    private async Task<IReadOnlyList<UsageSummary>> SummariseAsync(
        string source,
        string groupBy,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct,
        string prefix = "",
        string timeColumn = "reviewed_at")
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();

        var sql = new StringBuilder()
            .Append("SELECT ").Append(groupBy).Append(" AS k, COUNT(*), ")
            .Append("COALESCE(SUM(").Append(prefix).Append("input_tokens), 0), ")
            .Append("COALESCE(SUM(").Append(prefix).Append("output_tokens), 0), ")
            .Append("COALESCE(SUM(").Append(prefix).Append("cache_read_tokens), 0), ")
            .Append("COALESCE(SUM(").Append(prefix).Append("cache_write_tokens), 0), ")
            .Append("COALESCE(SUM(").Append(prefix).Append("cost_usd), 0) ")
            .Append("FROM ").Append(source);

        if (from is not null || to is not null)
        {
            _ = sql.Append(" WHERE 1 = 1");
            if (from is not null)
            {
                _ = sql.Append(" AND ").Append(timeColumn).Append(" >= $from");
                _ = cmd.Parameters.AddWithValue("$from", Stamp(from.Value));
            }

            if (to is not null)
            {
                _ = sql.Append(" AND ").Append(timeColumn).Append(" < $to");
                _ = cmd.Parameters.AddWithValue("$to", Stamp(to.Value));
            }
        }

        _ = sql.Append(" GROUP BY k ORDER BY 6 DESC, k");
        cmd.CommandText = sql.ToString();

        var rows = new List<UsageSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new UsageSummary(
                reader.IsDBNull(0) ? "(未知)" : reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                (decimal)reader.GetDouble(6)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<ReviewRecord>> ReadRecordsAsync(
        SqliteConnection conn, SqliteCommand cmd, CancellationToken ct)
    {
        var records = new List<ReviewRecord>();
        var hashes = new List<string>();

        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var usage = new ReviewUsage(
                    reader.GetInt64(16),
                    reader.GetInt64(17),
                    reader.GetInt64(18),
                    reader.GetInt64(19),
                    reader.GetInt64(20),
                    (decimal)reader.GetDouble(21),
                    reader.GetString(22),
                    reader.GetInt32(15),
                    []);

                records.Add(new ReviewRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetInt32(9),
                    Enum.TryParse<ReviewDecision>(reader.GetString(10), out var status)
                        ? status
                        : ReviewDecision.Error,
                    reader.GetInt32(11),
                    ParseStamp(reader.GetString(12)),
                    reader.GetInt64(13),
                    reader.GetString(14),
                    reader.GetInt32(15),
                    usage));

                hashes.Add(reader.GetString(0));
            }
        }

        // 分模型明细单独取一次，避免主查询变成会放大行数的 join。
        var details = await ReadModelUsagesAsync(conn, hashes, ct).ConfigureAwait(false);
        for (var i = 0; i < records.Count; i++)
        {
            if (details.TryGetValue(records[i].BlockHash, out var models))
            {
                records[i] = records[i] with { Usage = records[i].Usage with { Models = models } };
            }
        }

        return records;
    }

    private static async Task<Dictionary<string, List<ModelUsage>>> ReadModelUsagesAsync(
        SqliteConnection conn, IReadOnlyList<string> blockHashes, CancellationToken ct)
    {
        var byHash = new Dictionary<string, List<ModelUsage>>(StringComparer.Ordinal);
        if (blockHashes.Count == 0)
        {
            return byHash;
        }

        await using var cmd = conn.CreateCommand();
        var names = new List<string>(blockHashes.Count);
        for (var i = 0; i < blockHashes.Count; i++)
        {
            var name = $"$h{i.ToString(CultureInfo.InvariantCulture)}";
            names.Add(name);
            _ = cmd.Parameters.AddWithValue(name, blockHashes[i]);
        }

        cmd.CommandText = $"""
            SELECT r.block_hash, u.model, u.canonical_model, u.input_tokens, u.output_tokens,
                   u.cache_read_tokens, u.cache_write_tokens, u.thinking_tokens, u.cost_usd
            FROM review_model_usages u
            JOIN reviews r ON r.id = u.review_id
            WHERE r.block_hash IN ({string.Join(", ", names)})
            ORDER BY u.cost_usd DESC
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var hash = reader.GetString(0);
            if (!byHash.TryGetValue(hash, out var list))
            {
                list = [];
                byHash[hash] = list;
            }

            list.Add(new ModelUsage(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                (decimal)reader.GetDouble(8)));
        }

        return byHash;
    }

    private static string Stamp(DateTimeOffset value)
        => value.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string raw)
        => DateTimeOffset.ParseExact(
            raw, TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}

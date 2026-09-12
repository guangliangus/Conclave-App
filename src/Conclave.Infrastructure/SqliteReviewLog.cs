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
    /// 某个 PR 最近一次有效评审的评审节点（公钥指纹）；没有则 null。
    /// </summary>
    /// <remarks>
    /// 排除 Error 票：上一版是 Error 说明那个节点当时根本没跑成，没有「读过这份代码」的
    /// 优势，把它请回来只会重复同一个失败。
    /// <para>
    /// 按 <c>reviewed_at</c> 倒序而不是 <c>id</c>：让位重挂会重建整张投影表，
    /// 自增 id 的顺序不再对应链序，而出票时刻是写在票里的、重建后不变。
    /// </para>
    /// </remarks>
    public async Task<string?> ReadLastReviewerAsync(string project, int prId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);

        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                SELECT reviewer_id
                FROM reviews
                WHERE project = $project AND pr_id = $pr AND status <> $error
                ORDER BY reviewed_at DESC
                LIMIT 1
            """;
        _ = cmd.Parameters.AddWithValue("$project", project);
        _ = cmd.Parameters.AddWithValue("$pr", prId);
        _ = cmd.Parameters.AddWithValue("$error", ReviewDecision.Error.ToString());

        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value as string;
    }

    /// <summary>
    /// 某个节点自己在时间窗内烧掉的 token 与折算金额。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 按 <c>reviewer_id</c>（公钥指纹）过滤而不是 <c>reviewer_az</c>：投影表里混着 gossip
    /// 进来的别人的票，而 Claude 额度是按机器算的 —— 同一个人在两台机器上是两份额度。
    /// </para>
    /// <para>
    /// Error 票也算进来。子进程失败前烧掉的 token 不会因为失败而退回，
    /// 不计入会让额度用量系统性低报。
    /// </para>
    /// </remarks>
    public async Task<UsageSummary> ReadElectorUsageAsync(
        string electorId, DateTimeOffset from, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(electorId);

        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                SELECT COUNT(*),
                       COALESCE(SUM(input_tokens), 0),
                       COALESCE(SUM(output_tokens), 0),
                       COALESCE(SUM(cache_read_tokens), 0),
                       COALESCE(SUM(cache_write_tokens), 0),
                       COALESCE(SUM(cost_usd), 0)
                FROM reviews
                WHERE reviewer_id = $elector AND reviewed_at >= $from
            """;
        _ = cmd.Parameters.AddWithValue("$elector", electorId);
        _ = cmd.Parameters.AddWithValue("$from", from.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new UsageSummary(electorId, 0, 0, 0, 0, 0, 0m);
        }

        return new UsageSummary(
            electorId,
            reader.GetInt32(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            (decimal)reader.GetDouble(5));
    }

    /// <summary>
    /// 排行榜。分数由 <see cref="PointsProjection"/> 现算，SQL 只负责把输入捞出来。
    /// </summary>
    /// <remarks>
    /// 按 <c>reviewer_az</c>（人）分组而不是 <c>reviewer_id</c>（公钥指纹）：
    /// 一个人可以跑两台机器，那是两个 elector 但同一个人。
    /// ⚠️ 这个字段是节点<b>自报</b>的 —— 荣誉榜可以接受，
    /// 一旦分数能兑换任何东西，它就得换成白名单里的 pubkey→人 映射。
    /// </remarks>
    public async Task<IReadOnlyList<ScoreRow>> ReadLeaderboardAsync(
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();

        var sql = new StringBuilder(
            """
            SELECT reviewer_az, status, files_changed,
                   findings_critical, findings_major, findings_minor
            FROM reviews
            """);

        if (from is not null)
        {
            _ = sql.Append(" WHERE reviewed_at >= $from");
            _ = cmd.Parameters.AddWithValue(
                "$from", from.Value.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
        }

        if (to is not null)
        {
            _ = sql.Append(from is null ? " WHERE" : " AND").Append(" reviewed_at < $to");
            _ = cmd.Parameters.AddWithValue(
                "$to", to.Value.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
        }

        cmd.CommandText = sql.ToString();

        var tally = new Dictionary<string, (double Points, int Reviews, int Catches, int Files)>(
            StringComparer.Ordinal);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var person = reader.GetString(0);
            if (person.Length == 0)
            {
                // 认不出是谁的票不进榜。挂在「未知」下面等于把几个人的分并成一行。
                continue;
            }

            var decision = Enum.TryParse<ReviewDecision>(reader.GetString(1), out var d)
                ? d
                : ReviewDecision.Error;
            var files = reader.GetInt32(2);
            var critical = reader.GetInt32(3);
            var major = reader.GetInt32(4);
            var minor = reader.GetInt32(5);

            var score = PointsProjection.Score(decision, files, critical, major, minor);
            var prev = tally.TryGetValue(person, out var got) ? got : default;

            tally[person] = (
                prev.Points + score,
                // Error 票不计入「评审次数」：它没评成，计进去会让失败看起来像产出。
                prev.Reviews + (score > 0 ? 1 : 0),
                prev.Catches + (score > 0 ? critical + major : 0),
                prev.Files + (score > 0 ? files : 0));
        }

        return
        [
            .. tally
                .Select(kv => new ScoreRow(
                    kv.Key, Math.Round(kv.Value.Points, 1),
                    kv.Value.Reviews, kv.Value.Catches, kv.Value.Files))
                .OrderByDescending(r => r.Points)
                .ThenBy(r => r.Person, StringComparer.Ordinal),
        ];
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

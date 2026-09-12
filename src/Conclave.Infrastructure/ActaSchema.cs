using System.Globalization;
using Conclave.Domain;
using Microsoft.Data.Sqlite;

namespace Conclave.Infrastructure;

/// <summary>
/// Acta 的表结构。
/// </summary>
/// <remarks>
/// 建表抽出来给读写两侧共用。此前 DDL 只在 <see cref="SqliteActa"/> 的构造函数里，
/// 于是 <c>conclave report</c> 这种只解析 <c>IReviewLog</c> 的路径会撞上
/// <c>no such table: reviews</c> —— 表的存在依赖了另一个类恰好被构造过。
/// 让读取侧去假依赖写入侧也能解决，但那是把顺序约束藏进 DI；这里显式且幂等。
/// <para>
/// 同理，<b>父目录也在这里建</b>。SQLite 只建文件不建目录，而 <c>~/.conclave</c> 的创建
/// 原先只发生在 <c>ElectorIdentity</c> 的 DI 工厂里 —— <c>conclave report</c> 只解析
/// <see cref="Conclave.Application.Ports.IReviewLog"/>，从不碰那个工厂，于是全新机器上
/// 直接 <c>SQLite Error 14: unable to open database file</c>。
/// 实测在 CI 的干净 runner 上炸掉了 release 打包的自检，而开发机上 <c>~/.conclave</c>
/// 早就存在，永远看不到。跟上面那条是同一种病：状态的存在依赖了另一个类恰好被构造过。
/// </para>
/// </remarks>
internal static class ActaSchema
{
    internal static void EnsureCreated(string connectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        var dir = Path.GetDirectoryName(dataSource);
        if (!string.IsNullOrEmpty(dir))
        {
            _ = Directory.CreateDirectory(dir);
        }

        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        Execute(conn, Ddl);

        // 老库是 CREATE TABLE IF NOT EXISTS 建的，加列改不到它们，只能显式补。
        AddContentIdColumn(conn, "blocks");
        AddContentIdColumn(conn, "reviews");

        // 计分要的两样：难度（改动文件数）与严重度分布。reviews 原先只存了 findings 的
        // <b>条数</b>，而一条 critical 和一条 minor 在排行榜上不该同权。
        AddScoreColumns(conn);

        BackfillBlockContentIds(conn);
        BackfillReviewContentIds(conn);
        BackfillScoreColumns(conn);
        DeduplicateReviews(conn);

        // 唯一索引必须建在去重之后：老库里同一票存着几十份，先建索引会直接失败。
        Execute(conn, PostMigrationDdl);
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        _ = cmd.ExecuteNonQuery();
    }

    /// <summary>补 <c>content_id</c> 列；已经有就什么都不做。</summary>
    private static void AddContentIdColumn(SqliteConnection conn, string table)
    {
        using var probe = conn.CreateCommand();
        // 表名是这个方法的两个调用点写死的字面量，不接受外部输入。
        probe.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = 'content_id'";
        if (Convert.ToInt64(probe.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
        {
            return;
        }

        Execute(conn, $"ALTER TABLE {table} ADD COLUMN content_id TEXT NOT NULL DEFAULT ''");
    }

    /// <summary>
    /// 补上计分要的几列：改动文件数与按严重度分开的 finding 条数。
    /// </summary>
    /// <remarks>
    /// 存的是<b>分数的输入</b>而不是分数本身。存分数等于把公式冻在写入的那一刻 ——
    /// 改一次权重，老记录就跟新记录不是一把尺子，而排行榜最不能容忍的就是两把尺子。
    /// 分数由 <see cref="Conclave.Domain.PointsProjection"/> 在读取时现算。
    /// </remarks>
    private static void AddScoreColumns(SqliteConnection conn)
    {
        foreach (var column in ScoreColumns)
        {
            using var probe = conn.CreateCommand();
            // 列名是下面那个数组里写死的字面量，不接受外部输入。
            probe.CommandText =
                $"SELECT COUNT(*) FROM pragma_table_info('reviews') WHERE name = '{column}'";
            if (Convert.ToInt64(probe.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                continue;
            }

            Execute(conn, $"ALTER TABLE reviews ADD COLUMN {column} INTEGER NOT NULL DEFAULT 0");
        }
    }

    private static readonly string[] ScoreColumns =
        ["files_changed", "findings_critical", "findings_major", "findings_minor"];

    /// <summary>
    /// 给老行从链上回填那几列。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用 SQLite 的 JSON 函数直接从 <c>blocks.payload</c> 取，不把几百个块读进内存再回写 ——
    /// 跟 <see cref="BackfillReviewContentIds"/> 同一个理由。严重度是
    /// <c>JsonStringEnumConverter</c> 序列化的，所以链上是字符串而不是数字。
    /// </para>
    /// <para>
    /// 只回填还是初值 0 的行。真的「0 个文件 / 0 条 finding」重算一遍也得 0，
    /// 所以这个条件不会漏，只会白算一次。
    /// </para>
    /// </remarks>
    private static void BackfillScoreColumns(SqliteConnection conn)
    {
        Execute(conn, """
            UPDATE reviews
            SET files_changed = COALESCE((
                    SELECT json_extract(b.payload, '$.Pr.FilesChanged')
                    FROM blocks b WHERE b.block_hash = reviews.block_hash), 0),
                findings_critical = COALESCE((
                    SELECT COUNT(*) FROM blocks b,
                         json_each(json_extract(b.payload, '$.Findings'))
                    WHERE b.block_hash = reviews.block_hash
                      AND json_extract(value, '$.Severity') = 'Critical'), 0),
                findings_major = COALESCE((
                    SELECT COUNT(*) FROM blocks b,
                         json_each(json_extract(b.payload, '$.Findings'))
                    WHERE b.block_hash = reviews.block_hash
                      AND json_extract(value, '$.Severity') = 'Major'), 0),
                findings_minor = COALESCE((
                    SELECT COUNT(*) FROM blocks b,
                         json_each(json_extract(b.payload, '$.Findings'))
                    WHERE b.block_hash = reviews.block_hash
                      AND json_extract(value, '$.Severity') = 'Minor'), 0)
            WHERE files_changed = 0
              AND findings_critical = 0 AND findings_major = 0 AND findings_minor = 0
            """);
    }

    /// <summary>
    /// 给老块补算内容身份。
    /// </summary>
    /// <remarks>
    /// 刻意<b>重建一个 <see cref="Block"/> 再调 <see cref="Block.ContentId"/></b>，而不是在这里
    /// 照着那个公式拼一遍字符串：拼重了这里算出的身份跟运行时算出的对不上，
    /// 表现是老块永远被当成新块、重复照旧，而且没有任何报错。
    /// <see cref="Block.ContentId"/> 用不到的字段填占位值即可。
    /// </remarks>
    private static void BackfillBlockContentIds(SqliteConnection conn)
    {
        var pending = new List<(long Id, string ContentId)>();

        using (var read = conn.CreateCommand())
        {
            read.CommandText =
                "SELECT id, chain_id, created_at, kind, payload, elector_id FROM blocks WHERE content_id = ''";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var block = new Block
                {
                    ChainId = reader.GetString(1),
                    At = DateTimeOffset.ParseExact(
                        reader.GetString(2), TimestampFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                    Kind = Enum.Parse<BlockKind>(reader.GetString(3)),
                    PayloadJson = reader.GetString(4),
                    ElectorId = reader.GetString(5),

                    // ContentId 用不到这几个 —— 那正是它能扛住重挂的原因。
                    Index = 0,
                    PrevHash = Block.GenesisPrevHash,
                    PublicKey = string.Empty,
                    Signature = string.Empty,
                };

                pending.Add((reader.GetInt64(0), block.ContentId()));
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        using var tx = conn.BeginTransaction();
        foreach (var (id, contentId) in pending)
        {
            using var update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE blocks SET content_id = $content WHERE id = $id";
            _ = update.Parameters.AddWithValue("$content", contentId);
            _ = update.Parameters.AddWithValue("$id", id);
            _ = update.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// 投影行的身份就是它来源块的身份，照着 <c>block_hash</c> 抄过来即可。
    /// </summary>
    /// <remarks>
    /// 找不到来源块的行（理论上不该有）退回用自己的 <c>block_hash</c> 当身份 ——
    /// 不能留空，留空的话它们会在下一步去重里被当成同一票互相吃掉。
    /// </remarks>
    private static void BackfillReviewContentIds(SqliteConnection conn) => Execute(conn, """
            UPDATE reviews
            SET content_id = COALESCE(
                (SELECT b.content_id FROM blocks b WHERE b.block_hash = reviews.block_hash),
                reviews.block_hash)
            WHERE content_id = ''
        """);

    /// <summary>
    /// 同一票的多份投影只留最早那一份。
    /// </summary>
    /// <remarks>
    /// 投影是可重建的缓存，删重复不损失任何真相 —— 链还在，随时投得回来。
    /// 实测一个库里 134 行投影只对应 17 张票，账单因此报出 8 倍的次数与金额。
    /// </remarks>
    private static void DeduplicateReviews(SqliteConnection conn) => Execute(conn, """
            DELETE FROM reviews
            WHERE id NOT IN (SELECT MIN(id) FROM reviews GROUP BY content_id);

            DELETE FROM review_model_usages
            WHERE review_id NOT IN (SELECT id FROM reviews);
        """);

    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    private const string PostMigrationDdl = """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_reviews_content ON reviews (content_id);
        """;

    private const string Ddl = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS blocks (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                chain_id     TEXT    NOT NULL,
                block_index  INTEGER NOT NULL,
                revision_id  TEXT    NOT NULL,
                prev_hash    TEXT    NOT NULL,
                block_hash   TEXT    NOT NULL,
                created_at   TEXT    NOT NULL,
                kind         TEXT    NOT NULL,
                payload      TEXT    NOT NULL,
                elector_id   TEXT    NOT NULL,
                public_key   TEXT    NOT NULL,
                signature    TEXT    NOT NULL,
                -- 块的内容身份（Block.ContentId）。重挂改 index/prev_hash、块哈希跟着变，
                -- 这个不变 —— 「这块我是不是已经有了」只能拿它判。
                content_id   TEXT    NOT NULL DEFAULT ''
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_blocks_chain_block_index
                ON blocks (chain_id, block_index);
            CREATE INDEX IF NOT EXISTS ix_blocks_revision_id ON blocks (revision_id);
            CREATE INDEX IF NOT EXISTS ix_blocks_created_at  ON blocks (created_at);
            CREATE INDEX IF NOT EXISTS ix_blocks_kind        ON blocks (kind);
            CREATE INDEX IF NOT EXISTS ix_blocks_elector_id  ON blocks (elector_id);
            -- 刻意<b>不是</b>唯一索引：改造之前写脏的链里同一块内容存着几十份，
            -- 建唯一索引会直接失败。重复的产生由写入路径挡住（SqliteActa.TryApplyAsync），
            -- 而账单靠投影表那个唯一索引保证 —— 投影是可重建的缓存，链不是。
            CREATE INDEX IF NOT EXISTS ix_blocks_content
                ON blocks (chain_id, content_id);

            -- 评审记录投影。cost_usd 用 REAL 而不是全局规范里的 numeric(10,2)：
            -- 单次评审的折算金额常在 $0.001 量级，两位小数会全部归零。
            CREATE TABLE IF NOT EXISTS reviews (
                id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                block_hash         TEXT    NOT NULL,
                revision_id        TEXT    NOT NULL,
                project            TEXT    NOT NULL,
                repo               TEXT    NOT NULL,
                pr_id              INTEGER NOT NULL,
                pr_title           TEXT    NOT NULL,
                pr_author          TEXT    NOT NULL,
                reviewer_id        TEXT    NOT NULL,
                reviewer_az        TEXT    NOT NULL,
                seat_round         INTEGER NOT NULL,
                status             TEXT    NOT NULL,
                findings           INTEGER NOT NULL,
                reviewed_at        TEXT    NOT NULL,
                duration_ms        INTEGER NOT NULL,
                model              TEXT    NOT NULL,
                turns              INTEGER NOT NULL,
                input_tokens       INTEGER NOT NULL,
                output_tokens      INTEGER NOT NULL,
                cache_read_tokens  INTEGER NOT NULL,
                cache_write_tokens INTEGER NOT NULL,
                thinking_tokens    INTEGER NOT NULL,
                cost_usd           REAL    NOT NULL,
                cost_basis         TEXT    NOT NULL,
                -- 这一票的身份，来自它所在块的 Block.ContentId。
                content_id         TEXT    NOT NULL DEFAULT ''
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_reviews_block_hash ON reviews (block_hash);
            CREATE INDEX IF NOT EXISTS ix_reviews_revision_id ON reviews (revision_id);
            CREATE INDEX IF NOT EXISTS ix_reviews_reviewer_id ON reviews (reviewer_id);
            CREATE INDEX IF NOT EXISTS ix_reviews_reviewed_at ON reviews (reviewed_at);
            CREATE INDEX IF NOT EXISTS ix_reviews_repo        ON reviews (repo);
            -- 「作者 fix 之后仍由同一个节点复审」要按 PR 反查上一次的评审者，
            -- 而 revision_id 里不含 project，所以单独建这个复合索引。
            CREATE INDEX IF NOT EXISTS ix_reviews_pr
                ON reviews (project, pr_id, reviewed_at);

            CREATE TABLE IF NOT EXISTS review_model_usages (
                id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                review_id          INTEGER NOT NULL,
                model              TEXT    NOT NULL,
                canonical_model    TEXT    NOT NULL,
                input_tokens       INTEGER NOT NULL,
                output_tokens      INTEGER NOT NULL,
                cache_read_tokens  INTEGER NOT NULL,
                cache_write_tokens INTEGER NOT NULL,
                thinking_tokens    INTEGER NOT NULL,
                cost_usd           REAL    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_review_model_usages_review_id
                ON review_model_usages (review_id);

            CREATE TABLE IF NOT EXISTS acta_counters (
                id    INTEGER PRIMARY KEY AUTOINCREMENT,
                name  TEXT    NOT NULL,
                value INTEGER NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_acta_counters_name ON acta_counters (name);
        """;
}

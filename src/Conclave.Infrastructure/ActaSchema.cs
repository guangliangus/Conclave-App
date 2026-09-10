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
/// </remarks>
internal static class ActaSchema
{
    internal static void EnsureCreated(string connectionString)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Ddl;
        _ = cmd.ExecuteNonQuery();
    }

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
                signature    TEXT    NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_blocks_chain_block_index
                ON blocks (chain_id, block_index);
            CREATE INDEX IF NOT EXISTS ix_blocks_revision_id ON blocks (revision_id);
            CREATE INDEX IF NOT EXISTS ix_blocks_created_at  ON blocks (created_at);
            CREATE INDEX IF NOT EXISTS ix_blocks_kind        ON blocks (kind);
            CREATE INDEX IF NOT EXISTS ix_blocks_elector_id  ON blocks (elector_id);

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
                cost_basis         TEXT    NOT NULL
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

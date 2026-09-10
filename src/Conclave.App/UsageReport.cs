using System.Globalization;
using System.Text;
using Conclave.Application.Ports;

namespace Conclave.App;

/// <summary>
/// 把评审记录渲染成终端报表。
/// </summary>
/// <remarks>
/// 列宽按**显示宽度**而不是字符数来补 —— 中文和全角标点在终端里占两格，
/// 直接 <c>PadRight</c> 会让带中文的仓库名和 PR 标题把整张表拉歪。
/// </remarks>
internal static class UsageReport
{
    internal static string Render(
        UsageSummary total,
        IReadOnlyList<UsageSummary> byReviewer,
        IReadOnlyList<UsageSummary> byMonth,
        IReadOnlyList<UsageSummary> byRepo,
        IReadOnlyList<UsageSummary> byModel,
        IReadOnlyList<ReviewRecord> recent)
    {
        var sb = new StringBuilder();

        if (total.Reviews == 0)
        {
            _ = sb.AppendLine("账本里还没有评审记录。");
            _ = sb.AppendLine("先跑一次：conclave review <pr-id>");
            return sb.ToString();
        }

        _ = sb.AppendLine();
        _ = sb.Append("总计 ").Append(total.Reviews.ToString(CultureInfo.InvariantCulture))
              .Append(" 次评审 · ").Append(Tokens(total.TotalTokens)) .Append(" token · 折合 ")
              .Append(Money(total.CostUsd)).AppendLine();
        _ = sb.Append("缓存命中 ").Append(Percent(total.CacheHitRatio))
              .Append("（命中的输入 token 计价远低于新输入，这个数越高越省）").AppendLine();
        _ = sb.AppendLine();
        _ = sb.AppendLine("⚠️  金额是按 API 目录价折算，不是实际扣费。走 Max/Pro 订阅时边际成本为 0。");

        Section(sb, "按人", byReviewer, total);
        Section(sb, "按月", byMonth, total);
        Section(sb, "按仓库", byRepo, total);
        Section(sb, "按模型", byModel, total);

        _ = sb.AppendLine().AppendLine("最近的评审");
        // 用集合表达式而不是对象初始化器：初始化器里的 [...] 会被当成索引器初始化。
        List<string[]> rows = [["时间", "谁", "PR", "仓库", "状态", "问题", "TOKEN", "金额", "耗时"]];

        foreach (var r in recent)
        {
            rows.Add([
                r.ReviewedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
                r.ReviewerAz.Length > 0 ? r.ReviewerAz : r.ReviewerId[..8],
                r.PrId.ToString(CultureInfo.InvariantCulture),
                r.Repo,
                Labels.Decision(r.Status),
                r.Findings.ToString(CultureInfo.InvariantCulture),
                Tokens(r.Usage.TotalTokens),
                Money(r.Usage.CostUsd),
                (r.DurationMs / 1000.0).ToString("F0", CultureInfo.InvariantCulture) + "s",
            ]);
        }

        Table(sb, rows);
        return sb.ToString();
    }

    private static void Section(
        StringBuilder sb, string title, IReadOnlyList<UsageSummary> rows, UsageSummary total)
    {
        if (rows.Count == 0)
        {
            return;
        }

        _ = sb.AppendLine().Append(title).AppendLine();

        List<string[]> table = [["", "次数", "TOKEN", "金额", "占比"]];
        foreach (var row in rows)
        {
            table.Add([
                row.Key,
                row.Reviews.ToString(CultureInfo.InvariantCulture),
                Tokens(row.TotalTokens),
                Money(row.CostUsd),
                total.CostUsd == 0 ? "—" : Percent((double)(row.CostUsd / total.CostUsd)),
            ]);
        }

        Table(sb, table);
    }

    /// <summary>第一列左对齐，其余右对齐。</summary>
    private static void Table(StringBuilder sb, IReadOnlyList<string[]> rows)
    {
        var columns = rows[0].Length;
        var widths = new int[columns];

        foreach (var row in rows)
        {
            for (var c = 0; c < columns; c++)
            {
                widths[c] = Math.Max(widths[c], DisplayWidth(row[c]));
            }
        }

        for (var r = 0; r < rows.Count; r++)
        {
            _ = sb.Append("  ");
            for (var c = 0; c < columns; c++)
            {
                var cell = rows[r][c];
                var pad = widths[c] - DisplayWidth(cell);
                _ = c == 0
                    ? sb.Append(cell).Append(' ', pad)
                    : sb.Append(' ', pad).Append(cell);

                if (c < columns - 1)
                {
                    _ = sb.Append("  ");
                }
            }

            _ = sb.AppendLine();

            if (r == 0)
            {
                _ = sb.Append("  ").Append('─', widths.Sum() + (2 * (columns - 1))).AppendLine();
            }
        }
    }

    /// <summary>
    /// 字符串在终端里占几格。
    /// </summary>
    /// <remarks>
    /// 只按 CJK 与全角区间判双宽，够用于本项目会出现的文本（中文标题、仓库名、模型名）。
    /// 不引 wcwidth 那种完整实现 —— 那要一张大表，而这里只是对齐报表。
    /// </remarks>
    private static int DisplayWidth(string s)
    {
        var width = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            width += IsWide(rune.Value) ? 2 : 1;
        }

        return width;
    }

    private static bool IsWide(int cp)
        => cp is >= 0x1100 and <= 0x115F        // 韩文字母
            or >= 0x2E80 and <= 0x303E          // CJK 部首、日文标点
            or >= 0x3041 and <= 0x33FF          // 假名、CJK 兼容
            or >= 0x3400 and <= 0x4DBF          // CJK 扩展 A
            or >= 0x4E00 and <= 0x9FFF          // CJK 基本区
            or >= 0xA000 and <= 0xA4CF          // 彝文
            or >= 0xAC00 and <= 0xD7A3          // 韩文音节
            or >= 0xF900 and <= 0xFAFF          // CJK 兼容表意
            or >= 0xFE30 and <= 0xFE6F          // CJK 兼容形式
            or >= 0xFF00 and <= 0xFF60          // 全角 ASCII
            or >= 0xFFE0 and <= 0xFFE6          // 全角符号
            or >= 0x1F300 and <= 0x1FAFF;       // emoji

    private static string Tokens(long value) => Format.Tokens(value);

    private static string Money(decimal value) => Format.Money(value);

    private static string Percent(double ratio) => Format.Percent(ratio);
}

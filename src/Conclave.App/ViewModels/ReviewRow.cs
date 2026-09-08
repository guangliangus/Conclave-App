using System.Globalization;
using Conclave.Application.Ports;

namespace Conclave.App.ViewModels;

/// <summary>评审记录面板里的一行。</summary>
public sealed class ReviewRow
{
    public ReviewRow(ReviewRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        At = record.ReviewedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        Reviewer = record.ReviewerAz.Length > 0 ? record.ReviewerAz : record.ReviewerId[..8];
        Pr = record.PrId.ToString(CultureInfo.InvariantCulture);
        Repo = record.Repo;
        Title = record.PrTitle;
        Status = record.Status.ToString();
        Findings = record.Findings.ToString(CultureInfo.InvariantCulture);
        Tokens = Format.Tokens(record.Usage.TotalTokens);
        Cost = Format.Money(record.Usage.CostUsd);
        Duration = (record.DurationMs / 1000.0).ToString("F0", CultureInfo.InvariantCulture) + "s";

        // 悬浮时给出拆解：缓存读写与新输入的计价差一个量级，混着看判断不了优化方向。
        Breakdown = string.Format(
            CultureInfo.InvariantCulture,
            "输入 {0} · 输出 {1} · 缓存读 {2} · 缓存写 {3} · 思考 {4}\n缓存命中 {5:P0} · {6} 轮 · {7}\n{8}",
            Format.Tokens(record.Usage.InputTokens),
            Format.Tokens(record.Usage.OutputTokens),
            Format.Tokens(record.Usage.CacheReadTokens),
            Format.Tokens(record.Usage.CacheWriteTokens),
            Format.Tokens(record.Usage.ThinkingTokens),
            record.Usage.CacheHitRatio,
            record.Usage.Turns,
            record.Usage.CostBasis == "list" ? "目录价折算" : record.Usage.CostBasis,
            record.Usage.Models.Count > 0
                ? string.Join('\n', record.Usage.Models.Select(m =>
                    $"  {m.CanonicalModel}: {Format.Money(m.CostUsd)}"))
                : "  (无分模型明细)");
    }

    public string At { get; }

    public string Reviewer { get; }

    public string Pr { get; }

    public string Repo { get; }

    public string Title { get; }

    public string Status { get; }

    public string Findings { get; }

    public string Tokens { get; }

    public string Cost { get; }

    public string Duration { get; }

    public string Breakdown { get; }
}


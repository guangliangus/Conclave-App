using System.Globalization;
using Conclave.Application.Ports;
using Conclave.Domain;

namespace Conclave.App.ViewModels;

/// <summary>评审记录面板里的一行。</summary>
public sealed class ReviewRow
{
    public ReviewRow(
        ReviewRecord record, string? azureDevOpsOrgUrl = null, TableLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(record);

        Layout = layout ?? new TableLayout();

        BlockHash = record.BlockHash;
        At = record.ReviewedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        Reviewer = record.ReviewerAz.Length > 0
            ? Labels.ShortAccount(record.ReviewerAz)
            : record.ReviewerId[..8];
        ReviewerFull = record.ReviewerAz.Length > 0 ? record.ReviewerAz : record.ReviewerId;
        Pr = record.PrId.ToString(CultureInfo.InvariantCulture);
        Repo = record.Repo;
        Title = record.PrTitle;
        Status = Badge.Status(record.Status);
        Findings = record.Findings.ToString(CultureInfo.InvariantCulture);
        Tokens = Format.Tokens(record.Usage.TotalTokens);
        Cost = Format.Money(record.Usage.CostUsd);
        Duration = (record.DurationMs / 1000.0).ToString("F0", CultureInfo.InvariantCulture) + "s";

        // 悬浮时给出拆解：缓存读写与新输入的计价差一个量级，混着看判断不了优化方向。
        Breakdown = string.Format(
            CultureInfo.InvariantCulture,
            "{0}#{1} · {2}\n输入 {3} · 输出 {4} · 缓存读 {5} · 缓存写 {6} · 思考 {7}\n缓存命中 {8:P0} · {9} 轮 · {10}\n{11}",
            record.Repo,
            record.PrId,
            record.RevisionId,
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

        // 账本里只有 project / repo / PR 号，没有 remoteUrl —— 所以这里只能按组织地址拼。
        Url = PrLink.For(azureDevOpsOrgUrl, record.Project, record.Repo, record.PrId);
        UrlTip = Url ?? "拿不到 PR 网页地址：没配 Conclave:AzureDevOpsOrgUrl";

        PrLabel = "PR " + Pr;
        Subtitle = string.Join("  ·  ", new[] { Repo, Reviewer }.Where(x => x.Length > 0));
    }

    /// <summary>四张表共用的「该显示几列」。</summary>
    public TableLayout Layout { get; }

    /// <summary>
    /// 这一票所在区块的哈希 —— <c>reviews</c> 投影表的主键，所以天然唯一且稳定。
    /// </summary>
    /// <remarks>
    /// 界面上不显示，只给 <see cref="RowSync"/> 当身份用：账单表 200 行、每 30 秒刷一次，
    /// 没有身份就只能整张表拆了重建。
    /// </remarks>
    public string BlockHash { get; }

    public string At { get; }

    /// <summary>去掉域前缀的评审者名；没有 az 身份时退回公钥指纹前 8 位。</summary>
    public string Reviewer { get; }

    public string ReviewerFull { get; }

    public string Pr { get; }

    public string Repo { get; }

    public string Title { get; }

    public Badge Status { get; }

    public string Findings { get; }

    public string Tokens { get; }

    public string Cost { get; }

    public string Duration { get; }

    public string Breakdown { get; }

    /// <summary>PR 页面地址；没配组织地址时为 <c>null</c>。</summary>
    public string? Url { get; }

    public bool HasUrl => Url is not null;

    public string UrlTip { get; }

    /// <summary>「PR 123」—— 标题下面那行里可点的那一截。</summary>
    public string PrLabel { get; }

    /// <summary>标题下面那行小字里不可点的部分：仓库 · 谁评的。</summary>
    /// <remarks>
    /// 跟 <see cref="PrRow.Subtitle"/> 同一个理由：这几样各占一列时吃掉 300px，
    /// 把标题挤成一条缝，而它们只是定位信息、不是人扫账单时在读的东西。
    /// PR 号单独拆出来（<see cref="PrLabel"/>）是因为它现在是个链接。
    /// </remarks>
    public string Subtitle { get; }
}

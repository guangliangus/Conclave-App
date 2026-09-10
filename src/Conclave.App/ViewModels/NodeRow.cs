using System.Globalization;
using Conclave.Domain;

namespace Conclave.App.ViewModels;

/// <summary>
/// 「在线节点」表里的一行：mesh 里的一个 elector。
/// </summary>
/// <remarks>
/// <para>
/// 这张表要回答的是「席位为什么落在它身上」。所以列不是随便挑的 —— 额度、负载、近 24h
/// 出票数正好是 <see cref="Elector.Weight"/> 的三个输入，权重那一列则是它们的乘积。
/// 有人问「为什么我的 PR 老是同一台机器在评」时，答案就在这五列里。
/// </para>
/// <para>
/// 离线的节点<b>不隐藏</b>：<see cref="Application.Ports.IMesh.Members"/> 不按心跳过滤，
/// 而「某台机器掉了」正是需要看见的事。隐藏它只会让人以为 mesh 里从来只有这几台。
/// </para>
/// </remarks>
public sealed class NodeRow
{
    /// <summary>接近上限：还能接活，但值得看一眼。跟额度面板同一条线。</summary>
    private const double WarnAt = 0.6;

    public NodeRow(
        Elector node,
        bool isSelf,
        DateTimeOffset now,
        IReadOnlyList<string> reviewing,
        TableLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(reviewing);

        Layout = layout ?? new TableLayout();

        IsSelf = isSelf;
        Id = node.Id;
        IdShort = node.Id.Length > 12 ? node.Id[..12] : node.Id;

        Name = string.IsNullOrWhiteSpace(node.AzIdentity)
            ? IdShort
            : Labels.ShortAccount(node.AzIdentity);

        var alive = node.IsAlive(now);

        Subtitle = string.Join("  ·  ", new[]
        {
            IdShort,
            string.Format(CultureInfo.InvariantCulture, "{0} 个 project", node.Projects.Count),
            node.Endpoint,
        }.Where(x => x.Length > 0));

        // 三处区分都是必要的：
        //   「额度满」跟「在线」分开 —— 前者心跳照样新鲜，但在席位表里等于不存在；
        //   本机心跳过期报「心跳停滞」而不是「离线」—— 界面明摆着在跑，说它离线只会让人
        //   以为界面坏了，而真实含义是发现循环卡住、别的节点已经不会把席位分给我了。
        Status = (alive, node.HasHeadroom, isSelf) switch
        {
            (false, _, true) => new Badge("心跳停滞", BadgeTone.Bad),
            (false, _, false) => new Badge("离线", BadgeTone.Mute),
            (_, false, _) => new Badge("额度满", BadgeTone.Warn),
            (_, _, true) => new Badge("本节点", BadgeTone.Gold),
            _ => new Badge("在线", BadgeTone.Ok),
        };

        QuotaPercent = node.Utilization * 100;
        QuotaText = Format.Percent(node.Utilization);
        IsBad = node.Utilization >= Elector.MaxUtilization;
        IsWarn = !IsBad && node.Utilization >= WarnAt;

        LoadText = string.Format(
            CultureInfo.InvariantCulture, "{0}/{1}", node.RunningJobs, node.MaxConcurrent);
        ReviewsText = node.Reviews24h.ToString(CultureInfo.InvariantCulture);

        // 权重是相对值，只在同一张表里互相比较才有意义，所以给两位小数而不是百分比。
        WeightText = alive && node.HasHeadroom
            ? node.Weight.ToString("F2", CultureInfo.InvariantCulture)
            : "—";

        var since = now - node.LastHeartbeat;
        HeartbeatText = since < TimeSpan.Zero
            ? "时钟超前"
            : Format.Elapsed(since) + "前";

        Reviewing = reviewing.Count == 0
            ? string.Empty
            : string.Format(
                CultureInfo.InvariantCulture, "在评 {0}", string.Join("、", reviewing));

        Tip = string.Join('\n', new[]
        {
            $"指纹 {node.Id}",
            $"az 身份 {(string.IsNullOrWhiteSpace(node.AzIdentity) ? "(未取到)" : node.AzIdentity)}",
            node.Endpoint.Length > 0 ? $"端点 {node.Endpoint}" : "端点 (未广播)",
            $"最后心跳 {node.LastHeartbeat.ToLocalTime():MM-dd HH:mm:ss}"
                + (alive ? string.Empty : $"（超过 {Elector.HeartbeatWindow.TotalSeconds:F0} 秒未更新，已判离线）"),
            node.HasHeadroom
                ? $"权重 = 1/(1+在跑) × 1/(1+近 24h×0.1) × (1-额度) = {node.Weight:F3}"
                : $"额度已过 {Format.Percent(Elector.MaxUtilization)}，不参与席位分配",
            Reviewing.Length > 0 ? Reviewing : "当前没有在评的 PR",
        });
    }

    /// <summary>三张表共用的「该显示几列」。</summary>
    public TableLayout Layout { get; }

    public bool IsSelf { get; }

    public string Id { get; }

    /// <summary>指纹前 12 位；完整值进 tooltip。</summary>
    public string IdShort { get; }

    public string Name { get; }

    /// <summary>名字下面那行小字：指纹 · project 数 · 端点。</summary>
    public string Subtitle { get; }

    public Badge Status { get; }

    public string QuotaText { get; }

    /// <summary>0–100，喂给额度条。</summary>
    public double QuotaPercent { get; }

    public bool IsWarn { get; }

    public bool IsBad { get; }

    /// <summary>在跑的 claude 子进程数 / 上限。</summary>
    public string LoadText { get; }

    public string ReviewsText { get; }

    /// <summary>加权 HRW 里的权重；离线或额度满时为「—」。</summary>
    public string WeightText { get; }

    public string HeartbeatText { get; }

    /// <summary>这个节点此刻在评哪些 PR；没有时为空。</summary>
    public string Reviewing { get; }

    public bool HasReviewing => Reviewing.Length > 0;

    public string Tip { get; }
}

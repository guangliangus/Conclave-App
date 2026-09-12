using System.Globalization;
using Conclave.Application;
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
        IReadOnlyList<UsageWindow>? quotas = null,
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

        // 两个版本号都摆在明面上而不是只进 tooltip：版本过旧会让那台机器的评审在服务端
        // 直接被拒，而且只有它会挂 —— 别的节点照常出票，表面上看是「某个 PR 偶尔评不出来」。
        // 混合版本的集群里，这一格能省掉一次逐台去问的排查。
        //
        // Conclave 自己的版本同理，而且它比 claude 的更难从别处看出来：升级是各机器各自
        // 装的，一台落在旧版上时，它的行为差异（新加的规则、改过的阈值）不会有任何报错，
        // 只会表现成「这台机器的判断跟别人不一样」。
        Subtitle = string.Join("  ·  ", new[]
        {
            IdShort,
            string.Format(CultureInfo.InvariantCulture, "{0} 个 project", node.Projects.Count),
            node.AppVersion.Length > 0 ? "Conclave v" + node.AppVersion : string.Empty,
            node.ClaudeVersion.Length > 0 ? "claude " + node.ClaudeVersion : string.Empty,
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

        // 每一行都画各个窗口的原始读数（会话 5h / 周 7d），不画折算过的那个标量：
        // 「额度用了多少」人只认 Claude 自己报的那个数，而 node.Utilization 是
        // UsagePressure 把最紧的窗口折回 0.8 那把尺子之后的结果 —— 两者对不上是常态
        // （实测 7d 74% / 阈值 95% 折出来是 62%），同一个界面上摆两个对不上的百分比
        // 只会让人以为有一处是坏的。折算值改进 tooltip，那里放得下一句解释。
        //
        // 本节点的明细来自本机探针，对端的来自它上报的实时状态（LiveState.UsageWindows）——
        // 调用方负责挑，这里只管画。
        //
        // 只画参与入席判定的窗口：这张表回答的是「席位为什么落在它身上」，
        // 而 Fable 那种按模型细分的子额度压根不参与（UsageWindow.Gates）。
        // 对端发来的已经筛过一遍，再筛一次是幂等的，省得依赖发信方的版本。
        Quotas = quotas is null
            ? []
            : [.. quotas.Where(w => w.Gates).Select(w => new UsageWindowRow(w, now))];

        // 拆不出窗口时退回画那个标量，并在标签上写明是「压力」而不是额度：
        // 真值读不到（退到按预算折算）、或者对端是还不广播明细的旧版本。
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
            AppVersionLine(node, isSelf),
            node.ClaudeVersion.Length > 0
                ? $"claude {node.ClaudeVersion}（那台机器上实际会被起的那一份）"
                : "claude 版本未知 —— 节点还没报上来，或者那台机器上根本找不到 claude",
            node.Endpoint.Length > 0 ? $"端点 {node.Endpoint}" : "端点 (未广播)",
            $"最后心跳 {node.LastHeartbeat.ToLocalTime():MM-dd HH:mm:ss}"
                + (alive ? string.Empty : $"（超过 {Elector.HeartbeatWindow.TotalSeconds:F0} 秒未更新，已判离线）"),
            HasQuotas
                ? $"入席用的压力值 {QuotaText}（各窗口取「离自己那条线最近」的一个，"
                    + $"折回 {Format.Percent(Elector.MaxUtilization)} 这把尺子）"
                : isSelf
                    ? $"入席用的压力值 {QuotaText}（拆不出窗口 —— 真值读不到，按预算折算的）"
                    : $"入席用的压力值 {QuotaText}（这台机器没上报窗口明细 —— "
                        + "要么版本太旧，要么它那边也只有折算值）",
            node.HasHeadroom
                ? $"权重 = 1/(1+在跑) × 1/(1+近 24h×0.1) × (1-压力) = {node.Weight:F3}"
                : $"压力已过 {Format.Percent(Elector.MaxUtilization)}，不参与席位分配",
            Reviewing.Length > 0 ? Reviewing : "当前没有在评的 PR",
        });
    }

    /// <summary>四张表共用的「该显示几列」。</summary>
    public TableLayout Layout { get; }

    public bool IsSelf { get; }

    public string Id { get; }

    /// <summary>指纹前 12 位；完整值进 tooltip。</summary>
    public string IdShort { get; }

    public string Name { get; }

    /// <summary>名字下面那行小字：指纹 · project 数 · 端点。</summary>
    public string Subtitle { get; }

    public Badge Status { get; }

    /// <summary>
    /// 这个节点各个额度窗口的原始读数；拆不出窗口时为空。
    /// </summary>
    /// <remarks>
    /// 对端那份来自它上报的实时状态（<see cref="LiveState.UsageWindows"/>），随
    /// <c>GET /state</c> 过来、连同整份状态一起验签。它<b>不</b>参与任何判定 ——
    /// 决定入不入席的是心跳里那个签过名的 <see cref="Elector.Utilization"/> 标量。
    /// </remarks>
    public IReadOnlyList<UsageWindowRow> Quotas { get; }

    /// <summary>有逐窗口的读数可画。</summary>
    public bool HasQuotas => Quotas.Count > 0;

    /// <summary>折算成入席尺度的压力值。<see cref="HasQuotas"/> 为假时才画在表里。</summary>
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

    /// <summary>
    /// tooltip 里那行版本：它跑的是哪一版，跟本节点是不是同一版。
    /// </summary>
    /// <remarks>
    /// 差异要直接写出来而不是让人自己比对两行小字 —— 「为什么只有它评不出来」「为什么它
    /// 的结论跟别人不一样」，多数时候答案就是有人没升级。协议版本一起带上：不兼容的心跳
    /// 在 <c>HttpMesh</c> 那层就被挡掉了，所以这张表里出现的一定是同一个协议版本，
    /// 真出现不一样就说明有别的问题。
    /// </remarks>
    private static string AppVersionLine(Elector node, bool isSelf)
    {
        if (node.AppVersion.Length == 0)
        {
            return "Conclave 版本未知 —— 那台机器还没报上来";
        }

        var line = $"Conclave v{node.AppVersion}（心跳协议 v{node.ProtocolVersion}）";

        return isSelf || node.AppVersion == AppInfo.Version
            ? line
            : line + $"，本节点是 v{AppInfo.Version}";
    }
}

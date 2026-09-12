
using Conclave.Domain;

namespace Conclave.App.ViewModels;

/// <summary>徽章的语义色。</summary>
public enum BadgeTone
{
    /// <summary>还没开始 / 没有结论。</summary>
    Mute = 0,

    /// <summary>通过。</summary>
    Ok,

    /// <summary>需要人接手。</summary>
    Warn,

    /// <summary>驳回或失败。</summary>
    Bad,

    /// <summary>在流程中。</summary>
    Info,

    /// <summary>跟本节点有关。</summary>
    Gold,

    /// <summary>还没有值，只占个位置。</summary>
    Bare,
}

/// <summary>
/// 表格里的一枚状态徽章：一段文字 + 一种语义色。
/// </summary>
/// <remarks>
/// <para>
/// 色和文字打包在一起，而不是让 XAML 去判断 —— 「什么结论显示什么颜色」是一条业务映射，
/// 散在模板里就会出现同一个 <c>Reject</c> 在两张表里两种颜色。
/// </para>
/// <para>
/// 暴露成 <c>IsOk</c> 这类布尔而不是画刷，是为了让颜色仍然由样式表和深浅主题决定：
/// XAML 里写 <c>Classes.ok="{Binding Tone.IsOk}"</c>，ViewModel 不碰任何 Avalonia 类型。
/// </para>
/// </remarks>
public sealed record Badge(string Text, BadgeTone Tone)
{
    public bool IsOk => Tone == BadgeTone.Ok;

    public bool IsWarn => Tone == BadgeTone.Warn;

    public bool IsBad => Tone == BadgeTone.Bad;

    public bool IsInfo => Tone == BadgeTone.Info;

    public bool IsGold => Tone == BadgeTone.Gold;

    public bool IsBare => Tone == BadgeTone.Bare;

    /// <summary>
    /// 评审结论 + 问题数，给没有单独「问题」列的 PR 队列用。
    /// </summary>
    /// <remarks>
    /// 带上量词「条」：光写「待作者 5」看不出这个 5 是问题数、票数还是轮次 ——
    /// 这三样在这个界面上都存在，而且都是个位数，猜错了不会有任何提示。
    /// 名词（「问题」）放不进那一格 —— 最长的「通过·有建议 13 条」已经占满 120px，
    /// 所以名词由 <see cref="OutcomeTip"/> 在 tooltip 里补。
    /// </remarks>
    /// <param name="decision">null 表示还没有结论。</param>
    /// <param name="findings">合并后的问题数；0 就不显示。</param>
    public static Badge Outcome(ReviewDecision? decision, int findings)
    {
        if (decision is null)
        {
            return new Badge("—", BadgeTone.Bare);
        }

        var badge = Status(decision.Value);
        return findings > 0 ? badge with { Text = $"{badge.Text} {findings} 条" } : badge;
    }

    /// <summary>
    /// 徽章上那几个字的展开说法，给 tooltip 用。
    /// </summary>
    /// <remarks>
    /// 跟 <see cref="Outcome"/> 放在一起，理由同 <see cref="Labels"/>：两处各写一遍
    /// 迟早漂移成「同一个状态两个说法」。
    /// <para>
    /// 「合并后」是要点 —— quorum≥2 时几个节点各报一份，这个数是聚成簇之后的条数，
    /// 不是把各家的加起来（见 <see cref="Conclave.Domain.QuorumEngine"/>）。
    /// </para>
    /// </remarks>
    public static string OutcomeTip(ReviewDecision? decision, int findings)
    {
        if (decision is null)
        {
            return "还没有结论";
        }

        var label = Labels.Decision(decision.Value);

        return findings > 0
            ? $"{label} · 合并后 {findings} 条问题"
            : $"{label} · 没有报出问题";
    }

    /// <summary>只有结论，不带问题数 —— 评审记录那张表自己有「问题」列。</summary>
    public static Badge Status(ReviewDecision decision) => new(
        Labels.Decision(decision),
        decision switch
        {
            ReviewDecision.Approve or ReviewDecision.ApproveWithSuggestions => BadgeTone.Ok,
            ReviewDecision.WaitForAuthor => BadgeTone.Warn,
            // Error 不是评审意见而是子进程挂了，跟「驳回」共用红色但文字分得清。
            _ => BadgeTone.Bad,
        });

    /// <summary>
    /// 阶段徽章。
    /// </summary>
    /// <remarks>
    /// 阶段名由 Application 层给（带「1/2」这种计数），这里只决定配色：
    /// 金色 = 此刻正在烧 token，蓝色 = 在流程里等，灰色 = 没开始或已结束。
    /// </remarks>
    public static Badge Stage(string stage) => stage switch
    {
        var s when s.StartsWith("评审中", StringComparison.Ordinal) => new Badge(s, BadgeTone.Gold),
        var s when s.StartsWith("已入席", StringComparison.Ordinal) => new Badge(s, BadgeTone.Info),
        var s when s.StartsWith("已投票", StringComparison.Ordinal) => new Badge(s, BadgeTone.Info),
        var s => new Badge(s, BadgeTone.Mute),
    };

    /// <summary>区块类型徽章。</summary>
    public static Badge Of(BlockKind kind) => new(
        Labels.Kind(kind),
        kind switch
        {
            BlockKind.Summons => BadgeTone.Mute,
            BlockKind.Seating => BadgeTone.Info,
            BlockKind.Ballot => BadgeTone.Gold,
            BlockKind.Promulgation => BadgeTone.Ok,
            BlockKind.Recess => BadgeTone.Warn,
            _ => BadgeTone.Mute,
        });
}

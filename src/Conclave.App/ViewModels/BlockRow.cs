using System.Globalization;
using Conclave.Domain;

namespace Conclave.App.ViewModels;

/// <summary>Acta 账本浏览器里的一行。</summary>
public sealed class BlockRow
{
    /// <summary>链上确实没有这个值时显示的占位。</summary>
    private const string None = "—";

    public BlockRow(Block block, TableLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(block);

        Layout = layout ?? new TableLayout();

        At = block.At.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        // 全局单链之后链名恒为 "acta"，没有信息量；改显示这块属于哪个 PR 版本。
        Revision = Conclave.Domain.Acta.RevisionIdOf(block) ?? None;
        Index = block.Index.ToString(CultureInfo.InvariantCulture);
        Kind = Badge.Of(block.Kind);
        Elector = Short(block.ElectorId);
        Hash = block.Hash()[..12];

        var (payload, broken) = Decode(block);

        // 账本浏览器不该因为一个坏 payload 就整块崩掉。
        Summary = broken ? "(payload 解析失败)" : Describe(payload);

        AuthorFull = PrOf(payload)?.Author ?? string.Empty;
        Author = AuthorFull.Length > 0 ? Labels.ShortAccount(AuthorFull) : None;
        AuthorTip = AuthorFull.Length > 0 ? AuthorFull : WhyNoAuthor(block.Kind);

        (Reviewer, ReviewerTip) = Who(payload, block.ElectorId);
    }

    /// <summary>四张表共用的「该显示几列」。</summary>
    public TableLayout Layout { get; }

    public string At { get; }

    public string Revision { get; }

    public string Index { get; }

    public Badge Kind { get; }

    /// <summary>签名节点公钥指纹前 8 位。</summary>
    public string Elector { get; }

    public string Hash { get; }

    /// <summary>提这个 PR 的人，去掉域前缀；这种块不带 PR 快照时是「—」。</summary>
    public string Author { get; }

    public string AuthorFull { get; }

    /// <summary>完整账号；没有的话说清楚为什么没有，而不是留一个空 tooltip。</summary>
    public string AuthorTip { get; }

    /// <summary>
    /// 谁评的。
    /// </summary>
    /// <remarks>
    /// 出票块上是这一票的评审者，公布块上是参与这个结论的那几个人；两者都取不到时
    /// 退回签名节点的公钥指纹 —— 那答的是「哪台机器」而不是「谁」，所以 tooltip 里
    /// 得说清楚，见 <see cref="Who"/>。
    /// </remarks>
    public string Reviewer { get; }

    public string ReviewerTip { get; }

    public string Summary { get; }

    /// <summary>
    /// 把载荷解成具体类型，一块只解一次。
    /// </summary>
    /// <remarks>
    /// 摘要、作者、签名者三处都要读载荷。各自再 <c>block.Payload&lt;T&gt;()</c> 一遍的话
    /// 一行要反序列化三次，而这张表 60 行、每分钟刷四轮。
    /// <para>
    /// <c>Broken</c> 与「这种块没有载荷类型」要分开：前者得在摘要里说出来，
    /// 后者是正常的空。
    /// </para>
    /// </remarks>
    private static (object? Payload, bool Broken) Decode(Block block)
    {
        try
        {
            object? payload = block.Kind switch
            {
                BlockKind.Summons => block.Payload<SummonsPayload>(),
                BlockKind.Seating => block.Payload<SeatingPayload>(),
                BlockKind.Ballot => block.Payload<BallotPayload>(),
                BlockKind.Promulgation => block.Payload<PromulgationPayload>(),
                BlockKind.Recess => block.Payload<RecessPayload>(),
                _ => null,
            };

            return (payload, false);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, true);
        }
    }

    /// <summary>
    /// 评审时的 PR 快照 —— 只有召集块和出票块带着它。
    /// </summary>
    /// <remarks>
    /// <c>Summons</c> 停写之后快照跟着票走（<see cref="BallotPayload.Pr"/>），
    /// 但那之前落链的老票没有这个字段，公布块则从来就不带 —— 两种情况都取不到作者。
    /// </remarks>
    private static PrMeta? PrOf(object? payload) => payload switch
    {
        SummonsPayload s => s.Pr,
        BallotPayload b => b.Pr,
        PromulgationPayload p => p.Pr,
        _ => null,
    };

    /// <summary>
    /// 「谁评的」这一列，连它的 tooltip。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 出票块上是这一票的评审者（<see cref="BallotPayload.ReviewerAz"/>，落在那一票的
    /// 签名范围内，所以不可否认）；公布块上是
    /// <see cref="PromulgationPayload.Reviewers"/> —— 那是公布者签的转述，权威的那份
    /// 仍在票上，所以两者都显示，但只有前者称得上证据。
    /// </para>
    /// <para>
    /// 都取不到时退回公钥指纹。它答的是「哪台机器签的这块」，跟「谁评的」不是一个
    /// 问题 —— 尤其公布块的签名者是公布者而不是评审者（见
    /// <c>ReviewOrchestrator.IsPromulgator</c>），所以 tooltip 必须把这层说破，
    /// 否则那串十六进制会被当成评审者。
    /// </para>
    /// </remarks>
    private (string Text, string Tip) Who(object? payload, string electorId)
    {
        if (payload is BallotPayload b && !string.IsNullOrWhiteSpace(b.ReviewerAz))
        {
            return (Labels.ShortAccount(b.ReviewerAz), $"{b.ReviewerAz} · 节点 {electorId}");
        }

        if (payload is PromulgationPayload { Reviewers.Count: > 0 } p)
        {
            var who = p.Reviewers!;
            var more = who.Count > 1 ? $" +{who.Count - 1}" : string.Empty;

            return (
                Labels.ShortAccount(who[0]) + more,
                $"评审 {string.Join(" · ", who)}\n公布者节点 {electorId}");
        }

        return (
            Elector,
            payload is PromulgationPayload
                ? $"老块：公布块那会儿还不带评审者名单 —— 他们在同一 revision 的出票行上\n公布者节点 {electorId}"
                : $"节点 {electorId}");
    }

    private static string WhyNoAuthor(BlockKind kind) => kind switch
    {
        BlockKind.Ballot => "老票：PR 快照那会儿还在召集块上，没跟着票走",
        BlockKind.Promulgation => "老块：公布块那会儿还不带 PR 快照 —— 作者在同一 revision 的出票行上",
        _ => "这种块不带 PR 快照",
    };

    /// <summary>
    /// payload 里真正有信息量的那几个字段。
    /// </summary>
    /// <remarks>
    /// 刻意不再重复 revision id、作者和签名节点 —— 三者都已经是独立的列，
    /// 摘要里再抄一遍会把这一列挤成一串重复的十六进制。
    /// </remarks>
    private static string Describe(object? payload) => payload switch
    {
        SummonsPayload s => $"{s.Pr.Repo} · quorum {s.Quorum} · rules {s.RulesFingerprint}",

        SeatingPayload t => $"round {t.Round} → {Short(t.ElectorId)}",

        BallotPayload b => $"round {b.Round} · {Labels.Decision(b.Decision)} · {b.Findings.Count} 条 · "
            + $"{b.DurationMs / 1000} 秒 · {b.Model}",

        PromulgationPayload p => $"{Labels.Decision(p.Decision)} · {p.Findings.Count} 条 · "
            + $"{p.ActualQuorum}/{p.ExpectedQuorum} 票"
            + (p.Degraded ? " · 降级" : string.Empty),

        RecessPayload r => $"round {r.Round} · {r.Reason}",

        _ => string.Empty,
    };

    private static string Short(string electorId)
        => electorId.Length > 8 ? electorId[..8] : electorId;
}

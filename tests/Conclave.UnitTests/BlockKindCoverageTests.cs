using Conclave.Domain;

namespace Conclave.UnitTests;

/// <summary>
/// 每种 <see cref="BlockKind"/> 都必须被链上的两处分派认得。
/// </summary>
/// <remarks>
/// <para>
/// 加一种区块类型要同时改两个 switch，而它们的失败方式一轻一重：
/// </para>
/// <list type="bullet">
/// <item>
/// 漏了 <see cref="Acta.RevisionIdOf"/> —— 返回 null，<c>SqliteActa.AcceptAsync</c> 会带着
/// 「载荷里读不出 revision id」的警告把整个区块拒收。**响亮**，跑一次就发现。
/// </item>
/// <item>
/// 漏了 <see cref="ActaProjection.Project"/> —— 落到 <c>default: break</c>，区块进了库
/// 但投影看不见它。**静默**：链上有这块、UI 也列得出来，可编排层当它不存在。
/// 那种 bug 不会报错，只会让某个 PR 永远停在某个阶段不动。
/// <para>
/// 现在链上只写 <c>Ballot</c> 与 <c>Promulgation</c>，另外三种是停写的历史块。
/// 表里用 <c>null</c> 显式标出「故意不收」，这样「故意」与「忘了」仍然分得开。
/// </para>
/// </item>
/// </list>
/// <para>
/// 所以按这个仓库对付静默分叉的老办法办 —— 靠守卫，不靠 reviewer
/// （同 <c>DomainPurityTests</c>）。表里少一行就编译不过测试，
/// switch 里少一个 case 就断言失败。
/// </para>
/// <para>
/// App 层还有三处 <see cref="BlockKind"/> 的 switch（<c>Labels.Kind</c>、<c>Badge</c>、
/// <c>BlockRow.Describe</c>），刻意<b>不</b>在这里钉：它们都有 <c>_ =&gt;</c> 兜底，
/// 漏了只是显示难看，不影响链的正确性。
/// </para>
/// </remarks>
public class BlockKindCoverageTests
{
    private const string RevisionId = "2721@aaaaaaaa";

    private static readonly Revision Rev = new("liontrip-cms", 2721, "aaaaaaaa11111111");

    /// <param name="Kind">区块类型。</param>
    /// <param name="PayloadJson">该类型的一个合法载荷。</param>
    /// <param name="Absorbed">
    /// 投影确实收下了这一块；<b>null 表示这种块已停写</b>，投影故意不收它。
    /// <para>
    /// 分成两类而不是直接删掉那几行，是为了让「故意不收」和「忘了写 case」仍然区分得开 ——
    /// 后者才是这张表要防的。
    /// </para>
    /// </param>
    private sealed record Sample(BlockKind Kind, string PayloadJson, Func<ChainState, bool>? Absorbed);

    private static Sample Of<T>(BlockKind kind, T payload, Func<ChainState, bool>? absorbed)
        => new(kind, ActaJson.Serialize(payload), absorbed);

    /// <summary>
    /// 每种区块类型一行。
    /// </summary>
    /// <remarks>
    /// 新增 <see cref="BlockKind"/> 却不加这一行，
    /// <see cref="Every_block_kind_has_a_sample"/> 会失败 —— 那是这张表的全部意义：
    /// 强迫加类型的人当场回答「它的 revision id 从哪读」「投影怎么收它」。
    /// </remarks>
    private static readonly Sample[] Samples =
    [
        // 已停写（2026-09-09）：队列与「谁在评」改由实时状态承载，
        // 写进不可变的链只会留下一地清不掉的僵尸。老区块仍要能读懂。
        Of(BlockKind.Summons, new SummonsPayload(Rev, TestElectors.Pr(), 1, "rules"), null),

        Of(BlockKind.Seating, new SeatingPayload(RevisionId, 0, "e1"), null),

        Of(BlockKind.Ballot,
            new BallotPayload(RevisionId, 0, ReviewDecision.Approve, [], "m", 1),
            state => state.Ballots.ContainsKey(0)),

        Of(BlockKind.Promulgation,
            new PromulgationPayload(RevisionId, ReviewDecision.Approve, [], false, 1, 1),
            state => state.Promulgation is not null),

        // 已停写：节点掉线后它的 ActiveReview 随心跳窗口过期而消失，PR 自动回队列。
        Of(BlockKind.Recess, new RecessPayload(RevisionId, 0, "seating timeout"), null),
    ];

    private static Block BlockOf(Sample sample) => new()
    {
        ChainId = Acta.ChainId,
        Index = 0,
        PrevHash = Block.GenesisPrevHash,
        At = TestElectors.Now,
        Kind = sample.Kind,
        PayloadJson = sample.PayloadJson,
        ElectorId = "e1",
        PublicKey = "pk",
        Signature = "sig",
    };

    [Fact]
    public void Every_block_kind_has_a_sample()
    {
        var missing = Enum.GetValues<BlockKind>()
            .Except(Samples.Select(s => s.Kind))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"新增了 BlockKind 但没在 Samples 里加一行：{string.Join(", ", missing)}");
    }

    /// <summary>漏了会被 SqliteActa 拒收 —— 响亮，但仍然是个 bug。</summary>
    [Fact]
    public void Every_block_kind_yields_its_revision_id()
    {
        var offenders = Samples
            .Where(s => Acta.RevisionIdOf(BlockOf(s)) != RevisionId)
            .Select(s => $"{s.Kind} → {Acta.RevisionIdOf(BlockOf(s)) ?? "null"}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Acta.RevisionIdOf 读不出 revision id，这种块会被 SqliteActa 整块拒收：\n"
                + string.Join('\n', offenders));
    }

    /// <summary>漏了投影会静默忽略这种块 —— 这条是真正要防的。</summary>
    [Fact]
    public void Every_block_kind_is_absorbed_by_the_projection()
    {
        var offenders = Samples
            .Where(s => s.Absorbed is not null)
            .Where(s => !s.Absorbed!(ActaProjection.Project([BlockOf(s)], RevisionId)))
            .Select(s => s.Kind.ToString())
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "ActaProjection.Project 静默忽略了这些块（落到了 default 分支），"
                + "链上有、编排层看不见：\n" + string.Join('\n', offenders));
    }

    /// <summary>
    /// 已停写的块必须投影成空状态。
    /// </summary>
    /// <remarks>
    /// 反方向的守卫：万一哪天有人把 <c>Summons</c> 重新接回投影，而队列早已改由实时状态
    /// 驱动，那就会出现两个真相来源 —— 界面上一个 PR 同时「在队列里」和「已召集」。
    /// 这条钉住「停写就是停写」。
    /// </remarks>
    [Fact]
    public void Retired_block_kinds_project_to_nothing()
    {
        foreach (var sample in Samples.Where(s => s.Absorbed is null))
        {
            var state = ActaProjection.Project([BlockOf(sample)], RevisionId);

            Assert.Empty(state.Ballots);
            Assert.Null(state.Promulgation);
        }
    }

    /// <summary>
    /// 投影按 revisionId 过滤，别的 revision 的块一律不收。
    /// </summary>
    /// <remarks>
    /// 全 mesh 一条链，上面叠着所有 PR 所有版本的块。漏了这层过滤，
    /// 上一版的 Promulgation 会让新版本被当成「已评审」，作者 push 的修复再也不会被评。
    /// </remarks>
    [Fact]
    public void No_block_kind_leaks_across_revisions()
    {
        var leaked = Samples
            .Where(s => s.Absorbed is not null)
            .Where(s => s.Absorbed!(ActaProjection.Project([BlockOf(s)], "9999@bbbbbbbb")))
            .Select(s => s.Kind.ToString())
            .ToList();

        Assert.True(
            leaked.Count == 0,
            "这些块被投影到了别的 revision 上：\n" + string.Join('\n', leaked));
    }
}

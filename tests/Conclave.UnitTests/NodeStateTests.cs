using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;

namespace Conclave.UnitTests;

/// <summary>
/// <see cref="NodeState.Changed"/> 只在数据真的变了时才发。
/// </summary>
/// <remarks>
/// 编排循环每 15 秒无条件调一次 <see cref="NodeState.SetPipeline"/>，而 UI 收到这个事件
/// 就把几张表整个 <c>Clear()</c> 重建。不去重的话界面每 15 秒抖一次：滚动回顶、
/// 悬浮态消失、开着的菜单被关掉 —— 而这段时间里往往什么都没发生。
/// </remarks>
public class NodeStateTests
{
    private static readonly Revision Rev = new("liontrip-cms", 2721, "aaaaaaaa11111111");

    private static PrMeta Pr(string title = "feat: something") => new()
    {
        PrId = 2721,
        Project = "liontrip-cms",
        Repo = "cms-apostrophe",
        Title = title,
        Author = @"LIONMAIL\youngsun",
        SrcCommit = "aaaaaaaa11111111",
    };

    private static PrView View(string stage = "待评审", PrMeta? pr = null)
        => new(Rev, pr ?? Pr(), stage, 1, 0, null, 0, -1);

    private static Block Block(long index) => new()
    {
        ChainId = Acta.ChainId,
        Index = index,
        PrevHash = Domain.Block.GenesisPrevHash,
        At = TestElectors.Now,
        Kind = BlockKind.Ballot,
        PayloadJson = "{}",
        ElectorId = "e1",
        PublicKey = "pk",
        Signature = "sig",
    };

    /// <summary>数一数事件发了几次。</summary>
    private static int Count(Action<NodeState> act)
    {
        var state = new NodeState();
        var raised = 0;

        // 先把初值写进去，再开始数 —— 第一次写必然是变化，不是要测的东西。
        act(state);
        state.Changed += (_, _) => raised++;
        act(state);

        return raised;
    }

    [Fact]
    public void Setting_the_same_pipeline_again_raises_nothing()
        => Assert.Equal(0, Count(s => s.SetPipeline([View()])));

    [Fact]
    public void Setting_the_same_blocks_again_raises_nothing()
        => Assert.Equal(0, Count(s => s.SetRecentBlocks([Block(0), Block(1)])));

    [Fact]
    public void Setting_the_same_status_again_raises_nothing()
        => Assert.Equal(0, Count(s => s.SetStatus("mesh 内 2 个节点在线")));

    /// <summary>但真的变了必须发 —— 少发一次事件，界面就停在旧数据上。</summary>
    [Fact]
    public void A_changed_stage_still_raises()
    {
        var state = new NodeState();
        state.SetPipeline([View("待评审")]);

        var raised = 0;
        state.Changed += (_, _) => raised++;
        state.SetPipeline([View("评审中")]);

        Assert.Equal(1, raised);
    }

    /// <summary>PR 快照里的字段变了也算变 —— 作者改了标题，队列那一行要跟着换。</summary>
    [Fact]
    public void A_changed_pr_snapshot_still_raises()
    {
        var state = new NodeState();
        state.SetPipeline([View(pr: Pr("feat: 原标题"))]);

        var raised = 0;
        state.Changed += (_, _) => raised++;
        state.SetPipeline([View(pr: Pr("feat: 改过的标题"))]);

        Assert.Equal(1, raised);
    }

    /// <summary>条数变了也算变。</summary>
    [Fact]
    public void Dropping_a_row_still_raises()
    {
        var state = new NodeState();
        state.SetRecentBlocks([Block(0), Block(1)]);

        var raised = 0;
        state.Changed += (_, _) => raised++;
        state.SetRecentBlocks([Block(0)]);

        Assert.Equal(1, raised);
    }

    /// <summary>
    /// 额度读数<b>不</b>触发表格重画，只发它自己那条事件。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 探针每 60 秒读一次，而 <see cref="UsageReading.ReadAt"/> 每次都不同 ——
    /// 界面上「上次刷新时刻」正是靠它把「没有变化」和「没在刷新」分开，所以这条
    /// <b>不能</b>去重。
    /// </para>
    /// <para>
    /// 但它影响的只有额度那一块。混在 <see cref="NodeState.Changed"/> 里的话，
    /// 每分钟都会把 PR 队列、Acta、通知几张表整个拆了重建 —— 明明只有几个百分比变了。
    /// </para>
    /// </remarks>
    [Fact]
    public void A_fresh_usage_reading_repaints_only_the_usage_panel()
    {
        var state = new NodeState();
        state.SetUsage(UsageReading.Unbounded);

        var tables = 0;
        var usage = 0;
        state.Changed += (_, _) => tables++;
        state.UsageChanged += (_, _) => usage++;

        state.SetUsage(UsageReading.Unbounded with { ReadAt = DateTimeOffset.UtcNow.AddSeconds(60) });

        Assert.Equal(1, usage);
        Assert.Equal(0, tables);
    }

    /// <summary>
    /// 反过来，表格的变化不该惊动额度面板。
    /// </summary>
    /// <remarks>
    /// 两条事件各管各的，才谈得上「谁变了就重画谁」。
    /// </remarks>
    [Fact]
    public void A_pipeline_change_does_not_repaint_the_usage_panel()
    {
        var state = new NodeState();
        state.SetPipeline([View("待评审")]);

        var usage = 0;
        state.UsageChanged += (_, _) => usage++;
        state.SetPipeline([View("评审中")]);

        Assert.Equal(0, usage);
    }
}

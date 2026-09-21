using Conclave.Application;

// 命名空间 Conclave.App 跟类 App 同名，`TrayApp.TrayFace` 会被解析成命名空间成员。
// 跟 App.axaml.cs 头部注释说的是同一个坑，那边的办法是全限定，这里用别名。
using TrayApp = Conclave.App.App;

namespace Conclave.UnitTests;

/// <summary>
/// 菜单栏图标的三张脸，以及它们各自的悬浮提示。
/// </summary>
/// <remarks>
/// 图标是这个进程<b>唯一</b>常驻可见的东西 —— 面板要点一下才开，而人多半不会主动去点。
/// 所以「有活等着你」这件事能不能被看见，全在这几行上。
/// </remarks>
public class TrayFaceTests
{
    /// <summary>
    /// 一份快照。
    /// </summary>
    /// <remarks>
    /// 取的是 <see cref="NodeState.Attention"/> 而不是 <c>NodeState</c> 本身 ——
    /// 图标和提示语必须算自同一份快照，分两次读会撕出「有角标 + 说空闲」。
    /// </remarks>
    private static (int PendingAssignments, bool Reviewing) State(int pending, bool reviewing)
    {
        var state = new NodeState();
        state.SetPendingAssignments(pending);
        state.SetReviewing(reviewing);
        return state.Attention;
    }

    /// <remarks>
    /// 写成 Fact 而不是 Theory：<c>TrayFace</c> 是 internal，而 xUnit 要求 public 的测试方法，
    /// 拿它当参数类型会撞上可访问性不一致。挪进方法体里就没这回事。
    /// </remarks>
    [Fact]
    public void Each_situation_has_its_own_face()
    {
        Assert.Equal(TrayApp.TrayFace.Idle, TrayApp.FaceOf(State(0, reviewing: false)));
        Assert.Equal(TrayApp.TrayFace.Busy, TrayApp.FaceOf(State(0, reviewing: true)));
        Assert.Equal(TrayApp.TrayFace.Pending, TrayApp.FaceOf(State(1, reviewing: false)));
    }

    /// <summary>
    /// 待确认压过评审中。
    /// </summary>
    /// <remarks>
    /// 只能显示一张脸，而这两件事对人的要求完全不同：评审会自己跑完，待确认不会 ——
    /// 没人点，那个 PR 就一直卡着。所以要人动手的那个优先。
    /// </remarks>
    [Fact]
    public void A_pending_assignment_outranks_a_running_review()
        => Assert.Equal(TrayApp.TrayFace.Pending, TrayApp.FaceOf(State(1, reviewing: true)));

    /// <summary>被图标盖掉的那件事，提示语里要补回来。</summary>
    [Fact]
    public void The_tooltip_says_both_when_both_are_true()
        => Assert.Equal("Conclave · 2 条指派待确认 · 评审中", TrayApp.TipFor(State(2, reviewing: true)));

    [Theory]
    [InlineData(0, false, "Conclave · 空闲")]
    [InlineData(0, true, "Conclave · 评审中")]
    [InlineData(1, false, "Conclave · 1 条指派待确认")]
    public void The_tooltip_names_what_is_happening(int pending, bool reviewing, string expected)
        => Assert.Equal(expected, TrayApp.TipFor(State(pending, reviewing)));

    /// <summary>
    /// 处理掉一条不等于全处理完。
    /// </summary>
    /// <remarks>
    /// 记的是条数不是 bool，正是为了这个：两条里点掉一条，图标不该就此恢复。
    /// </remarks>
    [Fact]
    public void Clearing_one_of_two_keeps_the_badge_on()
    {
        var state = new NodeState();
        state.SetPendingAssignments(2);
        state.SetPendingAssignments(1);

        Assert.Equal(TrayApp.TrayFace.Pending, TrayApp.FaceOf(state.Attention));

        state.SetPendingAssignments(0);
        Assert.Equal(TrayApp.TrayFace.Idle, TrayApp.FaceOf(state.Attention));
    }

    /// <summary>没变就不发事件 —— 收到 Changed 的那头会把几张表整个重建。</summary>
    [Fact]
    public void Setting_the_same_count_raises_nothing()
    {
        var state = new NodeState();
        var raised = 0;
        state.Changed += (_, _) => raised++;

        state.SetPendingAssignments(1);
        state.SetPendingAssignments(1);
        state.SetPendingAssignments(1);

        Assert.Equal(1, raised);
    }

    /// <summary>三张托盘图都得在 Assets 里 —— 少一张的表现是那个状态下图标不动。</summary>
    [Theory]
    [InlineData("conclave-tray-idle.png")]
    [InlineData("conclave-tray-busy.png")]
    [InlineData("conclave-tray-alert.png")]
    public void The_tray_assets_are_all_there(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conclave.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        Assert.True(File.Exists(
            Path.Combine(dir.FullName, "src", "Conclave.App", "Assets", name)), name);
    }
}

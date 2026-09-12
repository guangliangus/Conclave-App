using System.Collections.ObjectModel;
using Conclave.App.ViewModels;
using Conclave.Application;
using Conclave.Domain;

namespace Conclave.UnitTests;

/// <summary>
/// 「在线节点」表里那一列额度。
/// </summary>
/// <remarks>
/// 这一组钉的是<b>那一列画的是哪个数</b>。每一行画的都是 Claude 自己报的窗口读数，
/// 而 <see cref="Elector.Utilization"/> 是 <see cref="Conclave.Application.UsagePressure"/>
/// 折算过的压力值 —— 两者对不上是常态（7d 74% / 阈值 95% 折出来是 62%），
/// 曾经就是因为表里画的是后者、额度卡画的是前者，同一个界面上两个「CLAUDE 额度」对不上。
/// 对端那一份现在随实时状态过来（<see cref="LiveState.UsageWindows"/>），所以「本机画真值、
/// 别人画折算值」这个不一致也没了；只有拆不出窗口时才退回画标量。
/// </remarks>
public sealed class NodeRowTests
{
    private static readonly DateTimeOffset Now = TestElectors.Now;

    private static UsageWindow Window(string key, double utilization, bool gates = true)
        => new(key, utilization, Now.AddHours(4)) { Gates = gates };

    private static NodeRow Row(
        double utilization, IReadOnlyList<UsageWindow>? quotas, bool isSelf = true)
        => new(
            TestElectors.Make("n1", utilization: utilization, heartbeat: Now),
            isSelf,
            Now,
            [],
            quotas);

    [Fact]
    public void Self_shows_the_raw_window_readings_not_the_pressure_scalar()
    {
        // 截图上的那一组：5h 3% · 7d 74%，折算出来的压力是 62%。
        var row = Row(0.6231, [Window("five_hour", 0.03), Window("seven_day", 0.74)]);

        Assert.True(row.HasQuotas);
        Assert.Equal(["5h", "7d"], row.Quotas.Select(q => q.Short));

        // 要的就是原始读数本身，不是 62%。
        Assert.Equal(["3%", "74%"], row.Quotas.Select(q => q.PercentText));
    }

    [Fact]
    public void Model_scoped_sub_quota_is_not_shown_in_the_node_table()
    {
        // Fable 的周额度打满不妨碍用 Opus 评审，它进不了入席判定，
        // 也就不该出现在这张「席位为什么落在它身上」的表里。
        var row = Row(0.62, [
            Window("five_hour", 0.03),
            Window("seven_day", 0.74),
            Window("seven_day:Fable", 0.45, gates: false),
        ]);

        Assert.Equal(["5h", "7d"], row.Quotas.Select(q => q.Short));
    }

    [Fact]
    public void A_peer_shows_the_same_5h_and_7d_rows_as_this_node()
    {
        // 明细现在随实时状态过来（LiveState.UsageWindows），所以别人那一行画的也是真实读数。
        // 早先这里只有一个折算标量，于是同一张表上本机写「5h 31% / 7d 77%」、
        // 别人写「压力 26%」—— 两个不是一回事的数并排摆着。
        var row = Row(0.62, [Window("five_hour", 0.26), Window("seven_day", 0.41)], isSelf: false);

        Assert.True(row.HasQuotas);
        Assert.Equal(["5h", "7d"], row.Quotas.Select(q => q.Short));
        Assert.Equal(["26%", "41%"], row.Quotas.Select(q => q.PercentText));
    }

    [Fact]
    public void A_peer_that_broadcasts_no_window_detail_falls_back_to_the_pressure_scalar()
    {
        // 还没升级的节点上报的状态里没有这个字段，反序列化成空列表 —— 那一行退回画压力值，
        // 而不是画出一列空白。tooltip 要说清是「没上报」，别让人以为是额度读数丢了。
        var row = Row(0.62, [], isSelf: false);

        Assert.False(row.HasQuotas);
        Assert.Equal("62%", row.QuotaText);
        Assert.Contains("没上报窗口明细", row.Tip, StringComparison.Ordinal);
    }

    [Fact]
    public void Self_without_window_detail_also_falls_back()
    {
        // 真值读不到、退到「按预算折算」时没有窗口可拆 —— 那一行退回画压力值，
        // 而不是画出一列空白。本机的空跟对端的空原因不同，tooltip 分开说。
        var row = Row(0.4, []);

        Assert.False(row.HasQuotas);
        Assert.Equal("40%", row.QuotaText);
        Assert.Contains("按预算折算", row.Tip, StringComparison.Ordinal);
    }

    [Fact]
    public void Changing_a_window_percentage_actually_repaints_the_row()
    {
        // RowSync 的内容指纹用反射遍历公开属性，嵌套对象走 Convert.ToString ——
        // UsageWindowRow 要是普通 class，这里只会得到类型名，于是百分比变了
        // 这一行也不会被换掉，表现是那一列永远停在旧值、且没有任何报错。
        // 这条用例就是钉住「它必须是 record」。
        ObservableCollection<NodeRow> target =
            [Row(0.62, [Window("five_hour", 0.03), Window("seven_day", 0.74)])];

        RowSync.Apply(
            target,
            [Row(0.62, [Window("five_hour", 0.55), Window("seven_day", 0.74)])],
            static r => r.Id);

        Assert.Equal("55%", target[0].Quotas[0].PercentText);
    }

    /// <summary>
    /// claude 版本要摆在明面上。
    /// </summary>
    /// <remarks>
    /// 版本过旧的节点会被服务端直接拒（<c>API Error: 400 … does not support this model</c>），
    /// 而且只有它自己会挂 —— 别的节点照常出票，表面上只是「某个 PR 偶尔评不出来」。
    /// 真事故里，找出是哪台机器花掉的时间远多于修它。
    /// </remarks>
    [Fact]
    public void The_claude_version_is_on_the_row_not_only_in_the_tooltip()
    {
        var row = new NodeRow(
            TestElectors.Make("n1", claudeVersion: "2.1.104"), isSelf: false, Now, []);

        Assert.Contains("claude 2.1.104", row.Subtitle, StringComparison.Ordinal);
        Assert.Contains("claude 2.1.104", row.Tip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_node_that_reports_no_claude_version_says_so_instead_of_showing_a_blank()
    {
        // 老节点不广播这个字段，那台机器上找不到 claude 也是空 —— 两种都得说人话。
        var row = new NodeRow(
            TestElectors.Make("n1", claudeVersion: ""), isSelf: false, Now, []);

        Assert.DoesNotContain("claude", row.Subtitle, StringComparison.Ordinal);
        Assert.Contains("claude 版本未知", row.Tip, StringComparison.Ordinal);
    }

    /// <summary>
    /// Conclave 自己的版本也要摆在明面上。
    /// </summary>
    /// <remarks>
    /// 它比 claude 的版本更难从别处看出来：升级是各机器各自装的，一台落在旧版上不会有
    /// 任何报错，只表现成「这台机器的判断跟别人不一样」。
    /// </remarks>
    [Fact]
    public void The_app_version_is_on_the_row_not_only_in_the_tooltip()
    {
        var row = new NodeRow(
            TestElectors.Make("n1", appVersion: "1.4.2"), isSelf: false, Now, []);

        Assert.Contains("Conclave v1.4.2", row.Subtitle, StringComparison.Ordinal);
        Assert.Contains("Conclave v1.4.2", row.Tip, StringComparison.Ordinal);
    }

    /// <summary>版本跟本节点不一样时直接说出来，不让人自己比对两行小字。</summary>
    [Fact]
    public void A_peer_on_another_version_says_which_version_this_node_is_on()
    {
        var row = new NodeRow(
            TestElectors.Make("n1", appVersion: "0.0.1-ancient"), isSelf: false, Now, []);

        Assert.Contains($"本节点是 v{AppInfo.Version}", row.Tip, StringComparison.Ordinal);
    }

    /// <summary>本节点那一行不必跟自己比。</summary>
    [Fact]
    public void The_self_row_does_not_compare_itself_with_itself()
    {
        var row = new NodeRow(
            TestElectors.Make("n1", appVersion: "0.0.1-ancient"), isSelf: true, Now, []);

        Assert.DoesNotContain("本节点是 v", row.Tip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_node_that_reports_no_app_version_says_so_instead_of_showing_a_blank()
    {
        var row = new NodeRow(
            TestElectors.Make("n1", appVersion: ""), isSelf: false, Now, []);

        Assert.DoesNotContain("Conclave v", row.Subtitle, StringComparison.Ordinal);
        Assert.Contains("Conclave 版本未知", row.Tip, StringComparison.Ordinal);
    }
}

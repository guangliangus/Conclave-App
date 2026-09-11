using System.Collections.ObjectModel;
using Conclave.App.ViewModels;
using Conclave.Application.Ports;
using Conclave.Domain;

namespace Conclave.UnitTests;

/// <summary>
/// 「在线节点」表里那一列额度。
/// </summary>
/// <remarks>
/// 这一组钉的是<b>那一列画的是哪个数</b>。本节点画 Claude 自己报的窗口读数，
/// 而 <see cref="Elector.Utilization"/> 是 <see cref="Conclave.Application.UsagePressure"/>
/// 折算过的压力值 —— 两者对不上是常态（7d 74% / 阈值 95% 折出来是 62%），
/// 曾经就是因为表里画的是后者、额度卡画的是前者，同一个界面上两个「CLAUDE 额度」对不上。
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
    public void Peer_falls_back_to_the_pressure_scalar()
    {
        // 心跳里只有一个标量（Beacon.SigningPayload），对端的窗口明细拿不到。
        var row = Row(0.62, quotas: null, isSelf: false);

        Assert.False(row.HasQuotas);
        Assert.Empty(row.Quotas);
        Assert.Equal("62%", row.QuotaText);
    }

    [Fact]
    public void Self_without_window_detail_also_falls_back()
    {
        // 真值读不到、退到「按预算折算」时没有窗口可拆 —— 那一行退回画压力值，
        // 而不是画出一列空白。
        var row = Row(0.4, []);

        Assert.False(row.HasQuotas);
        Assert.Equal("40%", row.QuotaText);
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
}

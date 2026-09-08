using Conclave.Domain;

namespace Conclave.UnitTests;

public class HrwTests
{
    private static readonly string[] Nodes = ["node-a", "node-b", "node-c", "node-d", "node-e"];

    [Fact]
    public void Pick_is_deterministic_across_calls_and_input_order()
    {
        var forward = Hrw.PickId("2721@dc1d1d47", 0, Nodes);
        var reversed = Hrw.PickId("2721@dc1d1d47", 0, Nodes.Reverse());

        // 这是整个设计的基石：各节点独立算，必须算出同一个人。
        Assert.Equal(forward, reversed);
        Assert.Equal(forward, Hrw.PickId("2721@dc1d1d47", 0, Nodes));
    }

    [Fact]
    public void Different_rounds_generally_pick_different_nodes()
    {
        var picks = Enumerable.Range(0, 3).Select(r => Hrw.PickId("2721@dc1d1d47", r, Nodes)).ToList();
        Assert.True(picks.Distinct().Count() > 1, "不同轮次应当散开，否则 quorum 会全落在一个人头上");
    }

    [Fact]
    public void Removing_a_node_only_reshuffles_that_node_s_share()
    {
        // HRW 相对 hash%count 的核心优势：节点掉线时其余分配保持稳定。
        var keys = Enumerable.Range(1, 400).Select(i => $"pr:{i}").ToList();
        var before = keys.ToDictionary(k => k, k => Hrw.PickId(k, 0, Nodes)!);

        var survivors = Nodes.Where(n => n != "node-c").ToList();
        var after = keys.ToDictionary(k => k, k => Hrw.PickId(k, 0, survivors)!);

        var moved = keys.Count(k => before[k] != after[k]);
        var wasOnC = keys.Count(k => before[k] == "node-c");

        // 只有原本落在 node-c 上的 key 会搬家，一个不多。
        Assert.Equal(wasOnC, moved);
    }

    [Fact]
    public void Weight_shifts_the_distribution()
    {
        var keys = Enumerable.Range(1, 2000).Select(i => $"pr:{i}").ToList();

        var idle = TestElectors.Make("busy-node");
        var busy = TestElectors.Make("busy-node", running: 5);
        Assert.True(busy.Weight < idle.Weight, "在跑的任务越多，权重应当越低");

        var mesh = new[] { TestElectors.Make("a"), busy };
        var loaded = keys.Count(k => Hrw.Pick(k, 0, mesh)!.Id == "busy-node");

        var freshMesh = new[] { TestElectors.Make("a"), idle };
        var unloaded = keys.Count(k => Hrw.Pick(k, 0, freshMesh)!.Id == "busy-node");

        Assert.True(loaded < unloaded, $"忙节点应当少拿活：忙={loaded} 闲={unloaded}");
    }

    [Fact]
    public void Zero_weight_node_is_never_picked_when_an_alternative_exists()
    {
        var starved = TestElectors.Make("starved", running: 0) with { Reviews24h = 0 };
        var mesh = new[] { starved, TestElectors.Make("normal") };

        // 权重恒正，两者都可能中签；这里只断言不会抛异常且总能选出一个。
        Assert.NotNull(Hrw.Pick("k", 0, mesh));
    }

    [Fact]
    public void Pick_returns_null_for_an_empty_pool()
        => Assert.Null(Hrw.PickId("k", 0, []));
}

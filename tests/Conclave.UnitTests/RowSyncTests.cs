using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Conclave.App.ViewModels;

namespace Conclave.UnitTests;

/// <summary>
/// 表格刷新的就地对齐。
/// </summary>
/// <remarks>
/// <para>
/// 这一组用例钉的是<b>发了几个集合事件</b>，不是「最后内容对不对」—— 后者本来也不会错。
/// 事件数才是要害：<c>ItemsControl</c> 每收到一个 <c>Reset</c> 就把整张表的容器拆了重建，
/// 而 Avalonia 12.1.2 的 macOS 后端每重建一轮界面会漏一批 CoreText 字体对象
/// （实测每 100 秒 69 套）。一次「什么都没变」的刷新发出零个事件，是这次改造的全部意义。
/// </para>
/// <para>
/// 「没变的行还是同一个实例」也要钉住：实例换了，绑在它上面的容器就得重做，
/// 哪怕内容一模一样。
/// </para>
/// </remarks>
public sealed class RowSyncTests
{
    /// <summary>一个最小的行对象：有身份，有会变的内容。跟真实行类型一样是不可变的。</summary>
    private sealed class Row(string id, string text, int count = 0)
    {
        public string Id { get; } = id;

        public string Text { get; } = text;

        public int Count { get; } = count;
    }

    /// <summary>把集合上发生的事件录下来。</summary>
    private sealed class Recorder
    {
        private readonly List<NotifyCollectionChangedAction> _actions = [];

        internal Recorder(INotifyCollectionChanged collection)
            => collection.CollectionChanged += (_, e) => _actions.Add(e.Action);

        internal IReadOnlyList<NotifyCollectionChangedAction> Actions => _actions;
    }

    private static ObservableCollection<Row> Seed(params Row[] rows) => [.. rows];

    private static void Apply(ObservableCollection<Row> target, params Row[] next)
        => RowSync.Apply(target, next, static r => r.Id);

    [Fact]
    public void Nothing_changed_means_no_collection_events_at_all()
    {
        var target = Seed(new Row("a", "甲"), new Row("b", "乙"), new Row("c", "丙"));
        var before = target.ToList();
        var events = new Recorder(target);

        // 定时器到点了，但后台什么都没发生 —— 这是绝大多数刷新的实际情况。
        Apply(target, new Row("a", "甲"), new Row("b", "乙"), new Row("c", "丙"));

        Assert.Empty(events.Actions);

        // 实例也必须是原来那几个：换了实例，容器一样要重建。
        Assert.Equal(before, target);
    }

    [Fact]
    public void Only_the_row_that_changed_is_replaced()
    {
        var target = Seed(new Row("a", "甲"), new Row("b", "乙"), new Row("c", "丙"));
        var untouched = target[0];
        var events = new Recorder(target);

        Apply(target, new Row("a", "甲"), new Row("b", "乙乙"), new Row("c", "丙"));

        Assert.Equal([NotifyCollectionChangedAction.Replace], events.Actions);
        Assert.Same(untouched, target[0]);
        Assert.Equal("乙乙", target[1].Text);
        Assert.Equal("丙", target[2].Text);
    }

    [Fact]
    public void A_property_nobody_remembered_still_counts()
    {
        // 指纹是反射出来的，所以给行加字段不会漏。手写拼接的版本会在这里静默失效：
        // 界面上那一格永远停在旧值，而且不报任何错。
        var target = Seed(new Row("a", "甲", count: 1));
        var events = new Recorder(target);

        Apply(target, new Row("a", "甲", count: 2));

        Assert.Equal([NotifyCollectionChangedAction.Replace], events.Actions);
        Assert.Equal(2, target[0].Count);
    }

    [Fact]
    public void A_new_row_lands_at_its_position_without_touching_the_rest()
    {
        var target = Seed(new Row("a", "甲"), new Row("c", "丙"));
        var first = target[0];
        var last = target[1];
        var events = new Recorder(target);

        Apply(target, new Row("a", "甲"), new Row("b", "乙"), new Row("c", "丙"));

        Assert.Equal([NotifyCollectionChangedAction.Add], events.Actions);
        Assert.Same(first, target[0]);
        Assert.Equal("b", target[1].Id);
        Assert.Same(last, target[2]);
    }

    [Fact]
    public void A_row_that_went_away_is_removed_and_nothing_else_moves()
    {
        var target = Seed(new Row("a", "甲"), new Row("b", "乙"), new Row("c", "丙"));
        var first = target[0];
        var last = target[2];
        var events = new Recorder(target);

        Apply(target, new Row("a", "甲"), new Row("c", "丙"));

        Assert.Equal([NotifyCollectionChangedAction.Remove], events.Actions);
        Assert.Same(first, target[0]);
        Assert.Same(last, target[1]);
    }

    [Fact]
    public void Reordering_moves_rows_instead_of_rebuilding_them()
    {
        // 队列排序会变（已出结论的往下沉），但行本身没变 —— 那该是 Move 不是 Reset。
        var target = Seed(new Row("a", "甲"), new Row("b", "乙"), new Row("c", "丙"));
        var a = target[0];
        var events = new Recorder(target);

        Apply(target, new Row("c", "丙"), new Row("b", "乙"), new Row("a", "甲"));

        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, events.Actions);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Replace, events.Actions);
        Assert.Equal(["c", "b", "a"], target.Select(r => r.Id));
        Assert.Same(a, target[2]);
    }

    [Fact]
    public void Emptying_the_table_removes_every_row()
    {
        var target = Seed(new Row("a", "甲"), new Row("b", "乙"));

        Apply(target);

        Assert.Empty(target);
    }

    [Fact]
    public void Filling_an_empty_table_adds_every_row_in_order()
    {
        var target = new ObservableCollection<Row>();

        Apply(target, new Row("a", "甲"), new Row("b", "乙"));

        Assert.Equal(["a", "b"], target.Select(r => r.Id));
    }

    [Fact]
    public void A_wholesale_turnover_still_ends_up_correct()
    {
        // 最坏情况：一个都对不上。内容对就行，事件数这里不作要求。
        var target = Seed(new Row("a", "甲"), new Row("b", "乙"), new Row("c", "丙"));

        Apply(target, new Row("x", "子"), new Row("y", "丑"));

        Assert.Equal(["x", "y"], target.Select(r => r.Id));
        Assert.Equal(["子", "丑"], target.Select(r => r.Text));
    }

    [Fact]
    public void Insert_remove_and_edit_in_one_pass()
    {
        var target = Seed(new Row("a", "甲"), new Row("b", "乙"), new Row("c", "丙"));
        var kept = target[2];

        Apply(target, new Row("z", "零"), new Row("b", "乙乙"), new Row("c", "丙"));

        Assert.Equal(["z", "b", "c"], target.Select(r => r.Id));
        Assert.Equal("乙乙", target[1].Text);
        Assert.Same(kept, target[2]);
    }
}

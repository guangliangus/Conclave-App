using Conclave.Application;

namespace Conclave.UnitTests;

/// <summary>
/// 通知收件箱。
/// </summary>
/// <remarks>
/// 它是纯内存状态，所以这里直接对 <see cref="NodeState"/> 断言，不需要 Harness。
/// 值得测的只有三件事：顺序、去重、以及「未读」和「列表」是两码事。
/// </remarks>
public class NoticeTests
{
    [Fact]
    public void Newest_first_and_every_one_counts_as_unread()
    {
        var state = new NodeState();

        state.Notify(NoticeKind.Info, "有人请你评审");
        state.Notify(NoticeKind.Ok, "对方接受了");

        Assert.Equal(["对方接受了", "有人请你评审"], state.Notices.Select(n => n.Title));
        Assert.Equal(2, state.UnreadNotices);
    }

    /// <summary>轮询失败这类问题每轮都会复现，不去重会把真正的事件挤出去。</summary>
    [Fact]
    public void Same_content_within_a_minute_is_recorded_once()
    {
        var state = new NodeState();

        state.Notify(NoticeKind.Warn, "读不到额度", "claude 子进程超时");
        state.Notify(NoticeKind.Warn, "读不到额度", "claude 子进程超时");

        Assert.Single(state.Notices);
        Assert.Equal(1, state.UnreadNotices);
    }

    /// <summary>去重看的是整条内容 —— 同一句话换了理由就是另一件事。</summary>
    [Fact]
    public void Different_detail_is_a_different_notice()
    {
        var state = new NodeState();

        state.Notify(NoticeKind.Warn, "对方拒绝了", "额度不够");
        state.Notify(NoticeKind.Warn, "对方拒绝了", "已经在评别的 PR");

        Assert.Equal(2, state.Notices.Count);
    }

    /// <summary>
    /// 只有紧挨着的那条参与去重。
    /// </summary>
    /// <remarks>
    /// 「同一件事又发生了一次」值得记 —— 中间夹了别的事件说明时间线在走。
    /// 去重要防的是同一句话连着刷屏，不是永久屏蔽。
    /// </remarks>
    [Fact]
    public void An_event_recurring_after_something_else_is_recorded_again()
    {
        var state = new NodeState();

        state.Notify(NoticeKind.Warn, "读不到额度");
        state.Notify(NoticeKind.Ok, "结论已公布");
        state.Notify(NoticeKind.Warn, "读不到额度");

        Assert.Equal(3, state.Notices.Count);
    }

    [Fact]
    public void Marking_read_clears_the_badge_but_keeps_the_list()
    {
        var state = new NodeState();
        state.Notify(NoticeKind.Info, "有人请你评审");

        state.MarkNoticesRead();

        Assert.Equal(0, state.UnreadNotices);
        Assert.Single(state.Notices);
    }

    [Fact]
    public void Clearing_empties_both()
    {
        var state = new NodeState();
        state.Notify(NoticeKind.Info, "有人请你评审");

        state.ClearNotices();

        Assert.Empty(state.Notices);
        Assert.Equal(0, state.UnreadNotices);
    }

    /// <summary>面板订阅的是 <c>Changed</c>；不发这个事件的话通知要等下一轮定时刷新才出现。</summary>
    [Fact]
    public void Notifying_wakes_the_ui()
    {
        var state = new NodeState();
        var raised = 0;
        state.Changed += (_, _) => raised++;

        state.Notify(NoticeKind.Info, "有人请你评审");

        Assert.Equal(1, raised);
    }

    /// <summary>已经是零的时候不发事件 —— 停在通知页上时每轮刷新都会调一次它。</summary>
    [Fact]
    public void Marking_read_twice_wakes_the_ui_once()
    {
        var state = new NodeState();
        state.Notify(NoticeKind.Info, "有人请你评审");

        var raised = 0;
        state.Changed += (_, _) => raised++;
        state.MarkNoticesRead();
        state.MarkNoticesRead();

        Assert.Equal(1, raised);
    }

    /// <summary>200 条封顶，掉的是最老的那些。</summary>
    [Fact]
    public void The_list_is_capped_and_drops_the_oldest()
    {
        var state = new NodeState();
        for (var i = 0; i < 250; i++)
        {
            state.Notify(NoticeKind.Info, "第 " + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 件事");
        }

        Assert.Equal(200, state.Notices.Count);
        Assert.Equal("第 249 件事", state.Notices[0].Title);
        Assert.Equal("第 50 件事", state.Notices[^1].Title);
    }
}

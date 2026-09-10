using Conclave.Application;

namespace Conclave.UnitTests;

/// <summary>
/// 评审日志的增量取法。
/// </summary>
/// <remarks>
/// 面板每两秒按序号要一次增量，所以序号语义错了会表现成「日志重复几十遍」或者「不再更新」——
/// 两种都很难在界面上定位到这里，值得钉住。
/// </remarks>
public sealed class ReviewProgressLogTests
{
    private const string Rev = "2721@aaaaaaaa";

    [Fact]
    public void Reading_from_the_last_sequence_returns_only_what_is_new()
    {
        var log = new ReviewProgressLog();
        log.Begin(Rev, "开始");
        log.Append(Rev, "第一行");

        var first = log.Read(Rev, 0);
        Assert.Equal(2, first.Lines.Count);
        Assert.True(first.Running);

        log.Append(Rev, "第二行");

        var second = log.Read(Rev, first.Next);
        Assert.Single(second.Lines);
        Assert.Contains("第二行", second.Lines[0], StringComparison.Ordinal);

        // 没有新行时给空，序号原地不动 —— 面板据此什么都不追加。
        var third = log.Read(Rev, second.Next);
        Assert.Empty(third.Lines);
        Assert.Equal(second.Next, third.Next);
    }

    [Fact]
    public void Every_line_is_stamped_with_a_time()
    {
        var log = new ReviewProgressLog();
        log.Append(Rev, "拉取工作区");

        // 时间戳是判断「卡住了还是正常慢」的唯一依据 —— 一行日志没有时刻就没有意义。
        Assert.Matches(@"^\d{2}:\d{2}:\d{2} 拉取工作区$", log.Read(Rev, 0).Lines[0]);
    }

    [Fact]
    public void Blank_lines_are_dropped()
    {
        var log = new ReviewProgressLog();
        log.Append(Rev, "  ");
        log.Append(Rev, string.Empty);

        // stream-json 里认不出的事件会被压成空串，它们不该在面板上留下空行。
        Assert.Empty(log.Read(Rev, 0).Lines);
    }

    [Fact]
    public void Finishing_flips_running_so_the_panel_stops_polling()
    {
        var log = new ReviewProgressLog();
        log.Begin(Rev, "开始");
        log.End(Rev, "完成");

        var chunk = log.Read(Rev, 0);
        Assert.False(chunk.Running);
        Assert.Equal(2, chunk.Lines.Count);
    }

    [Fact]
    public void Restarting_the_same_revision_clears_the_previous_run()
    {
        var log = new ReviewProgressLog();
        log.Begin(Rev, "第一次");
        log.End(Rev, "失败");

        log.Begin(Rev, "重试");

        // 同一版重试时把上一次的日志留着，会让人以为这次已经跑到那一步了。
        var chunk = log.Read(Rev, 0);
        Assert.Single(chunk.Lines);
        Assert.Contains("重试", chunk.Lines[0], StringComparison.Ordinal);
        Assert.True(chunk.Running);
    }

    [Fact]
    public void An_unknown_revision_reads_as_empty_and_not_running()
    {
        var chunk = new ReviewProgressLog().Read("9999@ffffffff", 12);

        // 对端刚好把这一版挤出缓冲时会走到这里：面板要显示「取不到」，而不是崩掉。
        Assert.Empty(chunk.Lines);
        Assert.False(chunk.Running);
        Assert.Equal(12, chunk.Next);
    }

    [Fact]
    public void An_out_of_range_sequence_falls_back_to_what_is_still_buffered()
    {
        var log = new ReviewProgressLog();
        log.Begin(Rev, "开始");

        // 序号比实际有的还大（对端重启过、或者换了一次评审）：给现有的，不要给空。
        var chunk = log.Read(Rev, 999);
        Assert.Single(chunk.Lines);
    }
}

using System.Globalization;
using Conclave.App.ViewModels;
using Conclave.Application;

namespace Conclave.UnitTests;

/// <summary>
/// 日志面板的行上限。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReviewProgressLog"/> 那头是环形缓冲，只留最后
/// <see cref="ReviewProgressLog.MaxLines"/> 行；而面板是<b>按序号取增量</b>的，
/// 于是「源端还留着多少行」和「面板攒了多少行」是两回事 —— 后者原先只增不减。
/// 一次十几分钟的评审（<c>claude --output-format stream-json --verbose</c> 会把每个
/// 工具返回体全文打出来）能推进来几千行，而日志面板的每一行都是一个 TextBlock。
/// </para>
/// <para>
/// 钉的是<b>不变量</b>而不是某个具体数字：面板里的行数任何时候都不超过源端的上限，
/// 留下的是最后那一批，而状态栏上的计数走另一条路（累计收到过多少行）——
/// 它不能跟着一起封顶，否则一条还在跑的评审看起来会像卡在上限上。
/// </para>
/// </remarks>
public sealed class ReviewLogViewModelTests
{
    private const string Rev = "edison-test!2878!a8d427bb";

    /// <summary>
    /// 建一个已经在跑的日志面板。
    /// </summary>
    /// <remarks>
    /// 必须<b>先</b>往日志里写一行再建 ViewModel：构造函数会立刻 poll 一次，而
    /// <see cref="ReviewProgressLog.Read"/> 对一个还不存在的 revision 返回的是
    /// <c>Running: false</c>，面板会据此判定「评完了」并停掉轮询。
    /// 这一行也会被第一次 poll 收走，所以累计计数从 1 起。
    /// </remarks>
    private static (ReviewLogViewModel Vm, ReviewProgressLog Log, int Seeded) Make()
    {
        var self = TestElectors.Make("alan");
        var log = new ReviewProgressLog();
        log.Append(Rev, "拉代码进临时工作区");

        var vm = new ReviewLogViewModel(new FakeMesh(self), log, Rev, self.Id, "edison-test!2878");
        return (vm, log, 1);
    }

    private static void Fill(ReviewProgressLog log, int from, int count)
    {
        for (var i = from; i < from + count; i++)
        {
            log.Append(Rev, "行 " + i.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Suspend + Resume 是现成的内部入口，Resume 会立刻补一次增量。</summary>
    private static void Poll(ReviewLogViewModel vm)
    {
        vm.Suspend();
        vm.Resume();
    }

    [Fact]
    public void The_panel_never_holds_more_lines_than_the_source_ring()
    {
        var (vm, log, _) = Make();

        // 三轮，每轮都灌满一整个环 —— 轮与轮之间环整个转过一遍，所以每次增量
        // 拿回来的都是全新的 MaxLines 行。不封顶的话这里会是三倍。
        for (var round = 0; round < 3; round++)
        {
            Fill(log, round * ReviewProgressLog.MaxLines, ReviewProgressLog.MaxLines);
            Poll(vm);
        }

        Assert.Equal(ReviewProgressLog.MaxLines, vm.Lines.Count);
    }

    [Fact]
    public void Trimming_drops_from_the_front_so_the_newest_lines_survive()
    {
        var (vm, log, _) = Make();

        Fill(log, 0, ReviewProgressLog.MaxLines);
        Poll(vm);

        Fill(log, ReviewProgressLog.MaxLines, 10);
        Poll(vm);

        Assert.Equal(ReviewProgressLog.MaxLines, vm.Lines.Count);
        Assert.EndsWith(
            "行 " + (ReviewProgressLog.MaxLines + 9).ToString(CultureInfo.InvariantCulture),
            vm.Lines[^1],
            StringComparison.Ordinal);
        Assert.DoesNotContain(vm.Lines, l => l.EndsWith("行 0", StringComparison.Ordinal));
    }

    [Fact]
    public void The_status_counts_every_line_received_not_just_the_ones_still_shown()
    {
        var (vm, log, seeded) = Make();

        Fill(log, 0, ReviewProgressLog.MaxLines);
        Poll(vm);
        Fill(log, ReviewProgressLog.MaxLines, ReviewProgressLog.MaxLines);
        Poll(vm);

        var total = seeded + (2 * ReviewProgressLog.MaxLines);

        Assert.Equal(ReviewProgressLog.MaxLines, vm.Lines.Count);
        Assert.Contains(
            total.ToString(CultureInfo.InvariantCulture) + " 行",
            vm.Status,
            StringComparison.Ordinal);
    }
}

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
public sealed class ReviewLogViewModelTests : IDisposable
{
    private const string Rev = "edison-test!2878!a8d427bb";

    private readonly TempHome _home = new("logvm");

    public void Dispose() => _home.Dispose();

    /// <summary>
    /// 建一个已经在跑的日志面板。
    /// </summary>
    /// <remarks>
    /// 先往日志里写一行再建 ViewModel，构造函数那次 poll 就已经跟上了这条日志。
    /// 这一行会被第一次 poll 收走，所以累计计数从 1 起。
    /// </remarks>
    private (ReviewLogViewModel Vm, ReviewProgressLog Log, int Seeded) Make()
    {
        var self = TestElectors.Make("alan");
        var log = new ReviewProgressLog();
        log.Append(Rev, "拉代码进临时工作区");

        var vm = new ReviewLogViewModel(
            new FakeMesh(self), log, _home.Archive, Rev, () => self.Id, "edison-test!2878");
        return (vm, log, 1);
    }

    [Fact]
    public void Opening_the_panel_before_the_local_review_starts_keeps_waiting_for_it()
    {
        // 本机的内存缓冲还没建：Read 给的是空块、Running=false。早先这就被当成「评完了」，
        // 定时器永久停掉，之后只能手点「拉取日志」才看得到新行。
        var self = TestElectors.Make("alan");
        var log = new ReviewProgressLog();
        var vm = new ReviewLogViewModel(
            new FakeMesh(self), log, _home.Archive, Rev, () => self.Id, "edison-test!2878");

        Assert.Empty(vm.Lines);
        Assert.DoesNotContain("已结束", vm.Status, StringComparison.Ordinal);

        log.Begin(Rev, "评审 edison-test#2878 round=0");
        log.Append(Rev, "拉取工作区");
        Poll(vm);

        Assert.Equal(2, vm.Lines.Count);
        Assert.Contains("正在评", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_that_did_not_know_the_reviewer_yet_switches_to_live_once_it_does()
    {
        // 飞书「开始评审」的深链常常比表格先到：打开时那一行的评审者还是空的，
        // 面板只能先摆出本机留档。等表格跟上，它得自己切到实时，而不是冻在那份快照上。
        var self = TestElectors.Make("alan");
        string? reviewer = null;
        _home.Archive.Append(Rev, "上一轮留下的一行");

        var log = new ReviewProgressLog();
        var vm = new ReviewLogViewModel(
            new FakeMesh(self), log, _home.Archive, Rev, () => reviewer, "edison-test!2878");

        Assert.Contains(vm.Lines, l => l.Contains("上一轮留下的一行", StringComparison.Ordinal));
        Assert.Equal("—", vm.Reviewer);

        log.Begin(Rev, "评审开始");
        reviewer = self.Id;
        Poll(vm);

        Assert.Equal("本节点", vm.Reviewer);
        Assert.Contains("正在评", vm.Status, StringComparison.Ordinal);
        Assert.Contains(vm.Lines, l => l.Contains("评审开始", StringComparison.Ordinal));

        // 留档快照要先清掉：真实场景里它和实时缓冲是同一批行，接着追加会重一遍。
        Assert.DoesNotContain(vm.Lines, l => l.Contains("上一轮留下的一行", StringComparison.Ordinal));
    }

    [Fact]
    public void A_remote_reviewer_that_has_no_log_yet_is_asked_again_instead_of_given_up_on()
    {
        var self = TestElectors.Make("alan");
        var peer = TestElectors.Make("peer-a");
        var mesh = new FakeMesh(self, [peer]);

        var vm = new ReviewLogViewModel(
            mesh, new ReviewProgressLog(), _home.Archive, Rev, () => peer.Id, "edison-test!2878");

        Assert.Empty(vm.Lines);

        mesh.PeerLogs[Rev] = new LogChunk(Rev, 0, 2, ["10:00:01 评审开始", "10:00:02 拉取工作区"], Running: true);
        Poll(vm);

        Assert.Equal(2, vm.Lines.Count);
        Assert.Contains("正在评", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_followed_review_that_ends_stops_the_polling()
    {
        // 停轮询的条件只剩这一个：真跟上过的那条日志自己报了收尾。
        var (vm, log, _) = Make();

        log.End(Rev, "评审结束");
        Poll(vm);
        Assert.Contains("已结束", vm.Status, StringComparison.Ordinal);

        log.Append(Rev, "收尾之后的一行");
        Poll(vm);

        Assert.DoesNotContain(vm.Lines, l => l.Contains("收尾之后的一行", StringComparison.Ordinal));
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

    /// <summary>没有节点在评这一版时的面板：实时那条路是空的，只能靠留档。</summary>
    private ReviewLogViewModel Orphan(FakeMesh mesh, string? reviewerId = null)
        => new(mesh, new ReviewProgressLog(), _home.Archive, Rev, () => reviewerId, "edison-test!2878");

    [Fact]
    public void A_review_that_is_over_falls_back_to_the_copy_on_disk()
    {
        // 「评审失败了/没跑完，我想看日志」= 没有节点在评这一版，实时接口给不出东西。
        // 本机跑过它的话盘上那份还在（留一天），面板要自己退到那条路上，
        // 而不是甩一句「没有节点在评这一版」。
        _home.Archive.Append(Rev, "round=1 跑到一半挂了");

        var vm = Orphan(new FakeMesh(TestElectors.Make("alan")));

        Assert.Contains(vm.Lines, l => l.Contains("跑到一半挂了", StringComparison.Ordinal));
        Assert.Contains(_home.Archive.LivePathOf(Rev), vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pulling_asks_every_other_node_when_nobody_admits_to_reviewing_it()
    {
        // 这条正是需求里最难的一半：评审早就结束了，实时状态里那一格是空的，
        // 于是<b>连「该问谁」都不知道</b> —— 只能挨个问。
        var self = TestElectors.Make("alan");
        var a = TestElectors.Make("peer-a");
        var b = TestElectors.Make("peer-b");
        var mesh = new FakeMesh(self, [a, b]);
        mesh.PeerFullLogs[b.Id + "|" + Rev] = "10:00:01 round=1\n10:14:47 claude 退出码 1";

        var vm = Orphan(mesh);
        await vm.PullCommand.ExecuteAsync(null);

        Assert.Contains(a.Id + "|" + Rev, mesh.FullLogAsks);
        Assert.Contains(b.Id + "|" + Rev, mesh.FullLogAsks);

        // 拉回来的要存本地（下次不用再问），并且当场显示出来。
        Assert.Contains("claude 退出码 1", _home.Archive.Read(Rev)!.Text, StringComparison.Ordinal);
        Assert.Contains(vm.Lines, l => l.Contains("claude 退出码 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pulling_asks_only_the_reviewer_when_it_is_known()
    {
        var self = TestElectors.Make("alan");
        var a = TestElectors.Make("peer-a");
        var b = TestElectors.Make("peer-b");
        var mesh = new FakeMesh(self, [a, b]);
        mesh.PeerFullLogs[b.Id + "|" + Rev] = "b 的日志";

        var vm = Orphan(mesh, reviewerId: b.Id);
        await vm.PullCommand.ExecuteAsync(null);

        Assert.Equal([b.Id + "|" + Rev], mesh.FullLogAsks);
    }

    [Fact]
    public async Task When_no_node_has_it_the_panel_says_so_instead_of_looking_broken()
    {
        var self = TestElectors.Make("alan");
        var mesh = new FakeMesh(self, [TestElectors.Make("peer-a")]);

        var vm = Orphan(mesh);
        await vm.PullCommand.ExecuteAsync(null);

        Assert.Contains("没有这一版的日志", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pulling_does_not_yank_the_view_out_from_under_a_running_review()
    {
        // 还在实时跟着的时候，拉取只存盘 —— 拿一份快照盖掉滚动中的日志，
        // 看起来就像评审停了。
        var (vm, log, _) = Make();
        Fill(log, 0, 5);
        Poll(vm);

        await vm.PullCommand.ExecuteAsync(null);

        Assert.Contains(vm.Lines, l => l.EndsWith("行 4", StringComparison.Ordinal));
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

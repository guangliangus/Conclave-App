using Conclave.Application;

namespace Conclave.UnitTests;

/// <summary>
/// 评审日志的落盘、保留与拉取。
/// </summary>
/// <remarks>
/// <para>
/// 这一层存在的理由是「人想翻日志的时刻，内存里那份往往已经没了」：评审失败了要看为什么，
/// 节点崩了要看跑到哪一步，而 <see cref="ReviewProgressLog"/> 是个只留几百行、
/// 进程一退就没的环形缓冲。所以要钉的不是「写得对不对」，而是几条<b>会静默失效</b>的性质：
/// 重试不能把上一轮的日志擦掉、过期的要真的被删、拉回来的不能盖掉本机那份、
/// revision id 不能把文件写到目录外面去。
/// </para>
/// <para>
/// 这些都得真的碰磁盘（<see cref="TempHome"/> 给每个用例一个独立目录）—— 它们要验的
/// 恰恰是文件在不在。
/// </para>
/// </remarks>
public sealed class ReviewLogArchiveTests : IDisposable
{
    private const string Rev = "2916@a1b2c3d";

    private readonly TempHome _home = new("archive");

    public void Dispose() => _home.Dispose();

    [Fact]
    public void Every_line_that_reaches_the_panel_also_lands_on_disk()
    {
        var log = new ReviewProgressLog(_home.Archive);

        log.Begin(Rev, "评审 liontrip-cms#2916 round=1");
        log.Append(Rev, "▸ Bash: go vet ./...");
        log.End(Rev, "完成：Approve · 0 条问题");

        var text = File.ReadAllText(_home.Archive.LivePathOf(Rev));

        Assert.Contains("评审 liontrip-cms#2916 round=1", text, StringComparison.Ordinal);
        Assert.Contains("▸ Bash: go vet ./...", text, StringComparison.Ordinal);
        Assert.Contains("完成：Approve", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_retry_starts_a_fresh_panel_but_keeps_the_failed_attempt_on_disk()
    {
        var log = new ReviewProgressLog(_home.Archive);

        log.Begin(Rev, "round=1");
        log.Append(Rev, "第一轮这里挂了");
        log.Begin(Rev, "round=2");
        log.Append(Rev, "第二轮");

        // 内存里只剩这一轮 —— 实时面板要看的是现在在跑的那次。
        var chunk = log.Read(Rev, 0);
        Assert.DoesNotContain(chunk.Lines, l => l.Contains("第一轮", StringComparison.Ordinal));

        // 盘上两轮都在。「上一轮为什么失败」正是最常要翻的东西，
        // 要是跟内存一样被 Begin 清掉，这条需求就等于没做。
        var text = File.ReadAllText(_home.Archive.LivePathOf(Rev));
        Assert.Contains("第一轮这里挂了", text, StringComparison.Ordinal);
        Assert.Contains("第二轮", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unfinished_review_is_still_readable_line_by_line()
    {
        // 节点崩了、进程被砍了 —— 没有 End，也没有最后那张票。
        // 每一行都是当场落盘的，所以已经跑过的那部分一行不少。
        var log = new ReviewProgressLog(_home.Archive);
        log.Begin(Rev, "round=1");
        log.Append(Rev, "▸ Read: internal/api/handler.go");

        var archived = _home.Archive.Read(Rev);

        Assert.NotNull(archived);
        Assert.Contains("internal/api/handler.go", archived.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Logs_past_the_retention_are_swept_and_fresh_ones_are_not()
    {
        _home.Archive.Append(Rev, "旧的");
        _home.Archive.Append("3000@ffffffff", "新的");

        var stale = _home.Archive.LivePathOf(Rev);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(2));

        _home.Archive.Sweep();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(_home.Archive.LivePathOf("3000@ffffffff")));
    }

    /// <summary>
    /// 按最后写入时间判，不是创建时间。
    /// </summary>
    /// <remarks>
    /// 一次评审能跑 45 分钟，而保留期跨过去的那一刻要是按创建时间算，正在写的那份会被删掉。
    /// <para>
    /// 两个时间都显式设：Linux 上 <c>File.SetCreationTimeUtc</c> 会<b>连带改掉最后
    /// 写入时间</b>（Unix 没有设 birth time 的系统调用，.NET 退回去动 mtime），只设创建时间
    /// 的话这条测试在 Linux 上测的就不再是它想测的东西 —— 文件真的变成三天前写的，被正常
    /// 清掉，然后断言失败。macOS 的 APFS 有独立 birthtime，所以本地看不出来，CI 上才炸。
    /// 顺序也不能反。
    /// </para>
    /// </remarks>
    [Fact]
    public void A_long_running_review_is_not_swept_out_from_under_itself()
    {
        _home.Archive.Append(Rev, "开始");

        var path = _home.Archive.LivePathOf(Rev);
        File.SetCreationTimeUtc(path, DateTime.UtcNow - TimeSpan.FromDays(3));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

        _home.Archive.Sweep();

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Retention_zero_turns_the_sweep_off_rather_than_deleting_everything()
    {
        _home.Options.LogRetention = TimeSpan.Zero;
        _home.Archive.Append(Rev, "一行");
        File.SetLastWriteTimeUtc(_home.Archive.LivePathOf(Rev), DateTime.UtcNow - TimeSpan.FromDays(9));

        _home.Archive.Sweep();

        Assert.True(File.Exists(_home.Archive.LivePathOf(Rev)));
    }

    [Fact]
    public void Re_pulling_from_the_same_peer_overwrites_instead_of_piling_up()
    {
        var first = _home.Archive.SavePulled(Rev, "peer-aaaaaaaa-long", "旧快照");
        var second = _home.Archive.SavePulled(Rev, "peer-aaaaaaaa-long", "新快照");

        Assert.Equal(first, second);
        Assert.Single(Directory.EnumerateFiles(_home.Archive.Root));
        Assert.Equal("新快照", File.ReadAllText(second!));
    }

    [Fact]
    public void A_pulled_copy_never_overwrites_what_this_node_wrote_itself()
    {
        // 本机那份是一行行落下来的，对端给的是它当时的快照 —— 两者不是同一个东西，
        // 拉一次就把自己那份盖掉是纯粹的损失。
        _home.Archive.Append(Rev, "本机逐行写的");
        _ = _home.Archive.SavePulled(Rev, "peer-bbbb", "对端给的快照");

        var archived = _home.Archive.Read(Rev);

        Assert.NotNull(archived);
        Assert.Equal(_home.Archive.LivePathOf(Rev), archived.Path);
        Assert.Contains("本机逐行写的", archived.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pulled_copy_is_what_comes_back_when_this_node_never_ran_it()
    {
        _ = _home.Archive.SavePulled(Rev, "peer-bbbb", "对端给的快照");

        var archived = _home.Archive.Read(Rev);

        Assert.NotNull(archived);
        Assert.Equal("对端给的快照", archived.Text);
    }

    [Fact]
    public void A_revision_id_can_never_write_outside_the_log_directory()
    {
        // id 现在是 2916@a1b2c3d 这种形状，本来就安全；钉的是日后它的构成变了的时候
        // 不至于变成一个路径穿越 —— 它是直接拼在目录后面的。
        _home.Archive.Append("../../etc/passwd", "不该落在外面");

        var files = Directory.EnumerateFiles(_home.Archive.Root).ToList();

        Assert.Single(files);
        Assert.Equal(
            Path.GetFullPath(_home.Archive.Root),
            Path.GetFullPath(Path.GetDirectoryName(files[0])!));
    }

    [Fact]
    public void Without_an_archive_everything_still_works_in_memory()
    {
        // 单测默认走这条（不碰磁盘），所以它不能是个半残的模式。
        var log = new ReviewProgressLog();
        log.Begin(Rev, "开始");
        log.Append(Rev, "一行");

        Assert.False(log.Has("不存在的版本"));
        Assert.Contains("一行", log.ReadAll(Rev), StringComparison.Ordinal);
    }

    [Fact]
    public void The_full_read_prefers_disk_because_memory_has_already_dropped_the_start()
    {
        // 内存是 MaxLines 的环形缓冲，一次长评审的开头早被挤掉了 —— 而事后要看的
        // 恰恰常是开头（拉代码、起会话那几步）。盘上那份没有上限。
        var log = new ReviewProgressLog(_home.Archive);
        log.Begin(Rev, "开头这一行");

        for (var i = 0; i < ReviewProgressLog.MaxLines + 50; i++)
        {
            log.Append(Rev, "行 " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.DoesNotContain(log.Read(Rev, 0).Lines, l => l.Contains("开头这一行", StringComparison.Ordinal));
        Assert.Contains("开头这一行", log.ReadAll(Rev), StringComparison.Ordinal);
    }
}

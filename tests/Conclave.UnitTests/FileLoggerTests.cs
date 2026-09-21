using Conclave.Application;
using Conclave.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Conclave.UnitTests;

/// <summary>
/// 进程日志落盘。装成 .app 之后控制台那份是写给 /dev/null 的，这份是唯一还能翻的。
/// </summary>
public sealed class FileLoggerTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "conclave-flog-" + Guid.NewGuid().ToString("N")[..8]);

    private ConclaveOptions Options(TimeSpan? retention = null)
        => new() { HomeDirectory = _home, LogRetention = retention ?? TimeSpan.FromDays(1) };

    [Fact]
    public void A_logged_line_lands_on_disk_with_its_message()
    {
        using var provider = new FileLoggerProvider(Options());
        provider.CreateLogger("Conclave.Test").LogError("拉 {Repo} 的临时工作区超时", "PIM");

        var file = Directory.EnumerateFiles(provider.Root, "conclave-*.log").Single();
        var text = File.ReadAllText(file);

        Assert.Contains("拉 PIM 的临时工作区超时", text, StringComparison.Ordinal);
        Assert.Contains("fail", text, StringComparison.Ordinal);
        Assert.Contains("Conclave.Test", text, StringComparison.Ordinal);
    }

    /// <summary>异常要带堆栈，否则「失败了」和「为什么失败」还是差一截。</summary>
    [Fact]
    public void The_exception_is_written_out_not_just_the_message()
    {
        using var provider = new FileLoggerProvider(Options());
        provider.CreateLogger("X").LogError(
            new InvalidOperationException("没有共同祖先"), "拉工作区失败");

        var text = File.ReadAllText(
            Directory.EnumerateFiles(provider.Root, "conclave-*.log").Single());

        Assert.Contains("拉工作区失败", text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Contains("没有共同祖先", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 必须写在 logs/app/ 子目录里。
    /// </summary>
    /// <remarks>
    /// ReviewLogArchive.Sweep 清的是 EnumerateFiles(LogDirectory, "*.log")，不递归。
    /// 平铺在 logs/ 下的话，它会连正在写的这份一起删掉。
    /// </remarks>
    [Fact]
    public void Process_logs_stay_out_of_the_directory_the_review_archive_sweeps()
    {
        using var provider = new FileLoggerProvider(Options());
        provider.CreateLogger("X").LogInformation("hi");

        Assert.Equal(Path.Combine(_home, "logs", "app"), provider.Root);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_home, "logs"), "*.log"));
    }

    [Fact]
    public void Logs_past_the_retention_window_are_swept_at_startup()
    {
        var options = Options(TimeSpan.FromHours(1));
        var root = Path.Combine(_home, "logs", "app");
        _ = Directory.CreateDirectory(root);

        var stale = Path.Combine(root, "conclave-2020-01-01.log");
        File.WriteAllText(stale, "上一版留下的");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(2));

        var fresh = Path.Combine(root, "conclave-2020-01-02.log");
        File.WriteAllText(fresh, "刚写的");

        using var provider = new FileLoggerProvider(options);

        Assert.False(File.Exists(stale), "过了保留期的应该被清掉");
        Assert.True(File.Exists(fresh), "还在保留期内的不能动");
    }

    /// <summary>写不下去也不能把进程带崩 —— 日志是诊断手段，不是业务。</summary>
    [Fact]
    public void A_broken_log_directory_does_not_take_the_process_down()
    {
        var options = Options();
        // 把 logs/app 这个位置占成文件，CreateDirectory 必然失败。
        _ = Directory.CreateDirectory(Path.Combine(_home, "logs"));
        File.WriteAllText(Path.Combine(_home, "logs", "app"), "占位");

        using var provider = new FileLoggerProvider(options);

        provider.CreateLogger("X").LogError("这行写不进去，但不该抛");
    }

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }
}

using Conclave.Infrastructure;

namespace Conclave.UnitTests;

/// <summary>
/// 子进程的输出回调。
/// </summary>
/// <remarks>
/// 评审日志面板的整个前提是「行是<b>边跑边</b>出来的」。如果回调其实是在进程退出后
/// 一次性回放，那面板就只是个「评完再看」的东西 —— 而它要解决的正是「评审中的十几分钟
/// 里看不见任何东西」。所以这里跑真的子进程，并且断言第一行在进程还活着的时候就到了。
/// </remarks>
public sealed class ProcessRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "conclave-proc-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Output_lines_arrive_while_the_process_is_still_running()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new List<string>();

        // 先吐一行，再睡 1 秒，最后再吐一行。缓冲式收集的话第一行要等 1 秒后才到。
        var script = Write("#!/bin/sh\necho 第一行\nsleep 1\necho 第二行\n");

        var run = ProcessRunner.RunAsync(
            script, [], ct: CancellationToken.None,
            onOutputLine: line =>
            {
                lock (lines)
                {
                    lines.Add(line);
                }

                first.TrySetResult();
            });

        // 进程还在 sleep 里，第一行就该已经到了。3 秒的余量是给 CI 上的慢机器。
        var arrived = await Task.WhenAny(first.Task, Task.Delay(TimeSpan.FromSeconds(3)))
            .ConfigureAwait(true);
        Assert.Same(first.Task, arrived);
        Assert.False(run.IsCompleted, "第一行到达时子进程不应该已经退出");

        var result = await run.ConfigureAwait(true);

        Assert.True(result.Success);
        lock (lines)
        {
            Assert.Equal(["第一行", "第二行"], lines);
        }

        // 回调之外，完整输出照样收着 —— 出票解析用的是它。
        Assert.Contains("第二行", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Standard_error_goes_to_its_own_callback()
    {
        var errors = new List<string>();
        var script = Write("#!/bin/sh\necho 出错了 >&2\nexit 3\n");

        var result = await ProcessRunner.RunAsync(
            script, [], ct: CancellationToken.None,
            onErrorLine: errors.Add).ConfigureAwait(true);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(["出错了"], errors);
    }

    [Fact]
    public async Task A_throwing_callback_does_not_break_the_run()
    {
        var script = Write("#!/bin/sh\necho a\necho b\n");

        // 日志面板的问题不该让评审失败：回调抛出来会在 OutputDataReceived 的线程上冒，
        // 那条路上没人接，结果是丢输出甚至整个评审挂掉。
        var result = await ProcessRunner.RunAsync(
            script, [], ct: CancellationToken.None,
            onOutputLine: _ => throw new InvalidOperationException("面板炸了"))
            .ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Contains("b", result.StdOut, StringComparison.Ordinal);
    }

    private string Write(string body)
    {
        _ = Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "script-" + Guid.NewGuid().ToString("N")[..6] + ".sh");
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // 临时目录清不掉不影响测试结论。
        }
    }
}

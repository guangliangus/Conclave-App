using System.Diagnostics;
using System.Text;

namespace Conclave.Infrastructure;

/// <summary>子进程的执行结果。</summary>
/// <param name="ExitCode">退出码。</param>
/// <param name="StdOut">标准输出。</param>
/// <param name="StdErr">标准错误。</param>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// 起子进程并收集输出。
/// </summary>
/// <remarks>
/// 用 <see cref="ProcessStartInfo.ArgumentList"/> 而不是拼 <c>Arguments</c> 字符串 ——
/// PR 标题里带引号或空格的情况很常见，拼字符串会引号逃逸出错。
/// </remarks>
public static class ProcessRunner
{
    /// <param name="executable">要跑的可执行文件（已解析成绝对路径）。</param>
    /// <param name="arguments">参数，逐个传给 <see cref="ProcessStartInfo.ArgumentList"/>。</param>
    /// <param name="workingDirectory">工作目录；null 表示继承当前进程的。</param>
    /// <param name="environment">追加/覆盖的环境变量。</param>
    /// <param name="ct">取消时子进程会被杀掉。</param>
    /// <param name="onOutputLine">
    /// 每读到一行标准输出就回调一次，用来把进度实时递给别处（评审日志面板）。
    /// <para>
    /// 回调跑在 <see cref="Process.OutputDataReceived"/> 的线程上，所以实现必须
    /// 又快又不抛异常 —— 它抛出来会把这一行的读取整个吃掉。
    /// </para>
    /// </param>
    /// <param name="onErrorLine">同上，标准错误。</param>
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            _ = stdout.AppendLine(e.Data);
            Tee(onOutputLine, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            _ = stderr.AppendLine(e.Data);
            Tee(onErrorLine, e.Data);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动 {executable}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// 把一行递给回调，吞掉它抛出的任何异常。
    /// </summary>
    /// <remarks>
    /// 这个回调只为「让人看见进度」而存在。它抛异常时，异常会在
    /// <see cref="Process.OutputDataReceived"/> 的线程上冒出来 —— 那条路上没有人接，
    /// 结果是丢掉后续输出，甚至整个评审失败。日志面板的问题不该有这个后果。
    /// </remarks>
    private static void Tee(Action<string>? sink, string line)
    {
        if (sink is null)
        {
            return;
        }

        try
        {
            sink(line);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("评审日志回调抛异常：" + ex);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception)
        {
            // 进程已经自己退了 —— 取消路径上没什么可做的。
        }
    }
}

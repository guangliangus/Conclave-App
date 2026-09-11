using System.Diagnostics;
using System.Text;

namespace Conclave.Infrastructure;

/// <summary>子进程的执行结果。</summary>
/// <param name="ExitCode">退出码。</param>
/// <param name="StdOut">标准输出（可能已按 <see cref="ProcessRunner.MaxCapturedChars"/> 截断，见 <see cref="Truncated"/>）。</param>
/// <param name="StdErr">标准错误，同上。</param>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    /// <summary>
    /// 输出超过上限，<b>开头</b>那部分已经被丢掉。
    /// </summary>
    /// <remarks>
    /// 调用方要拿整份输出当 JSON 解析的（<c>az</c>）必须查这一位：截断过的前缀不是合法
    /// JSON，直接喂给解析器只会得到一句不知所云的语法错误，而真正的原因是「输出太大」。
    /// 流式输出（<c>claude --output-format stream-json</c>）不受影响 —— 要的那一行在末尾。
    /// </remarks>
    public bool Truncated { get; init; }
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
    /// <summary>
    /// 单个流最多留多少个字符，超了从<b>开头</b>丢。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 原先是一个没有上限的 <see cref="StringBuilder"/>。<c>az</c> 和 <c>git</c> 的输出
    /// 天然有界，所以一直没出事；而 <c>claude --output-format stream-json --verbose</c>
    /// 把<b>每一个工具返回体</b>（读进去的文件全文）都打出来，一次十五分钟的评审能吐出
    /// 几百 MB —— 全攒在内存里，退出时再 <c>ToString()</c> 一份，等于原始字节数的四倍
    /// UTF-16。上限不是优化，是让「话痨的一次评审」不能单独把进程打爆。
    /// </para>
    /// <para>
    /// 丢开头而不是丢结尾：需要整份输出的调用方（az）本来就不该超，超了会由
    /// <see cref="ProcessResult.Truncated"/> 报出来；而会超的那个调用方（claude）要的
    /// <c>type=result</c> 那一行恰好在末尾。
    /// </para>
    /// <para>
    /// 16M 字符 ≈ 16MB 纯 ASCII 输出，比 <c>az</c> 见过的最大一次（整个 collection 的
    /// 活跃 PR 列表，几百 KB）高两个数量级。
    /// </para>
    /// </remarks>
    public const int MaxCapturedChars = 16 * 1024 * 1024;

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
        var stdout = new BoundedCapture();
        var stderr = new BoundedCapture();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stdout.Add(e.Data);
            Tee(onOutputLine, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stderr.Add(e.Data);
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

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString())
        {
            Truncated = stdout.Truncated || stderr.Truncated,
        };
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

    /// <summary>
    /// 一个流的有界收集：按行留，超过 <see cref="MaxCapturedChars"/> 就从头挤掉。
    /// </summary>
    /// <remarks>
    /// 用行队列而不是 <see cref="StringBuilder"/> + 截断：在 StringBuilder 前面删字符
    /// 每次都是 O(n) 的搬运，而挤掉一整行是 O(1) 摊还。
    /// <para>
    /// 写入发生在 <see cref="Process.OutputDataReceived"/> 的线程上，读取发生在
    /// <c>WaitForExitAsync</c> 返回之后的调用线程上 —— 两者理论上可以重叠（进程已退出但
    /// 最后一批行还在派发），所以加锁。
    /// </para>
    /// </remarks>
    private sealed class BoundedCapture
    {
        private readonly Queue<string> _lines = new();
        private readonly Lock _gate = new();
        private long _chars;

        internal bool Truncated { get; private set; }

        internal void Add(string line)
        {
            lock (_gate)
            {
                _lines.Enqueue(line);
                _chars += line.Length + 1;

                // 留最后一行 —— 单行就超上限时至少还能看见它的尾巴。
                while (_chars > MaxCapturedChars && _lines.Count > 1)
                {
                    _chars -= _lines.Dequeue().Length + 1;
                    Truncated = true;
                }
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return string.Join('\n', _lines);
            }
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

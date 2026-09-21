using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Conclave.Application;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure;

/// <summary>
/// 把 <see cref="ILogger"/> 的输出按天写进 <c>~/.conclave/logs/app/</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么非要有这个。</b> 只配 <c>AddSimpleConsole</c> 的话，装成 <c>.app</c> 之后
/// 全部日志原地蒸发 —— LaunchServices 起的进程 stdout/stderr 直接接 <c>/dev/null</c>
/// （<c>lsof</c> 实测 fd1/fd2 都是 <c>/dev/null</c>），统一日志里也一条都没有。
/// 于是代码里那些写得很细的诊断（找不到可执行文件时的指路、飞书 99992402 的成因、
/// 「分支在深度 N 内没有共同祖先，加深到 M」）在生产上一个字都看不到。
/// PIM#3261 查超时那次就卡在这儿：判定只能靠链上时间戳倒推。
/// </para>
/// <para>
/// <b>刻意写进子目录 <c>logs/app/</c>。</b> <c>ReviewLogArchive.Sweep</c> 清的是
/// <c>EnumerateFiles(Root, "*.log")</c>，不递归 —— 平铺在 <c>logs/</c> 下会被它连
/// 正在写的那份一起删掉。子目录既躲开它，也让「评审日志」和「进程日志」在肉眼上分得开。
/// 过期清理由本类自己做，用的是同一个 <see cref="ConclaveOptions.LogRetention"/>。
/// </para>
/// <para>
/// <b>永不抛。</b> 日志写不下去是小事，让它把进程带崩是大事 —— 所有 IO 都吞异常。
/// </para>
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly ConclaveOptions _options;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private StreamWriter? _writer;
    private DateOnly _writerDay;

    public FileLoggerProvider(ConclaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        Sweep();
    }

    /// <summary>进程日志的目录。</summary>
    public string Root => Path.Combine(_options.LogDirectory, "app");

    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>
    /// 写一行。
    /// </summary>
    /// <remarks>
    /// 整个写入串行化并且 <c>AutoFlush</c>：这份日志存在的意义就是进程出事后还能翻，
    /// 攒在缓冲区里等崩溃时一起丢掉的话就白做了。量级是一天几千行，不值得为吞吐冒这个险。
    /// </remarks>
    internal void Write(string line)
    {
        try
        {
            lock (_gate)
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (_writer is null || _writerDay != today)
                {
                    _writer?.Dispose();
                    _ = Directory.CreateDirectory(Root);
                    _writer = new StreamWriter(
                        Path.Combine(
                            Root,
                            "conclave-" + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log"),
                        append: true,
                        // 不要 Encoding.UTF8：那个会在文件开头写 BOM，grep/head 读第一行
                        // 时会多出一个看不见的字符。日志是拿命令行翻的，别给它添乱。
                        NoBom)
                    {
                        AutoFlush = true,
                    };
                    _writerDay = today;
                }

                _writer.WriteLine(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 磁盘满、目录被删、权限变了 —— 都不该让一条日志把进程带走。
        }
    }

    /// <summary>删掉过了保留期的进程日志。</summary>
    /// <remarks>
    /// 按最后写入时间而不是创建时间：跨天那一刻昨天那份可能还在被写，按创建时间会删错。
    /// 这条跟 <c>ReviewLogArchive.Sweep</c> 的理由一致。
    /// </remarks>
    private void Sweep()
    {
        if (_options.LogRetention <= TimeSpan.Zero || !Directory.Exists(Root))
        {
            return;
        }

        var deadline = DateTime.UtcNow - _options.LogRetention;

        try
        {
            foreach (var path in Directory.EnumerateFiles(Root, "conclave-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < deadline)
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 正开着之类，下次启动再说。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>一个类别的写入口。格式对齐 <c>AddSimpleConsole</c>，肉眼读得惯。</summary>
internal sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (!IsEnabled(logLevel))
        {
            return;
        }

        var text = new StringBuilder()
            .Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(Level(logLevel))
            .Append(' ')
            .Append(category)
            .Append(" | ")
            .Append(formatter(state, exception));

        if (exception is not null)
        {
            _ = text.Append('\n').Append(exception);
        }

        provider.Write(text.ToString());
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none",
    };
}

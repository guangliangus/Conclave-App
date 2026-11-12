using Conclave.Application;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure;

/// <summary>
/// 给评审子进程（<c>claude -p</c>）拼一份完整的 PATH，不再原样继承 app 自己那份。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有这个。</b> 从 Finder、Dock、登录项启动 <c>.app</c> 时 PATH 只有
/// <c>/usr/bin:/bin:/usr/sbin:/sbin</c>；自动更新走 <c>open -n</c> 重启，又会把这份原样传给新版
/// （实测 <c>open</c> 会带上调用方的环境），所以这样起来的节点<b>一直</b>是最小 PATH。
/// Conclave 自己找 az / claude 有 <see cref="ExecutableResolver"/> 兜底，但 claude 子进程的
/// Bash 工具用的就是继承来的 PATH（<c>--restricted</c> 下它不读 shell 配置）——
/// <c>az_pr.sh</c> 里的 <c>az</c>、跑检查要的 <c>go</c> / <c>node</c> / <c>npm</c> 全找不到。
/// 实测每一场评审都靠 Claude 自己猜 <c>export PATH=/opt/homebrew/bin:…</c> 才跑得下去：
/// 多花几轮、拿不到 PR 标题和描述，猜不中（nvm 装的 node）就只能以「检查跑不了」出票。
/// </para>
/// <para>
/// <b>顺序</b>：配置的 <c>ExtraToolPaths</c> → Conclave 自己用的 az 所在目录 → 登录 shell 的 PATH →
/// 当前进程的 PATH 与解析器的兜底目录。配置压过一切；az 紧跟其后，保证脚本调的跟 Conclave
/// 发评论用的（登录身份在它那里）是同一份；登录 shell 那份是用户自己排好的顺序，原样保留。
/// </para>
/// <para>
/// <b>登录 shell 用 <c>-i -l</c>。</b>只用 <c>-l</c> 拿不到写在 <c>.zshrc</c> 里的目录
/// （<c>~/.local/bin</c>、<c>~/go/bin</c>、nvm），而大多数人的 PATH 就写在那里。交互式 rc
/// 可能往输出里打东西，所以结果用标记夹住再取；也可能慢或挂住，所以有超时。读不到就只用
/// 其余几项，评审照常开始。
/// </para>
/// <para>
/// <b>相对路径一律丢掉。</b>子进程的工作目录是 PR 的代码，PATH 里留一个 <c>.</c> 或
/// <c>node_modules/.bin</c>，PR 里放一个叫 <c>git</c> 的文件就会被当成 git 执行。
/// </para>
/// <para>
/// 只探一次、整个进程共用：PATH 极少变，没必要每场评审起一次 shell。改了 shell 配置重启 Conclave 即可。
/// </para>
/// </remarks>
public sealed class ChildProcessPath(
    ConclaveOptions options,
    ExecutableResolver executables,
    ILogger<ChildProcessPath> logger)
{
    internal const string BeginMarker = "__CONCLAVE_PATH_BEGIN__";
    internal const string EndMarker = "__CONCLAVE_PATH_END__";

    private readonly Lock _gate = new();
    private Task<IReadOnlyList<string>>? _loginShell;

    /// <summary>用哪个 shell 探 PATH；<c>null</c> 表示不探。默认取 <c>$SHELL</c>。</summary>
    internal string? Shell { get; init; } = DefaultShell(Environment.GetEnvironmentVariable("SHELL"));

    /// <summary>登录 shell 多久不返回就放弃。交互式 rc 里有 nvm 之类的也就一两秒。</summary>
    internal TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 评审子进程该用的 PATH。
    /// </summary>
    /// <param name="ct">只取消等待；探测本身有自己的超时，不会因为某场评审被取消而作废。</param>
    public async Task<string> GetAsync(CancellationToken ct)
    {
        var login = await LoginShellDirectories().WaitAsync(ct).ConfigureAwait(false);

        var azDirectory = executables.TryResolve(options.AzExecutable) is { } az
            ? Path.GetDirectoryName(az)
            : null;

        return Compose([
            .. options.ExtraToolPaths,
            azDirectory ?? string.Empty,
            .. login,
            .. executables.SearchDirectories(),
        ]);
    }

    /// <summary>
    /// 按顺序去重拼成 PATH：展开 <c>~</c>、去掉结尾的 <c>/</c>、丢掉空项和相对路径。
    /// </summary>
    internal static string Compose(IEnumerable<string> directories)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();

        foreach (var raw in directories)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var dir = ConclaveOptions.ExpandHome(raw.Trim());
            if (!Path.IsPathRooted(dir))
            {
                continue;
            }

            if (dir.Length > 1)
            {
                dir = dir.TrimEnd('/');
            }

            if (seen.Add(dir))
            {
                kept.Add(dir);
            }
        }

        return string.Join(Path.PathSeparator, kept);
    }

    /// <summary>
    /// 从登录 shell 的输出里取出标记之间的 PATH。取最后一对，rc 里打印的东西不影响。
    /// </summary>
    internal static IReadOnlyList<string> ParseProbe(string output)
    {
        var begin = output.LastIndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0)
        {
            return [];
        }

        var start = begin + BeginMarker.Length;
        var end = output.IndexOf(EndMarker, start, StringComparison.Ordinal);
        if (end < 0)
        {
            return [];
        }

        return output[start..end].Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// <c>$SHELL</c> 能用就用它；没有（某些启动方式不带）就退到系统自带的 zsh、bash。
    /// </summary>
    internal static string? DefaultShell(string? shellVariable)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(shellVariable)
            && Path.IsPathRooted(shellVariable)
            && File.Exists(shellVariable))
        {
            return shellVariable;
        }

        return ((string[])["/bin/zsh", "/bin/bash"]).FirstOrDefault(File.Exists);
    }

    private Task<IReadOnlyList<string>> LoginShellDirectories()
    {
        lock (_gate)
        {
            return _loginShell ??= ProbeAsync();
        }
    }

    private async Task<IReadOnlyList<string>> ProbeAsync()
    {
        if (Shell is not { } shell)
        {
            logger.LogWarning("找不到可用的 shell，评审子进程的 PATH 只用配置和兜底目录");
            return [];
        }

        using var timeout = new CancellationTokenSource(ProbeTimeout);
        try
        {
            var result = await ProcessRunner.RunAsync(
                shell,
                ["-i", "-l", "-c", $"printf '%s%s%s' '{BeginMarker}' \"$PATH\" '{EndMarker}'"],
                ct: timeout.Token).ConfigureAwait(false);

            var directories = ParseProbe(result.StdOut);
            if (directories.Count == 0)
            {
                logger.LogWarning(
                    "从登录 shell {Shell} 读不出 PATH（退出码 {ExitCode}），评审子进程只用配置和兜底目录：{Stderr}",
                    shell, result.ExitCode, Truncate(result.StdErr, 500));
                return [];
            }

            logger.LogInformation(
                "评审子进程的 PATH 取自登录 shell {Shell}：{Path}",
                shell, string.Join(Path.PathSeparator, directories));
            return directories;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "登录 shell {Shell} 超过 {Timeout} 秒没返回，评审子进程的 PATH 只用配置和兜底目录",
                shell, ProbeTimeout.TotalSeconds);
            return [];
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException
            or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "起不来登录 shell {Shell}，评审子进程的 PATH 只用配置和兜底目录", shell);
            return [];
        }
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}

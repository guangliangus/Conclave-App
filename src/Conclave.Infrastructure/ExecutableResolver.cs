using System.Collections.Concurrent;
using Conclave.Application;

namespace Conclave.Infrastructure;

/// <summary>
/// 把 <c>az</c> / <c>claude</c> 这样的裸命令名解析成绝对路径。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不能直接依赖 PATH。</b> 从 Finder 或 <c>open</c> 启动 <c>.app</c> 时，
/// LaunchServices 只给一个最小 PATH（<c>/usr/bin:/bin:/usr/sbin:/sbin</c>），
/// 而 <c>az</c> 在 <c>/opt/homebrew/bin</c>、<c>claude</c> 在 <c>~/.local/bin</c>。
/// 于是同一个二进制在终端里一切正常，双击图标启动就变成「读不到 az 登录身份」
/// 和「0 个 project」—— 而报错还指向了错误的方向（让人去跑 az devops login）。
/// </para>
/// <para>
/// launchd 也是同一个坑，所以 LaunchAgent 的 plist 里显式写了 PATH。这里做的是
/// 进程内的兜底：无论怎么被启动都能找到工具。
/// </para>
/// </remarks>
public sealed class ExecutableResolver(ConclaveOptions options)
{
    /// <summary>
    /// PATH 之外还去这些目录找。
    /// </summary>
    /// <remarks>
    /// 覆盖 Homebrew（Apple Silicon 与 Intel 两种前缀）、pipx/uv 的用户目录、
    /// dotnet 全局工具、MacPorts。
    /// </remarks>
    private static readonly string[] FallbackDirectories =
    [
        "/opt/homebrew/bin",
        "/usr/local/bin",
        "~/.local/bin",
        "~/bin",
        "~/.dotnet/tools",
        "/opt/local/bin",
    ];

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// 解析一个命令名。已经是能用的绝对路径时原样返回。
    /// </summary>
    /// <exception cref="FileNotFoundException">找不到时抛出，消息里列出找过的目录。</exception>
    public string Resolve(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return _cache.GetOrAdd(command, Locate);
    }

    /// <summary>解析不到时返回 null，用于「有没有装」这种探测。</summary>
    public string? TryResolve(string command)
    {
        try
        {
            return Resolve(command);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private string Locate(string command)
    {
        // 配置里给了绝对路径就直接用 —— 这是最终的逃生出口。
        if (command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            var expanded = ConclaveOptions.ExpandHome(command);
            if (File.Exists(expanded))
            {
                return expanded;
            }

            throw new FileNotFoundException($"配置的可执行文件不存在：{expanded}", expanded);
        }

        var searched = new List<string>();

        foreach (var dir in Directories())
        {
            searched.Add(dir);
            var candidate = Path.Combine(dir, command);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"找不到可执行文件 {command}。找过：{string.Join(", ", searched)}。"
                + $"若装在别处，把绝对路径写进 ~/.conclave/appsettings.json 的 "
                + $"Conclave.{(command == "az" ? "AzExecutable" : "ClaudeExecutable")}，"
                + "或加到 Conclave.ExtraToolPaths。",
            command);
    }

    private IEnumerable<string> Directories()
    {
        // 顺序：配置 > PATH > 兜底目录。配置能压过一切，兜底只在 PATH 不含时起作用。
        foreach (var dir in options.ExtraToolPaths)
        {
            yield return ConclaveOptions.ExpandHome(dir);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return dir;
        }

        foreach (var dir in FallbackDirectories)
        {
            yield return ConclaveOptions.ExpandHome(dir);
        }
    }
}

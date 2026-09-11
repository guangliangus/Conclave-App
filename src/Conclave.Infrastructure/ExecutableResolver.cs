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
    /// <para>
    /// 覆盖 Homebrew（Apple Silicon 与 Intel 两种前缀）、pipx/uv 的用户目录、
    /// dotnet 全局工具、MacPorts。
    /// </para>
    /// <para>
    /// <b>用户目录排在系统目录前面</b>，这个顺序是踩出来的：claude 的官方安装器装在
    /// <c>~/.local/bin</c>，而 <c>/usr/local/bin</c> 里常常还躺着一份没卸干净的旧 npm 版
    /// （<c>npm i -g @anthropic-ai/claude-code</c>）。原先的顺序让后者赢，于是终端里
    /// <c>claude --version</c> 是新的、Conclave 起的却是旧的 —— 实测表现为评审直接
    /// 「API Error: 400 … does not support this model; version 2.1.251 or newer is required」，
    /// 而日志里完全看不出用的是哪个文件。
    /// </para>
    /// <para>
    /// 顺序只是缩小概率。真正的兜底是 <see cref="ResolveAll"/>：claude 那条路会把每个候选
    /// 都验一次版本再挑（见 <c>ClaudeCli</c>），不指望这张表排得对。
    /// </para>
    /// </remarks>
    private static readonly string[] FallbackDirectories =
    [
        "~/.local/bin",
        "~/bin",
        "/opt/homebrew/bin",
        "/usr/local/bin",
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

    /// <summary>
    /// 按搜索顺序列出<b>全部</b>候选，而不只是第一个。
    /// </summary>
    /// <remarks>
    /// 同一个命令在一台机器上有好几份是常态（npm 全局装一份、官方安装器又装一份），
    /// 而「排在前面」不等于「是对的那份」。要在多份之间做选择的调用方（<c>ClaudeCli</c>
    /// 会逐个验版本）需要看到全部，光有 <see cref="Resolve"/> 做不到。
    /// <para>
    /// 按真实路径去重：<c>~/.local/bin/claude</c> 这种符号链接和它的目标是同一个文件，
    /// 验两遍纯属浪费。返回的仍是找到它的那个路径，不是链接目标。
    /// </para>
    /// <para>
    /// 配置里给的是绝对路径时只会有一个候选 —— 配置就是最终答案，不参与挑选。
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> ResolveAll(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            var expanded = ConclaveOptions.ExpandHome(command);
            return File.Exists(expanded) ? [expanded] : [];
        }

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dir in Directories())
        {
            var candidate = Path.Combine(dir, command);
            if (File.Exists(candidate) && seen.Add(RealPath(candidate)))
            {
                found.Add(candidate);
            }
        }

        return found;
    }

    /// <summary>符号链接走到底，用于去重。走不动（不是链接、或者断了）就用原路径。</summary>
    private static string RealPath(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName
                ?? Path.GetFullPath(path);
        }
        catch (IOException)
        {
            return path;
        }
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

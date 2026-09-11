using System.Globalization;
using System.Text.RegularExpressions;
using Conclave.Application;
using Conclave.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure;

/// <summary>一份装在机器上的 claude：在哪，什么版本（验不出来就是 null）。</summary>
public sealed record ClaudeInstallation(string Path, Version? Version)
{
    /// <summary>写进日志和票据里的样子：<c>2.1.268 · /Users/x/.local/bin/claude</c>。</summary>
    public override string ToString()
        => (Version?.ToString() ?? "版本未知") + " · " + Path;
}

/// <summary>
/// 「用哪个 claude」的唯一答案。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不能只用 <see cref="ExecutableResolver.Resolve"/>。</b> 一台机器上有好几份
/// claude 是常态：官方安装器装在 <c>~/.local/bin</c>，而 <c>npm i -g</c> 的旧版本躺在
/// <c>/usr/local/bin</c> 里没人卸。搜索顺序只能决定「先看到哪个」，决定不了「哪个是对的」。
/// </para>
/// <para>
/// 真实事故：某个节点连着三次评审全挂在
/// <c>API Error: 400 … Claude Code 2.1.104 does not support this model;
/// version 2.1.251 or newer is required</c>。那台机器的人早就更新过了 ——
/// 他终端里的 <c>claude --version</c> 是新的，因为 shell 的 PATH 把 <c>~/.local/bin</c>
/// 排在前面；而 Conclave 是从 Finder 启动的 <c>.app</c>，LaunchServices 只给
/// <c>/usr/bin:/bin:/usr/sbin:/sbin</c>，于是解析落到兜底目录、挑中了那份旧的 npm 版。
/// </para>
/// <para>
/// 所以这里不赌顺序：把<b>全部</b>候选找出来，each 跑一次 <c>--version</c>，挑版本号最大的。
/// 一台机器上多出来的旧版本会被<b>记成警告</b>而不是默默忽略 —— 集群里有几十台机器时，
/// 「让每个人自己去收拾」不是个可行的方案，程序自己挑对、并且把现状说出来才是。
/// </para>
/// <para>
/// 配置里把 <c>Conclave.ClaudeExecutable</c> 写成绝对路径时不参与挑选：配置就是最终答案
/// （仍然会验一次版本，好让日志和票据里有这个数）。
/// </para>
/// </remarks>
public sealed partial class ClaudeCli(
    ConclaveOptions options,
    ExecutableResolver executables,
    ILogger<ClaudeCli> logger) : IClaudeCli
{
    /// <summary>
    /// 验一个候选的版本最多等多久。
    /// </summary>
    /// <remarks>
    /// <c>claude --version</c> 只是打一行字，但那个二进制有 200MB，冷启动（刚开机、
    /// 或者刚被杀毒扫过）慢得超乎想象。给够，反正整个探测半小时才做一次。
    /// </remarks>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 结果缓存多久。
    /// </summary>
    /// <remarks>
    /// 不能永久缓存：claude 会自己原地更新，而票据里记的版本号要是长期过期的，
    /// 排查时就会指向一台已经修好的机器。半小时够短，而每次探测也就起两三个进程。
    /// </remarks>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private ClaudeInstallation? _cached;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    /// <summary>挑一份 claude 出来用。并发调用只会有一个真的去探测。</summary>
    /// <exception cref="FileNotFoundException">一份都没找到。</exception>
    public async Task<ClaudeInstallation> ResolveAsync(CancellationToken ct = default)
    {
        if (_cached is { } fresh && DateTimeOffset.UtcNow - _cachedAt < CacheFor)
        {
            return fresh;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is { } stillFresh && DateTimeOffset.UtcNow - _cachedAt < CacheFor)
            {
                return stillFresh;
            }

            var chosen = await ProbeAsync(ct).ConfigureAwait(false);
            _cached = chosen;
            _cachedAt = DateTimeOffset.UtcNow;
            return chosen;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>
    /// 版本号，拿不到就是空串。
    /// </summary>
    /// <remarks>
    /// 心跳每轮调一次，摊给整个 mesh 看（见 <c>Elector.ClaudeVersion</c>）。
    /// 一份 claude 都没有<b>不是</b>这里该炸的事 —— 那台机器评不了审是真的，
    /// 但心跳本身还得照发，否则别人只会看到它「离线」，更难查。
    /// </remarks>
    public async Task<string> VersionAsync(CancellationToken ct)
    {
        try
        {
            return (await ResolveAsync(ct).ConfigureAwait(false)).Version?.ToString() ?? string.Empty;
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }
    }

    private async Task<ClaudeInstallation> ProbeAsync(CancellationToken ct)
    {
        var candidates = executables.ResolveAll(options.ClaudeExecutable);
        if (candidates.Count == 0)
        {
            // 消息跟 ExecutableResolver 那条对齐：告诉人往哪写路径。
            throw new FileNotFoundException(
                $"找不到可执行文件 {options.ClaudeExecutable}。"
                    + "若装在别处，把绝对路径写进 ~/.conclave/appsettings.json 的 "
                    + "Conclave.ClaudeExecutable，或加到 Conclave.ExtraToolPaths。",
                options.ClaudeExecutable);
        }

        var found = new List<ClaudeInstallation>(candidates.Count);
        foreach (var path in candidates)
        {
            found.Add(new ClaudeInstallation(path, await VersionOfAsync(path, ct).ConfigureAwait(false)));
        }

        var chosen = Choose(found);

        // 只有一份就不啰嗦，这是绝大多数机器的情况。
        if (found.Count == 1)
        {
            logger.LogInformation("claude {Installation}", chosen);
            return chosen;
        }

        // 多份并存本身就是个该被看见的事实：今天挑对了，明天有人把新版装到别处就又是一次
        // 「他明明更新过了」。所以列出来，而且是 Warning。
        logger.LogWarning(
            "这台机器上有 {Count} 份 claude，用的是 {Chosen}；另外还有：{Others}",
            found.Count,
            chosen,
            string.Join(" / ", found.Where(f => f != chosen).Select(f => f.ToString())));

        return chosen;
    }

    /// <summary>
    /// 在多份之间挑一份：版本号最大的赢。
    /// </summary>
    /// <remarks>
    /// 验不出版本的排在最后（可能根本不是 claude，只是重名），但仍然可用 ——
    /// 全都验不出时至少还能按搜索顺序起一个，总好过直接不干活。
    /// 版本相同则保持搜索顺序，让配置和 PATH 仍然说了算。
    /// </remarks>
    internal static ClaudeInstallation Choose(IReadOnlyList<ClaudeInstallation> found)
        => found
            .Select((installation, order) => (installation, order))
            .OrderByDescending(x => x.installation.Version is not null)
            .ThenByDescending(x => x.installation.Version)
            .ThenBy(x => x.order)
            .First().installation;

    /// <summary>
    /// 从 <c>claude --version</c> 的输出里取版本号。取不到返回 null。
    /// </summary>
    /// <remarks>
    /// 实测输出是 <c>2.1.268 (Claude Code)</c>。只认开头那串数字点数字 —— 格式将来变了
    /// 最坏的结果是「版本未知」，而那只影响挑选的优先级和日志好不好看，不该让评审起不来。
    /// </remarks>
    internal static Version? ParseVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var match = VersionPattern().Match(output);
        return match.Success && Version.TryParse(match.Value, out var version) ? version : null;
    }

    private async Task<Version?> VersionOfAsync(string path, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);

            var result = await ProcessRunner.RunAsync(
                path, ["--version"], ct: timeout.Token).ConfigureAwait(false);

            return result.ExitCode == 0 ? ParseVersion(result.StdOut) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "{Path} --version 超过 {Timeout} 秒没返回",
                path,
                ProbeTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 重名的东西、没有执行位的文件、架构不对的二进制，都会落到这里。
            // 它只是「这个候选不算数」，不是错误。
            logger.LogDebug(ex, "{Path} --version 起不来，当作版本未知", path);
            return null;
        }
    }

    [GeneratedRegex(@"\d+(?:\.\d+)+")]
    private static partial Regex VersionPattern();
}

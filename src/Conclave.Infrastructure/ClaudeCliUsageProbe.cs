using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Conclave.Application;
using Conclave.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure;

/// <summary>
/// 跑 <c>claude -p "/usage"</c> 取订阅额度真值。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是这条路。</b> 订阅额度的真值一共有四个来源，实测比较过：
/// </para>
/// <list type="bullet">
/// <item>
/// 交互式会话的 statusLine（stdin JSON 里的 <c>rate_limits</c>）—— 给得出官方百分比，
/// 但只在交互式会话触发（<c>claude -p</c> 不触发，<c>Stop</c>/<c>SessionEnd</c> 钩子的
/// JSON 里也没有），而且要往用户的全局 <c>~/.claude/settings.json</c> 里塞配置。
/// </item>
/// <item>
/// 本地会话记录 <c>~/.claude/projects/**/*.jsonl</c> —— headless 可读、精确，
/// 但<b>只有分子没有分母</b>（订阅上限官方不公开）。
/// </item>
/// <item>
/// <c>GET https://api.anthropic.com/api/oauth/usage</c> —— 权威（<c>/usage</c> 面板背后
/// 就是它），但未公开，且要去 keychain 里读 OAuth token。
/// </item>
/// <item>
/// <b><c>claude -p "/usage"</c> —— 采用这个。</b> 二进制里这条命令声明了
/// <c>supportsNonInteractive: true</c>，是官方明确支持无头运行的本地命令。
/// 实测 <c>total_cost_usd=0</c>、<c>num_turns=0</c>、<c>duration_api_ms=0</c>、
/// 墙钟约 0.8 秒 —— 纯本地，不打模型、不花额度。既不用改用户的全局配置，
/// 也不用碰凭据和未公开端点。
/// </item>
/// </list>
/// <para>
/// 唯一代价是<b>解析文本</b>：格式随版本可能变。所以解析一律宽松匹配，认不出就返回空
/// 并说明原因，由 <see cref="ClaudeUsageMeter"/> 降级到按预算折算 ——
/// 绝不猜一个数字出来，那比没有数字更糟。
/// </para>
/// </remarks>
public sealed partial class ClaudeCliUsageProbe(
    ConclaveOptions options,
    ClaudeCli claudeCli,
    ILogger<ClaudeCliUsageProbe> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<UsageWindow> _cached = [];
    private string _cachedNote = string.Empty;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    /// <summary>读一次额度窗口。命中缓存时不起子进程。</summary>
    /// <returns>窗口列表（可能为空）与一行人读的说明。</returns>
    public async Task<(IReadOnlyList<UsageWindow> Windows, string Note)> ReadAsync(CancellationToken ct)
    {
        var ttl = options.ClaudeUsage.CliProbeInterval;
        if (DateTimeOffset.UtcNow - _cachedAt < ttl)
        {
            return (_cached, _cachedNote);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 双检：等锁期间别的调用可能已经刷新过了，没必要再起一个子进程。
            if (DateTimeOffset.UtcNow - _cachedAt < ttl)
            {
                return (_cached, _cachedNote);
            }

            var (windows, note) = await ProbeAsync(ct).ConfigureAwait(false);

            // 只有成功拿到窗口才刷新缓存时间戳。失败不缓存，下一次还会重试 ——
            // 但也不清掉上一次的好数据：claude 偶发起不来时，几十秒前的百分比仍然可用。
            if (windows.Count > 0)
            {
                _cached = windows;
                _cachedNote = note;
                _cachedAt = DateTimeOffset.UtcNow;
                return (windows, note);
            }

            return (_cached, _cached.Count > 0 ? _cachedNote : note);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private async Task<(IReadOnlyList<UsageWindow> Windows, string Note)> ProbeAsync(CancellationToken ct)
    {
        string claude;
        try
        {
            // 跟评审走同一个入口：一台机器上有好几份 claude 时，额度必须读自
            // 真正会去跑评审的那一份，否则报出来的额度是另一个账号/另一套缓存的。
            claude = (await claudeCli.ResolveAsync(ct).ConfigureAwait(false)).Path;
        }
        catch (FileNotFoundException ex)
        {
            logger.LogWarning(ex, "找不到 claude 可执行文件，拿不到额度真值");
            return ([], "找不到 claude 可执行文件");
        }

        ProcessResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.ClaudeUsage.CliProbeTimeout);

            // 工作目录用 Conclave 自己的 home：/usage 是本地命令，不依赖仓库上下文，
            // 而挑一个固定的自有目录能避开「当前目录恰好是个没被信任的工作区」这类干扰。
            _ = Directory.CreateDirectory(options.HomeDirectory);

            result = await ProcessRunner.RunAsync(
                claude,
                ["-p", "/usage", "--output-format", "json"],
                workingDirectory: options.HomeDirectory,
                ct: timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("claude -p /usage 超过 {Timeout} 未返回", options.ClaudeUsage.CliProbeTimeout);
            return ([], "读额度超时");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "起 claude -p /usage 失败");
            return ([], "起 claude 子进程失败");
        }

        if (!result.Success)
        {
            logger.LogWarning(
                "claude -p /usage 退出码 {Code}：{Err}", result.ExitCode, Truncate(result.StdErr));
            return ([], $"claude -p /usage 退出码 {result.ExitCode}");
        }

        var text = ExtractResultText(result.StdOut);
        if (text is null)
        {
            return ([], "claude 的输出不是预期的 JSON");
        }

        var windows = Parse(text, DateTimeOffset.Now);
        if (windows.Count == 0)
        {
            // API key 用户没有订阅额度，输出里本来就没有这几行 —— 那是正常情况，不该刷警告。
            logger.LogDebug("claude -p /usage 的输出里没有额度行：{Text}", Truncate(text));
            return ([], "claude 未报告订阅额度（API key 用户或格式已变）");
        }

        return (windows, "来自 claude -p /usage");
    }

    /// <summary>
    /// 从 <c>--output-format json</c> 的外壳里取正文。
    /// </summary>
    /// <remarks>
    /// 套一层 JSON 而不是直接读 stdout：外壳里还有 <c>is_error</c> 可判，
    /// 而裸 stdout 将来可能混进别的提示行。
    /// </remarks>
    private string? ExtractResultText(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            if (root.TryGetProperty("is_error", out var isError)
                && isError.ValueKind == JsonValueKind.True)
            {
                logger.LogWarning("claude -p /usage 报错：{Text}", Truncate(stdout));
                return null;
            }

            return root.TryGetProperty("result", out var r) ? r.GetString() : null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "claude -p /usage 的输出解析失败");
            return null;
        }
    }

    /// <summary>
    /// 解析 <c>/usage</c> 的正文。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 实测形态（v2.1.266）：
    /// </para>
    /// <code>
    /// Current session: 46% used · resets Sep 9 at 1:40pm (Asia/Shanghai)
    /// Current week (all models): 40% used · resets Sep 14 at 2am (Asia/Shanghai)
    /// Current week (Fable): 23% used · resets Sep 14 at 2am (Asia/Shanghai)
    /// </code>
    /// <para>
    /// 分成两级正则：先抠出「标签 + 百分比」，重置时刻单独再试一次。这样
    /// <b>重置时刻的格式变了不会连带丢掉百分比</b> —— 百分比是席位规则真正要用的那个数，
    /// 重置时刻只是给人看的。
    /// </para>
    /// <para>
    /// <paramref name="now"/> 由调用方传入而不是在里面读时钟：文本里的日期<b>不带年份</b>
    /// （<c>Sep 14</c>），要靠「现在」推断，而这个推断必须能在测试里钉住。
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<UsageWindow> Parse(string text, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(text);

        var windows = new List<UsageWindow>(3);

        foreach (var line in text.Split('\n'))
        {
            var m = LineRegex().Match(line);
            if (!m.Success)
            {
                continue;
            }

            if (!double.TryParse(
                    m.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                continue;
            }

            var scope = m.Groups["scope"].Success ? m.Groups["scope"].Value.Trim() : string.Empty;
            var key = KeyOf(m.Groups["kind"].Value, scope);

            windows.Add(new UsageWindow(
                key,
                Math.Round(Math.Clamp(pct / 100.0, 0, 1), 4),
                ParseReset(line, now))
            {
                // 按模型细分的周额度不参与「还能不能接活」的判定：Fable 的周额度打满
                // 并不妨碍用 Opus 评审。它照样显示，但不该把节点挡在门外。
                Gates = !key.StartsWith(UsageWindow.ScopedSevenDayPrefix, StringComparison.Ordinal),
            });
        }

        return windows;
    }

    /// <summary>
    /// 文本标签 → 稳定的机器键。
    /// </summary>
    /// <remarks>
    /// 刻意翻译成 <c>five_hour</c> / <c>seven_day</c> 这套键，而不是把
    /// <c>Current session</c> 原样当键：显示名归展示层（<c>Conclave.App.Labels</c>）决定，
    /// 而且这两个键跟 statusLine JSON、<c>/api/oauth/usage</c> 用的是同一套词，
    /// 将来换回任何一个来源都不用动界面。
    /// </remarks>
    private static string KeyOf(string kind, string scope)
    {
        if (kind.Equals("session", StringComparison.OrdinalIgnoreCase))
        {
            return "five_hour";
        }

        // week：(all models) 是总额度，(Fable) 这种是按模型细分的子额度。
        return scope.Length == 0 || scope.Equals("all models", StringComparison.OrdinalIgnoreCase)
            ? "seven_day"
            : UsageWindow.ScopedSevenDayPrefix + scope;
    }

    /// <summary>
    /// 解析 <c>resets Sep 14 at 2am (Asia/Shanghai)</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两处需要小心：文本里<b>没有年份</b>，而且时刻是 12 小时制、分钟可省
    /// （<c>2am</c> / <c>1:40pm</c>）。
    /// </para>
    /// <para>
    /// 年份按「重置总是在未来」推断：先套当前年，算出来若落在过去就加一年 ——
    /// 跨年那几天（12 月底看到 <c>Jan 3</c>）不这样处理会得到一个去年的时刻，
    /// 于是那个窗口会被当成已过期而丢掉。
    /// </para>
    /// <para>
    /// 括号里的时区就是 Claude 渲染时用的时区，一般等于本机时区；能解析就按它算，
    /// 认不出就退回本机时区。
    /// </para>
    /// </remarks>
    private static DateTimeOffset? ParseReset(string line, DateTimeOffset now)
    {
        var m = ResetRegex().Match(line);
        if (!m.Success)
        {
            return null;
        }

        if (!DateTime.TryParseExact(
                m.Groups["mon"].Value, "MMM", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var month))
        {
            return null;
        }

        if (!int.TryParse(m.Groups["day"].Value, CultureInfo.InvariantCulture, out var day)
            || !int.TryParse(m.Groups["h"].Value, CultureInfo.InvariantCulture, out var hour12))
        {
            return null;
        }

        var minute = m.Groups["min"].Success
            && int.TryParse(m.Groups["min"].Value, CultureInfo.InvariantCulture, out var parsedMinute)
                ? parsedMinute
                : 0;

        var pm = m.Groups["ap"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
        var hour = (hour12 % 12) + (pm ? 12 : 0);

        var zone = ResolveZone(m.Groups["tz"].Success ? m.Groups["tz"].Value : null);
        var local = now.ToOffset(zone.GetUtcOffset(now));

        for (var yearShift = 0; yearShift <= 1; yearShift++)
        {
            var year = local.Year + yearShift;
            if (day < 1 || day > DateTime.DaysInMonth(year, month.Month))
            {
                continue;
            }

            var naive = new DateTime(year, month.Month, day, hour, minute, 0, DateTimeKind.Unspecified);
            var candidate = new DateTimeOffset(naive, zone.GetUtcOffset(naive));

            // 重置时刻总在未来。留一点余量，免得刚好卡在边界上把当前窗口判成过期。
            if (candidate > now.AddHours(-1))
            {
                return candidate;
            }
        }

        return null;
    }

    private static TimeZoneInfo ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private static string Truncate(string s)
        => s.Length <= 400 ? s.Trim() : string.Concat(s.AsSpan(0, 400).Trim(), "…");

    /// <summary>
    /// <c>Current session: 46% used</c> / <c>Current week (all models): 40% used</c>。
    /// </summary>
    /// <remarks>
    /// 刻意不要求行尾是 <c>· resets …</c>：那一段单独解析，缺了也不该丢掉百分比。
    /// </remarks>
    [GeneratedRegex(
        @"^\s*Current\s+(?<kind>session|week)\s*(?:\(\s*(?<scope>[^)]*)\s*\))?\s*:\s*(?<pct>\d+(?:\.\d+)?)\s*%\s*used",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex LineRegex();

    /// <summary><c>resets Sep 14 at 2am (Asia/Shanghai)</c>。</summary>
    [GeneratedRegex(
        @"resets\s+(?<mon>[A-Za-z]{3,})\w*\s+(?<day>\d{1,2})\s+at\s+(?<h>\d{1,2})(?::(?<min>\d{2}))?\s*(?<ap>am|pm)?\s*(?:\(\s*(?<tz>[^)]+?)\s*\))?",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex ResetRegex();
}

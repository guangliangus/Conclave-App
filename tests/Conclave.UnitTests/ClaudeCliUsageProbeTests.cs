using Conclave.Application;
using Conclave.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

/// <summary>
/// 从 <c>claude -p "/usage"</c> 的输出里解析订阅额度。
/// </summary>
/// <remarks>
/// 这是整条链上唯一<b>解析文本</b>的地方，格式随 Claude Code 版本可能变，所以两头都要测：
/// <see cref="ClaudeCliUsageProbe.Parse"/> 用实测原文钉住格式，端到端那几条用一个假的
/// <c>claude</c> 脚本跑真实的子进程 + JSON 外壳 + 缓存路径。
/// </remarks>
public sealed class ClaudeCliUsageProbeTests : IDisposable
{
    /// <summary>v2.1.266 的实测原文。</summary>
    private const string RealOutput = """
        You are currently using your subscription to power your Claude Code usage

        Current session: 46% used · resets Sep 9 at 1:40pm (Asia/Shanghai)
        Current week (all models): 40% used · resets Sep 14 at 2am (Asia/Shanghai)
        Current week (Fable): 23% used · resets Sep 14 at 2am (Asia/Shanghai)

        What's contributing to your limits usage?
        Last 24h · 1401 requests · 30 sessions
        """;

    private static readonly DateTimeOffset Now = new(2026, 9, 9, 11, 6, 0, TimeSpan.FromHours(8));

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "conclave-probe-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_real_output_parses_into_three_windows()
    {
        var windows = ClaudeCliUsageProbe.Parse(RealOutput, Now);

        Assert.Equal(3, windows.Count);
        Assert.Equal(["five_hour", "seven_day", "seven_day:Fable"], windows.Select(w => w.Key));
        Assert.Equal([0.46, 0.40, 0.23], windows.Select(w => w.Utilization));
    }

    [Fact]
    public void Window_keys_are_translated_not_taken_from_the_display_text()
    {
        // 「Current session」是给人看的英文标签。原样当键会让它漏到界面上（实测见过一次），
        // 而且换回 statusLine JSON 或 /api/oauth/usage 时键就对不上了 ——
        // 那两个来源用的都是 five_hour / seven_day。
        var windows = ClaudeCliUsageProbe.Parse(RealOutput, Now);

        Assert.DoesNotContain(windows, w => w.Key.Contains("Current", StringComparison.Ordinal));
    }

    [Fact]
    public void Model_scoped_weekly_limits_do_not_gate_seating()
    {
        var windows = ClaudeCliUsageProbe.Parse(RealOutput, Now);

        // Fable 的周额度打满并不妨碍用 Opus 评审 —— 拿它把节点挡在门外是错的。
        // 但它照样要显示：人需要知道钱花在哪个模型上。
        Assert.True(windows.Single(w => w.Key == "five_hour").Gates);
        Assert.True(windows.Single(w => w.Key == "seven_day").Gates);
        Assert.False(windows.Single(w => w.Key == "seven_day:Fable").Gates);
    }

    [Fact]
    public void Reset_times_are_parsed_in_the_reported_zone()
    {
        var windows = ClaudeCliUsageProbe.Parse(RealOutput, Now);
        var session = windows.Single(w => w.Key == "five_hour");
        var week = windows.Single(w => w.Key == "seven_day");

        // 1:40pm Asia/Shanghai = 05:40Z；文本里没有年份，靠「现在」推出 2026。
        Assert.Equal(
            new DateTimeOffset(2026, 9, 9, 5, 40, 0, TimeSpan.Zero),
            session.ResetsAt!.Value.ToUniversalTime());

        // 2am 的分钟被省掉了，要按 0 分处理而不是解析失败。
        Assert.Equal(
            new DateTimeOffset(2026, 9, 13, 18, 0, 0, TimeSpan.Zero),
            week.ResetsAt!.Value.ToUniversalTime());
    }

    [Fact]
    public void A_reset_date_in_the_next_year_rolls_the_year_over()
    {
        // 12 月底看到「Jan 3」：套当前年会得到一个去年的时刻，那个窗口就会被当成已过期丢掉。
        var newYearsEve = new DateTimeOffset(2026, 12, 30, 22, 0, 0, TimeSpan.FromHours(8));
        var text = "Current week (all models): 12% used · resets Jan 3 at 2am (Asia/Shanghai)";

        var window = Assert.Single(ClaudeCliUsageProbe.Parse(text, newYearsEve));

        Assert.Equal(2027, window.ResetsAt!.Value.Year);
        Assert.True(window.ResetsAt > newYearsEve);
    }

    [Fact]
    public void A_percentage_survives_an_unparseable_reset_time()
    {
        // 百分比是席位规则真正要用的那个数，重置时刻只是给人看的 ——
        // 后者的格式变了不该连带丢掉前者。
        var text = "Current session: 73% used · resets sometime later";

        var window = Assert.Single(ClaudeCliUsageProbe.Parse(text, Now));

        Assert.Equal(0.73, window.Utilization);
        Assert.Null(window.ResetsAt);
    }

    [Fact]
    public void Output_without_limit_lines_yields_nothing()
    {
        // API key 用户没有订阅额度，输出里本来就没有这几行 —— 返回空、让上层降级，
        // 而不是猜一个数字出来。
        Assert.Empty(ClaudeCliUsageProbe.Parse(
            "You are currently using the Anthropic API to power your Claude Code usage", Now));
        Assert.Empty(ClaudeCliUsageProbe.Parse(string.Empty, Now));
    }

    [Fact]
    public async Task The_probe_runs_the_cli_and_reads_the_json_envelope()
    {
        var probe = Probe(FakeClaude(RealOutput));

        var (windows, note) = await probe.ReadAsync(CancellationToken.None);

        Assert.Equal(3, windows.Count);
        Assert.Contains("claude -p /usage", note);
    }

    [Fact]
    public async Task The_probe_caches_so_the_panel_does_not_fork_per_refresh()
    {
        var counter = Path.Combine(_dir, "calls");
        var probe = Probe(FakeClaude(RealOutput, countTo: counter));

        for (var i = 0; i < 5; i++)
        {
            _ = await probe.ReadAsync(CancellationToken.None);
        }

        // 面板每 30 秒刷一次、发现循环每 60 秒一轮，多个读者共用一份 60 秒缓存。
        Assert.Equal(1, CallCount(counter));
    }

    [Fact]
    public async Task A_failing_cli_keeps_the_last_good_reading()
    {
        var script = FakeClaude(RealOutput);
        var options = Options(script);
        options.ClaudeUsage.CliProbeInterval = TimeSpan.Zero;   // 每次都真跑
        var probe = new ClaudeCliUsageProbe(
            options, new ClaudeCli(options, new ExecutableResolver(options), NullLogger<ClaudeCli>.Instance), NullLogger<ClaudeCliUsageProbe>.Instance);

        Assert.Equal(3, (await probe.ReadAsync(CancellationToken.None)).Windows.Count);

        // claude 偶发起不来时，一分钟前的百分比仍然比「没有数据」有用 ——
        // 清空会让节点看起来额度未生效，从而继续接活。
        await File.WriteAllTextAsync(
            script, "#!/bin/sh\nexit 1\n", CancellationToken.None);

        var (windows, _) = await probe.ReadAsync(CancellationToken.None);
        Assert.Equal(3, windows.Count);
    }

    [Fact]
    public async Task Garbage_from_the_cli_is_reported_not_guessed()
    {
        var options = Options(FakeClaudeRaw("这不是 JSON"));
        var probe = new ClaudeCliUsageProbe(
            options, new ClaudeCli(options, new ExecutableResolver(options), NullLogger<ClaudeCli>.Instance), NullLogger<ClaudeCliUsageProbe>.Instance);

        var (windows, note) = await probe.ReadAsync(CancellationToken.None);

        Assert.Empty(windows);
        Assert.Contains("JSON", note, StringComparison.OrdinalIgnoreCase);
    }

    private ClaudeCliUsageProbe Probe(string executable)
    {
        var options = Options(executable);
        return new ClaudeCliUsageProbe(
            options, new ClaudeCli(options, new ExecutableResolver(options), NullLogger<ClaudeCli>.Instance), NullLogger<ClaudeCliUsageProbe>.Instance);
    }

    private ConclaveOptions Options(string executable)
    {
        _ = Directory.CreateDirectory(_dir);
        return new ConclaveOptions
        {
            HomeDirectory = Path.Combine(_dir, "home"),
            ClaudeExecutable = executable,
        };
    }

    /// <summary>
    /// 一个假的 <c>claude</c>：吐出跟真命令同形的 <c>--output-format json</c> 外壳。
    /// </summary>
    /// <remarks>
    /// 刻意跑真的子进程而不是给探针塞一个接口：这样 <c>ProcessRunner</c>、JSON 外壳解析、
    /// 缓存这几段都真的被跑过。它们出问题的方式（拿错字段、缓存不生效）单测假货是发现不了的。
    /// </remarks>
    private string FakeClaude(string usageText, string? countTo = null)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "result",
            subtype = "success",
            is_error = false,
            num_turns = 0,
            total_cost_usd = 0,
            result = usageText,
        });

        var count = countTo is null
            ? string.Empty
            : $"printf x >> '{countTo}'\n";

        // 假货也得认 --version：ClaudeCli 会先问一次版本再决定用哪一份 claude，
        // 而那一问不该算进「探了几次额度」的计数里 —— 它自己是有缓存的。
        return WriteScript(
            "#!/bin/sh\n"
            + "if [ \"$1\" = \"--version\" ]; then echo '2.1.268 (Claude Code)'; exit 0; fi\n"
            + $"{count}cat <<'JSON'\n{payload}\nJSON\n");
    }

    private string FakeClaudeRaw(string stdout)
        => WriteScript($"#!/bin/sh\nprintf '%s' '{stdout}'\n");

    private string WriteScript(string body)
    {
        _ = Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "claude-" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    private static int CallCount(string counter)
        => File.Exists(counter)
            ? File.ReadAllText(counter).Length
            : 0;

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

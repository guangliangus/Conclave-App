using Conclave.Application;
using Conclave.Domain;
using Conclave.Application.Ports;
using Conclave.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

/// <summary>
/// Claude 额度用量：<c>claude -p "/usage"</c> 的真值，与按预算折算的兜底。
/// </summary>
/// <remarks>
/// 这个数决定「本节点还接不接评审任务」（见 <see cref="Conclave.Domain.Elector.MaxUtilization"/>），
/// 而它算错的两个方向都很糟：低报会让快用完额度的机器继续吸席位、然后一张张出 Error 票；
/// 高报会让机器无声地退出 mesh，PR 停在「已召集」不动。所以两个方向都要测。
/// </remarks>
public sealed class ClaudeUsageMeterTests : IDisposable
{
    private const string SelfId = "35641f207ebfe263";

    /// <summary>
    /// 所有用例都钉在这一刻：2026-09-09 周三中午。
    /// </summary>
    /// <remarks>
    /// 周额度的阈值按工作日配速算，所以读数跟「今天周几」有关。不钉住的话同样的输入
    /// 换一天跑就是另一个数 —— 那种用例今天绿明天红，比没有还糟。
    /// <para>
    /// 假 <c>/usage</c> 文本里的周窗口是「Sep 14 at 2am」重置，即 9/7 02:00 → 9/14 02:00，
    /// 五个工作日整。周三中午已过 2.42 个工作日，故周阈值 = (2.42+1)/5 ≈ 68%。
    /// </para>
    /// </remarks>
    /// <remarks>
    /// 时刻与时区都写死成 <b>+08</b>（跟下面假 <c>/usage</c> 文本里声明的 Asia/Shanghai 一致）。
    /// 早先这里是 <c>DateTimeKind.Local</c>：<c>now</c> 跟着 runner 的时区走，而重置时刻是从
    /// 那段文本里按 Asia/Shanghai 解析出来的<b>绝对</b>时刻 —— 两边只在 UTC+8 上对得上。
    /// CI（UTC）上周阈值从 68% 变成 70%，于是卡住的窗口从 7d 翻成 5h，同一份输入得出另一个数。
    /// </remarks>
    private static readonly DateTimeOffset Wednesday =
        new(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(8));

    /// <summary>固定时钟 + 固定时区。用自造的偏移时区，不依赖机器上有没有 tzdata。</summary>
    private sealed class FixedClock(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone(
            "conclave-test-utc8", TimeSpan.FromHours(8), "UTC+08", "UTC+08");
    }

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "conclave-usage-" + Guid.NewGuid().ToString("N"));

    private ConclaveOptions Options(long tokenBudget = 40_000_000, decimal costBudget = 0)
    {
        _ = Directory.CreateDirectory(_dir);
        return new ConclaveOptions
        {
            HomeDirectory = Path.Combine(_dir, "home"),
            ClaudeUsage = new ClaudeUsageOptions
            {
                Window = TimeSpan.FromDays(7),
                TokenBudget = tokenBudget,
                CostUsdBudget = costBudget,
            },
        };
    }

    /// <summary>
    /// 装一个 meter。<paramref name="usageText"/> 非空时用一个假的 <c>claude</c> 提供真值。
    /// </summary>
    /// <remarks>
    /// 探针是具体类型（它自带缓存，为了测试把它抽成接口不值得），所以真值靠一个假的
    /// <c>claude</c> 脚本注入 —— 顺带把子进程、JSON 外壳、文本解析这几段也真的跑过一遍。
    /// </remarks>
    private ClaudeUsageMeter Meter(
        ConclaveOptions options, FakeReviewLog log, string? usageText = null)
    {
        options.ClaudeExecutable = usageText is not null
            ? FakeClaude(usageText)
            // 指向一个不存在的可执行文件：探针拿不到真值，干净地降级到折算。
            : Path.Combine(_dir, "no-such-claude");

        var probe = new ClaudeCliUsageProbe(
            options, new ClaudeCli(options, new ExecutableResolver(options), NullLogger<ClaudeCli>.Instance), NullLogger<ClaudeCliUsageProbe>.Instance);

        return new ClaudeUsageMeter(options, probe, log, new FixedClock(Wednesday));
    }

    private string FakeClaude(string usageText)
    {
        _ = Directory.CreateDirectory(_dir);
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "result",
            subtype = "success",
            is_error = false,
            result = usageText,
        });

        var path = Path.Combine(_dir, "claude-" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(path, "#!/bin/sh\ncat <<'JSON'\n" + payload + "\nJSON\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    /// <summary>照 v2.1.266 的实测格式拼一段 <c>/usage</c> 正文。</summary>
    private static string Usage(string session, string week)
        => $"Current session: {session}% used · resets Sep 9 at 1:40pm (Asia/Shanghai)\n"
            + $"Current week (all models): {week}% used · resets Sep 14 at 2am (Asia/Shanghai)";

    [Fact]
    public async Task The_real_reading_takes_the_tightest_gating_window()
    {
        var reading = await Meter(Options(tokenBudget: 0), new FakeReviewLog(), Usage("46", "40"))
            .ReadAsync(SelfId, CancellationToken.None);

        // 周额度暂时只当硬顶（UsagePressure.WeeklyCeilingOnly），40% 一点压力都不贡献 ——
        // 卡住的是 5h，而 SessionGate 就取自 Elector.MaxUtilization，
        // 所以上报的数恰好等于它的原始读数。
        Assert.Equal(0.46, reading.Utilization, 4);
        Assert.Contains("卡在 5h", reading.Detail);
        Assert.Equal("claude-cli", reading.Source);
        Assert.Contains("5h 46%", reading.Detail);
        Assert.Contains("7d 40%", reading.Detail);   // 不判定，但照样显示
    }

    [Fact]
    public async Task The_reset_time_comes_from_the_window_that_actually_binds()
    {
        // 周额度到了硬顶：5h 窗口今天下午就重置，但真正卡住我们的是几天后才重置的那个。
        // 报「半小时后恢复」是误导。
        var reading = await Meter(Options(tokenBudget: 0), new FakeReviewLog(), Usage("10", "96"))
            .ReadAsync(SelfId, CancellationToken.None);

        // 96% 过了硬顶 95%，压力 96/95 > 1，折回 0.8 那把尺子就是 80.8% —— 刚好出局。
        Assert.Equal(0.96 / UsagePressure.WeeklyCeiling * Elector.MaxUtilization, reading.Utilization, 4);
        Assert.True(reading.Utilization >= Elector.MaxUtilization);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 14, 2, 0, 0, TimeSpan.FromHours(8)), reading.ResetsAt);
    }

    [Fact]
    public async Task A_model_scoped_window_is_shown_but_does_not_gate()
    {
        var text = Usage("10", "20")
            + "\nCurrent week (Fable): 95% used · resets Sep 14 at 2am (Asia/Shanghai)";

        var reading = await Meter(Options(tokenBudget: 0), new FakeReviewLog(), text)
            .ReadAsync(SelfId, CancellationToken.None);

        // Fable 的周额度打满不妨碍用 Opus 评审 —— 聚合值不该被它拉到 95%，
        // 但它照样要显示：人需要知道钱花在哪个模型上。卡住的是 5h。
        Assert.Equal(0.10, reading.Utilization, 4);
        Assert.Equal(3, reading.Windows.Count);
        Assert.Contains("7d/Fable 95%", reading.Detail);
    }

    [Fact]
    public async Task The_real_reading_wins_over_a_lower_budget_estimate()
    {
        var log = new FakeReviewLog { Tokens = 4_000_000 };   // 折算只有 10%

        // 折算只看得见 Conclave 自己出过的票，实测低报约三个数量级 ——
        // 真值更高时必须以它为准。
        var reading = await Meter(Options(tokenBudget: 40_000_000), log, Usage("85", "40"))
            .ReadAsync(SelfId, CancellationToken.None);

        Assert.Equal(0.85, reading.Utilization);
        Assert.Equal("claude-cli", reading.Source);
    }

    [Fact]
    public async Task A_tighter_budget_never_overrides_the_real_reading()
    {
        var log = new FakeReviewLog { Tokens = 52_000_000 };   // 折算 132%，夹到 100%

        // 折算的分母是个没有出处的估计（默认 4000 万 token / 7 天，官方并没有公开 token
        // 配额），真值是订阅侧的事实 —— 估计值不该有权把节点关在门外。这里的数字是实测：
        // 这台机器 7 天出票 5285 万 token，于是折算判它「额度满」，而 Claude 自己报的是
        // 5h 18% / 7d 76%，压力 18%，本来还能接一整天的活。
        var reading = await Meter(Options(tokenBudget: 40_000_000), log, Usage("18", "76"))
            .ReadAsync(SelfId, CancellationToken.None);

        Assert.Equal(0.18, reading.Utilization, 4);
        Assert.Equal("claude-cli", reading.Source);
        Assert.Equal(2, reading.Windows.Count);

        // 不参与判定，但要看得见 —— 折算是唯一能看出「本节点自己烧了多少」的数。
        Assert.Contains("本机折算 100%", reading.Detail);
        Assert.Contains("不参与判定", reading.Detail);
    }

    [Fact]
    public async Task An_unavailable_cli_falls_back_to_the_budget_and_says_why()
    {
        var log = new FakeReviewLog { Tokens = 10_000_000 };

        var reading = await Meter(Options(tokenBudget: 40_000_000), log)
            .ReadAsync(SelfId, CancellationToken.None);

        // 折算值和真值都是百分比，界面上分不出来的话人会以为自己看的是订阅额度 ——
        // 所以「为什么没有真值」必须跟着读数一起带出去。
        Assert.Equal(0.25, reading.Utilization);
        Assert.Equal("budget", reading.Source);
        Assert.Contains("没有 Claude 额度真值", reading.Detail);
    }

    [Fact]
    public async Task The_tighter_of_the_two_budgets_wins()
    {
        // token 用了 25%，金额用了 80% —— 先触线的那个说了算，否则配了金额预算等于没配。
        var log = new FakeReviewLog { Tokens = 10_000_000, Cost = 80m };

        var reading = await Meter(Options(tokenBudget: 40_000_000, costBudget: 100m), log)
            .ReadAsync(SelfId, CancellationToken.None);

        Assert.Equal(0.8, reading.Utilization);
    }

    [Fact]
    public async Task Utilization_is_clamped_to_one_when_the_budget_is_blown()
    {
        // 夹到 1 而不是让它变成 9：Elector.Weight 里有 (1 - Utilization)，
        // 负权重会让 Hrw.Score 走进 NegativeInfinity 分支，语义上没错但难排查。
        var reading = await Meter(
                Options(tokenBudget: 1_000_000), new FakeReviewLog { Tokens = 9_000_000 })
            .ReadAsync(SelfId, CancellationToken.None);

        Assert.Equal(1.0, reading.Utilization);
    }

    [Fact]
    public async Task Utilization_is_rounded_to_four_places_to_match_the_signed_beacon()
    {
        // Beacon.SigningPayload 用 F4 格式化这个数。源头不取整的话，签名覆盖的数字
        // 和实际参与席位计算的数字就不是同一个 —— 差异极小，但性质不对。
        var reading = await Meter(
                Options(tokenBudget: 3_000_000), new FakeReviewLog { Tokens = 1_000_000 })
            .ReadAsync(SelfId, CancellationToken.None);

        Assert.Equal(0.3333, reading.Utilization);
    }

    [Fact]
    public async Task No_real_value_and_no_budget_means_the_ceiling_rule_is_inert()
    {
        var log = new FakeReviewLog { Tokens = 999_000_000 };

        var reading = await Meter(Options(tokenBudget: 0, costBudget: 0), log)
            .ReadAsync(SelfId, CancellationToken.None);

        Assert.Equal(0, reading.Utilization);
        Assert.Equal("unbounded", reading.Source);
        Assert.Equal(0, log.Calls);   // 没配预算就不必去查投影表
    }

    [Fact]
    public async Task Only_this_nodes_own_reviews_count_towards_its_budget()
    {
        var log = new FakeReviewLog { Tokens = 4_000_000 };

        _ = await Meter(Options(), log).ReadAsync(SelfId, CancellationToken.None);

        // 投影表里混着 gossip 进来的别人的票，而额度是按机器算的 ——
        // 同一个人在两台机器上是两份额度，所以必须按 electorId 过滤。
        Assert.Equal(SelfId, log.LastElectorId);
    }

    [Fact]
    public async Task The_budget_window_matches_the_configured_window()
    {
        var options = Options();
        options.ClaudeUsage.Window = TimeSpan.FromHours(5);
        var log = new FakeReviewLog();

        var before = DateTimeOffset.UtcNow;
        _ = await Meter(options, log).ReadAsync(SelfId, CancellationToken.None);

        Assert.True(Math.Abs((log.LastFrom - (before - TimeSpan.FromHours(5))).TotalSeconds) < 5);
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

    /// <summary>只回答「这个节点在这个窗口里用了多少」的最小假货。</summary>
    private sealed class FakeReviewLog : IReviewLog
    {
        internal long Tokens { get; set; }

        internal decimal Cost { get; set; }

        internal int Calls { get; private set; }

        internal string? LastElectorId { get; private set; }

        internal DateTimeOffset LastFrom { get; private set; }

        public Task<UsageSummary> ReadElectorUsageAsync(
            string electorId, DateTimeOffset from, CancellationToken ct)
        {
            Calls++;
            LastElectorId = electorId;
            LastFrom = from;

            // 全塞进 InputTokens：UsageSummary.TotalTokens 是四类之和，这里只关心总量。
            return Task.FromResult(new UsageSummary(electorId, 1, Tokens, 0, 0, 0, Cost));
        }

        public Task<string?> ReadLastReviewerAsync(string project, int prId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ReviewRecord>> ReadRecentAsync(int limit, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ReviewRecord>> ReadByRevisionAsync(string revisionId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ScoreRow>> ReadLeaderboardAsync(
            DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<UsageSummary>> SummariseByReviewerAsync(
            DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<UsageSummary>> SummariseByMonthAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<UsageSummary>> SummariseByRepoAsync(
            DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<UsageSummary>> SummariseByModelAsync(
            DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<UsageSummary> ReadTotalAsync(
            DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
            => throw new NotSupportedException();
    }
}

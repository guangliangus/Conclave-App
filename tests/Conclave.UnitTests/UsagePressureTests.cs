using Conclave.Application;
using Conclave.Domain;

namespace Conclave.UnitTests;

/// <summary>
/// 各额度窗口各有各的阈值；周额度当下只当硬顶用。
/// </summary>
/// <remarks>
/// <para>
/// 两个方向都要钉住，因为它们的失败方式一样静默：
/// 太紧 → 周三就把节点关到周末，mesh 白养一台机器；
/// 太松 → 周额度耗尽的机器继续吸席位，一张张出 Error 票。
/// </para>
/// <para>
/// <see cref="UsagePressure.WeeklyCeilingOnly"/> 开着，所以走 <see cref="UsagePressure.Tightest"/>
/// 的用例断言的是「周额度不再贡献压力、只在硬顶出局」；按工作日配速那套算术照常测，
/// 只是直接打 <see cref="UsagePressure.PaceGate"/> —— 它现在没人调用，但摘掉周额度权重是
/// 临时的，恢复时这套算术要能直接用。
/// </para>
/// </remarks>
public class UsagePressureTests
{
    /// <summary>实测这台机器上的周窗口形状：周一 01:59 到周一 01:59，五个工作日整。</summary>
    private static readonly DateTimeOffset WeekReset = Local(2026, 9, 14, 1, 59);

    private static DateTimeOffset Local(int y, int m, int d, int hh = 0, int mm = 0)
        => new(new DateTime(y, m, d, hh, mm, 0, DateTimeKind.Local));

    private static UsageWindow Week(double utilization)
        => new("seven_day", utilization, WeekReset);

    private static UsageWindow Session(double utilization)
        => new("five_hour", utilization, WeekReset);

    private static double Reported(IEnumerable<UsageWindow> windows, DateTimeOffset now)
        => UsagePressure.Reported(UsagePressure.Tightest(windows, now)!.Value);

    private static bool HasHeadroom(IEnumerable<UsageWindow> windows, DateTimeOffset now)
        => Reported(windows, now) < Elector.MaxUtilization;

    /// <summary>
    /// 会话窗口卡线时，上报的数就是它的原始读数。
    /// </summary>
    /// <remarks>
    /// <see cref="UsagePressure.SessionGate"/> 取自 <see cref="Elector.MaxUtilization"/>，
    /// 所以这个恒等式是结构性的。它保证「额度 57%」在改造前后是同一个意思。
    /// </remarks>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.34)]
    [InlineData(0.57)]
    [InlineData(0.79)]
    public void A_binding_session_window_is_reported_verbatim(double utilization)
    {
        // 周额度压到 0，确保卡住的是会话窗口。
        var now = Local(2026, 9, 9, 12, 0);   // 周三中午
        Assert.Equal(utilization, Reported([Session(utilization), Week(0)], now), 4);
    }

    /// <summary>
    /// 【临时】周额度不再贡献压力：同一个用量，哪天读都是 5h 那个数。
    /// </summary>
    /// <remarks>
    /// 摘掉之前，周三的线只有 68%（配速），周额度 80% 会把这台机器关到周末；
    /// 摘掉之后周额度在硬顶以下一点压力都不贡献，上报的就是 5h 的原始读数 ——
    /// 于是它也不会通过 <c>Elector.Weight</c> 里的 <c>1 - 压力</c> 压低本机被派活的概率。
    /// </remarks>
    [Fact]
    public void The_week_contributes_no_pressure_below_the_hard_ceiling()
    {
        foreach (var now in new[]
        {
            Local(2026, 9, 7, 9, 0),    // 周一上午：配速几乎是 0，旧规则下 42% 就出局
            Local(2026, 9, 9, 12, 0),   // 周三中午：旧规则的线是 68%
            Local(2026, 9, 11, 12, 0),  // 周五中午
        })
        {
            Assert.Equal(0.10, Reported([Session(0.10), Week(0.80)], now), 4);
            Assert.True(HasHeadroom([Session(0.10), Week(0.94)], now));
        }
    }

    /// <summary>周额度只在到硬顶时才算数 —— 这就是「摘掉权重」的全部意思。</summary>
    [Fact]
    public void The_week_only_counts_once_it_reaches_the_hard_ceiling()
    {
        Assert.False(UsagePressure.Counts(Week(UsagePressure.WeeklyCeiling - 0.01)));
        Assert.True(UsagePressure.Counts(Week(UsagePressure.WeeklyCeiling)));
        Assert.True(UsagePressure.Counts(Session(0.01)));
    }

    /// <summary>
    /// 只剩周额度时没有任何窗口算数 —— 调用方得自己按「压力 0」处理。
    /// </summary>
    /// <remarks>
    /// <c>ClaudeUsageMeter</c> 靠这个返回 <c>null</c> 走「没有参与判定的窗口」那一支。
    /// 早先它拿 <c>gating[0]</c> 兜底，那会把周额度的原始读数当成压力报出去 ——
    /// 正是这条规则要避免的事。
    /// </remarks>
    [Fact]
    public void Nothing_binds_when_only_the_week_is_live()
        => Assert.Null(UsagePressure.Tightest([Week(0.50)], Local(2026, 9, 9, 12, 0)));

    /// <summary>但真的快耗尽时必须出局，否则接到的每一个都会失败。</summary>
    [Fact]
    public void A_nearly_exhausted_week_still_takes_the_node_out()
        => Assert.False(HasHeadroom([Session(0.10), Week(0.96)], Local(2026, 9, 9, 12, 0)));

    /// <summary>
    /// 配速随工作日推进放宽：一周的额度给五个工作日花，到第 N 天就该只花掉 N/5。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 现在没人调用它（<see cref="UsagePressure.WeeklyCeilingOnly"/> 直接给硬顶），
    /// 但这套算术要留着能用 —— 所以照常测。周窗口是周一 01:59 到周一 01:59，
    /// 五个工作日整，走完的工作日按小时平滑计入，再加一个工作日的余量。
    /// </para>
    /// <para>
    /// 时区不传 = 本机，跟 <c>WeekReset</c> 同一把尺子，所以 CI 在 UTC 上也是这些数。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(9, 7, 9, 0, 0.2585)]    // 周一上午：余量那一个工作日几乎就是全部
    [InlineData(9, 9, 12, 0, 0.6835)]   // 周三中午：2.42 + 1 个工作日
    [InlineData(9, 10, 12, 0, 0.8835)]  // 周四中午
    [InlineData(9, 11, 12, 0, 0.95)]    // 周五中午：算出来 1.08，被硬顶压住
    public void The_pace_gate_tracks_the_working_week(
        int month, int day, int hour, int minute, double expected)
        => Assert.Equal(
            expected,
            UsagePressure.PaceGate(WeekReset, Local(2026, month, day, hour, minute)),
            4);

    /// <summary>
    /// 允许超前一个工作日，超前两天就歇着。
    /// </summary>
    /// <remarks>
    /// 没有这个余量的话，周一上午跑一次大评审就立刻越线 —— 那时配速几乎还是 0。
    /// </remarks>
    [Fact]
    public void The_pace_gate_allows_one_working_day_of_running_ahead()
    {
        // 周一早上配速≈0，余量一个工作日 = 20%。
        var gate = UsagePressure.PaceGate(WeekReset, Local(2026, 9, 7, 9, 0));

        Assert.True(gate > 0.18);
        Assert.True(gate < 0.42);
    }

    /// <summary>周末不再按配速放宽，但硬顶还在。</summary>
    /// <remarks>
    /// 五个工作日走完之后配速到 1.0，若没有 <see cref="UsagePressure.WeeklyCeiling"/>，
    /// 一台已经用掉 99% 的机器会被判成「还能接」。
    /// </remarks>
    [Fact]
    public void The_weekend_still_honours_the_hard_ceiling()
    {
        var saturday = Local(2026, 9, 12, 12, 0);

        Assert.True(HasHeadroom([Session(0), Week(0.90)], saturday));
        Assert.False(HasHeadroom([Session(0), Week(0.99)], saturday));
    }

    /// <summary>按模型细分的子额度打满，不该妨碍用别的模型评审。</summary>
    [Fact]
    public void A_model_scoped_sub_quota_never_gates()
    {
        var fable = new UsageWindow("seven_day:Fable", 1.0, WeekReset) { Gates = false };
        var now = Local(2026, 9, 9, 12, 0);

        Assert.True(HasHeadroom([Session(0.10), Week(0.10), fable], now));
    }

    /// <summary>拿不到重置时刻就算不出配速，退回硬顶而不是不设限。</summary>
    [Fact]
    public void Without_a_reset_time_the_pace_gate_falls_back_to_the_ceiling()
        => Assert.Equal(
            UsagePressure.WeeklyCeiling,
            UsagePressure.PaceGate(null, Local(2026, 9, 9, 12, 0)),
            4);

    /// <summary>【临时】周额度的线就是硬顶，跟今天周几无关。</summary>
    [Fact]
    public void The_weeks_line_is_the_hard_ceiling_on_every_day()
    {
        Assert.Equal(
            UsagePressure.WeeklyCeiling,
            UsagePressure.GateFor(Week(0.5), Local(2026, 9, 9, 12, 0)),
            4);
        Assert.Equal(
            UsagePressure.WeeklyCeiling,
            UsagePressure.GateFor(Week(0.5), Local(2026, 9, 7, 9, 0)),
            4);
    }

    /// <summary>没见过的窗口键按会话窗口处理 —— 保守方向。</summary>
    [Fact]
    public void An_unknown_window_key_is_gated_like_a_session()
        => Assert.Equal(
            UsagePressure.SessionGate,
            UsagePressure.GateFor(new UsageWindow("one_month", 0.5, WeekReset), Local(2026, 9, 9)),
            4);
}

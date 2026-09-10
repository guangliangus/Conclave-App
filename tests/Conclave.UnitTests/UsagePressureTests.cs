using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;

namespace Conclave.UnitTests;

/// <summary>
/// 各额度窗口各有各的阈值，周额度按工作日配速。
/// </summary>
/// <remarks>
/// 两个方向都要钉住，因为它们的失败方式一样静默：
/// 太紧 → 周三就把节点关到周末，mesh 白养一台机器；
/// 太松 → 周额度耗尽的机器继续吸席位，一张张出 Error 票。
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
    /// 周额度 80% 不再是一判到底的死刑，而是看走到了工作周的哪一天。
    /// </summary>
    /// <remarks>
    /// 旧规则 <c>max(5h, 7d) &gt;= 0.8</c> 下，周额度一碰 80% 就出局，而且要一直出局到
    /// 本周重置 —— 周三碰上就白歇四天。配速规则把它变成一个跟日历有关的判断：
    /// <list type="bullet">
    /// <item>周三中午配速才 2.42/5，用掉 80% 是超前 1.6 个工作日 —— 超出一天的余量，节流。</item>
    /// <item>周五中午配速 4.42/5，同样 80% 就是正常进度 —— 放行。</item>
    /// </list>
    /// 这正是「按工作日比例」想要的：管的是<b>烧得太快</b>，不是<b>烧得太多</b>。
    /// </remarks>
    [Fact]
    public void Eighty_percent_of_the_week_throttles_on_wednesday_but_passes_on_friday()
    {
        Assert.False(HasHeadroom([Session(0.10), Week(0.80)], Local(2026, 9, 9, 12, 0)));
        Assert.True(HasHeadroom([Session(0.10), Week(0.80)], Local(2026, 9, 11, 12, 0)));
    }

    /// <summary>
    /// 旧规则真正的病：出局之后到本周重置为止都出局。配速规则会自己走出来。
    /// </summary>
    /// <remarks>
    /// 同一个用量不再随时间恶化 —— 用量不动、日子往前走，阈值自己涨上来。
    /// 旧规则下这条曲线是平的：一旦越线，只能等重置。
    /// </remarks>
    [Fact]
    public void A_throttled_node_recovers_as_the_week_advances_without_using_less()
    {
        var stuck = new[] { Session(0.05), Week(0.70) };

        Assert.False(HasHeadroom(stuck, Local(2026, 9, 8, 12, 0)));   // 周二
        Assert.True(HasHeadroom(stuck, Local(2026, 9, 10, 12, 0)));   // 周四，用量一点没变
    }

    /// <summary>但真的快耗尽时必须出局，否则接到的每一个都会失败。</summary>
    [Fact]
    public void A_nearly_exhausted_week_still_takes_the_node_out()
        => Assert.False(HasHeadroom([Session(0.10), Week(0.96)], Local(2026, 9, 9, 12, 0)));

    /// <summary>
    /// 配速随工作日推进放宽：同一个周用量，周一嫌多、周四就正常。
    /// </summary>
    /// <remarks>
    /// 这是「按工作日比例」的全部意思 —— 一周的额度给五个工作日花，
    /// 到第 N 天就该只花掉 N/5。周一烧掉 55% 是失控，周四烧掉 55% 是正常进度。
    /// </remarks>
    [Fact]
    public void The_same_weekly_usage_is_reckless_on_monday_and_fine_on_thursday()
    {
        var monday = Local(2026, 9, 7, 12, 0);
        var thursday = Local(2026, 9, 10, 12, 0);

        Assert.False(HasHeadroom([Session(0.05), Week(0.55)], monday));
        Assert.True(HasHeadroom([Session(0.05), Week(0.55)], thursday));
    }

    /// <summary>
    /// 允许超前一个工作日，超前两天就歇着。
    /// </summary>
    /// <remarks>
    /// 没有这个余量的话，周一上午跑一次大评审就立刻越线 —— 那时配速几乎还是 0。
    /// </remarks>
    [Fact]
    public void One_working_day_of_running_ahead_is_allowed_two_is_not()
    {
        var mondayMorning = Local(2026, 9, 7, 9, 0);

        // 周一早上配速≈0，余量一个工作日 = 20%。
        Assert.True(HasHeadroom([Session(0), Week(0.18)], mondayMorning));
        Assert.False(HasHeadroom([Session(0), Week(0.42)], mondayMorning));
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
    public void Without_a_reset_time_the_week_falls_back_to_the_ceiling()
    {
        var now = Local(2026, 9, 9, 12, 0);
        var unknown = new UsageWindow("seven_day", 0.90, null);

        Assert.Equal(UsagePressure.WeeklyCeiling, UsagePressure.GateFor(unknown, now), 4);
    }

    /// <summary>没见过的窗口键按会话窗口处理 —— 保守方向。</summary>
    [Fact]
    public void An_unknown_window_key_is_gated_like_a_session()
        => Assert.Equal(
            UsagePressure.SessionGate,
            UsagePressure.GateFor(new UsageWindow("one_month", 0.5, WeekReset), Local(2026, 9, 9)),
            4);
}

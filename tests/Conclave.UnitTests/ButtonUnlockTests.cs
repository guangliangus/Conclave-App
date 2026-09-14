using Conclave.App.ViewModels;

namespace Conclave.UnitTests;

/// <summary>
/// 点过的按钮先灰掉防连点，到点再自己放开。
/// </summary>
/// <remarks>
/// <para>
/// 灰掉那一半本来就有（<c>MarkJustDone</c> 打个乐观标记，<see cref="PrRow"/> 据此
/// 把动作置灰）。缺的是<b>放开</b>：清理写在 <c>Refresh</c> 里，而 <c>Refresh</c> 得有人触发 ——
/// 通用的 live 计时器在这个场景下根本不转（它的判据是「有行在显示时长或指派提示」，
/// 而刚点「评审」的那一行两样都没有），就算转，30 秒的间隔也比 20 秒的标记寿命还长。
/// 于是按钮灰下去之后要等一个不相干的事件顺手带一次重画才放开。
/// </para>
/// <para>
/// 这一组钉的是那枚一次性闹钟该定在几点。计时器本身要 Avalonia 的 dispatcher，
/// 测不了；能错的是这段算术，所以把它单拎出来。
/// </para>
/// </remarks>
public sealed class ButtonUnlockTests
{
    private static readonly DateTimeOffset Now = TestElectors.Now;

    /// <summary>一条标记都没有就不必醒。</summary>
    [Fact]
    public void Nothing_marked_schedules_nothing()
        => Assert.Null(MainViewModel.UnlockDelay([], Now));

    /// <summary>
    /// 刚点过的那一条，等它自己的寿命。
    /// </summary>
    /// <remarks>
    /// 20 秒 = 编排间隔（15 秒）+ 5 秒余量：早于后台转一圈就放开的话，
    /// 按钮会先亮一下再被真实状态改回去，闪一下比一直灰着更像坏了。
    /// </remarks>
    [Fact]
    public void A_fresh_mark_waits_out_its_full_lifetime()
    {
        var delay = MainViewModel.UnlockDelay([Now], Now);

        Assert.Equal(TimeSpan.FromSeconds(20), delay);
    }

    /// <summary>已经等掉一半的，只等剩下那一半。</summary>
    [Fact]
    public void A_half_aged_mark_only_waits_out_the_remainder()
    {
        var delay = MainViewModel.UnlockDelay([Now.AddSeconds(-12)], Now);

        Assert.Equal(TimeSpan.FromSeconds(8), delay);
    }

    /// <summary>
    /// 几行同时灰着时，按<b>最早</b>那条定闹钟。
    /// </summary>
    /// <remarks>
    /// 按最晚那条定的话，先点的那一行要陪着后点的一起等 —— 它早就该能点了。
    /// </remarks>
    [Fact]
    public void The_earliest_mark_sets_the_alarm()
    {
        var delay = MainViewModel.UnlockDelay(
            [Now.AddSeconds(-2), Now.AddSeconds(-15), Now.AddSeconds(-7)], Now);

        // 最早那条已经过了 15 秒，还剩 5 秒。
        Assert.Equal(TimeSpan.FromSeconds(5), delay);
    }

    /// <summary>
    /// 已经过期的要立刻响，不是「不排」。
    /// </summary>
    /// <remarks>
    /// 返回 null 的话这条标记就再也没人来清，按钮永久灰着 —— 正是这次要修的那个毛病。
    /// 而 DispatcherTimer.Interval 不收非正数，所以给一个最小正值。
    /// </remarks>
    [Fact]
    public void An_already_expired_mark_fires_immediately_instead_of_never()
    {
        var delay = MainViewModel.UnlockDelay([Now.AddMinutes(-5)], Now);

        Assert.NotNull(delay);
        Assert.True(delay > TimeSpan.Zero, "DispatcherTimer.Interval 不收非正数");
        Assert.True(delay < TimeSpan.FromSeconds(1), $"该立刻响，却排到了 {delay}");
    }

    /// <summary>时钟往回跳也不能排出一个荒唐的长等待。</summary>
    [Fact]
    public void A_mark_from_the_future_still_waits_at_most_one_lifetime()
    {
        // NTP 校时、休眠唤醒都能让「点击时刻」落在未来一点点。
        var delay = MainViewModel.UnlockDelay([Now.AddSeconds(3)], Now);

        Assert.NotNull(delay);
        Assert.True(
            delay <= TimeSpan.FromSeconds(23),
            $"最多一个寿命加上那点偏差，却排到了 {delay}");
    }
}

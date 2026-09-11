using System.Runtime.Versioning;
using Conclave.App.Interop;

namespace Conclave.UnitTests;

/// <summary>
/// <see cref="TrayAnimator.FrameAt"/>：拍号折成帧号。
/// </summary>
/// <remarks>
/// 菜单栏的呼吸帧只存半个周期（全睁 → 最眯），回程靠这个折返走回去。差一格不会报错，
/// 只会在最睁或最眯那一帧上卡半拍 —— 表现是「呼吸时抖一下」，一个只能靠盯着菜单栏
/// 才发现的毛病。整个 <see cref="TrayAnimator"/> 里唯一算得上逻辑的就是这一行。
/// <para>
/// 类上的平台标注只为让 CA1416 闭嘴：被测的是纯函数，不碰 AppKit，任何系统上都跑得了。
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public class TrayAnimatorTests
{
    /// <summary>7 张帧 = 12 拍一轮，两端各只出现一次。</summary>
    [Fact]
    public void A_full_cycle_hits_each_end_exactly_once()
    {
        const int frames = 7;
        var cycle = Enumerable.Range(0, 2 * (frames - 1))
            .Select(tick => TrayAnimator.FrameAt(tick, frames))
            .ToArray();

        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 5, 4, 3, 2, 1], cycle);
    }

    /// <summary>每一拍只挪一格：跳帧在 18pt 上就是一次可见的抽动。</summary>
    [Fact]
    public void Consecutive_ticks_move_one_frame()
    {
        const int frames = 7;
        const int period = 2 * (frames - 1);

        for (var tick = 0; tick < period; tick++)
        {
            var here = TrayAnimator.FrameAt(tick, frames);
            var next = TrayAnimator.FrameAt((tick + 1) % period, frames);
            Assert.Equal(1, Math.Abs(next - here));
        }
    }

    /// <summary>帧号永远落在数组里 —— 越界就是评审中直接崩一次。</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(12)]
    public void Any_frame_count_stays_in_range(int frames)
    {
        for (var tick = 0; tick < 2 * (frames - 1); tick++)
        {
            Assert.InRange(TrayAnimator.FrameAt(tick, frames), 0, frames - 1);
        }
    }
}

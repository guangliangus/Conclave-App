namespace Conclave.Application;

/// <summary>
/// 让跑着的循环跟上热更新后的间隔。
/// </summary>
/// <remarks>
/// <see cref="PeriodicTimer"/> 只在构造时读一次间隔，而后台循环都是
/// <c>using var timer = new PeriodicTimer(options.X)</c> 起手、跑到进程结束。
/// 不在每轮同步一次的话，间隔类配置改了要等重启才换节奏 —— 而那正是
/// <see cref="ConfigHotReload"/> 想消掉的那种「改了没反应」。
/// </remarks>
public static class LoopInterval
{
    /// <summary>把 <paramref name="timer"/> 的间隔调成 <paramref name="wanted"/>。</summary>
    /// <remarks>
    /// 非正的间隔直接忽略：<see cref="PeriodicTimer.Period"/> 的 setter 对 0 和负数会抛，
    /// 而配置里写错一个 0 不该把后台循环整条带倒 —— 那比不生效严重得多。
    /// </remarks>
    public static void Follow(PeriodicTimer timer, TimeSpan wanted)
    {
        ArgumentNullException.ThrowIfNull(timer);

        if (wanted > TimeSpan.Zero && timer.Period != wanted)
        {
            timer.Period = wanted;
        }
    }
}

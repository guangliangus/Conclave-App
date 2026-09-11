using System.Runtime.Versioning;
using Avalonia.Threading;

namespace Conclave.App.Interop;

/// <summary>
/// 菜单栏图标的逐帧动画：一个定时器按拍换图，就这么多。
/// </summary>
/// <remarks>
/// <para>
/// macOS 没有「会动的状态栏图标」这种东西 —— <c>NSStatusItem</c> 只认一张 <c>NSImage</c>，
/// 动画只能是定时换图。（顺手记一条弯路：animated GIF 塞进去不会自己动，
/// <c>NSStatusBarButton</c> 只画静态帧，它不是 <c>NSImageView</c>。）
/// </para>
/// <para>
/// 另一条路是给 button 开 <c>wantsLayer</c>、拿 <c>CABasicAnimation</c> 转 transform：
/// 一张图就够，动画跑在 CoreAnimation 那边，主线程零开销。没走是因为它要从 P/Invoke
/// 过 <c>CATransform3D</c> 的结构体 ABI —— 而这一层<b>没有编译期保障</b>，
/// 结构体传参正是其中最难验的部分（见 <see cref="MacStatusItem"/> 里关于 stret 的那段）。
/// 换图只用已经验过的那几个签名。
/// </para>
/// <para>
/// 帧只有半个周期（全睁 → 最眯），回程倒着放 —— 呼吸是对称的，存满一圈就是 5 张
/// 逐字节重复的 PNG。所以一轮是 <c>2*(N-1)</c> 拍，不是 N 拍。
/// </para>
/// <para>
/// ⚠️ 跟 <see cref="MacStatusItem"/> 一样，所有方法只能在 UI 线程上调。
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class TrayAnimator
{
    private readonly MacStatusItem _item;
    private readonly string[] _frames;
    private readonly DispatcherTimer _timer;

    /// <summary>周期内的拍号，<c>0 .. 2*(N-1)-1</c>。不是帧号 —— 回程那一半要折过来。</summary>
    private int _tick;

    /// <summary>
    /// 起来之后一共响了多少拍。
    /// </summary>
    /// <remarks>
    /// 给 <c>conclave tray-selftest</c> 用：帧读得出来<b>不等于</b>动画在动。定时器优先级
    /// 建错、或者压根没 Start，表现都是「图标停在第一帧」—— 那跟静态图标肉眼分不出来。
    /// </remarks>
    internal int Ticks { get; private set; }

    /// <param name="item">要换图的菜单栏图标。</param>
    /// <param name="frames">半个周期的帧路径，按顺序；第一帧是静止时也说得过去的那张。</param>
    /// <param name="interval">帧间隔。</param>
    /// <exception cref="InvalidOperationException">某一帧读不出来（构造时就全读一遍）。</exception>
    internal TrayAnimator(MacStatusItem item, IReadOnlyList<string> frames, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(frames);

        if (frames.Count < 2)
        {
            throw new ArgumentException("至少要两帧才谈得上动画", nameof(frames));
        }

        _item = item;
        _frames = [.. frames];

        // 在这里就把每一帧读进缓存：调用方还在 OnFrameworkInitializationCompleted 的
        // try 里，读不出来能退回静态图标；等到定时器回调里才发现就只能是未捕获异常了。
        _item.PreloadIcons(_frames);

        // 优先级用 Normal 而不是 Background。Background 排在 Input/Render 后面，UI 一忙
        // 就被饿着 —— 实测（conclave tray-selftest 打的拍数）主面板正在建的那 280ms 里
        // 12fps 的定时器只响了 1 拍，看起来就是「评审一开始图标先卡住」。
        // 让它抢这点资源是划算的：一拍的活就是一次 setImage:，抢不走任何东西。
        _timer = new DispatcherTimer(interval, DispatcherPriority.Normal, OnTick);
    }

    /// <summary>开始呼吸。已经在跑就什么都不做（每轮编排都会发一次状态变更）。</summary>
    internal void Start()
    {
        if (_timer.IsEnabled)
        {
            return;
        }

        _tick = 0;
        _item.SetIcon(_frames[0]);
        _timer.Start();
    }

    /// <summary>停下，并把图标落回 <paramref name="restPath"/>。停着的时候不占 CPU。</summary>
    internal void Stop(string restPath)
    {
        _timer.Stop();
        _item.SetIcon(restPath);
    }

    /// <summary>
    /// 拍号 → 帧号：去程原样，回程折回来。
    /// </summary>
    /// <remarks>
    /// N=7 时一轮 12 拍，帧号走 0 1 2 3 4 5 6 5 4 3 2 1 再回到 0 —— 两端都不重复，
    /// 差一格就是在最睁或最眯那一帧上卡半拍，看起来是「呼吸时抖了一下」。
    /// 纯函数，<c>TrayAnimatorTests</c> 逐拍对过。
    /// </remarks>
    internal static int FrameAt(int tick, int frameCount)
    {
        var period = 2 * (frameCount - 1);
        return tick < frameCount ? tick : period - tick;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // 定时器回调里抛出去就是 UI 线程上的未捕获异常 —— 为一个眨眼把进程带走不值。
        // 真出事就停掉动画，图标停在最后一帧，后台的轮询与评审一点不受影响。
        try
        {
            _tick = (_tick + 1) % (2 * (_frames.Length - 1));
            _item.SetIcon(_frames[FrameAt(_tick, _frames.Length)]);
            Ticks++;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _timer.Stop();
            Console.Error.WriteLine("菜单栏动画停了（图标还在，只是不动）：" + ex.Message);
        }
    }
}

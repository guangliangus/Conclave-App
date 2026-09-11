using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Conclave.App.Interop;
using Conclave.Application;
using Conclave.App.ViewModels;
using Conclave.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Conclave.App;

/// <remarks>
/// 基类刻意写全限定名。分层命名空间 <c>Conclave.Application</c> 是外层 <c>Conclave</c>
/// 的成员，而 C# 名称解析里外层命名空间成员的优先级高于编译单元级的 using 别名 ——
/// 所以 <c>using Application = Avalonia.Application;</c> 在这里是无效的，只能全限定。
/// </remarks>
public partial class App : global::Avalonia.Application
{
    /// <summary>菜单栏两张模板图：空闲闭眼打盹，评审中睁眼。</summary>
    private const string TrayIdleAsset = "avares://conclave/Assets/conclave-tray-idle.png";
    private const string TrayBusyAsset = "avares://conclave/Assets/conclave-tray-busy.png";

    /// <summary>
    /// 「评审中」那套呼吸帧的张数（<c>conclave-tray-busy-0..6.png</c>）。
    /// </summary>
    /// <remarks>
    /// 只有半个周期（全睁 → 最眯），回程由 <see cref="TrayAnimator"/> 倒着放。
    /// 跟 <c>scripts/make-icon.py</c> 里的 <c>TRAY_FRAMES</c> 对齐 —— 那边加减帧，这里要跟。
    /// </remarks>
    private const int TrayBusyFrameCount = 7;

    /// <summary>
    /// 呼吸动画的帧间隔。
    /// </summary>
    /// <remarks>
    /// 80ms × 12 拍 ≈ 一秒一次呼吸，看着像在读东西而不是在抽搐。再快没有收益：
    /// 18pt 上眼睑总共也就动两三个像素，只是白烧电。
    /// </remarks>
    private static readonly TimeSpan TrayFrameInterval = TimeSpan.FromMilliseconds(80);

    /// <summary>
    /// 「点击之前面板还是前台窗口」的判定窗口。
    /// </summary>
    /// <remarks>
    /// 点菜单栏图标会先把键盘焦点交给状态项，<b>然后</b>才触发点击 —— 走到点击处理里时
    /// <c>IsActive</c> 已经是 <c>false</c> 了。只看它的话，「面板在前台时再点一下收起」
    /// 这条永远不成立，图标就变成只能开不能关。所以再认一条「刚刚才失焦」：
    /// <c>_deactivatedAt</c> 落在这个窗口内，说明失焦就是这一下点击造成的。
    /// <para>
    /// 200ms 不够（低端机上两个事件间隔能到 150ms 以上），500ms 又会吃掉真实的连续两次点击。
    /// </para>
    /// </remarks>
    private static readonly TimeSpan ToggleGrace = TimeSpan.FromMilliseconds(350);

    private MacStatusItem? _statusItem;
    private TrayAnimator? _trayAnimator;
    private TrayIcon? _tray;

    /// <summary>解到磁盘上的两张托盘图的路径（macOS 的 NSImage 走文件）。</summary>
    private string? _trayIdlePath;
    private string? _trayBusyPath;

    /// <summary>菜单栏当前画的是哪张，换图只在真的变了时做。</summary>
    private bool? _trayShowsBusy;
    private MainWindow? _dashboard;
    private DateTimeOffset _deactivatedAt = DateTimeOffset.MinValue;
    private bool _quitting;

    /// <summary>
    /// 由 <see cref="Program"/> 在启动 Avalonia 之前塞进来。
    /// </summary>
    /// <remarks>
    /// Avalonia 的 <c>AppBuilder.Configure&lt;App&gt;()</c> 自己 new App，拿不到构造函数注入，
    /// 所以只能用静态属性把容器递进来。
    /// </remarks>
    public static IServiceProvider? Services { get; set; }

    /// <summary>
    /// 自检模式：建好图标后程序化点一下，验证整条通路，然后退出。
    /// </summary>
    /// <remarks>
    /// 由 <c>conclave tray-selftest</c> 置位。见 <see cref="RunSelfTest"/>。
    /// </remarks>
    public static bool SelfTest { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// 菜单栏常驻：启动<b>不</b>开窗口，只挂一个状态栏图标。
    /// </summary>
    /// <remarks>
    /// 三件事必须一起做，少一件就会「面板关了再也叫不回来」：
    /// <list type="number">
    /// <item>
    /// <see cref="ShutdownMode.OnExplicitShutdown"/> —— 默认 <c>OnLastWindowClose</c>，
    /// 关掉面板整个进程就退了，后台的轮询和编排跟着死。
    /// </item>
    /// <item>
    /// 不设 <c>desktop.MainWindow</c> —— 设了 <c>StartWithClassicDesktopLifetime</c>
    /// 会在启动时把它 <c>Show()</c> 出来。窗口改成第一次点图标时才建。
    /// </item>
    /// <item>
    /// 关窗只是隐藏 —— 不进 Dock 之后，真关掉的窗口没有别的入口能打开。
    /// </item>
    /// </list>
    /// <para>
    /// 还有一条隐含的：accessory 应用没有自己的菜单栏，<b><c>Cmd+Q</c> 不再有效</b>，
    /// 而点击图标现在直接出主面板、没有右键菜单 —— 所以<b>退出的唯一入口在面板顶栏上</b>
    /// （见 <see cref="RequestQuit"/>）。少了它就只能去活动监视器杀进程。
    /// </para>
    /// </remarks>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            if (OperatingSystem.IsMacOS())
            {
                // 建不起来就没有图标（也就没法开面板），但后台的轮询与评审照常跑 ——
                // 那才是这个进程的本职。让它把整个进程带走是最差的结果：
                // 实测过一次（托盘图标文件被别的节点占着），三个节点死了两个。
                try
                {
                    _trayIdlePath = ExtractTrayIcon(TrayIdleAsset, "tray-idle.png");
                    _trayBusyPath = ExtractTrayIcon(TrayBusyAsset, "tray-busy.png");
                    _statusItem = new MacStatusItem(_trayIdlePath, "Conclave · 空闲");
                    _statusItem.Clicked += ToggleDashboard;

                    // 动画是锦上添花。帧解不出来就退回原来的静态两张 ——
                    // 图标和点击照常，只是评审中不会眨眼。不值得为它把入口整个丢掉。
                    try
                    {
                        _trayAnimator = new TrayAnimator(
                            _statusItem, ExtractBusyFrames(), TrayFrameInterval);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                                  or InvalidOperationException or ArgumentException)
                    {
                        Console.Error.WriteLine("呼吸帧加载失败，评审中改用静态图标：" + ex.Message);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                              or InvalidOperationException or ExternalException)
                {
                    Console.Error.WriteLine(
                        "菜单栏图标建不起来，本节点只在后台跑（没有界面入口）：" + ex.Message);
                }
            }
            else
            {
                // Win32 / 部分 Linux DE 上 Avalonia 自己的 TrayIcon 能收到点击，
                // 没必要为它们再写一层原生互操作。
                _tray = new TrayIcon
                {
                    Icon = new WindowIcon(AssetLoader.Open(new Uri(TrayIdleAsset))),
                    ToolTipText = "Conclave · 空闲",
                };
                _tray.Clicked += (_, _) => ToggleDashboard();
            }

            WatchReviewingState();

            if (SelfTest)
            {
                RunSelfTest(desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 点一下图标：没开就开，在前台就收起，被别的窗口压住就提到前面。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「被压住时提到前面」这一条不能省。面板<b>不</b>失焦隐藏（理由见
    /// <see cref="ShowDashboard"/>），所以它可以是「可见但在别人后面」——
    /// 这种状态下人点图标要的是把它翻上来，而不是把一个自己看不见的窗口关掉。
    /// </para>
    /// <para>
    /// 原先图标点出来的是一个 440 宽的浮层，要在它的页脚再点一下「任务池」才到主面板。
    /// 浮层上那些东西（状态、今日/累计、额度条、两个开关）主面板顶栏与那条额度里全都有，
    /// 于是那一跳纯粹是多一次点击，浮层已经退役。
    /// </para>
    /// </remarks>
    private void ToggleDashboard()
    {
        var frontmost = _dashboard is { IsVisible: true }
            && (_dashboard.IsActive || DateTimeOffset.UtcNow - _deactivatedAt < ToggleGrace);

        if (frontmost)
        {
            _dashboard!.Hide();
            return;
        }

        ShowDashboard();
    }

    /// <summary>
    /// 打开主面板：PR 队列 / 我的 PR / 评审记录 / Acta / 在线节点。
    /// </summary>
    /// <remarks>
    /// <b>它不失焦隐藏</b>：这是一张要在上面比对几十行数据的表，切出去查个 PR
    /// 回来发现它自己关了是纯粹添乱。标题栏那个 x 也只是隐藏、不销毁 ——
    /// 关掉之后轮询与评审在后台继续跑，而不进 Dock 之后真关掉的窗口没有别的入口能再打开。
    /// </remarks>
    internal void ShowDashboard()
    {
        if (_dashboard is null)
        {
            _dashboard = new MainWindow { DataContext = Services?.GetRequiredService<MainViewModel>() };
            _dashboard.Closing += OnDashboardClosing;
            _dashboard.Deactivated += OnDashboardDeactivated;

            // 只在建窗口时定位一次。之后每次点图标都重新摆一遍的话，
            // 人自己挪过、缩放过的位置会被悄悄改回去。
            PositionDashboard(_dashboard);
        }

        _dashboard.Show();

        if (OperatingSystem.IsMacOS())
        {
            // accessory 应用光 Show 不会被系统切到前台，窗口会出现在别人后面。
            MacStatusItem.ActivateApp();
        }

        _dashboard.Activate();
    }

    /// <summary>
    /// 记一下失焦时刻。
    /// </summary>
    /// <remarks>
    /// 只记时刻，<b>不</b>隐藏 —— 它是 <see cref="ToggleGrace"/> 那条判据的唯一输入：
    /// 「点图标的时候面板是不是还在前台」。
    /// </remarks>
    private void OnDashboardDeactivated(object? sender, EventArgs e)
        => _deactivatedAt = DateTimeOffset.UtcNow;

    private void OnDashboardClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting)
        {
            return;
        }

        e.Cancel = true;
        (sender as Window)?.Hide();
    }

    /// <summary>
    /// 把主面板居中摆到<b>点击那块屏</b>上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 多屏下「面板出现在主屏」是错的 —— 人在副屏上点的图标，面板得在副屏上出来。
    /// 判断依据是点击瞬间的鼠标位置（见 <see cref="MacStatusItem.MouseLocation"/>）：
    /// 那一刻鼠标就在图标上，所以它同时回答了「图标在哪」和「点在哪块屏」。
    /// </para>
    /// <para>
    /// 居中而不是像原先的浮层那样吊在图标底下：1280×840 的窗口贴着菜单栏右上角摆，
    /// 会有一大半探到屏幕外面去。<see cref="Screen.WorkingArea"/> 已按屏排除了菜单栏与
    /// Dock，所以居中算出来的位置天然不会压在它们下面。
    /// </para>
    /// </remarks>
    private static void PositionDashboard(Window window)
    {
        var screens = window.Screens;
        var anchor = ClickAnchor(screens);

        var screen = (anchor is null ? null : screens.ScreenFromPoint(anchor.Value))
            ?? screens.Primary
            ?? screens.All.FirstOrDefault();

        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var width = (int)(window.Width * screen.Scaling);
        var height = (int)(window.Height * screen.Scaling);

        // 窗口比工作区还大时（小屏 + 1280×840）取 0：宁可下沿被裁掉，
        // 也不能算出一个负偏移把标题栏推到屏幕上方去 —— 那时窗口就抓不住了。
        var x = area.X + Math.Max(0, (area.Width - width) / 2);
        var y = area.Y + Math.Max(0, (area.Height - height) / 2);

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(x, y);
    }

    /// <summary>
    /// 点击位置，换算到 Avalonia 的屏幕坐标系。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两套坐标系的差别（<c>conclave tray-selftest</c> 会把两边的真实数字都打出来）：
    /// </para>
    /// <list type="bullet">
    /// <item>Cocoa：pt，原点在<b>主屏左下</b>，Y 向上。</item>
    /// <item>Avalonia：原点在<b>主屏左上</b>，Y 向下；主屏的 <c>Bounds.Y</c> 恒为 0，
    /// 排在主屏上方的副屏 <c>Bounds.Y</c> 是负数（实测 -1080）。</item>
    /// </list>
    /// <para>
    /// 所以换算就是一次翻转。<b>实测 Avalonia 在 macOS 上报的 Bounds 是 pt 而不是 px</b>
    /// （内置屏物理 3024x1964，它报 1512x982，<c>Scaling</c> 是 1），于是这里的
    /// <c>* Scaling</c> 当下是个恒等变换 —— 留着是为了万一将来 Avalonia 改成报真实像素
    /// （那时 <c>Scaling</c> 会变成 2）这段仍然成立。真出了偏移，先跑自检看那两行数。
    /// </para>
    /// </remarks>
    private static PixelPoint? ClickAnchor(Screens screens)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        var (cocoaX, cocoaY) = MacStatusItem.MouseLocation();
        return ToAvalonia(screens, cocoaX, cocoaY);
    }

    /// <summary>Cocoa 全局坐标 → Avalonia 屏幕坐标。纯函数，自检拿它做往返验算。</summary>
    private static PixelPoint? ToAvalonia(Screens screens, double cocoaX, double cocoaY)
    {
        if (screens.Primary is not { } primary)
        {
            return null;
        }

        var scale = primary.Scaling;
        return new PixelPoint(
            (int)Math.Round(cocoaX * scale),
            (int)Math.Round(primary.Bounds.Height - (cocoaY * scale)));
    }

    /// <summary><see cref="ToAvalonia"/> 的逆变换。只有自检用 —— 用来造出「在某块屏上点了一下」。</summary>
    private static (double X, double Y) ToCocoa(Screens screens, PixelPoint point)
    {
        var scale = screens.Primary?.Scaling ?? 1;
        var height = screens.Primary?.Bounds.Height ?? 0;
        return (point.X / scale, (height - point.Y) / scale);
    }

    /// <summary>
    /// 真正退出。面板顶栏那个「退出」按钮唯一的去处。
    /// </summary>
    /// <remarks>
    /// 必须走 <c>desktop.Shutdown()</c> 而不是 <c>Environment.Exit</c> ——
    /// 前者会让 <c>Program.Main</c> 的 <c>finally</c> 跑到，后台服务有 5 秒收尾，
    /// 正在跑的 claude 子进程被取消而不是被砍。
    /// </remarks>
    internal void RequestQuit()
    {
        _quitting = true;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    /// <summary>
    /// 把嵌入的模板图落到磁盘 —— <c>NSImage initWithContentsOfFile:</c> 要一个真实路径。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 走文件而不是 <c>initWithData:</c> + <c>NSData dataWithBytes:length:</c>：
    /// 后者要多一个带指针和 <c>nuint</c> 的 msgSend 签名，而这层的每一个签名都是
    /// 没有编译期保障的。
    /// </para>
    /// <para>
    /// <b>路径必须每个进程一份。</b> 原先是固定的 <c>$TMPDIR/conclave-tray.png</c>：
    /// 同时起多个节点时（<c>scripts/dev-cluster.sh ui 3</c>）<c>File.Create</c>
    /// 用 <c>FileShare.None</c> 打开，抢不到的进程直接 <see cref="IOException"/> ——
    /// 而它在 <c>OnFrameworkInitializationCompleted</c> 里没人接，进程当场崩。
    /// 实测三个节点起来两个死掉，界面上表现成「起了 3 个只看到 1 个图标」。
    /// </para>
    /// <para>
    /// 优先落在本节点自己的 HomeDirectory 里：那本来就是一节点一份，跟着节点数据一起清掉，
    /// 不在临时目录里堆垃圾。取不到配置时退回临时目录 + 进程号。
    /// </para>
    /// </remarks>
    /// <summary>
    /// 菜单栏图标跟着「本节点在不在评」换脸。
    /// </summary>
    /// <remarks>
    /// 订的是 <see cref="NodeState.Changed"/> 而不是 ViewModel 的属性：面板可能一次都没打开过
    /// （ViewModel 是第一次点图标才建的），而菜单栏图标从进程起来就该是对的。
    /// 事件在后台线程上来，换图必须回 UI 线程；没变就不动 —— 每轮编排都会发一次 Changed。
    /// 「评审中」在 macOS 上不是一张静态图而是一段呼吸动画（见 <see cref="TrayAnimator"/>），
    /// 空闲时定时器是停的，不占 CPU。
    /// </remarks>
    private void WatchReviewingState()
    {
        var state = Services?.GetService<NodeState>();
        if (state is null)
        {
            return;
        }

        state.Changed += (_, _) =>
        {
            var busy = state.Reviewing;
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyTrayState(busy));
        };
        ApplyTrayState(state.Reviewing);
    }

    private void ApplyTrayState(bool busy)
    {
        if (_trayShowsBusy == busy)
        {
            return;
        }

        _trayShowsBusy = busy;
        var tip = busy ? "Conclave · 评审中" : "Conclave · 空闲";

        if (OperatingSystem.IsMacOS() && _statusItem is not null
            && _trayIdlePath is not null && _trayBusyPath is not null)
        {
            if (_trayAnimator is null)
            {
                _statusItem.SetIcon(busy ? _trayBusyPath : _trayIdlePath);
            }
            else if (busy)
            {
                _trayAnimator.Start();
            }
            else
            {
                _trayAnimator.Stop(_trayIdlePath);
            }

            _statusItem.SetToolTip(tip);
        }
        else if (_tray is not null)
        {
            _tray.Icon = new WindowIcon(AssetLoader.Open(new Uri(busy ? TrayBusyAsset : TrayIdleAsset)));
            _tray.ToolTipText = tip;
        }
    }

    /// <summary>把评审中那套呼吸帧解到磁盘，按序返回路径。</summary>
    /// <remarks>
    /// 编号拼出来而不是写 7 个常量：这串帧是 <c>scripts/make-icon.py</c> 一次生成的一组，
    /// 手写一遍只是多一处会忘记同步的地方。
    /// </remarks>
    private static List<string> ExtractBusyFrames()
    {
        var frames = new List<string>(TrayBusyFrameCount);

        for (var i = 0; i < TrayBusyFrameCount; i++)
        {
            var n = i.ToString(CultureInfo.InvariantCulture);
            frames.Add(ExtractTrayIcon(
                $"avares://conclave/Assets/conclave-tray-busy-{n}.png", $"tray-busy-{n}.png"));
        }

        return frames;
    }

    private static string ExtractTrayIcon(string asset, string fileName)
    {
        var home = Services?.GetService<ConclaveOptions>()?.HomeDirectory;

        string path;
        if (string.IsNullOrWhiteSpace(home))
        {
            path = Path.Combine(
                Path.GetTempPath(),
                $"conclave-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}-{fileName}");
        }
        else
        {
            _ = Directory.CreateDirectory(home);
            path = Path.Combine(home, fileName);
        }

        using var stream = AssetLoader.Open(new Uri(asset));
        using var file = File.Create(path);
        stream.CopyTo(file);
        return path;
    }

    /// <summary>
    /// 无人值守地验证「点图标 → 出主面板」这条通路。
    /// </summary>
    /// <remarks>
    /// 这层原生互操作没有编译期保障 —— 选择器名打错、方法签名跟 ABI 不符，都只表现为
    /// 运行时「点了没反应」。而它又恰好是唯一没法用单元测试覆盖的部分（要主线程 + AppKit）。
    /// <c>performClick:</c> 走的正是真人点击的同一条 target/action 通路，
    /// 所以这个自检能盖住除「肉眼看图标」以外的全部。
    /// <para>
    /// Avalonia 升级、macOS 大版本升级之后都该跑一次。
    /// </para>
    /// </remarks>
    /// <summary>
    /// 把两套坐标系都打出来。
    /// </summary>
    /// <remarks>
    /// 多屏定位的全部难点就是「Cocoa 的 pt 全局空间」和「Avalonia 的 px 全局空间」
    /// 怎么对齐 —— 混合 DPI 时两者不是一个缩放系数的关系。排查这类问题时第一眼要看的
    /// 就是这几个数，所以留在自检输出里。
    /// </remarks>
    private static void DumpGeometry()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { Windows.Count: > 0 } desktop)
        {
            Console.WriteLine("（没有窗口，拿不到 Screens）");
            return;
        }

        var screens = desktop.Windows[0].Screens;
        Console.WriteLine($"Avalonia 屏幕 {screens.ScreenCount} 块（Bounds/WorkingArea 单位 = px）：");
        foreach (var s in screens.All)
        {
            Console.WriteLine(
                $"  {(s.IsPrimary ? "主" : "副")} bounds={s.Bounds} working={s.WorkingArea} scaling={s.Scaling}");
        }

        if (OperatingSystem.IsMacOS())
        {
            var (mx, my) = MacStatusItem.MouseLocation();
            Console.WriteLine($"Cocoa mouseLocation = ({mx:F1}, {my:F1})  pt，原点主屏左下，Y 向上");

            var anchor = ClickAnchor(screens);
            var hit = anchor is null ? null : screens.ScreenFromPoint(anchor.Value);
            Console.WriteLine(
                $"→ 换算后 anchor={anchor}  命中屏={(hit is null ? "无" : $"{(hit.IsPrimary ? "主" : "副")} {hit.Bounds}")}");
        }

        var panel = desktop.Windows[0];
        Console.WriteLine(
            $"主面板 Position={panel.Position} 实测尺寸={panel.Bounds.Width:F0}x{panel.Bounds.Height:F0}");
    }

    /// <summary>
    /// 对<b>每一块</b>实际连接的屏做一次坐标往返，验证「点哪块屏、面板就在哪块屏」。
    /// </summary>
    /// <remarks>
    /// 真人点击只会落在一块屏上，所以光靠 <see cref="DumpGeometry"/> 里那一次真实鼠标位置
    /// 永远验不到副屏 —— 而「多屏时面板跑到主屏去了」正是要修的那个 bug。
    /// 这里按每块屏菜单栏上的一个点反算出 Cocoa 坐标，再正着走一遍变换，
    /// 看它是否回到同一块屏。
    /// </remarks>
    private static List<string> CheckEveryScreen(Screens screens)
    {
        var problems = new List<string>();

        foreach (var screen in screens.All)
        {
            // 该屏菜单栏上的一点：横向居中，纵向在 Bounds 上沿往下 5。
            var expected = new PixelPoint(
                screen.Bounds.X + (screen.Bounds.Width / 2),
                screen.Bounds.Y + 5);

            var (cocoaX, cocoaY) = ToCocoa(screens, expected);
            var back = ToAvalonia(screens, cocoaX, cocoaY);
            var hit = back is null ? null : screens.ScreenFromPoint(back.Value);

            var where = screen.IsPrimary ? "主屏" : $"副屏 {screen.Bounds}";
            if (back != expected)
            {
                problems.Add($"{where}：坐标往返不闭合，{expected} → Cocoa({cocoaX:F1},{cocoaY:F1}) → {back}");
            }
            else if (hit is null || hit.Bounds != screen.Bounds)
            {
                problems.Add($"{where}：换算后落到了别的屏（{(hit is null ? "无" : hit.Bounds.ToString())}）");
            }
            else
            {
                Console.WriteLine($"  ✓ {where} 往返闭合，命中自己");
            }
        }

        return problems;
    }

    /// <summary>
    /// 让呼吸动画真的跑几拍。
    /// </summary>
    /// <remarks>
    /// 「帧都读得出来」跟「动画在动」是两回事：定时器没 Start、优先级建错、
    /// 回调里静默抛异常，三种都表现为图标停在第一帧 —— 而那跟静态图标肉眼分不出来。
    /// 所以这里是等真实的 <c>DispatcherTimer</c> 响，不是调 <c>OnTick</c> 假装一下。
    /// <para>
    /// 跑三拍而不是一整轮：一轮 12 拍将近一秒，而「响了」跟「响了 12 次」验到的是同一件事。
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("macos")]
    private static async Task<List<string>> CheckAnimationAsync(TrayAnimator animator, string restPath)
    {
        var problems = new List<string>();

        try
        {
            animator.Start();
            await Task.Delay(TrayFrameInterval * 3.5).ConfigureAwait(true);
            animator.Stop(restPath);

            if (animator.Ticks == 0)
            {
                problems.Add("呼吸动画的定时器一拍都没响 —— 评审中图标会一直停在第一帧");
            }
            else
            {
                Console.WriteLine($"  ✓ 呼吸动画跑了 {animator.Ticks} 拍");
            }
        }
        catch (InvalidOperationException ex)
        {
            problems.Add("呼吸动画跑不起来：" + ex.Message);
        }

        return problems;
    }

    private void RunSelfTest(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var problems = new List<string>();

            // 自检主体整个包住。lambda 是 async 的，里面漏出来的异常不会像同步版那样
            // 走 Dispatcher 的未处理异常通道把进程带走，而是变成一个没人 await 的
            // Task 故障 —— 表现是自检既不打结论也不退出，挂在那里等人 Ctrl+C。
            try
            {
                // 平台守卫写成正向条件而不是 `if (_statusItem is null) … else`：
                // CA1416 认 OperatingSystem.IsMacOS() 这种形式的收窄，认不出后者。
                if (OperatingSystem.IsMacOS() && _statusItem is not null)
                {
                    _statusItem.PerformClick();

                    if (_dashboard is null)
                    {
                        problems.Add("performClick: 之后主面板没被创建 —— target/action 没接上");
                    }
                    else if (!_dashboard.IsVisible)
                    {
                        problems.Add("主面板建了但不可见");
                    }

                    // TrayAnimator 的构造函数会把每一帧都读成 NSImage，所以它建起来了
                    // 就等于「7 张呼吸帧都打进了 avares、也都解得出来」——
                    // 这类错（漏打一张、改名没同步）平时只表现为评审中图标不动。
                    if (_trayAnimator is null || _trayIdlePath is null)
                    {
                        problems.Add($"呼吸帧没加载出来（应有 {TrayBusyFrameCount} 张），评审中会是静态图标");
                    }
                    else
                    {
                        Console.WriteLine($"  ✓ 呼吸帧 {TrayBusyFrameCount} 张全部可读");
                        problems.AddRange(
                            await CheckAnimationAsync(_trayAnimator, _trayIdlePath).ConfigureAwait(true));
                    }
                }
                else
                {
                    problems.Add("没建出 NSStatusItem —— 这个自检只在 macOS 上有意义");
                }

                DumpGeometry();

                if (Avalonia.Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime { Windows.Count: > 0 } live)
                {
                    Console.WriteLine("每屏坐标往返：");
                    problems.AddRange(CheckEveryScreen(live.Windows[0].Screens));
                }
            }
            // 兜底捕获所有异常：自检的任何一步炸了都该变成一条「失败」，然后正常退出
            catch (Exception ex)
            {
                problems.Add("自检自己炸了：" + ex);
            }

            if (problems.Count == 0)
            {
                Console.WriteLine("✅ 托盘自检通过：点击 → 面板已显示");
            }
            else
            {
                Console.Error.WriteLine("❌ 托盘自检失败：");
                problems.ForEach(p => Console.Error.WriteLine("   · " + p));
            }

            _quitting = true;
            desktop.Shutdown(problems.Count == 0 ? 0 : 1);
        });
    }
}

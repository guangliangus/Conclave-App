using System.Collections.Specialized;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Conclave.App.ViewModels;

namespace Conclave.App.Views;

/// <summary>
/// 主面板。点菜单栏图标直接出这个窗口；标题栏的 x 只是隐藏，后台继续跑。
/// </summary>
/// <remarks>
/// 版本号在 code-behind 里塞，不走 ViewModel：那是程序集的元数据，
/// 跟「本节点当前状态」不是一类东西，<see cref="ViewModels.MainViewModel"/> 不该知道它。
/// </remarks>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 表格的列数跟着窗口宽度走。用 SizeChanged 而不是在 XAML 里绑
        // $parent[Window].Bounds.Width：那样每个单元格都要挂一个转换器，
        // 而断点是「整张表共用一套」的东西，算一次分发给所有行更省。
        SizeChanged += OnWindowSizeChanged;
        DataContextChanged += (_, _) => Bind();

        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        VersionText.Text = version is null
            ? string.Empty
            : $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>
    /// 挂上对 ViewModel 的两处监听：日志面板的开合（改行高）与新日志行（滚到底）。
    /// </summary>
    /// <remarks>
    /// <c>Lines</c> 那个集合每换一次日志就是新的对象，所以订阅要跟着
    /// <see cref="MainViewModel.Log"/> 一起换 —— 订在旧集合上的话，换了一行看日志之后
    /// 就再也不自动滚了。
    /// </remarks>
    private MainViewModel? _bound;
    private INotifyCollectionChanged? _watchedLines;

    private void Bind()
    {
        if (DataContext is not MainViewModel vm || ReferenceEquals(_bound, vm))
        {
            return;
        }

        _bound = vm;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.Log) or nameof(MainViewModel.TablesCollapsed))
            {
                ApplyLogLayout();
            }

            if (e.PropertyName == nameof(MainViewModel.Log))
            {
                WatchLogLines(vm);
            }
        };

        ApplyLogLayout();
    }

    /// <summary>
    /// 把「滚到底」挂到当前这份日志的行集合上。
    /// </summary>
    /// <remarks>
    /// <b>先退订再订。</b> 原先是每次 <c>Log</c> 换人就 <c>+=</c> 一个新的 lambda、从不退订，
    /// 于是开第 N 次日志面板之后，每来一行就要滚 N 次、也就多算 N 次滚动布局 ——
    /// 而每一次布局都会在 Avalonia 的 macOS 后端里留下一批字体对象。
    /// </remarks>
    private void WatchLogLines(MainViewModel vm)
    {
        if (_watchedLines is not null)
        {
            _watchedLines.CollectionChanged -= OnLogLinesChanged;
            _watchedLines = null;
        }

        if (vm.Log is { } log)
        {
            log.Lines.CollectionChanged += OnLogLinesChanged;
            _watchedLines = log.Lines;
        }
    }

    /// <summary>
    /// 窗口显示/隐藏时告诉 ViewModel。
    /// </summary>
    /// <remarks>
    /// 面板的 x 只是 <c>Hide()</c>（<c>App.OnDashboardClosing</c> 把 Closing 取消掉了），
    /// 所以没有 Closed 事件可用，只能看 <see cref="Visual.IsVisibleProperty"/>。
    /// 为什么非停不可见 <see cref="MainViewModel.SetVisible"/>。
    /// </remarks>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty && DataContext is MainViewModel vm)
        {
            vm.SetVisible(change.GetNewValue<bool>());
        }
    }

    /// <summary>离底部这个距离以内就算「贴着底」。一行大概 17px。</summary>
    private const double StickyTail = 40;

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Layout.Width = e.NewSize.Width;
        }
    }

    /// <summary>顶栏的「退出」。真正的收尾在 <see cref="App.RequestQuit"/> 里。</summary>
    private void OnQuitClick(object? sender, RoutedEventArgs e)
        => (Avalonia.Application.Current as App)?.RequestQuit();

    /// <summary>
    /// 按住顶栏拖窗口。
    /// </summary>
    /// <remarks>
    /// 去掉系统标题栏之后窗口没有别的地方能抓。只在<b>没被别人处理过</b>的按下上开始拖：
    /// 顶栏上还有两个开关和两个按钮，它们会先吃掉自己的 <c>PointerPressed</c>，
    /// 所以点它们不会变成拖窗口。
    /// </remarks>
    private void OnChromeDrag(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginMoveDrag(e);
    }

    /// <summary>
    /// 点完动作把浮层收起来。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MenuFlyout</c> 会自己关，普通 <c>Flyout</c> 不会 —— 不收的话点完「认领」
    /// 那张浮层还挂在那儿，看起来像没生效。
    /// </para>
    /// <para>
    /// <b>关浮层必须排到命令后面</b>，这里原先是同步 <c>Close()</c>，于是
    /// <b>「指派给某某」整条路径静默失效</b>：三个节点跑了一整天，日志里一条指派记录都没有。
    /// </para>
    /// <para>
    /// 机制：<c>Button.OnClick</c> 是<b>先发 Click 事件、等它回来再读 <c>Command</c> 去执行</b>。
    /// 而浮层里那份候选列表是 <c>ItemsSource="{Binding AssignTargets}"</c> 绑出来的 ——
    /// 在 Click 里同步关掉浮层，浮层内容从树上摘下来、DataContext 归零，这条绑定
    /// 跟着推了个空值进去，<c>ItemsControl</c> 于是把容器全清了，被点的那个按钮的
    /// <c>Command</c> / <c>CommandParameter</c> 一起变回 null。等 <c>OnClick</c> 回来读，
    /// 它读到的是 null —— 什么都不执行，<b>不报错、不记日志</b>。
    /// </para>
    /// <para>
    /// 只有「浮层里的按钮 + 绑定出来的列表」这个组合会中招：常驻的「评审 / 认领」
    /// 不在浮层里，所以一直是好的 —— 这也是为什么它看起来像「只有指派坏了」。
    /// </para>
    /// <para>
    /// 所以先把 Popup 抓在手里（这时它还在树上），再 <c>Post</c> 出去等这一轮点击派发完。
    /// 命令是在 <c>OnClick</c> 里同步跑掉的，轮不到它被解绑。
    /// </para>
    /// </remarks>
    private void OnRowActionClick(object? sender, RoutedEventArgs e)
    {
        var popup = (sender as Control)?.FindLogicalAncestorOfType<Popup>();
        if (popup is not null)
        {
            Dispatcher.UIThread.Post(popup.Close);
        }
    }

    /// <summary>
    /// 日志面板开合时重排行高。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RowDefinition"/> 不在可视树里，DataContext 到不了它 ——
    /// 所以 <c>Height</c> 绑不了，只能在这里按状态设。索引对应 MainWindow.axaml 里
    /// <c>ContentGrid.RowDefinitions</c> 的注释编号，改那边的行顺序要同步改这里。
    /// </para>
    /// <para>
    /// 日志开着时给它一个像素高度、表格留 <c>*</c>：两个都是 <c>*</c> 的话拖分隔条
    /// 只是改比例，而人更常做的是「把日志拉高一点」。收起表格时反过来 ——
    /// 表格那行归零，日志吃掉全部。
    /// </para>
    /// </remarks>
    private const int TablesRowIndex = 3;
    private const int LogRowIndex = 5;

    /// <summary>日志面板刚打开时的高度。约 16 行，够看出「在动」又不至于吃掉整张表。</summary>
    private const double InitialLogHeight = 300;

    private void ApplyLogLayout()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var tables = ContentGrid.RowDefinitions[TablesRowIndex];
        var log = ContentGrid.RowDefinitions[LogRowIndex];

        if (!vm.HasLog)
        {
            tables.Height = new GridLength(1, GridUnitType.Star);
            tables.MinHeight = 0;
            log.Height = new GridLength(0);
            return;
        }

        if (vm.TablesCollapsed)
        {
            tables.Height = new GridLength(0);
            tables.MinHeight = 0;
            log.Height = new GridLength(1, GridUnitType.Star);
            return;
        }

        tables.Height = new GridLength(1, GridUnitType.Star);
        tables.MinHeight = 120;

        // 已经拖过分隔条的话保留人调好的高度，不要每次刷新都拽回 300。
        if (!log.Height.IsAbsolute || log.Height.Value <= 0)
        {
            log.Height = new GridLength(InitialLogHeight);
        }
    }

    /// <summary>
    /// 新行进来就滚到底。
    /// </summary>
    /// <remarks>
    /// 人自己往上翻的时候不该被拽回来，所以只在已经贴着底部时才滚。
    /// </remarks>
    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var tail = LogScroll.Extent.Height - LogScroll.Viewport.Height;
        if (tail - LogScroll.Offset.Y <= StickyTail)
        {
            LogScroll.ScrollToEnd();
        }
    }

    /// <summary>
    /// 在浏览器里打开这一行对应的 PR。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 走 <see cref="TopLevel.Launcher"/> 而不是 <c>Process.Start(url)</c>：后者在 macOS 上
    /// 要靠 <c>UseShellExecute</c> 转 <c>open</c>，各平台行为不一样，而这里唯一要的就是
    /// 「交给系统默认浏览器」。
    /// </para>
    /// <para>
    /// 地址由 <see cref="Conclave.Domain.PrLink"/> 拼好放在行上，所以这里不认识
    /// Azure DevOps 的 URL 形态 —— 队列行与账本行只是两个都带 <c>Url</c> 的对象。
    /// </para>
    /// </remarks>
    private void OnPrLinkClick(object? sender, RoutedEventArgs e)
    {
        var url = (sender as Control)?.DataContext switch
        {
            PrRow row => row.Url,
            ReviewRow row => row.Url,
            _ => null,
        };

        if (url is null || TopLevel.GetTopLevel(this)?.Launcher is not { } launcher)
        {
            return;
        }

        // 打不开只记在状态栏上：这是个「顺手点一下」的动作，不该弹窗打断。
        _ = LaunchAsync(launcher, url);
    }

    /// <summary>打开新版本的 GitHub Release 页面（更新说明在那儿）。</summary>
    private void OnReleasePageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel { UpdateReleasePage: { } page }
            && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            _ = LaunchAsync(launcher, page.ToString());
        }
    }

    private async Task LaunchAsync(Avalonia.Platform.Storage.ILauncher launcher, string url)
    {
        try
        {
            _ = await launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Status = $"打不开 {url}：{ex.Message}";
            }
        }
    }
}

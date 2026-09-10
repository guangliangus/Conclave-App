using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Conclave.App.Interop;

/// <summary>
/// 自己建一个 NSStatusItem —— 只为了拿到「单击」这一个事件。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用 Avalonia 的 <c>TrayIcon</c>。</b> 它在 macOS 上收不到点击，而这不是
/// 文档过时：<c>libAvaloniaNative.dylib</c> 里的 <c>AvnTrayIcon</c> 一共只导出
/// <c>SetIcon</c> / <c>SetMenu</c> / <c>SetToolTipText</c> / <c>SetIsVisible</c> /
/// <c>SetIsTemplateIcon</c> 五个方法（<c>nm</c> 验证过），native ABI 里就没有回调入口。
/// 所以「点图标直接出面板」绕不开自己走 Objective-C runtime。
/// </para>
/// <para>
/// 代价要说清楚：这层是<b>没有编译期保障</b>的。选择器名字打错、方法签名跟 ABI 不符，
/// 都只会在运行时表现为「点了没反应」或者直接 crash。所以
/// <see cref="PerformClick"/> 存在的唯一理由就是让 <c>conclave tray-selftest</c>
/// 能在没有人手点击的情况下把整条通路走一遍。
/// </para>
/// <para>
/// ⚠️ <b>所有方法必须在主线程调用。</b> AppKit 不是线程安全的，而这里没有任何一处
/// 替你切线程。<c>App.OnFrameworkInitializationCompleted</c> 本身就在主线程上。
/// </para>
/// <para>
/// 刻意<b>不</b>用 <c>[button window].frame</c> 去取图标位置：<c>NSRect</c> 是 32 字节，
/// x86_64 上必须走 <c>objc_msgSend_stret</c> 而 arm64 走普通 <c>objc_msgSend</c> ——
/// 一个我没法在两种架构上都验证的分歧。面板位置改由 Avalonia 自己按屏幕算。
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class MacStatusItem : IDisposable
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";

    /// <summary><c>NSVariableStatusItemLength</c>：宽度跟着图标走。</summary>
    private const double VariableLength = -1.0;

    /// <summary>菜单栏图标的绘制尺寸，pt。22pt 的栏里留一点上下余量。</summary>
    private const double IconPoints = 18.0;

    /// <summary>运行时注册的 target 类名。同名类在一个进程里只能注册一次。</summary>
    private const string TargetClass = "ConclaveStatusItemTarget";

    /// <summary>回调选择器。<c>conclave</c> 前缀是为了不跟 AppKit 自己的选择器撞。</summary>
    private const string ClickSelector = "conclaveStatusItemClicked:";

    /// <summary>
    /// 保持 rooted 的委托。
    /// </summary>
    /// <remarks>
    /// 交给 ObjC 的是它的函数指针，GC 不知道 native 那边还握着 —— 不留这个静态引用，
    /// 委托会被回收，然后第一次点击就是野指针跳转。
    /// </remarks>
    private static readonly ClickHandler Thunk = OnClickedFromObjC;

    /// <summary>
    /// 当前实例。
    /// </summary>
    /// <remarks>
    /// ObjC 的回调只给 <c>(self, sel, sender)</c>，没有用户数据槽可以塞托管句柄。
    /// 一个进程只有一个菜单栏图标，所以用静态字段找回实例是够的 ——
    /// 真要支持多个就得给 target 类加实例变量，那是另一档复杂度。
    /// </remarks>
    private static MacStatusItem? _current;

    private static IntPtr _targetClass;

    private readonly IntPtr _item;
    private readonly IntPtr _button;
    private bool _disposed;

    /// <summary>单击图标。已经在主线程上，调用方不必再切。</summary>
    internal event Action? Clicked;

    /// <param name="iconPath">模板图（纯黑 + alpha）的绝对路径。</param>
    /// <param name="toolTip">悬停提示。</param>
    internal MacStatusItem(string iconPath, string toolTip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iconPath);

        // 建 status item 之前必须先有 NSApplication —— Avalonia 已经建好了，
        // 这里取的是同一个实例（sharedApplication 是单例）。
        _ = MsgSend(objc_getClass("NSApplication"), Sel("sharedApplication"));

        var bar = MsgSend(objc_getClass("NSStatusBar"), Sel("systemStatusBar"));
        if (bar == IntPtr.Zero)
        {
            throw new InvalidOperationException("拿不到 NSStatusBar.systemStatusBar");
        }

        _item = MsgSend(bar, Sel("statusItemWithLength:"), VariableLength);
        if (_item == IntPtr.Zero)
        {
            throw new InvalidOperationException("statusItemWithLength: 返回 nil");
        }

        // 手动 retain：这里没有 ARC，而 statusItemWithLength: 返回的是 autoreleased 对象。
        _ = MsgSend(_item, Sel("retain"));

        _button = MsgSend(_item, Sel("button"));
        if (_button == IntPtr.Zero)
        {
            throw new InvalidOperationException("NSStatusItem.button 是 nil（系统版本过低？）");
        }

        SetIcon(iconPath);
        SetToolTip(toolTip);
        WireClick();

        _current = this;
    }

    /// <summary>换图。空闲 / 评审中两张模板图靠它切换，必须在 UI 线程上调。</summary>
    internal void SetIcon(string path)
    {
        var image = MsgSend(
            MsgSend(objc_getClass("NSImage"), Sel("alloc")),
            Sel("initWithContentsOfFile:"),
            NSString(path));

        if (image == IntPtr.Zero)
        {
            throw new InvalidOperationException($"NSImage 读不出 {path}");
        }

        // 模板图：macOS 只用 alpha 当蒙版，按浅色/深色菜单栏自己染色。
        // 不设的话深色菜单栏上就是一团黑。
        MsgSendBool(image, Sel("setTemplate:"), true);

        // 必须显式设尺寸。NSImage 的 size 来自 PNG 的像素尺寸（44x44），
        // 直接塞进 22pt 高的菜单栏会顶满甚至被裁。
        MsgSendSize(image, Sel("setSize:"), new CGSize(IconPoints, IconPoints));

        _ = MsgSend(_button, Sel("setImage:"), image);

        // 没有 ARC：alloc/init 出来的那一份归我们，setImage: 已经自己 retain 了一份，
        // 我们这份得还回去 —— 换图是常态之后不还就是每次泄一张。
        _ = MsgSend(image, Sel("release"));
    }

    internal void SetToolTip(string toolTip)
        => MsgSend(_button, Sel("setToolTip:"), NSString(toolTip ?? string.Empty));

    private void WireClick()
    {
        if (_targetClass == IntPtr.Zero)
        {
            var cls = objc_allocateClassPair(objc_getClass("NSObject"), TargetClass, IntPtr.Zero);
            if (cls == IntPtr.Zero)
            {
                throw new InvalidOperationException($"注册不了 ObjC 类 {TargetClass}");
            }

            // "v@:@" = 返回 void，参数是 (self, SEL, id)。签名写错不会编译报错，
            // 只会在调用时按错误的 ABI 取参数。
            if (!class_addMethod(
                    cls, Sel(ClickSelector), Marshal.GetFunctionPointerForDelegate(Thunk), "v@:@"))
            {
                throw new InvalidOperationException($"挂不上 {ClickSelector}");
            }

            objc_registerClassPair(cls);
            _targetClass = cls;
        }

        var target = MsgSend(MsgSend(_targetClass, Sel("alloc")), Sel("init"));
        _ = MsgSend(_button, Sel("setTarget:"), target);
        _ = MsgSend(_button, Sel("setAction:"), Sel(ClickSelector));
    }

    /// <summary>
    /// 程序化地点一下自己。
    /// </summary>
    /// <remarks>
    /// <c>performClick:</c> 走的是 target/action 的同一条路，所以它验证的正是真人点击
    /// 会走的通路 —— 选择器名、方法签名、委托是否还活着，全在里面。
    /// 唯一验不到的是「图标在菜单栏上肉眼看着对不对」。
    /// </remarks>
    internal void PerformClick()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = MsgSend(_button, Sel("performClick:"), IntPtr.Zero);
    }

    /// <summary>
    /// 把本应用切到前台。
    /// </summary>
    /// <remarks>
    /// accessory 应用（不进 Dock）开一个窗口<b>不会</b>让系统激活它，窗口会出现在
    /// 当前应用后面。必须显式 activate 一次。
    /// </remarks>
    internal static void ActivateApp()
    {
        var app = MsgSend(objc_getClass("NSApplication"), Sel("sharedApplication"));
        MsgSendBool(app, Sel("activateIgnoringOtherApps:"), true);
    }

    private static void OnClickedFromObjC(IntPtr self, IntPtr selector, IntPtr sender)
        => _current?.Clicked?.Invoke();

    private static IntPtr NSString(string value)
        => MsgSendUtf8(objc_getClass("NSString"), Sel("stringWithUTF8String:"), value);

    private static IntPtr Sel(string name) => sel_registerName(name);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var bar = MsgSend(objc_getClass("NSStatusBar"), Sel("systemStatusBar"));
        if (bar != IntPtr.Zero)
        {
            _ = MsgSend(bar, Sel("removeStatusItem:"), _item);
        }

        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ClickHandler(IntPtr self, IntPtr selector, IntPtr sender);

    /// <summary>
    /// 鼠标当前位置，Cocoa 全局坐标（pt，原点在主屏左下，Y 向上）。
    /// </summary>
    /// <remarks>
    /// 点击发生的那一刻鼠标就在图标上，所以这既是「图标在哪」也是「点在哪块屏上」——
    /// 一次调用同时回答了两个问题。
    /// <para>
    /// 用它而不是 <c>[[button window] frame]</c>：<c>NSRect</c> 是 32 字节，x86_64 上
    /// 必须走 <c>objc_msgSend_stret</c>、arm64 上走普通 <c>objc_msgSend</c>，是个我没法
    /// 在两种架构上都验证的分歧。<c>NSPoint</c> 只有 16 字节，两种 ABI 都按寄存器返回
    /// （x86_64 是两个 SSE eightbyte，arm64 是 HFA），没有这个坑。
    /// </para>
    /// </remarks>
    internal static (double X, double Y) MouseLocation()
    {
        var p = MsgSendPoint(objc_getClass("NSEvent"), Sel("mouseLocation"));
        return (p.X, p.Y);
    }

    /// <summary><c>CGSize</c>：两个 double。arm64 与 x86_64 都按寄存器传，无 stret 分歧。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGSize(double width, double height)
    {
        public readonly double Width = width;
        public readonly double Height = height;
    }

    /// <summary><c>CGPoint</c>：同样 16 字节，同样无 stret 分歧。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGPoint
    {
        public readonly double X;
        public readonly double Y;
    }

    // 刻意用 DllImport 而不是源生成的 LibraryImport：后者要求整个项目开
    // AllowUnsafeBlocks，为一个互操作文件给整个 UI 项目放开 unsafe 不值。
    // 这些签名全是 blittable 的指针和 double，运行时 marshalling 与生成代码等价。
    // 字符串一律显式 LPUTF8Str —— ObjC 这几个入口都是 const char*，
    // 靠 DllImport 默认的 ANSI 语义在不同平台上不是同一个东西。
    [DllImport(Objc)]
    private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Objc)]
    private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Objc)]
    private static extern IntPtr objc_allocateClassPair(
        IntPtr superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr extraBytes);

    [DllImport(Objc)]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport(Objc)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool class_addMethod(
        IntPtr cls, IntPtr name, IntPtr imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    // objc_msgSend 每种签名各声明一次。它不是可变参数函数 —— 按调用点的真实签名
    // 声明是唯一正确的用法，用一个「万能」签名去凑会在 ABI 上出错。
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, double arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendUtf8(
        IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    // ObjC 的 BOOL 在 arm64 macOS 上是 signed char，所以是 U1 而不是 4 字节 bool。
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendBool(
        IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.U1)] bool arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendSize(IntPtr receiver, IntPtr selector, CGSize arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern CGPoint MsgSendPoint(IntPtr receiver, IntPtr selector);
}

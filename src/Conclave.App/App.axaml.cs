using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
    /// <summary>
    /// 由 <see cref="Program"/> 在启动 Avalonia 之前塞进来。
    /// </summary>
    /// <remarks>
    /// Avalonia 的 <c>AppBuilder.Configure&lt;App&gt;()</c> 自己 new App，拿不到构造函数注入，
    /// 所以只能用静态属性把容器递进来。
    /// </remarks>
    public static IServiceProvider? Services { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services?.GetRequiredService<MainViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

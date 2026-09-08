using Avalonia;
using Conclave.App.ViewModels;
using Conclave.Application;
using Conclave.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conclave.App;

internal sealed class Program
{
    /// <summary>
    /// UI 与后台服务同进程：Generic Host 在后台线程跑 <see cref="DiscoveryService"/> 与
    /// <see cref="ReviewOrchestrator"/>，Avalonia 占主线程。
    /// </summary>
    /// <remarks>
    /// 顺序很重要：先 <c>StartAsync</c> 把容器建好（<see cref="MainViewModel"/> 要从里面取），
    /// 再启动 Avalonia。Avalonia 的 <c>StartWithClassicDesktopLifetime</c> 会阻塞到窗口关闭，
    /// 所以停机收尾放在它返回之后。
    /// </remarks>
    [STAThread]
    public static int Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        _ = builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });

        _ = builder.Services.AddConclaveNode();
        _ = builder.Services.AddSingleton<MainViewModel>();

        using var host = builder.Build();
        host.Start();

        App.Services = host.Services;

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // 给后台服务 5 秒收尾：正在跑的 claude 子进程会被取消，已出的票已经落链了。
            host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}

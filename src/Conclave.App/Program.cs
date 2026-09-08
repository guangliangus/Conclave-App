using Avalonia;
using System.Globalization;
using Conclave.App.ViewModels;
using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
using Conclave.Infrastructure;
using Microsoft.Extensions.Configuration;
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
        // 无头模式：conclave review <pr-id>。人手上只有一个 PR 号、不想等下一轮轮询时用，
        // 也是 bridge.sh 唯一需要保留的能力。
        if (args is ["review", var prArg] && int.TryParse(prArg, out var prId))
        {
            return HeadlessReview(args, prId);
        }

        // 无 UI 常驻：只贡献算力的 worker 机器用这个，也是本机跑多节点联调的方式。
        if (args is ["serve", ..])
        {
            return Serve(args);
        }

        // 账单：谁评审了什么、烧了多少 token、折合多少钱。
        if (args is ["report", ..])
        {
            return Report(args);
        }

        var builder = CreateBuilder(args);
        _ = builder.Services.AddConclaveNode(builder.Configuration);
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

    /// <summary>
    /// 起全部后台服务但不开窗口，直到 Ctrl+C。
    /// </summary>
    /// <remarks>
    /// 跟 UI 模式的区别只有「不启动 Avalonia」—— 轮询、编排、mesh 三个后台服务
    /// 完全一样。这也是 <c>Application 不引任何 UI 框架</c> 那条架构守卫的实际用途。
    /// </remarks>
    private static int Serve(string[] args)
    {
        var builder = CreateBuilder(args);
        _ = builder.Services.AddConclaveNode(builder.Configuration);

        using var host = builder.Build();
        host.Run();
        return 0;
    }

    /// <summary>
    /// 配置来源，从弱到强：程序目录的 appsettings.json → <c>~/.conclave/appsettings.json</c>
    /// → <c>CONCLAVE_</c> 前缀的环境变量 → 命令行。
    /// </summary>
    /// <remarks>
    /// 用户目录那份优先于程序目录那份：装好的 <c>.app</c> 里没法改文件，
    /// 但每台机器的 repo 路径和 mesh 端口都不一样，必须能在外面覆盖。
    /// </remarks>
    private static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var home = ResolveHome(args);

        _ = builder.Configuration
            // 显式加程序目录那份：Host.CreateApplicationBuilder 默认从 ContentRoot（工作目录）读，
            // 而 Finder 启动 .app 时工作目录是 /，打包进去的 appsettings.json 就永远读不到。
            .AddJsonFile(
                Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                optional: true, reloadOnChange: false)
            .AddJsonFile(Path.Combine(home, "appsettings.json"), optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("CONCLAVE_");

        _ = builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });

        return builder;
    }

    /// <summary>
    /// 打印评审账单然后退出。
    /// </summary>
    /// <remarks>
    /// 只读投影表，不起任何后台服务 —— 查账不该顺手触发一轮轮询。
    /// <c>--since 2026-09-01</c> 可限定起始日期。
    /// </remarks>
    private static int Report(string[] args)
    {
        var builder = CreateBuilder(args);
        _ = builder.Services.AddConclaveNode(builder.Configuration);

        using var host = builder.Build();
        var log = host.Services.GetRequiredService<IReviewLog>();

        DateTimeOffset? since = null;
        var idx = Array.IndexOf(args, "--since");
        if (idx >= 0 && idx + 1 < args.Length
            && DateTimeOffset.TryParse(
                args[idx + 1], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            since = parsed;
        }

        var ct = CancellationToken.None;
        var report = UsageReport.Render(
            log.ReadTotalAsync(since, null, ct).GetAwaiter().GetResult(),
            log.SummariseByReviewerAsync(since, null, ct).GetAwaiter().GetResult(),
            log.SummariseByMonthAsync(ct).GetAwaiter().GetResult(),
            log.SummariseByRepoAsync(since, null, ct).GetAwaiter().GetResult(),
            log.SummariseByModelAsync(since, null, ct).GetAwaiter().GetResult(),
            log.ReadRecentAsync(20, ct).GetAwaiter().GetResult());

        Console.Write(report);
        return 0;
    }

    /// <summary>
    /// 先探出 <c>Conclave:HomeDirectory</c>。
    /// </summary>
    /// <remarks>
    /// 配置文件本身就放在这个目录下，所以必须先知道目录才能加载它 —— 直接把路径写死成
    /// <c>~/.conclave</c> 会导致：用 <c>CONCLAVE_Conclave__HomeDirectory</c> 换了目录之后，
    /// 私钥和账本搬走了，配置却还在读老地方。实测本机跑双节点时两个节点都读到了
    /// <c>mesh 关</c>，正是这个原因。
    /// </remarks>
    private static string ResolveHome(string[] args)
    {
        var bootstrap = new ConfigurationBuilder()
            .AddEnvironmentVariables("CONCLAVE_")
            .AddCommandLine(args)
            .Build();

        var configured = bootstrap["Conclave:HomeDirectory"];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".conclave")
            : configured;
    }

    /// <summary>
    /// 评审一个指定的 PR 然后退出。不起 UI，也不起后台轮询。
    /// </summary>
    /// <remarks>
    /// 刻意手动驱动编排循环而不是 <c>host.Run()</c>：这样退出码能反映评审结果，
    /// 脚本（含现有的 bridge.sh）可以直接用。
    /// </remarks>
    private static int HeadlessReview(string[] args, int prId)
    {
        var builder = CreateBuilder(args);
        _ = builder.Services.AddConclaveNode(builder.Configuration);

        using var host = builder.Build();
        var services = host.Services;

        var discovery = services.GetRequiredService<DiscoveryService>();
        var orchestrator = services.GetRequiredService<ReviewOrchestrator>();
        var acta = services.GetRequiredService<IActaStore>();
        var console = services.GetRequiredService<ILogger<Program>>();

        return RunHeadlessAsync(discovery, orchestrator, acta, console, prId)
            .GetAwaiter().GetResult();
    }

    private static async Task<int> RunHeadlessAsync(
        DiscoveryService discovery,
        ReviewOrchestrator orchestrator,
        IActaStore acta,
        ILogger logger,
        int prId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var ct = cts.Token;

        var revision = await discovery.SummonAsync(prId, ct).ConfigureAwait(false);
        if (revision is null)
        {
            return 2;
        }

        orchestrator.RequestReview(revision.Id);

        for (var tick = 0; tick < 200 && !ct.IsCancellationRequested; tick++)
        {
            await orchestrator.TickAsync(ct).ConfigureAwait(false);
            await orchestrator.WhenIdleAsync().ConfigureAwait(false);

            var chain = await acta.ReadRevisionAsync(revision.Id, ct).ConfigureAwait(false);
            var state = ActaProjection.Project(chain, revision.Id);

            if (state.Promulgation is null)
            {
                continue;
            }

            var result = state.Promulgation;
            logger.LogInformation(
                "{Revision} → {Decision}，{Count} 条合并 finding（{Actual}/{Expected} 票{Degraded}）",
                revision.Id, result.Decision, result.Findings.Count,
                result.ActualQuorum, result.ExpectedQuorum,
                result.Degraded ? "，降级" : string.Empty);

            foreach (var finding in result.Findings)
            {
                logger.LogInformation(
                    "  [{Confidence:P0} {Mentions}/{Actual}] {Severity} {File}:{Line} — {Title}",
                    finding.Confidence, finding.Mentions, result.ActualQuorum,
                    finding.Best.Severity, finding.Best.File, finding.Best.Line, finding.Best.Title);
            }

            // 退出码给脚本用：0 通过、1 有问题、3 执行失败。
            return result.Decision switch
            {
                ReviewDecision.Approve or ReviewDecision.ApproveWithSuggestions => 0,
                ReviewDecision.Error => 3,
                _ => 1,
            };
        }

        logger.LogError("{Revision} 在超时前没有出结论", revision.Id);
        return 4;
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

using Avalonia;
using Conclave.App.ViewModels;
using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
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
        // 无头模式：conclave review <pr-id>。人手上只有一个 PR 号、不想等下一轮轮询时用，
        // 也是 bridge.sh 唯一需要保留的能力。
        if (args is ["review", var prArg] && int.TryParse(prArg, out var prId))
        {
            return HeadlessReview(args, prId);
        }

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

    /// <summary>
    /// 评审一个指定的 PR 然后退出。不起 UI，也不起后台轮询。
    /// </summary>
    /// <remarks>
    /// 刻意手动驱动编排循环而不是 <c>host.Run()</c>：这样退出码能反映评审结果，
    /// 脚本（含现有的 bridge.sh）可以直接用。
    /// </remarks>
    private static int HeadlessReview(string[] args, int prId)
    {
        var builder = Host.CreateApplicationBuilder(args);
        _ = builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        _ = builder.Services.AddConclaveNode();

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

            var chain = await acta.ReadChainAsync(revision.ChainId, ct).ConfigureAwait(false);
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

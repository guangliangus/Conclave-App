using Conclave.Application.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conclave.Application;

/// <summary>
/// 定时查有没有新版本，有就提示；<b>不装</b>。
/// </summary>
/// <remarks>
/// <para>
/// 装是人点的（见 <see cref="IUpdateInstaller"/>）。这个工具在后台跑评审，换二进制
/// 会打断手上那个十几分钟的 claude 进程 —— 时机得由人挑。
/// </para>
/// <para>
/// 同一个版本只提示一次。每小时查一次、每次都弹的话，「有新版本」会把通知列表灌满，
/// 而人之所以没装多半是有理由的（比如等一个 PR 评完）。
/// </para>
/// <para>
/// 查失败只记 Debug：断网、GitHub 抽风都是常态，不值一条通知。
/// </para>
/// </remarks>
public sealed class UpdateService(
    IUpdateSource source,
    NodeState state,
    ConclaveOptions options,
    ILogger<UpdateService> logger) : BackgroundService
{
    private string? _announced;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(options.Update.InitialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Update.CheckInterval);
        do
        {
            await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>查一次。公开给测试和「立即检查」按钮用。</summary>
    public async Task CheckOnceAsync(CancellationToken ct)
    {
        ReleaseInfo? latest;
        try
        {
            latest = await source.GetLatestAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "查更新失败");
            return;
        }

        if (latest is null || !AppInfo.IsNewer(latest.Version, AppInfo.Version))
        {
            // 最新的就是本机这一版（或更旧）：把之前的提示收掉。
            state.SetUpdateAvailable(null);
            return;
        }

        state.SetUpdateAvailable(latest);

        if (_announced != latest.Version)
        {
            _announced = latest.Version;
            logger.LogInformation("有新版本 v{Version}（本机 v{Current}）", latest.Version, AppInfo.Version);
            state.Notify(
                NoticeKind.Info,
                $"有新版本 v{latest.Version}",
                $"本机是 v{AppInfo.Version}。顶栏那条横幅可以装；会等手上的评审跑完再换");
        }
    }
}

using System.Diagnostics;
using Conclave.Application.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conclave.Application;

/// <summary>
/// 查有没有新版本，有就提示并（默认）自动装。
/// </summary>
/// <remarks>
/// <para>
/// <b>什么时候查</b>：每 <see cref="UpdateOptions.CheckInterval"/> 一次，外加一个提前触发口 ——
/// 心跳里带着每个节点跑的 app 版本，看见组里有人比自己新就立刻查一次。同一批机器往往
/// 同时该升，让先升的那台把消息顺手带给其余的，省掉最多一小时的等待。
/// 看成员表是纯内存操作，所以扫得比查 GitHub 密（<see cref="UpdateOptions.PeerScanInterval"/>）。
/// </para>
/// <para>
/// <b>什么时候装</b>：查到就装，不问人（<see cref="UpdateOptions.AutoInstall"/>）。
/// 「等空闲」由安装器负责 —— 下载、校验随时可做，只有替换二进制那一步要等本节点
/// 评完手上的 PR。所以自动装不会打断一次十几分钟的评审。
/// </para>
/// <para>
/// 同一个版本只提示一次、也只自动装一次。每小时查一次而每次都弹的话，「有新版本」
/// 会把通知列表灌满；装失败也不反复重试 —— 失败原因（磁盘满、包坏了）不会因为再等
/// 一小时就消失，而失败通知已经发过了，该由人来看。
/// </para>
/// <para>
/// 查失败只记 Debug：断网、GitHub 抽风都是常态，不值一条通知。
/// </para>
/// </remarks>
public sealed class UpdateService(
    IUpdateSource source,
    IUpdateInstaller installer,
    IMesh mesh,
    NodeState state,
    ConclaveOptions options,
    ILogger<UpdateService> logger) : BackgroundService
{
    private string? _announced;
    private string? _peerHint;
    private string? _installing;
    private Task _install = Task.CompletedTask;

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

        // 单调时钟而不是 UtcNow：笔记本合盖再打开、或者手动改系统时间，都不该
        // 让「上次查是多久以前」算出一个负数或者一整天。
        Stopwatch? since = null;

        using var timer = new PeriodicTimer(options.Update.PeerScanInterval);
        do
        {
            var hint = PeerRunningNewerVersion();
            var due = since is null || since.Elapsed >= options.Update.CheckInterval;

            if (!due && (hint is null || hint == _peerHint))
            {
                continue;
            }

            if (!due)
            {
                logger.LogInformation("组里有节点在跑 v{Peer}（本机 v{Current}），提前查一次更新",
                    hint, AppInfo.Version);
            }

            // 记下来是为了「同一个版本只因为它提前查一次」。对面可能是个开发态构建，
            // 版本号比任何 Release 都新，那种情况下每分钟查一次会白烧 GitHub 配额。
            _peerHint = hint;
            since ??= new Stopwatch();
            since.Restart();

            await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 当前那次自动安装；没有就是已完成的任务。
    /// </summary>
    /// <remarks>
    /// 安装是 fire-and-forget 的（它要等本节点评完手上的 PR，可能十几分钟），
    /// 暴露出来是为了让测试能等到它收尾 —— 跟 <c>ReviewOrchestrator.WhenIdleAsync</c> 同一个理由。
    /// </remarks>
    public Task Installing => _install;

    /// <summary>
    /// 组里有没有节点在跑比本机新的版本；有就返回其中最新的那个版本号。
    /// </summary>
    /// <remarks>
    /// 只看<b>心跳还新鲜</b>的节点：一台停在半年前那一版、早就关机的机器，
    /// 它的 <c>AppVersion</c> 留在成员表里也不该说明任何事。
    /// 单机模式下成员表只有自己，这里恒为 null。
    /// </remarks>
    public string? PeerRunningNewerVersion()
    {
        var now = DateTimeOffset.UtcNow;
        var self = mesh.Self.Id;
        string? newest = null;

        foreach (var peer in mesh.Members)
        {
            if (peer.Id == self || !peer.IsAlive(now) || peer.AppVersion.Length == 0)
            {
                continue;
            }

            if (AppInfo.IsNewer(peer.AppVersion, AppInfo.Version)
                && (newest is null || AppInfo.IsNewer(peer.AppVersion, newest)))
            {
                newest = peer.AppVersion;
            }
        }

        return newest;
    }

    /// <summary>查一次；查到新版就提示，并按配置自动装。公开给测试和「立即检查」用。</summary>
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

        var auto = options.Update.AutoInstall && installer.CanInstall;

        if (_announced != latest.Version)
        {
            _announced = latest.Version;
            logger.LogInformation("有新版本 v{Version}（本机 v{Current}）", latest.Version, AppInfo.Version);
            state.Notify(
                NoticeKind.Info,
                $"有新版本 v{latest.Version}",
                auto
                    ? $"本机是 v{AppInfo.Version}。会下载校验好，等本节点评完手上的 PR 后自动更新并重启"
                    : installer.CanInstall
                        ? $"本机是 v{AppInfo.Version}。自动更新已关掉，顶栏那条横幅上可以手动装"
                        : $"本机是 v{AppInfo.Version}。当前进程不在 .app 里（开发态），不做就地替换");
        }

        if (auto)
        {
            StartInstall(latest);
        }
        else if (options.Update.AutoInstall)
        {
            // 配置想自动装但这台机器装不了（进程不在 .app 里）。只说一次，别每小时一条。
            logger.LogDebug("自动更新跳过：当前进程不在 .app 里，没有可替换的目标");
        }
    }

    /// <summary>
    /// 起一次安装。
    /// </summary>
    /// <remarks>
    /// 刻意不 await：安装器会一直等到本节点评完手上的 PR，那可能是十几分钟，
    /// 而这期间查更新的循环还得照常转（比如新版被撤了要把横幅收掉）。
    /// 异常在里面吃掉 —— 安装器已经把失败写进状态和通知了，这里只是别让它
    /// 变成未观察的异常把进程带走。
    /// </remarks>
    private void StartInstall(ReleaseInfo release)
    {
        if (_installing == release.Version || !_install.IsCompleted)
        {
            return;
        }

        _installing = release.Version;
        _install = Task.Run(async () =>
        {
            try
            {
                await installer.InstallAsync(release, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "自动更新到 v{Version} 失败", release.Version);
            }
        });
    }
}

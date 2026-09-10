using System.Globalization;
using System.Security.Cryptography;
using Conclave.Application;
using Conclave.Application.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure.Update;

/// <summary>
/// macOS 上就地替换 <c>Conclave.app</c> 并重启。
/// </summary>
/// <remarks>
/// <para>
/// 步骤：下载到 <c>~/.conclave/updates/</c> → 校验 sha256 → <b>等本节点评完手上的 PR</b>
/// → <c>ditto -x -k</c> 解到旁边 → 去掉隔离标记（包没签名）→ 旧 .app 改名 <c>.previous</c>、
/// 新的挪进去 → 让 launchd 拉起新版。
/// </para>
/// <para>
/// <b>等空闲</b>是这里跟普通更新器唯一不同的地方：一次评审十几分钟、烧订阅额度，
/// 换二进制把它打断等于白烧。所以下载、校验都可以先做，替换那一步要等
/// <see cref="ReviewOrchestrator.WhenIdleAsync"/>。这段时间界面上显示「等评审跑完」。
/// </para>
/// <para>
/// <b>重启</b>：LaunchAgent 在的话用 <c>launchctl kickstart -k</c> —— launchd 发 SIGTERM
/// 给本进程（走 <c>Program.BridgeShutdown</c> 的优雅路径），再按 plist 拉起新版，
/// 不需要我们自己 fork。没装 LaunchAgent（手动 open 起的）就派一个 <c>sleep 2; open</c>
/// 出去，然后自己退出。
/// </para>
/// <para>
/// 旧版留一份 <c>Conclave.app.previous</c>：新版起不来时人还能手动换回去。只留一份。
/// </para>
/// </remarks>
public sealed class MacUpdateInstaller(
    ConclaveOptions options,
    ReviewOrchestrator orchestrator,
    NodeState state,
    IHostApplicationLifetime lifetime,
    ILogger<MacUpdateInstaller> logger) : IUpdateInstaller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public async Task InstallAsync(ReleaseInfo release, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("就地更新目前只做了 macOS");
        }

        var bundle = ResolveBundle();
        var updates = Path.Combine(options.HomeDirectory, "updates");
        _ = Directory.CreateDirectory(updates);

        try
        {
            var zip = Path.Combine(updates, Path.GetFileName(release.AssetUrl.LocalPath));
            await DownloadAsync(release, zip, ct).ConfigureAwait(false);
            await VerifyAsync(zip, release.Sha256, ct).ConfigureAwait(false);

            state.SetUpdateProgress(true, "已下载并校验，等本节点评完手上的 PR…");
            await orchestrator.WhenIdleAsync().WaitAsync(ct).ConfigureAwait(false);

            state.SetUpdateProgress(true, "正在替换 Conclave.app…");
            var staged = await UnpackAsync(zip, updates, release.Version, ct).ConfigureAwait(false);
            await SwapAsync(bundle, staged, ct).ConfigureAwait(false);

            logger.LogInformation("已装好 v{Version} 到 {Bundle}，重启", release.Version, bundle);
            state.Notify(NoticeKind.Ok, $"已更新到 v{release.Version}", "正在重启…");
            state.SetUpdateProgress(true, "已装好，正在重启…");

            await RelaunchAsync(bundle, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "更新到 v{Version} 失败", release.Version);
            state.SetUpdateProgress(false, "更新失败：" + ex.Message);
            state.Notify(NoticeKind.Bad, $"更新到 v{release.Version} 失败", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// 要替换的 .app。配置没写就从当前进程的可执行文件往上找。
    /// </summary>
    /// <remarks>
    /// <c>dotnet run</c> 起的开发进程不在任何 .app 里 —— 那时候明确报出来，
    /// 别去猜 <c>/Applications/Conclave.app</c>：开发机上那里装的可能是另一版。
    /// </remarks>
    private string ResolveBundle()
    {
        if (!string.IsNullOrWhiteSpace(options.Update.BundlePath))
        {
            return options.Update.BundlePath;
        }

        var dir = Path.GetDirectoryName(Environment.ProcessPath);
        while (!string.IsNullOrEmpty(dir))
        {
            if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException(
            "当前进程不在 .app 里（开发模式），没有可替换的目标。装好的 .app 才能就地更新");
    }

    private async Task DownloadAsync(ReleaseInfo release, string zip, CancellationToken ct)
    {
        state.SetUpdateProgress(true, "下载中…");

        using var response = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        _ = response.EnsureSuccessStatusCode();

        var total = release.Bytes > 0 ? release.Bytes : response.Content.Headers.ContentLength ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(zip);

        var buffer = new byte[1 << 16];
        long done = 0;
        var lastPercent = -1;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;

            if (total > 0)
            {
                var percent = (int)(done * 100 / total);
                if (percent / 5 != lastPercent / 5)
                {
                    lastPercent = percent;
                    state.SetUpdateProgress(true, string.Create(
                        CultureInfo.InvariantCulture,
                        $"下载中 {percent}%（{done / 1_048_576.0:F0} / {total / 1_048_576.0:F0} MB）"));
                }
            }
        }
    }

    private static async Task VerifyAsync(string zip, string expected, CancellationToken ct)
    {
        await using var stream = File.OpenRead(zip);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));

        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            // 校验不过的包一个字节都不能留：下次可能被当作「已下载」直接用。
            File.Delete(zip);
            throw new InvalidOperationException(
                $"下载的包校验和不对（期望 {expected[..Math.Min(12, expected.Length)]}…，实际 {actual[..12]}…），已删除");
        }
    }

    private static async Task<string> UnpackAsync(string zip, string updates, string version, CancellationToken ct)
    {
        var staging = Path.Combine(updates, "stage-" + version);
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        _ = Directory.CreateDirectory(staging);

        // ditto 而不是 ZipFile：要保住可执行位，否则解出来的 conclave 起不来。
        await RunAsync("/usr/bin/ditto", ["-x", "-k", zip, staging], ct).ConfigureAwait(false);

        var app = Directory.GetDirectories(staging, "*.app").FirstOrDefault()
            ?? throw new InvalidOperationException("zip 里没有 .app");

        // 包没签名：不去掉隔离标记，launchd 拉起来就是「已损坏」。
        await RunAsync("/usr/bin/xattr", ["-dr", "com.apple.quarantine", app], ct, tolerateFailure: true)
            .ConfigureAwait(false);

        return app;
    }

    private static async Task SwapAsync(string bundle, string staged, CancellationToken ct)
    {
        var previous = bundle + ".previous";
        if (Directory.Exists(previous))
        {
            Directory.Delete(previous, recursive: true);
        }

        // /bin/mv 而不是 Directory.Move：~/.conclave 和 /Applications 可能不在同一个卷上，
        // Directory.Move 跨卷直接抛；mv 会退成拷贝。
        await RunAsync("/bin/mv", [bundle, previous], ct).ConfigureAwait(false);
        await RunAsync("/bin/mv", [staged, bundle], ct).ConfigureAwait(false);
    }

    private async Task RelaunchAsync(string bundle, CancellationToken ct)
    {
        var uid = (await ProcessRunner.RunAsync("/usr/bin/id", ["-u"], ct: ct).ConfigureAwait(false))
            .StdOut.Trim();
        var service = $"gui/{uid}/{options.Update.LaunchAgentLabel}";

        var loaded = await ProcessRunner.RunAsync("/bin/launchctl", ["print", service], ct: ct)
            .ConfigureAwait(false);

        if (loaded.Success)
        {
            // launchd 会给本进程发 SIGTERM（走优雅退出），然后按 plist 拉起新版。
            logger.LogInformation("交给 launchd 重启：kickstart -k {Service}", service);
            _ = await ProcessRunner.RunAsync("/bin/launchctl", ["kickstart", "-k", service], ct: ct)
                .ConfigureAwait(false);
            return;
        }

        // 没有 LaunchAgent（手动 open 起的）：派一个 open 出去，等我们退干净了它再起新版。
        logger.LogInformation("没有 LaunchAgent，自己重启 {Bundle}", bundle);
        _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", $"sleep 2; /usr/bin/open -n '{bundle.Replace("'", "'\\''")}'" },
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        lifetime.StopApplication();
    }

    private static async Task RunAsync(
        string executable, string[] args, CancellationToken ct, bool tolerateFailure = false)
    {
        var result = await ProcessRunner.RunAsync(executable, args, ct: ct).ConfigureAwait(false);
        if (!result.Success && !tolerateFailure)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} {string.Join(' ', args)} 退出码 {result.ExitCode}：{result.StdErr.Trim()}");
        }
    }
}

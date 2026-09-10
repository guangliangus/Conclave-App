namespace Conclave.Application.Ports;

/// <summary>
/// 一个可装的新版本。
/// </summary>
/// <param name="Version">版本号，不带 v。</param>
/// <param name="Tag">Release 的 tag。</param>
/// <param name="Rid">这个包是给哪个架构的（<c>osx-arm64</c> / <c>osx-x64</c>）。</param>
/// <param name="AssetUrl">zip 的下载地址。</param>
/// <param name="Sha256">manifest 里记的 sha256。下载完必须对上，对不上不装。</param>
/// <param name="Bytes">包大小，进度条用。</param>
/// <param name="ReleasePage">Release 页面，给人看更新说明。</param>
public sealed record ReleaseInfo(
    string Version,
    string Tag,
    string Rid,
    Uri AssetUrl,
    string Sha256,
    long Bytes,
    Uri ReleasePage);

/// <summary>去哪查最新版。实现见 <c>Conclave.Infrastructure.Update.GitHubReleaseSource</c>。</summary>
public interface IUpdateSource
{
    /// <summary>
    /// 最新 Release 里给<b>本机架构</b>的那个包；没有（不是 macOS、Release 里缺这个架构）返回 null。
    /// </summary>
    /// <remarks>网络错误直接抛 —— 调用方决定记 Debug 还是提示，这里不吞。</remarks>
    Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct);
}

/// <summary>把一个新版本装到位。实现见 <c>Conclave.Infrastructure.Update.MacUpdateInstaller</c>。</summary>
public interface IUpdateInstaller
{
    /// <summary>
    /// 下载 → 校验 → 等本节点空闲 → 替换 .app → 重启进程。进度写进 <see cref="NodeState"/>。
    /// </summary>
    /// <remarks>
    /// 正常完成时这个进程会被结束（launchd 拉起新版），所以调用方别指望它返回之后还能干什么。
    /// 失败抛异常，.app 保持原样。
    /// </remarks>
    Task InstallAsync(ReleaseInfo release, CancellationToken ct);
}

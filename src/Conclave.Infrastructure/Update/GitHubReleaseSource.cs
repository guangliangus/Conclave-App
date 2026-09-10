using System.Runtime.InteropServices;
using System.Text.Json;
using Conclave.Application;
using Conclave.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure.Update;

/// <summary>
/// 从 GitHub Release 读最新版。
/// </summary>
/// <remarks>
/// <para>
/// 两次请求：<c>releases/latest</c> 拿到 tag 与资产列表，再拉资产里的 <c>manifest.json</c>
/// 拿 sha256。校验和从 manifest 读而不是从 GitHub 的资产元数据读 —— GitHub 的 API
/// 不给 sha256（只有大小），而 manifest 是 CI 算好、跟包一起发的。
/// </para>
/// <para>
/// 只认本机架构的包（<c>osx-arm64</c> / <c>osx-x64</c>）。不是 macOS 就返回 null：
/// 就地替换 .app 只做了 macOS，其他平台查到了也装不了，不如不提示。
/// </para>
/// <para>
/// 仓库是公开的，匿名访问即可。GitHub 要求带 User-Agent，不带直接 403。
/// </para>
/// </remarks>
public sealed class GitHubReleaseSource : IUpdateSource, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _repository;
    private readonly string? _rid;
    private readonly ILogger<GitHubReleaseSource> _logger;

    /// <param name="options">读 <see cref="UpdateOptions.Repository"/>。</param>
    /// <param name="logger">日志。</param>
    /// <param name="handler">只给测试用：传 null 走默认 handler，传进来的不由本类释放。</param>
    /// <param name="rid">
    /// 只给测试用：装哪个架构的包。默认按本机判（<see cref="CurrentRid"/>）——
    /// CI 在 Linux 上跑测试，那里 CurrentRid 是 null，不能让选包逻辑的测试跟着变成空转。
    /// </param>
    public GitHubReleaseSource(
        ConclaveOptions options,
        ILogger<GitHubReleaseSource> logger,
        HttpMessageHandler? handler = null,
        string? rid = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _repository = options.Update.Repository.Trim().Trim('/');
        _rid = rid ?? CurrentRid;
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Conclave/{AppInfo.Version}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>本机该装哪个包；不是 macOS 为 null。</summary>
    public static string? CurrentRid => OperatingSystem.IsMacOS()
        ? RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64"
        : null;

    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        if (_rid is not { } rid)
        {
            _logger.LogDebug("不是 macOS，跳过更新检查");
            return null;
        }

        using var release = await GetJsonAsync(
            new Uri($"https://api.github.com/repos/{_repository}/releases/latest"), ct)
            .ConfigureAwait(false);

        var root = release.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        var page = new Uri(root.GetProperty("html_url").GetString()!);

        var assets = root.GetProperty("assets").EnumerateArray()
            .Select(a => (
                Name: a.GetProperty("name").GetString() ?? string.Empty,
                Url: a.GetProperty("browser_download_url").GetString() ?? string.Empty))
            .Where(a => a.Name.Length > 0 && a.Url.Length > 0)
            .ToList();

        var manifestUrl = assets.FirstOrDefault(a => a.Name == "manifest.json").Url;
        if (string.IsNullOrEmpty(manifestUrl))
        {
            _logger.LogWarning("Release {Tag} 没有 manifest.json，不是 CI 发的包，忽略", tag);
            return null;
        }

        using var manifest = await GetJsonAsync(new Uri(manifestUrl), ct).ConfigureAwait(false);
        var version = manifest.RootElement.GetProperty("version").GetString() ?? tag.TrimStart('v');

        foreach (var asset in manifest.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("rid").GetString() != rid)
            {
                continue;
            }

            var file = asset.GetProperty("file").GetString() ?? string.Empty;
            var download = assets.FirstOrDefault(a => a.Name == file).Url;
            if (string.IsNullOrEmpty(download))
            {
                _logger.LogWarning("manifest 里列了 {File}，Release 资产里却没有", file);
                return null;
            }

            return new ReleaseInfo(
                version,
                tag,
                rid,
                new Uri(download),
                asset.GetProperty("sha256").GetString() ?? string.Empty,
                asset.TryGetProperty("bytes", out var b) ? b.GetInt64() : 0,
                page);
        }

        _logger.LogDebug("Release {Tag} 没有 {Rid} 的包", tag, rid);
        return null;
    }

    private async Task<JsonDocument> GetJsonAsync(Uri url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        _ = response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();
}

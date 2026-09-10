using System.Net;
using System.Text;
using Conclave.Application;
using Conclave.Infrastructure.Update;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

/// <summary>
/// 从 GitHub Release 选包。两次请求（releases/latest + manifest.json）都是假的，
/// 形状照着真实 API 与 <c>scripts/ci/release-assets.sh</c> 生成的 manifest 写。
/// </summary>
public sealed class GitHubReleaseSourceTests
{
    private const string Latest = """
        {
          "tag_name": "v1.3.0",
          "html_url": "https://github.com/guangliangus/Conclave-App/releases/tag/v1.3.0",
          "assets": [
            { "name": "manifest.json", "browser_download_url": "https://dl.test/manifest.json", "size": 400 },
            { "name": "SHA256SUMS", "browser_download_url": "https://dl.test/SHA256SUMS", "size": 200 },
            { "name": "Conclave-1.3.0-osx-arm64.zip", "browser_download_url": "https://dl.test/arm.zip", "size": 44660236 },
            { "name": "Conclave-1.3.0-osx-x64.zip", "browser_download_url": "https://dl.test/x64.zip", "size": 50409886 }
          ]
        }
        """;

    private const string Manifest = """
        {
          "name": "Conclave", "version": "1.3.0", "tag": "v1.3.0", "commit": "abc",
          "assets": [
            { "rid": "osx-arm64", "file": "Conclave-1.3.0-osx-arm64.zip", "sha256": "aa11", "bytes": 44660236 },
            { "rid": "osx-x64",   "file": "Conclave-1.3.0-osx-x64.zip",   "sha256": "bb22", "bytes": 50409886 }
          ]
        }
        """;

    /// <summary>按 URL 分发的假 GitHub。记下每个请求，好断言 User-Agent 之类的头。</summary>
    private sealed class FakeGitHub(Func<Uri, string?> answer) : HttpMessageHandler
    {
        internal List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var body = answer(request.RequestUri!);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
        }
    }

    private static string? Route(Uri url) => url.AbsolutePath switch
    {
        "/repos/guangliangus/Conclave-App/releases/latest" => Latest,
        "/manifest.json" => Manifest,
        _ => null,
    };

    private static GitHubReleaseSource Source(HttpMessageHandler handler, string rid = "osx-arm64")
        => new(new ConclaveOptions(), NullLogger<GitHubReleaseSource>.Instance, handler, rid);

    [Fact]
    public async Task Picks_the_asset_for_this_machines_architecture()
    {
        using var gh = new FakeGitHub(Route);

        var release = await Source(gh, "osx-arm64").GetLatestAsync(CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal("1.3.0", release.Version);
        Assert.Equal("v1.3.0", release.Tag);
        Assert.Equal("osx-arm64", release.Rid);
        Assert.Equal(new Uri("https://dl.test/arm.zip"), release.AssetUrl);
        Assert.Equal("aa11", release.Sha256);      // 校验和来自 manifest，GitHub API 不给
        Assert.Equal(44660236, release.Bytes);
        Assert.Contains("/releases/tag/v1.3.0", release.ReleasePage.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_intel_machine_gets_the_x64_zip()
    {
        using var gh = new FakeGitHub(Route);

        var release = await Source(gh, "osx-x64").GetLatestAsync(CancellationToken.None);

        Assert.Equal(new Uri("https://dl.test/x64.zip"), release!.AssetUrl);
        Assert.Equal("bb22", release.Sha256);
    }

    [Fact]
    public async Task Requests_identify_themselves_to_github()
    {
        using var gh = new FakeGitHub(Route);

        _ = await Source(gh).GetLatestAsync(CancellationToken.None);

        // GitHub 对没有 User-Agent 的请求直接 403，这不是风格问题。
        Assert.All(gh.Requests, r => Assert.NotEmpty(r.Headers.UserAgent));
        Assert.Equal(2, gh.Requests.Count);   // releases/latest + manifest.json，不多不少
    }

    [Fact]
    public async Task A_release_without_a_manifest_is_ignored()
    {
        // 手工上传的 Release 没有 manifest.json，就没有校验和 —— 没有校验和的包不装。
        using var gh = new FakeGitHub(url => url.AbsolutePath.EndsWith("latest", StringComparison.Ordinal)
            ? Latest.Replace("\"name\": \"manifest.json\"", "\"name\": \"README.md\"", StringComparison.Ordinal)
            : null);

        Assert.Null(await Source(gh).GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_missing_architecture_yields_null_not_the_wrong_zip()
    {
        using var gh = new FakeGitHub(Route);

        // manifest 里没有这个 rid：宁可不提示，也不能给一个跑不起来的包。
        Assert.Null(await Source(gh, "linux-x64").GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Not_on_macos_means_no_update_offer()
    {
        using var gh = new FakeGitHub(Route);
        var source = new GitHubReleaseSource(
            new ConclaveOptions(), NullLogger<GitHubReleaseSource>.Instance, gh, rid: null);

        // rid 为 null = 本机不是 macOS（CurrentRid 的语义）。就地替换只做了 macOS，
        // 其他平台查到了也装不了，不如不提示 —— 而且一个请求都不该发。
        if (GitHubReleaseSource.CurrentRid is null)
        {
            Assert.Null(await source.GetLatestAsync(CancellationToken.None));
            Assert.Empty(gh.Requests);
        }
    }
}

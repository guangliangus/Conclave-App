using Conclave.Application;
using Conclave.Application.Ports;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

/// <summary>
/// 更新检查：查到新版就提示一次，不装；查失败不吵。
/// </summary>
public sealed class UpdateServiceTests
{
    private static ReleaseInfo Release(string version) => new(
        version, "v" + version, "osx-arm64",
        new Uri($"https://dl.test/Conclave-{version}-osx-arm64.zip"), "aa11", 1234,
        new Uri($"https://github.com/x/y/releases/tag/v{version}"));

    /// <summary>比本机大一个大版本 —— 不管开发态是 1.0.0 还是打包成什么，都算「新」。</summary>
    private static string Newer()
    {
        var major = int.Parse(AppInfo.Version.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
        return $"{major + 1}.0.0";
    }

    private static (UpdateService Service, NodeState State, FakeUpdateSource Source) Build()
    {
        var source = new FakeUpdateSource();
        var state = new NodeState();
        var service = new UpdateService(source, state, new ConclaveOptions(), NullLogger<UpdateService>.Instance);
        return (service, state, source);
    }

    [Fact]
    public async Task A_newer_release_is_offered_and_announced_once()
    {
        var (service, state, source) = Build();
        source.Latest = Release(Newer());

        await service.CheckOnceAsync(CancellationToken.None);
        await service.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(source.Latest, state.UpdateAvailable);

        // 每小时查一次，每次都弹的话通知列表会被「有新版本」灌满 —— 同一版只说一次。
        Assert.Single(state.Notices, n => n.Title.Contains("新版本", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_current_or_an_older_release_offers_nothing()
    {
        var (service, state, source) = Build();
        source.Latest = Release(AppInfo.Version);

        await service.CheckOnceAsync(CancellationToken.None);

        Assert.Null(state.UpdateAvailable);
        Assert.Empty(state.Notices);
    }

    [Fact]
    public async Task An_offer_is_withdrawn_once_the_node_has_caught_up()
    {
        var (service, state, source) = Build();
        source.Latest = Release(Newer());
        await service.CheckOnceAsync(CancellationToken.None);
        Assert.NotNull(state.UpdateAvailable);

        // 下一次查到「最新的就是本机这一版」（比如人已经手动升过了）：横幅要收掉。
        source.Latest = Release(AppInfo.Version);
        await service.CheckOnceAsync(CancellationToken.None);

        Assert.Null(state.UpdateAvailable);
    }

    [Fact]
    public async Task A_failing_check_is_quiet()
    {
        var (service, state, source) = Build();
        source.Throw = new HttpRequestException("断网了");

        await service.CheckOnceAsync(CancellationToken.None);

        // 断网、GitHub 抽风都是常态，不值一条通知，更不能把后台服务炸掉。
        Assert.Null(state.UpdateAvailable);
        Assert.Empty(state.Notices);
    }
}

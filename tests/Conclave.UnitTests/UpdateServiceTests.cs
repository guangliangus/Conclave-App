using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

/// <summary>
/// 更新：查到新版提示一次并自动装；组里有人先升了就提前查；查失败不吵。
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

    private static Elector Node(string id, string version) => TestElectors.Make(
        id, heartbeat: DateTimeOffset.UtcNow, appVersion: version);

    private sealed record Rig(
        UpdateService Service,
        NodeState State,
        FakeUpdateSource Source,
        FakeUpdateInstaller Installer,
        FakeMesh Mesh);

    private static Rig Build(Action<ConclaveOptions>? configure = null, params Elector[] peers)
    {
        var source = new FakeUpdateSource();
        var installer = new FakeUpdateInstaller();
        var mesh = new FakeMesh(Node("self", AppInfo.Version), peers);
        var state = new NodeState();
        var options = new ConclaveOptions();
        configure?.Invoke(options);

        return new Rig(
            new UpdateService(source, installer, mesh, state, options, NullLogger<UpdateService>.Instance),
            state, source, installer, mesh);
    }

    [Fact]
    public async Task A_newer_release_is_offered_and_announced_once()
    {
        var (service, state, source, _, _) = Build();
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
        var (service, state, source, _, _) = Build();
        source.Latest = Release(AppInfo.Version);

        await service.CheckOnceAsync(CancellationToken.None);

        Assert.Null(state.UpdateAvailable);
        Assert.Empty(state.Notices);
    }

    [Fact]
    public async Task An_offer_is_withdrawn_once_the_node_has_caught_up()
    {
        var (service, state, source, _, _) = Build();
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
        var (service, state, source, _, _) = Build();
        source.Throw = new HttpRequestException("断网了");

        await service.CheckOnceAsync(CancellationToken.None);

        // 断网、GitHub 抽风都是常态，不值一条通知，更不能把后台服务炸掉。
        Assert.Null(state.UpdateAvailable);
        Assert.Empty(state.Notices);
    }

    // ── 自动安装 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_newer_release_installs_itself_without_being_asked()
    {
        var rig = Build();
        rig.Source.Latest = Release(Newer());

        await rig.Service.CheckOnceAsync(CancellationToken.None);
        await rig.Service.Installing;

        // 「不需要用户确认」：查到就装。等空闲是安装器里面的事，不是让人守着点按钮。
        Assert.Equal(rig.Source.Latest, Assert.Single(rig.Installer.Installed));
    }

    [Fact]
    public async Task The_same_version_is_installed_at_most_once()
    {
        var rig = Build();
        rig.Source.Latest = Release(Newer());

        await rig.Service.CheckOnceAsync(CancellationToken.None);
        await rig.Service.Installing;
        await rig.Service.CheckOnceAsync(CancellationToken.None);
        await rig.Service.Installing;

        // 每小时查一次。装过了（哪怕装失败了）就别再试同一个包 —— 失败原因不会
        // 因为再等一小时就消失，而失败通知已经发过一条了。
        _ = Assert.Single(rig.Installer.Installed);
    }

    [Fact]
    public async Task A_machine_that_cannot_be_replaced_is_only_told_about_it()
    {
        var rig = Build();
        rig.Installer.Installable = false;      // 进程不在 .app 里 = 开发态
        rig.Source.Latest = Release(Newer());

        await rig.Service.CheckOnceAsync(CancellationToken.None);
        await rig.Service.Installing;

        // 没有可替换的目标就别每小时试一次、每次失败一条通知。提示照旧。
        Assert.Empty(rig.Installer.Installed);
        Assert.NotNull(rig.State.UpdateAvailable);
        _ = Assert.Single(rig.State.Notices);
    }

    [Fact]
    public async Task Turning_auto_install_off_leaves_it_to_the_banner()
    {
        var rig = Build(o => o.Update.AutoInstall = false);
        rig.Source.Latest = Release(Newer());

        await rig.Service.CheckOnceAsync(CancellationToken.None);
        await rig.Service.Installing;

        Assert.Empty(rig.Installer.Installed);
        Assert.NotNull(rig.State.UpdateAvailable);
    }

    // ── 组里有人先升了 ───────────────────────────────────────────────────

    [Fact]
    public void A_peer_on_a_newer_version_is_a_reason_to_check_early()
    {
        var rig = Build(null, Node("peer", Newer()));

        // 心跳带着各节点的 app 版本。同一批机器往往同时该升，先升的那台顺手
        // 把消息带给其余的 —— 不必等满一个小时。
        Assert.Equal(Newer(), rig.Service.PeerRunningNewerVersion());
    }

    [Fact]
    public void The_newest_peer_version_wins()
    {
        var major = int.Parse(AppInfo.Version.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
        var rig = Build(
            null,
            Node("a", $"{major + 1}.0.0"),
            Node("b", $"{major + 2}.0.0"),
            Node("c", AppInfo.Version));

        Assert.Equal($"{major + 2}.0.0", rig.Service.PeerRunningNewerVersion());
    }

    [Fact]
    public void Peers_on_the_same_or_an_older_version_are_no_reason()
    {
        var rig = Build(null, Node("peer", AppInfo.Version), Node("old", "0.0.1"));

        Assert.Null(rig.Service.PeerRunningNewerVersion());
    }

    [Fact]
    public void A_peer_that_stopped_sending_heartbeats_proves_nothing()
    {
        // 停在某一版、早就关机的机器，它留在成员表里的版本号说明不了任何事。
        var rig = Build(null, TestElectors.Make("stale", appVersion: Newer()));

        Assert.Null(rig.Service.PeerRunningNewerVersion());
    }

    [Fact]
    public void On_a_single_node_there_is_nobody_to_learn_from()
    {
        var rig = Build();

        // 成员表里只有自己。本机版本再旧也不该把自己当成「组里有人更新了」。
        Assert.Null(rig.Service.PeerRunningNewerVersion());
    }
}


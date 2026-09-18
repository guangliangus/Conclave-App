using Conclave.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

/// <summary>
/// 盯配置文件的那个服务：读出来之后怎么落到活着的选项上，以及什么时候<b>不</b>落。
/// </summary>
/// <remarks>
/// 这里直接驱动 <c>Reload()</c>，不碰文件系统也不等防抖 —— 要测的是「读到什么就做什么」，
/// 而文件监视器什么时候触发是运行时的事，在单元测试里等它只会换来一个随机失败的用例。
/// </remarks>
public class ConfigReloadServiceTests
{
    private static ConfigReloadService Service(IConfiguration config, ConclaveOptions live, NodeState state)
        => new(config, live, state, NullLogger<ConfigReloadService>.Instance);

    private static IConfiguration Config(params (string Key, string Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value))
            .Build();

    [Fact]
    public void A_changed_file_lands_on_the_live_options()
    {
        var live = new ConclaveOptions { AutoReview = false, MaxReviewAttempts = 3 };

        using var service = Service(
            Config(("Conclave:AutoReview", "true"), ("Conclave:MaxReviewAttempts", "1")),
            live,
            new NodeState());

        service.Reload();

        Assert.True(live.AutoReview);
        Assert.Equal(1, live.MaxReviewAttempts);
    }

    /// <summary>
    /// 读出来是空的那一轮必须整个跳过。
    /// </summary>
    /// <remarks>
    /// 文件正被截断时（同步器先清空再写、编辑器保存）读到的就是一个什么都没有的配置节。
    /// 照单全收等于把配置清空：AutoReview 关掉、白名单清空、Lark 关掉，全部退回默认值，
    /// 而且一声不吭 —— 表面上只是「今天怎么不评审了」。
    /// </remarks>
    [Fact]
    public void An_empty_read_is_ignored_rather_than_wiping_the_config()
    {
        var live = new ConclaveOptions { AutoReview = true, MaxReviewAttempts = 1 };

        using var service = Service(Config(), live, new NodeState());

        service.Reload();

        Assert.True(live.AutoReview);
        Assert.Equal(1, live.MaxReviewAttempts);
    }

    /// <summary>要重启才生效的改动，得在界面上说出来，不能装作已经生效。</summary>
    [Fact]
    public void A_restart_only_change_says_so_on_the_panel()
    {
        var live = new ConclaveOptions();
        var state = new NodeState();

        using var service = Service(Config(("Conclave:Mesh:HttpPort", "50000")), live, state);

        service.Reload();

        var notice = Assert.Single(state.Notices);
        Assert.Equal(NoticeKind.Warn, notice.Kind);
        Assert.Contains("Mesh", notice.Detail, StringComparison.Ordinal);
        Assert.Equal(47708, live.Mesh.HttpPort);
    }

    /// <summary>全都热更得了的时候，通知是「已生效」而不是「要重启」。</summary>
    [Fact]
    public void A_fully_applied_change_does_not_ask_for_a_restart()
    {
        var state = new NodeState();

        using var service = Service(Config(("Conclave:AutoReview", "true")), new ConclaveOptions(), state);

        service.Reload();

        var notice = Assert.Single(state.Notices);
        Assert.Equal(NoticeKind.Ok, notice.Kind);
    }

    /// <summary>
    /// meshsettings.json 按 key 覆盖 appsettings.json，未覆盖的字段保留本地值。
    /// </summary>
    [Fact]
    public void Meshsettings_overrides_appsettings_key_by_key()
    {
        var appsettings = new Dictionary<string, string?>
        {
            ["Conclave:AutoReview"] = "false",
            ["Conclave:MaxReviewAttempts"] = "3",
            ["Conclave:Lark:AppSecret"] = "old-secret",
            ["Conclave:Quorum:Default"] = "1",
        };

        var meshsettings = new Dictionary<string, string?>
        {
            ["Conclave:AutoReview"] = "true",
            ["Conclave:Lark:AppSecret"] = "synced-secret",
            ["Conclave:ConfigSync:Version"] = "5",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(appsettings)
            .AddInMemoryCollection(meshsettings)
            .Build();

        var live = new ConclaveOptions();
        using var service = Service(config, live, new NodeState());
        service.Reload();

        // 覆盖的键取 meshsettings
        Assert.True(live.AutoReview);
        Assert.Equal("synced-secret", live.Lark.AppSecret);
        Assert.Equal(5, live.ConfigSync.Version);

        // 未被覆盖的键保留 appsettings 本地值
        Assert.Equal(3, live.MaxReviewAttempts);
        Assert.Equal(1, live.Quorum.Default);
    }
}

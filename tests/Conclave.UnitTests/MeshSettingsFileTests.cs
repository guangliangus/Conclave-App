using Conclave.Application;
using Microsoft.Extensions.Configuration;

namespace Conclave.UnitTests;

/// <summary>
/// 集群配置里的集合，必须整体替换本地的。
/// </summary>
/// <remarks>
/// 钉的是一个实测出来的坑：.NET 的配置对数组按<b>索引</b>合并，所以靠「meshsettings.json
/// 覆盖 appsettings.json」这条叠加关系去覆盖列表是错的，而错法完全静默 ——
/// 集群把列表改短或清空，本地多出来的那几项会继续生效。
/// </remarks>
public sealed class MeshSettingsFileTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "conclave-mesh-" + Guid.NewGuid().ToString("N"));

    public MeshSettingsFileTests() => Directory.CreateDirectory(_home);

    /// <summary>照 Program.CreateBuilder 的叠加顺序绑一遍，再走同步文件的集合替换。</summary>
    private ConclaveOptions Bind(string appsettings, string meshsettings)
    {
        File.WriteAllText(Path.Combine(_home, "appsettings.json"), appsettings);
        File.WriteAllText(Path.Combine(_home, MeshSettingsFile.Name), meshsettings);

        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(_home, "appsettings.json"))
            .AddJsonFile(Path.Combine(_home, MeshSettingsFile.Name))
            .Build();

        var options = new ConclaveOptions { HomeDirectory = _home };
        config.GetSection(SyncableConfig.Section).Bind(options);

        _ = MeshSettingsFile.ApplyCollections(options, MeshSettingsFile.TryRead(options)!);
        return options;
    }

    /// <summary>集群推一个更短的列表，本地多出来的不能留着。</summary>
    [Fact]
    public void A_shorter_synced_list_replaces_the_local_one_entirely()
    {
        var options = Bind(
            """{"Conclave":{"ProjectDenyList":["legacy-a","legacy-b","legacy-c"]}}""",
            """{"Conclave":{"ProjectDenyList":["only-x"]}}""");

        Assert.Equal(["only-x"], options.ProjectDenyList);
    }

    /// <summary>
    /// 集群清空列表，本地就得真的空。
    /// </summary>
    /// <remarks>
    /// 这是最糟的一种：空数组一个索引都不提供，纯靠配置叠加的话本地条目<b>全部</b>留着 ——
    /// 「白名单已经清了」在任何有本地条目的机器上完全无效。
    /// </remarks>
    [Fact]
    public void An_emptied_synced_list_really_empties_the_local_one()
    {
        var options = Bind(
            """{"Conclave":{"ProjectAllowList":["a","b"]}}""",
            """{"Conclave":{"ProjectAllowList":[]}}""");

        Assert.Empty(options.ProjectAllowList);
    }

    /// <summary>同步文件里没提到的集合，保持本地值 —— 那才是「按 key 覆盖」。</summary>
    [Fact]
    public void A_list_the_cluster_does_not_mention_keeps_its_local_value()
    {
        var options = Bind(
            """{"Conclave":{"ProjectDenyList":["local-only"]}}""",
            """{"Conclave":{"AutoReview":true}}""");

        Assert.Equal(["local-only"], options.ProjectDenyList);
        Assert.True(options.AutoReview);
    }

    /// <summary>字典是同一个毛病：集群删掉的映射，本地那条不能还在。</summary>
    [Fact]
    public void A_removed_user_map_entry_disappears_locally()
    {
        var options = Bind(
            """{"Conclave":{"Lark":{"UserMap":{"alice":"ou_a","bob":"ou_b"}}}}""",
            """{"Conclave":{"Lark":{"UserMap":{"alice":"ou_NEW"}}}}""");

        Assert.Equal("ou_NEW", Assert.Single(options.Lark.UserMap).Value);
        Assert.False(options.Lark.UserMap.ContainsKey("bob"));
    }

    /// <summary>标量仍然走配置叠加，这一层不碰它们。</summary>
    [Fact]
    public void Scalars_still_come_from_the_normal_config_layering()
    {
        var options = Bind(
            """{"Conclave":{"MaxReviewAttempts":3,"AutoReview":false}}""",
            """{"Conclave":{"AutoReview":true}}""");

        Assert.True(options.AutoReview);
        Assert.Equal(3, options.MaxReviewAttempts);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录删不掉不该让测试变红。
        }
    }
}

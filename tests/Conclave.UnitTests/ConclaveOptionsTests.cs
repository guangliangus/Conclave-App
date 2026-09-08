using Conclave.Application;
using Conclave.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Conclave.UnitTests;

public class ConclaveOptionsTests
{
    private static ConclaveOptions Bind(string json)
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        return new ServiceCollection()
            .AddConclaveNode(config)
            .BuildServiceProvider()
            .GetRequiredService<ConclaveOptions>();
    }

    [Fact]
    public void Configured_repo_roots_replace_the_defaults_instead_of_appending()
    {
        var opts = Bind("""
            { "Conclave": { "RepoSearchRoots": [ "~/only-here" ] } }
            """);

        // .NET 的配置绑定对集合是追加语义，属性初始化器里带默认值会得到两份 ——
        // .app 的启动日志里这个列表真的重复过一遍。
        Assert.Equal(["~/only-here"], opts.RepoSearchRoots);
    }

    [Fact]
    public void Defaults_apply_when_config_says_nothing()
    {
        var opts = Bind("""{ "Conclave": { } }""");

        Assert.Equal(ConclaveOptions.DefaultRepoSearchRoots, opts.RepoSearchRoots);
        Assert.Empty(opts.ProjectAllowList);           // 留空 = 轮询全部 project
        Assert.False(opts.AutoReview);                 // 安全默认
        Assert.False(opts.PostToAzureDevOps);
        Assert.False(opts.Mesh.Enabled);
    }

    [Fact]
    public void Defaults_are_not_applied_twice_when_options_are_passed_directly()
    {
        var explicitOptions = new ConclaveOptions();
        explicitOptions.RepoSearchRoots.Add("~/one");

        var opts = new ServiceCollection()
            .AddConclaveNode(explicitOptions)
            .BuildServiceProvider()
            .GetRequiredService<ConclaveOptions>();

        Assert.Equal(["~/one"], opts.RepoSearchRoots);
    }

    [Fact]
    public void Mesh_section_binds()
    {
        var opts = Bind("""
            { "Conclave": { "Mesh": { "Enabled": true, "HttpPort": 5555, "Fanout": 7 } } }
            """);

        Assert.True(opts.Mesh.Enabled);
        Assert.Equal(5555, opts.Mesh.HttpPort);
        Assert.Equal(7, opts.Mesh.Fanout);
        Assert.Equal("239.255.42.7", opts.Mesh.MulticastAddress);   // 未配置的字段保留默认
    }
}

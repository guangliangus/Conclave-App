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
    public void Defaults_apply_when_config_says_nothing()
    {
        var opts = Bind("""{ "Conclave": { } }""");

        Assert.Empty(opts.ProjectAllowList);           // 留空 = 轮询全部 project
        Assert.False(opts.AutoReview);                 // 安全默认
        Assert.False(opts.PostToAzureDevOps);
        Assert.False(opts.Mesh.Enabled);
        Assert.False(opts.ClaudeUsage.IsUnbounded);    // 默认带预算，额度硬规则默认生效
    }

    [Fact]
    public void Configured_project_allow_list_replaces_the_default_instead_of_appending()
    {
        var opts = Bind("""
            { "Conclave": { "ProjectAllowList": [ "edison-test" ] } }
            """);

        // .NET 的配置绑定对集合是追加语义（接口类型的集合有没有 setter 都一样）——
        // 属性初始化器里一旦带上默认值，「默认值 + 配置文件」就会得到两份。
        // 早先的 RepoSearchRoots 就这么在 .app 启动日志里重复过一遍，所以这类属性一律不给默认值。
        Assert.Equal(["edison-test"], opts.ProjectAllowList);
    }

    [Fact]
    public void Workspace_and_usage_override_live_under_the_home_directory()
    {
        var opts = Bind("""
            { "Conclave": { "HomeDirectory": "/tmp/conclave-x" } }
            """);

        // 工作区刻意跟账本同在 HomeDirectory 下：崩溃残留要能被下次启动整目录清掉，
        // 而系统临时目录里混着别人的东西，不敢整目录清。
        Assert.Equal(Path.Combine("/tmp/conclave-x", "work"), opts.WorkspaceRoot);
        
    }

    [Fact]
    public void Claude_usage_section_binds_and_can_be_disabled()
    {
        var opts = Bind("""
            { "Conclave": { "ClaudeUsage": { "Window": "05:00:00", "TokenBudget": 0, "CostUsdBudget": 0 } } }
            """);

        Assert.Equal(TimeSpan.FromHours(5), opts.ClaudeUsage.Window);
        Assert.True(opts.ClaudeUsage.IsUnbounded);     // 两个预算都清零 = 额度规则关掉
    }

    [Theory]
    [InlineData("edison-test", true)]        // 精确名
    [InlineData("EDISON-TEST", true)]        // 大小写不敏感 —— project 名大小写不统一
    [InlineData("my-test-proj", true)]       // *test* 命中中间
    [InlineData("testbed", true)]
    [InlineData("liontrip-cms", false)]
    [InlineData("payment-center", false)]
    [InlineData("contest-service", true)]    // ⚠️ *test* 也会命中 "contest"，通配就是这样
    public void The_project_deny_list_matches_with_wildcards(string project, bool denied)
    {
        var opts = Bind("""
            { "Conclave": { "ProjectDenyList": [ "*test*" ] } }
            """);

        Assert.Equal(denied, opts.IsProjectDenied(project));
    }

    [Fact]
    public void An_empty_deny_list_leaves_only_the_built_in_suffixes()
    {
        var opts = Bind("""{ "Conclave": { } }""");

        Assert.Empty(opts.ProjectDenyList);
        Assert.False(opts.IsProjectDenied("liontrip-cms"));
        Assert.Equal(["a", "b"], opts.FilterProjects(["a", "b"]));

        // 后缀排除是另一条路，而且默认开着 —— 黑名单空不代表什么都不排除。
        Assert.True(opts.IsProjectDenied("edison-test"));
    }

    [Theory]
    [InlineData("edison-test", true)]
    [InlineData("EDISON-TEST", true)]        // 大小写不敏感 —— project 名大小写不统一
    [InlineData("payments-qa", true)]
    [InlineData("Payments-QA", true)]
    [InlineData("liontrip-cms", false)]
    [InlineData("payment-center", false)]
    [InlineData("contest-service", false)]   // 后缀匹配不会像 *test* 那样把 contest 也带走
    [InlineData("test-harness", false)]      // 只认结尾，不认开头和中间
    [InlineData("qa", false)]                // 光叫 qa 不算 —— 要的是 "-qa" 这个后缀
    public void Non_production_projects_are_excluded_by_suffix_out_of_the_box(string project, bool denied)
    {
        var opts = Bind("""{ "Conclave": { } }""");

        Assert.Equal(denied, opts.IsProjectDenied(project));
    }

    [Fact]
    public void The_suffix_list_replaces_the_default_instead_of_adding_to_it()
    {
        // 这条正是它不做成数组的原因：数组在配置绑定里是<b>追加</b>的，
        // 自己配一份会变成「默认那两个 + 你写的」，而且没有任何提示。
        var opts = Bind("""
            { "Conclave": { "ExcludedProjectSuffixes": "-uat;-sandbox" } }
            """);

        Assert.False(opts.IsProjectDenied("edison-test"));
        Assert.True(opts.IsProjectDenied("billing-UAT"));
        Assert.True(opts.IsProjectDenied("demo-sandbox"));
    }

    [Fact]
    public void An_empty_suffix_setting_turns_the_exclusion_off_entirely()
    {
        var opts = Bind("""{ "Conclave": { "ExcludedProjectSuffixes": "" } }""");

        Assert.False(opts.IsProjectDenied("edison-test"));
        Assert.False(opts.IsProjectDenied("payments-qa"));
    }

    [Fact]
    public void Suffix_exclusion_and_the_deny_list_are_a_union()
    {
        var opts = Bind("""
            { "Conclave": { "ProjectDenyList": [ "*demo*" ] } }
            """);

        Assert.True(opts.IsProjectDenied("sales-demo"));     // 黑名单
        Assert.True(opts.IsProjectDenied("edison-test"));    // 后缀
        Assert.False(opts.IsProjectDenied("liontrip-cms"));
    }

    [Fact]
    public void Blank_and_spaced_suffixes_are_ignored_rather_than_matching_everything()
    {
        // 空段若当成「后缀是空串」，EndsWith("") 对每个 project 都是 true ——
        // 一次把所有 project 排空，而日志上只看得出「轮询 0 个 project」。
        var opts = Bind("""
            { "Conclave": { "ExcludedProjectSuffixes": " -qa ; ; -test ;" } }
            """);

        Assert.False(opts.IsProjectDenied("liontrip-cms"));
        Assert.True(opts.IsProjectDenied("payments-qa"));
        Assert.True(opts.IsProjectDenied("edison-test"));
    }

    [Fact]
    public void The_deny_list_applies_after_the_allow_list()
    {
        var opts = Bind("""
            {
              "Conclave": {
                "ProjectAllowList": [ "liontrip-cms", "edison-test" ],
                "ProjectDenyList": [ "*test*" ]
              }
            }
            """);

        // 白名单决定候选范围，黑名单再从里面剔除 —— 两个都配时黑名单说话。
        Assert.Equal(["liontrip-cms"], opts.FilterProjects(opts.ProjectAllowList));
    }

    [Fact]
    public void Blank_deny_patterns_are_ignored_rather_than_matching_everything()
    {
        var opts = Bind("""
            { "Conclave": { "ProjectDenyList": [ "", "  ", "*demo*" ] } }
            """);

        // 空模式若当成通配会把所有 project 一次排空 —— 那种失效是静默的：
        // 日志上看是「轮询 0 个 project」，很容易被当成权限问题去查。
        Assert.False(opts.IsProjectDenied("liontrip-cms"));
        Assert.True(opts.IsProjectDenied("sales-demo"));
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

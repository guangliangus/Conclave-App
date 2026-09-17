using Conclave.Application;

namespace Conclave.UnitTests;

/// <summary>
/// 配置热更新：哪些拷、哪些不拷、集合会不会叠起来。
/// </summary>
/// <remarks>
/// 三种做错的方式表现都是静默的：拷漏了是「改了配置没反应」，拷多了是「内存里的值跟实际
/// 行为对不上」（而面板和启动日志都照内存那份显示，于是再没地方看得出不一致），
/// 集合叠起来是「从白名单里删掉的 project 还在被轮询」。所以每一类各钉一条。
/// </remarks>
public class ConfigHotReloadTests
{
    [Fact]
    public void Hot_fields_take_effect_on_the_live_instance()
    {
        var live = new ConclaveOptions { AutoReview = false, MaxReviewAttempts = 3 };
        var fresh = new ConclaveOptions
        {
            AutoReview = true,
            MaxReviewAttempts = 1,
            PollInterval = TimeSpan.FromSeconds(5),
            PostToAzureDevOps = true,
        };

        var pending = ConfigHotReload.Apply(live, fresh);

        Assert.True(live.AutoReview);
        Assert.True(live.PostToAzureDevOps);
        Assert.Equal(1, live.MaxReviewAttempts);
        Assert.Equal(TimeSpan.FromSeconds(5), live.PollInterval);
        Assert.Empty(pending);
    }

    /// <summary>启动时就被别的东西吃掉的那几样，不拷，只报。</summary>
    [Fact]
    public void Restart_only_fields_are_reported_and_left_alone()
    {
        var live = new ConclaveOptions { HomeDirectory = "/old", AzIdentityOverride = string.Empty };
        live.Mesh.HttpPort = 47708;

        var fresh = new ConclaveOptions { HomeDirectory = "/new", AzIdentityOverride = "someone" };
        fresh.Mesh.HttpPort = 50000;

        var pending = ConfigHotReload.Apply(live, fresh);

        Assert.Equal("/old", live.HomeDirectory);
        Assert.Equal(string.Empty, live.AzIdentityOverride);
        Assert.Equal(47708, live.Mesh.HttpPort);
        Assert.Equal(["HomeDirectory", "AzIdentityOverride", "Mesh"], pending);
    }

    /// <summary>
    /// 集合整个换掉，不是往上叠。
    /// </summary>
    /// <remarks>
    /// .NET 的配置绑定对 <see cref="IList{T}"/> 是追加语义。这条钉的是「先绑到临时实例、
    /// 再整体换过去」这个做法本身 —— 换成「把配置直接绑进活着的那个实例」的话，
    /// 每热更新一次白名单就多一份，而多出来的那份看起来完全正常。
    /// </remarks>
    [Fact]
    public void Lists_are_replaced_not_appended()
    {
        var live = new ConclaveOptions();
        live.ProjectAllowList.Add("a");
        live.ProjectAllowList.Add("b");

        var fresh = new ConclaveOptions();
        fresh.ProjectAllowList.Add("c");

        _ = ConfigHotReload.Apply(live, fresh);

        Assert.Equal(["c"], live.ProjectAllowList);
    }

    /// <summary>
    /// 飞书那一整块跟着换，连 UserMap 一起。
    /// </summary>
    /// <remarks>
    /// <c>LarkNotifier</c> 每次现读 <c>options.Lark</c>，所以换掉这个对象就等于换了应用。
    /// 旧的 UserMap 必须一起走：留着的话，从配置里删掉的那条映射还会继续生效。
    /// </remarks>
    [Fact]
    public void The_lark_block_is_swapped_whole()
    {
        var live = new ConclaveOptions();
        live.Lark.AppId = "cli_old";
        live.Lark.UserMap["someone"] = "ou_old";

        var fresh = new ConclaveOptions();
        fresh.Lark.AppId = "cli_new";

        _ = ConfigHotReload.Apply(live, fresh);

        Assert.Equal("cli_new", live.Lark.AppId);
        Assert.Empty(live.Lark.UserMap);
    }
}

using Avalonia.Controls.ApplicationLifetimes;
using Conclave.Application;

namespace Conclave.UnitTests;

/// <summary>
/// <c>conclave://</c> 深链的拼与拆。
/// </summary>
/// <remarks>
/// 两头分处两个程序集（拼在 LarkCard、拆在 App），而对不上的表现是「点了按钮没反应」——
/// 系统找不到能处理的应用，既不报错也不提示，跟「压根没装 Conclave」一模一样。
/// 所以这里逐字钉住往返。
/// </remarks>
public class DeepLinkTests
{
    /// <summary>revision id 带 <c>/</c> 和 <c>@</c>，正是最容易在 URI 里被吃掉的两个。</summary>
    [Theory]
    [InlineData("liontrip-order/2954@bdcc84babd7486340b07812586a30f676862f331")]
    [InlineData("a/b/c@d")]
    [InlineData("带中文的/1@x")]
    [InlineData("has space/2@y")]
    [InlineData("amp&equals=in/3@z")]
    public void A_revision_survives_the_round_trip(string revisionId)
    {
        Assert.Equal(
            (DeepLinkTarget.Review, revisionId),
            DeepLink.Parse(new Uri(DeepLink.ForReview(revisionId))));

        Assert.Equal(
            (DeepLinkTarget.Pr, revisionId),
            DeepLink.Parse(new Uri(DeepLink.ForPr(revisionId))));
    }

    /// <summary>两种目标不能撞在一起 —— 一个开日志，一个只是在队列里指出来。</summary>
    [Fact]
    public void The_two_targets_are_different_links()
    {
        Assert.StartsWith("conclave://review?revision=", DeepLink.ForReview("x/1@a"), StringComparison.Ordinal);
        Assert.StartsWith("conclave://pr?revision=", DeepLink.ForPr("x/1@a"), StringComparison.Ordinal);
    }

    /// <summary>系统转交过来的 URI 是别的应用也能构造的，畸形输入一律返回 null 而不是抛。</summary>
    [Theory]
    [InlineData("https://example.com/review?revision=x")]   // 别的协议
    [InlineData("conclave://settings?revision=x")]           // 不认识的 host
    [InlineData("conclave://review")]                        // 没有查询串
    [InlineData("conclave://review?other=x")]                // 没有 revision
    [InlineData("conclave://review?revision=")]              // revision 是空的
    [InlineData("conclave://pr?revision=")]
    public void Anything_else_is_not_a_deep_link(string raw)
        => Assert.Null(DeepLink.Parse(new Uri(raw)));

    [Fact]
    public void A_null_uri_is_not_a_deep_link() => Assert.Null(DeepLink.Parse(null));

    /// <summary>协议名跟打包脚本里注册的那个必须一致。</summary>
    [Fact]
    public void The_scheme_matches_what_the_bundle_registers()
    {
        var plist = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "package-macos.sh"));

        Assert.Contains(
            $"<key>CFBundleURLSchemes</key>  <array><string>{DeepLink.Scheme}</string></array>",
            plist,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// URL 激活<b>不</b>在桌面生命周期上，只能从 <c>TryGetFeature</c> 取。
    /// </summary>
    /// <remarks>
    /// 这条钉的是一个真出过的洞：<c>HookDeepLinks</c> 原先写的是
    /// <c>ApplicationLifetime is IActivatableLifetime</c>，而进程走的是
    /// <c>StartWithClassicDesktopLifetime</c> —— 那个判断恒为假，于是<b>一条深链都收不到</b>，
    /// 不报错、不崩，表现成「点了按钮没反应」，跟压根没注册协议一模一样。
    /// <para>
    /// 哪天有人觉得 <c>TryGetFeature</c> 绕、想改回类型判断，红的是这条。
    /// </para>
    /// </remarks>
    [Fact]
    public void The_desktop_lifetime_is_not_where_url_activation_lives()
        => Assert.False(
            typeof(IActivatableLifetime).IsAssignableFrom(typeof(ClassicDesktopStyleApplicationLifetime)),
            "桌面生命周期开始实现 IActivatableLifetime 了 —— 可以去掉 App.HookDeepLinks 里的 TryGetFeature");

    /// <summary>从测试的输出目录往上找到仓库根（认 <c>Conclave.slnx</c>）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conclave.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }
}

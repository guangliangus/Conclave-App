using Conclave.Application;

namespace Conclave.UnitTests;

/// <summary>
/// 版本比较。「有新版本」到底该不该提示，全看这一个判断。
/// </summary>
public sealed class AppInfoTests
{
    [Theory]
    [InlineData("1.3.0", "1.2.9", true)]
    [InlineData("1.2.9", "1.3.0", false)]
    [InlineData("1.3.0", "1.3.0", false)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("1.3", "1.2.9.9", true)]          // 段数不同也能比
    [InlineData("v1.3.0", "1.2.0", true)]         // tag 带 v
    public void Numeric_parts_compare_by_segment(string candidate, string current, bool expected)
        => Assert.Equal(expected, AppInfo.IsNewer(candidate, current));

    [Theory]
    [InlineData("1.3.0", "1.3.0-beta.1", true)]      // 正式版新于预发布
    [InlineData("1.3.0-beta.1", "1.3.0", false)]
    [InlineData("1.3.0-beta.2", "1.3.0-beta.1", true)]
    [InlineData("1.3.0-rc.1", "1.3.0-beta.9", true)]  // 字面序：r > b
    public void Prerelease_suffix_orders_below_the_release(string candidate, string current, bool expected)
        => Assert.Equal(expected, AppInfo.IsNewer(candidate, current));

    [Theory]
    [InlineData("garbage", "1.0.0")]
    [InlineData("1.0.0", "garbage")]
    [InlineData("", "1.0.0")]
    [InlineData("1.3.0-", "1.0.0")]
    public void Unparseable_versions_are_never_newer(string candidate, string current)
        // 宁可少弹一次提示，不要因为 tag 打错格式而反复催人升级到一个不存在的版本。
        => Assert.False(AppInfo.IsNewer(candidate, current));

    [Fact]
    public void The_running_version_is_readable_and_comparable()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.Version));
        Assert.DoesNotContain('+', AppInfo.Version);   // 构建元数据要去掉
        Assert.True(AppInfo.IsNewer("999.0.0", AppInfo.Version));
    }
}

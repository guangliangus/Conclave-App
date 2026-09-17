using Microsoft.Extensions.Configuration;

namespace Conclave.UnitTests;

/// <summary>
/// 配置文件源怎么装 —— 跟 <c>Program.CreateBuilder</c> 里那段必须是同一套写法。
/// </summary>
/// <remarks>
/// 两件事别处覆盖不到，而且错了都是静默的：
/// <list type="bullet">
/// <item>
/// 绝对路径在 <c>AddJsonFile(Action&lt;JsonConfigurationSource&gt;)</c> 这个重载下解析不出来时，
/// 读到的是<b>空配置</b>而不是报错 —— 表现成「所有配置都退回默认值」，没有任何线索指向路径。
/// </item>
/// <item>
/// 不忽略加载异常的话，一次普通的保存（文件监视器在写到一半时就触发）会把
/// <c>FormatException</c> 抛到监视线程上。
/// </item>
/// </list>
/// </remarks>
public sealed class WatchedConfigFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "conclave-cfg-" + Guid.NewGuid().ToString("N"));

    public WatchedConfigFileTests() => Directory.CreateDirectory(_dir);

    /// <summary>照 <c>Program.CreateBuilder</c> 的方式装一个文件源。</summary>
    private IConfigurationRoot Build(string fileName)
    {
        var path = Path.Combine(_dir, fileName);

        return new ConfigurationBuilder()
            .AddJsonFile(source =>
            {
                source.Path = path;
                source.Optional = true;
                source.ReloadOnChange = true;
                source.OnLoadException = ctx => ctx.Ignore = true;
                source.ResolveFileProvider();
            })
            .Build();
    }

    [Fact]
    public void An_absolute_path_is_actually_read()
    {
        File.WriteAllText(
            Path.Combine(_dir, "appsettings.json"),
            """{"Conclave":{"MaxReviewAttempts":7}}""");

        Assert.Equal("7", Build("appsettings.json")["Conclave:MaxReviewAttempts"]);
    }

    /// <summary>文件不在是常态：程序目录那份打包才有，用户目录那份多数机器上没有。</summary>
    [Fact]
    public void A_missing_file_is_not_an_error()
        => Assert.Null(Build("nope.json")["Conclave:MaxReviewAttempts"]);

    /// <summary>写到一半的 JSON 只能当没读到，不能抛。</summary>
    [Fact]
    public void A_half_written_file_is_ignored_instead_of_throwing()
    {
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), """{"Conclave":{"MaxRev""");

        Assert.Null(Build("appsettings.json")["Conclave:MaxReviewAttempts"]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录删不掉不该让测试变红。
        }
    }
}

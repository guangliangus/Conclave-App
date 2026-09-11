using Conclave.Infrastructure.Update;

namespace Conclave.UnitTests;

/// <summary>
/// 「要替换的 .app 是哪个」的判定。
/// </summary>
/// <remarks>
/// 为什么这几条要有守卫：这个函数的返回值会被安装器整个 <c>mv</c> 走。认错一次
/// 不是「更新失败」，是一棵目录树被搬到 <c>.previous</c>、原地换成下载来的发布包。
/// 真出过一次 —— 开发态进程在 <c>src/Conclave.App/bin/Debug/net10.0/</c> 下，
/// 而当时的后缀比较是忽略大小写的，于是源码目录被当成了 bundle。
/// </remarks>
public sealed class MacUpdateInstallerTests : IDisposable
{
    private readonly string _root;

    public MacUpdateInstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "conclave-bundle-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_root);
    }

    /// <summary>造一个目录树，返回 <paramref name="executable"/> 的完整路径。</summary>
    private string Make(string executable, params string[] directories)
    {
        foreach (var dir in directories)
        {
            _ = Directory.CreateDirectory(Path.Combine(_root, dir));
        }

        var path = Path.Combine(_root, executable);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void A_real_bundle_is_found_from_the_executable_inside_it()
    {
        var exe = Make("Conclave.app/Contents/MacOS/conclave");

        Assert.Equal(Path.Combine(_root, "Conclave.app"), MacUpdateInstaller.FindBundle(exe));
    }

    /// <summary>
    /// 回归：开发态进程在 <c>src/Conclave.App/bin/Debug/net10.0/</c> 下，源码目录不是 bundle。
    /// </summary>
    [Fact]
    public void The_project_source_directory_is_not_mistaken_for_a_bundle()
    {
        var exe = Make("src/Conclave.App/bin/Debug/net10.0/conclave");

        Assert.Null(MacUpdateInstaller.FindBundle(exe));
    }

    /// <summary>
    /// 上一条的最小化版本：连 <c>Contents/MacOS</c> 都齐了，也只能靠大小写把它挡掉。
    /// </summary>
    [Fact]
    public void The_suffix_is_compared_case_sensitively()
    {
        var exe = Make("Conclave.App/bin/conclave", "Conclave.App/Contents/MacOS");

        Assert.Null(MacUpdateInstaller.FindBundle(exe));
    }

    /// <summary>名字对但里面没有 <c>Contents/MacOS</c>：不是 bundle，只是个碰巧同名的目录。</summary>
    [Fact]
    public void A_directory_merely_named_like_a_bundle_is_not_one()
    {
        var exe = Make("notes.app/conclave");

        Assert.Null(MacUpdateInstaller.FindBundle(exe));
    }

    [Fact]
    public void An_executable_outside_any_bundle_resolves_to_nothing()
    {
        var exe = Make("build/output/conclave");

        Assert.Null(MacUpdateInstaller.FindBundle(exe));
    }

    /// <summary>往上找不止一层：helper 进程可以嵌在 bundle 深处。</summary>
    [Fact]
    public void The_search_walks_up_past_intermediate_directories()
    {
        var exe = Make("Conclave.app/Contents/MacOS/helpers/probe");

        Assert.Equal(Path.Combine(_root, "Conclave.app"), MacUpdateInstaller.FindBundle(exe));
    }

    [Fact]
    public void No_executable_path_resolves_to_nothing()
    {
        Assert.Null(MacUpdateInstaller.FindBundle(null));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

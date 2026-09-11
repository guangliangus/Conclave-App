using Conclave.Application;
using Conclave.Infrastructure;

namespace Conclave.UnitTests;

/// <summary>
/// 外部工具的定位。
/// </summary>
/// <remarks>
/// 为什么这条要有守卫：从 Finder 或 <c>open</c> 启动 <c>.app</c> 时 LaunchServices 只给
/// <c>/usr/bin:/bin:/usr/sbin:/sbin</c>，而 <c>az</c> 在 <c>/opt/homebrew/bin</c>、
/// <c>claude</c> 在 <c>~/.local/bin</c>。同一个二进制在终端里一切正常、双击图标就变成
/// 「读不到 az 登录身份」和「0 个 project」—— 而提示还指向了错误的方向。
/// </remarks>
public sealed class ExecutableResolverTests : IDisposable
{
    private readonly string _dir;
    private readonly string _tool;

    public ExecutableResolverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "conclave-exe-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_dir);
        _tool = Path.Combine(_dir, "faketool");
        File.WriteAllText(_tool, "#!/bin/sh\nexit 0\n");
    }

    private static ExecutableResolver Build(params string[] extraPaths)
    {
        var options = new ConclaveOptions();
        foreach (var path in extraPaths)
        {
            options.ExtraToolPaths.Add(path);
        }

        return new ExecutableResolver(options);
    }

    [Fact]
    public void A_tool_outside_PATH_is_found_through_the_extra_paths()
    {
        Assert.Equal(_tool, Build(_dir).Resolve("faketool"));
    }

    [Fact]
    public void A_tool_on_PATH_is_found_without_any_configuration()
    {
        // /usr/bin/git 在最小 PATH 里也有 —— 这是唯一不受影响的那个工具。
        var resolved = Build().Resolve("git");

        Assert.EndsWith("git", resolved, StringComparison.Ordinal);
        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public void The_real_az_and_claude_are_found_even_with_a_LaunchServices_style_PATH()
    {
        // 先在完整 PATH 下解析一次，作为「这台机器上它到底在哪」的基准。
        var full = new[] { "az", "claude" }.ToDictionary(t => t, t => Build().TryResolve(t));

        var original = Environment.GetEnvironmentVariable("PATH");
        try
        {
            // 复刻 Finder 启动时的环境。
            Environment.SetEnvironmentVariable("PATH", "/usr/bin:/bin:/usr/sbin:/sbin");
            var resolver = Build();

            // 兜底目录里包含 Homebrew 与 ~/.local/bin，所以这两个仍应找得到 ——
            // 判据是「最小 PATH 下解析到的，跟完整 PATH 下是同一个文件」。
            //
            // 早先这里断言的是「路径不是 /usr/bin/xxx」，那写的是「我这台 Mac 上 az 装在
            // Homebrew」这个事实，不是解析器的行为：CI 的 ubuntu 镜像预装了
            // /usr/bin/az，于是解析完全正确，断言照样红。
            foreach (var tool in (string[])["az", "claude"])
            {
                if (full[tool] is not { } expected)
                {
                    continue;   // 这台机器上没装，跳过
                }

                Assert.Equal(expected, resolver.TryResolve(tool));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", original);
        }
    }

    [Fact]
    public void An_absolute_path_from_config_wins()
    {
        Assert.Equal(_tool, Build().Resolve(_tool));
    }

    [Fact]
    public void A_configured_absolute_path_that_does_not_exist_fails_loudly()
    {
        var ex = Assert.Throws<FileNotFoundException>(
            () => Build().Resolve("/nope/definitely-not-here"));

        Assert.Contains("不存在", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_tool_reports_where_it_looked_and_how_to_fix_it()
    {
        // 刻意用一个必然不存在的名字：这台机器上 az 是装了的，拿它测「找不到」测不出来。
        var ex = Assert.Throws<FileNotFoundException>(
            () => Build(_dir).Resolve("conclave-no-such-tool"));

        // 报错必须能自证：找过哪里、怎么配。否则用户只会看到「读不到 az 登录身份」
        // 然后去跑 az devops login —— 那不是原因。
        Assert.Contains("找不到可执行文件 conclave-no-such-tool", ex.Message, StringComparison.Ordinal);
        Assert.Contains(_dir, ex.Message, StringComparison.Ordinal);
        Assert.Contains("/opt/homebrew/bin", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ExtraToolPaths", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolution_is_cached_so_a_moved_tool_keeps_working_within_a_run()
    {
        var resolver = Build(_dir);
        var first = resolver.Resolve("faketool");
        File.Delete(_tool);

        Assert.Equal(first, resolver.Resolve("faketool"));
    }

    [Fact]
    public void TryResolve_returns_null_instead_of_throwing()
        => Assert.Null(Build().TryResolve("surely-no-such-tool-exists"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响结论
        }
    }

    /// <summary>
    /// 同名工具有好几份时要能全部列出来 —— 「排在前面」不等于「是对的那份」。
    /// </summary>
    /// <remarks>
    /// claude 就是这个形状：官方安装器装 <c>~/.local/bin</c>，旧的 npm 全局版留在
    /// <c>/usr/local/bin</c>。<c>ClaudeCli</c> 要看到全部候选才能逐个验版本再挑。
    /// </remarks>
    [Fact]
    public void Every_copy_is_listed_in_search_order()
    {
        var second = Path.Combine(_dir, "second");
        _ = Directory.CreateDirectory(second);
        var twin = Path.Combine(second, "faketool");
        File.WriteAllText(twin, "#!/bin/sh\nexit 0\n");

        var all = Build(_dir, second).ResolveAll("faketool");

        Assert.Equal([_tool, twin], all);
    }

    [Fact]
    public void A_symlink_to_a_copy_already_listed_is_not_listed_twice()
    {
        var second = Path.Combine(_dir, "link");
        _ = Directory.CreateDirectory(second);
        File.CreateSymbolicLink(Path.Combine(second, "faketool"), _tool);

        // 官方安装器装出来的就是这个形状：~/.local/bin/claude 是个指向版本目录的链接。
        Assert.Equal([_tool], Build(_dir, second).ResolveAll("faketool"));
    }

    [Fact]
    public void Nothing_found_is_an_empty_list_not_a_throw()
        => Assert.Empty(Build().ResolveAll("conclave-no-such-tool"));
}

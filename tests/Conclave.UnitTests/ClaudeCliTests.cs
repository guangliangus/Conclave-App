using Conclave.Application;
using Conclave.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

/// <summary>
/// 「用哪个 claude」。
/// </summary>
/// <remarks>
/// 这条守卫来自一次真实事故：某个节点连着三次评审全挂在
/// <c>API Error: 400 … Claude Code 2.1.104 does not support this model;
/// version 2.1.251 or newer is required</c>，而那台机器的人早更新过了 ——
/// 他终端里的 <c>claude</c> 是 <c>~/.local/bin</c> 里的新版，Conclave 从 Finder 启动、
/// 拿不到他的 PATH，落到兜底目录挑中了 <c>/usr/local/bin</c> 里那份没卸干净的旧 npm 版。
/// <para>
/// 所以这里要钉住的是：<b>有好几份时按版本号挑，不按谁排在前面。</b>
/// 集群里有几十台机器，「让每个人自己去收拾」不是个可行的方案。
/// </para>
/// </remarks>
public sealed class ClaudeCliTests : IDisposable
{
    private readonly string _dir;

    public ClaudeCliTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "conclave-cli-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_dir);
    }

    [Theory]
    [InlineData("2.1.268 (Claude Code)", "2.1.268")]
    [InlineData("2.1.104", "2.1.104")]
    [InlineData("  1.0.99 (Claude Code)\n", "1.0.99")]
    public void The_version_comes_out_of_the_version_line(string output, string expected)
        => Assert.Equal(Version.Parse(expected), ClaudeCli.ParseVersion(output));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("command not found")]
    [InlineData("v")]
    public void An_unreadable_version_is_null_rather_than_a_throw(string output)
        => Assert.Null(ClaudeCli.ParseVersion(output));

    [Fact]
    public void The_newest_wins_even_when_it_is_last_in_search_order()
    {
        var chosen = ClaudeCli.Choose([
            new ClaudeInstallation("/usr/local/bin/claude", new Version(2, 1, 104)),
            new ClaudeInstallation("/home/me/.local/bin/claude", new Version(2, 1, 268)),
        ]);

        Assert.Equal("/home/me/.local/bin/claude", chosen.Path);
    }

    [Fact]
    public void A_known_version_beats_an_unknown_one()
    {
        // 版本验不出来的多半根本不是 claude（重名的脚本、架构不对的二进制）。
        var chosen = ClaudeCli.Choose([
            new ClaudeInstallation("/opt/homebrew/bin/claude", null),
            new ClaudeInstallation("/home/me/.local/bin/claude", new Version(2, 1, 268)),
        ]);

        Assert.Equal("/home/me/.local/bin/claude", chosen.Path);
    }

    [Fact]
    public void All_unknown_falls_back_to_search_order()
    {
        // 全都验不出版本时也要给出一个 —— 起不来总好过不干活。
        var chosen = ClaudeCli.Choose([
            new ClaudeInstallation("/first/claude", null),
            new ClaudeInstallation("/second/claude", null),
        ]);

        Assert.Equal("/first/claude", chosen.Path);
    }

    [Fact]
    public void A_tie_keeps_search_order_so_configuration_still_decides()
    {
        var same = new Version(2, 1, 268);
        var chosen = ClaudeCli.Choose([
            new ClaudeInstallation("/configured/claude", same),
            new ClaudeInstallation("/fallback/claude", same),
        ]);

        Assert.Equal("/configured/claude", chosen.Path);
    }

    /// <summary>
    /// 事故本身：旧的排在前面，新的排在后面，必须挑到新的。
    /// </summary>
    /// <remarks>
    /// <b>版本号刻意取到 99.x</b>：<c>ExecutableResolver</c> 搜完 <c>ExtraToolPaths</c>
    /// 还会搜进程 PATH 和兜底目录，所以跑测试这台机器上<b>真的那份 claude 也在候选里</b>。
    /// 原先假的取 2.1.268，本机 claude 升到 2.1.269 之后就反过来赢了它，
    /// 这条用例跟着环境红了 —— 挂的是环境，不是被测的那个规则。
    /// 事故当年的数字是 2.1.104 / 2.1.268，但这条钉的是「谁新挑谁」，与具体数字无关。
    /// </remarks>
    [Fact]
    public async Task Two_installs_on_one_machine_resolve_to_the_newer_one()
    {
        var old = FakeClaude("old", "99.0.104");
        var recent = FakeClaude("new", "99.0.268");

        var options = new ConclaveOptions { HomeDirectory = Path.Combine(_dir, "home") };
        options.ExtraToolPaths.Add(old);        // 先被搜到的是旧的
        options.ExtraToolPaths.Add(recent);

        var cli = new ClaudeCli(
            options, new ExecutableResolver(options), NullLogger<ClaudeCli>.Instance);

        var chosen = await cli.ResolveAsync(CancellationToken.None);

        Assert.Equal(new Version(99, 0, 268), chosen.Version);
        Assert.StartsWith(recent, chosen.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_configured_absolute_path_is_used_as_is()
    {
        var pinned = Path.Combine(FakeClaude("pinned", "1.2.3"), "claude");
        var options = new ConclaveOptions
        {
            HomeDirectory = Path.Combine(_dir, "home"),
            ClaudeExecutable = pinned,
        };

        var cli = new ClaudeCli(
            options, new ExecutableResolver(options), NullLogger<ClaudeCli>.Instance);

        var chosen = await cli.ResolveAsync(CancellationToken.None);

        Assert.Equal(pinned, chosen.Path);
        Assert.Equal(new Version(1, 2, 3), chosen.Version);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不值得让测试红。
        }
    }

    /// <summary>
    /// 造一个只认 <c>--version</c> 的假 claude，返回它<b>所在的目录</b>。
    /// </summary>
    /// <remarks>
    /// 跑真的子进程而不是给 <see cref="ClaudeCli"/> 塞接口：要验的正是「起进程问版本」
    /// 这一段本身 —— 超时、非零退出、输出格式，塞假货全都测不到。
    /// </remarks>
    private string FakeClaude(string name, string version)
    {
        var dir = Path.Combine(_dir, name);
        _ = Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "claude");
        File.WriteAllText(path, $"#!/bin/sh\necho '{version} (Claude Code)'\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return dir;
    }
}

using System.Diagnostics;
using Conclave.Application;
using Conclave.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

/// <summary>
/// 评审子进程的 PATH（<see cref="ChildProcessPath"/>）。
/// </summary>
/// <remarks>
/// 为什么要守：双击启动的节点 PATH 只有 <c>/usr/bin:/bin:/usr/sbin:/sbin</c>，而 claude 子进程
/// 原样继承它 —— <c>az_pr.sh</c> 调不到 az、跑检查找不到 go / node，每一场评审都靠 Claude
/// 自己猜路径才跑得下去。这里钉住：登录 shell 的 PATH 要拿到、rc 的噪音和挂住都不能拖垮评审、
/// 配置排最前、相对路径不能漏进去。
/// </remarks>
public sealed class ChildProcessPathTests : IDisposable
{
    private readonly string _dir;

    public ChildProcessPathTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "conclave-path-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Script(string name, string body)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static ChildProcessPath Build(ConclaveOptions options, string? shell, TimeSpan? timeout = null)
        => new(options, new ExecutableResolver(options), NullLogger<ChildProcessPath>.Instance)
        {
            Shell = shell,
            ProbeTimeout = timeout ?? TimeSpan.FromSeconds(10),
        };

    private static string[] Split(string path) => path.Split(Path.PathSeparator);

    [Fact]
    public void Compose_keeps_order_and_drops_duplicates_blanks_and_relative_entries()
    {
        var path = ChildProcessPath.Compose(
            ["/opt/homebrew/bin/", "", ".", "node_modules/.bin", "/usr/bin", "/opt/homebrew/bin", "/"]);

        Assert.Equal(["/opt/homebrew/bin", "/usr/bin", "/"], Split(path));
    }

    [Fact]
    public void Compose_expands_the_home_prefix()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(Path.Combine(home, "go/bin"), ChildProcessPath.Compose(["~/go/bin"]));
    }

    [Fact]
    public void The_path_is_read_between_the_markers_ignoring_rc_noise()
    {
        var output = "Welcome!\n"
            + $"{ChildProcessPath.BeginMarker}stale{ChildProcessPath.EndMarker}\n"
            + $"(eval):1: can't change option: zle\n{ChildProcessPath.BeginMarker}/a:/b/:{ChildProcessPath.EndMarker}bye";

        Assert.Equal(["/a", "/b/"], ChildProcessPath.ParseProbe(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no markers at all")]
    [InlineData(ChildProcessPath.BeginMarker + "/a:/b")]
    public void Output_without_a_complete_marker_pair_yields_nothing(string output)
        => Assert.Empty(ChildProcessPath.ParseProbe(output));

    [Fact]
    public async Task The_command_handed_to_the_shell_prints_its_PATH()
    {
        // 假 shell 收到的参数是 -i -l -c <命令>，把命令交给 /bin/sh 真跑一遍 ——
        // 钉住的是「拼出来的那条命令本身是对的」，不只是解析。
        var shell = Script("fake-shell", "exec /bin/sh -c \"$4\"");

        var path = await Build(new ConclaveOptions(), shell).GetAsync(CancellationToken.None);

        var inherited = Environment.GetEnvironmentVariable("PATH")!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathRooted)
            .Select(d => d.Length > 1 ? d.TrimEnd('/') : d);
        Assert.All(inherited, d => Assert.Contains(d, Split(path)));
    }

    [Fact]
    public async Task Configured_paths_come_first_then_az_then_the_login_shell()
    {
        var extra = Path.Combine(_dir, "extra");
        var azDir = Path.Combine(_dir, "az-home");
        _ = Directory.CreateDirectory(azDir);
        var az = Path.Combine(azDir, "az");
        File.WriteAllText(az, "#!/bin/sh\n");

        var shell = Script(
            "noisy-shell",
            "echo 'motd banner'\n"
            + $"printf '%s%s%s\\n' {ChildProcessPath.BeginMarker} '/from/login:relative/dir:/usr/bin/' {ChildProcessPath.EndMarker}\n"
            + "echo goodbye");

        var options = new ConclaveOptions { AzExecutable = az };
        options.ExtraToolPaths.Add(extra);

        var dirs = Split(await Build(options, shell).GetAsync(CancellationToken.None));

        Assert.Equal([extra, azDir, "/from/login", "/usr/bin"], dirs.Take(4));
        Assert.DoesNotContain("relative/dir", dirs);
        Assert.Single(dirs, d => d == "/usr/bin");
    }

    [Fact]
    public async Task A_hanging_shell_is_abandoned_and_the_review_still_gets_a_path()
    {
        var shell = Script("hanging-shell", "exec sleep 30");
        var options = new ConclaveOptions();
        options.ExtraToolPaths.Add("/configured/bin");

        var sw = Stopwatch.StartNew();
        var path = await Build(options, shell, TimeSpan.FromMilliseconds(300)).GetAsync(CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"等了 {sw.Elapsed}");
        Assert.Equal("/configured/bin", Split(path)[0]);
        Assert.Contains("/usr/bin", Split(path));
    }

    [Fact]
    public async Task Without_a_shell_the_path_falls_back_to_config_and_the_search_directories()
    {
        var options = new ConclaveOptions();
        options.ExtraToolPaths.Add("/configured/bin");

        var dirs = Split(await Build(options, shell: null).GetAsync(CancellationToken.None));

        Assert.Equal("/configured/bin", dirs[0]);
        Assert.Contains("/opt/homebrew/bin", dirs);   // 解析器的兜底目录
    }

    [Fact]
    public async Task The_login_shell_is_probed_once_and_shared()
    {
        var counter = Path.Combine(_dir, "calls");
        var shell = Script(
            "counting-shell",
            $"echo x >> '{counter}'\nprintf '%s/once%s' {ChildProcessPath.BeginMarker} {ChildProcessPath.EndMarker}");
        var sut = Build(new ConclaveOptions(), shell);

        var all = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => sut.GetAsync(CancellationToken.None)));
        _ = await sut.GetAsync(CancellationToken.None);

        Assert.Single(File.ReadAllLines(counter));
        Assert.All(all, p => Assert.Contains("/once", Split(p)));
    }

    [Fact]
    public void An_unusable_SHELL_falls_back_to_a_system_shell()
    {
        Assert.Equal("/bin/sh", ChildProcessPath.DefaultShell("/bin/sh"));
        Assert.NotEqual("relative-shell", ChildProcessPath.DefaultShell("relative-shell"));
        Assert.NotEqual("/no/such/shell", ChildProcessPath.DefaultShell("/no/such/shell"));
    }
}

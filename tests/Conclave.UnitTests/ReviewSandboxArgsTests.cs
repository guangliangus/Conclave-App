using Conclave.Application;
using Conclave.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

/// <summary>
/// 评审子进程的隔离参数，以及随包 skill 的铺设。
/// </summary>
public sealed class ReviewSandboxArgsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "conclave-skill-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void The_sandbox_confines_file_tools_and_loads_the_bundled_skill()
    {
        var args = ClaudeReviewRunner.SandboxArgs("/home/x/.conclave/skills");

        Assert.Contains("--restricted", args);

        // plugin-dir 装载 skill，add-dir 才让 skill 读得到自己的 references/ ——
        // 少了后者 skill 装得上却在选评审视角那步瞎掉，所以两个都要，且指同一个目录。
        Assert.Equal(
            ["--plugin-dir", "/home/x/.conclave/skills"],
            args.SkipWhile(a => a != "--plugin-dir").Take(2).ToArray());
        Assert.Equal(
            ["--add-dir", "/home/x/.conclave/skills"],
            args.SkipWhile(a => a != "--add-dir").Take(2).ToArray());
    }

    /// <summary>
    /// 铺不出 skill 就一个参数都不加。
    /// </summary>
    /// <remarks>
    /// <c>--restricted</c> 会屏蔽 <c>~/.claude/skills</c>。这时若还加上它，评审就彻底
    /// 拿不到 <c>/az-pr-review</c> —— 宁可少一层隔离，也不能让评审整个失效。
    /// </remarks>
    [Fact]
    public void Nothing_is_added_when_the_skill_could_not_be_deployed()
    {
        Assert.Empty(ClaudeReviewRunner.SandboxArgs(null));
        Assert.Empty(ClaudeReviewRunner.SandboxArgs("   "));
    }

    /// <summary>
    /// 铺设是「先删再铺」，不是「覆盖」。
    /// </summary>
    /// <remarks>
    /// skill 里删掉一个 references 时，只覆盖会让那个文件永远留在目标目录，而 SKILL.md
    /// 已经不再提它 —— 最难查的那种不一致。
    /// </remarks>
    [Fact]
    public void Deploying_removes_files_that_the_package_no_longer_ships()
    {
        var options = new ConclaveOptions { HomeDirectory = _root };
        _ = Directory.CreateDirectory(options.SkillsDirectory);
        var stale = Path.Combine(options.SkillsDirectory, "stale.md");
        File.WriteAllText(stale, "上一版留下的");

        var deployed = new ReviewSkillDeployer(
            options, NullLogger<ReviewSkillDeployer>.Instance).Deploy();

        // 安装包里那份在测试宿主的输出目录下，跟着 Conclave.App 的 csproj 一起复制过来。
        // 拿不到就说明打包链断了，那本身就是要知道的事。
        Assert.NotNull(deployed);
        Assert.False(File.Exists(stale), "上一版的残留文件应该被清掉");
        Assert.True(File.Exists(Path.Combine(deployed, "skills", "az-pr-review", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(deployed, ".claude-plugin", "plugin.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

using Conclave.Application;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure;

/// <summary>
/// 把随包分发的评审 skill 铺到 <see cref="ConclaveOptions.SkillsDirectory"/>。
/// </summary>
/// <remarks>
/// <para>
/// 评审子进程用 <c>--restricted</c> 跑，那个模式会<b>忽略 user/project/local 设置文件</b>，
/// 连带机器上 <c>~/.claude/skills</c> 里的 skill 一并不可见 —— 实测不只是「不在列表里」，
/// <c>/skill-name</c> 直接调也报「命令不存在」。唯一还能装载的入口是 <c>--plugin-dir</c>，
/// 而它要的是 plugin 结构（<c>.claude-plugin/plugin.json</c> + <c>skills/&lt;名字&gt;/SKILL.md</c>）。
/// 所以 skill 得由我们自己铺一份出来。
/// </para>
/// <para>
/// <b>每次启动无条件覆盖</b>，不做版本比对：整份才 28KB，而「铺过了就跳过」要么得维护版本
/// 标记、要么会让人手改的内容活下来 —— 后者最坏，因为出票契约（回复末尾那个 json 围栏）
/// 同时写在 skill 和 <see cref="ClaudeReviewRunner.CollectPrompt"/> 里，skill 被改歪了
/// 表现是评审照跑、结论照出，就是没围栏，而每一轮都是完整的账单。
/// </para>
/// <para>
/// 铺不出来<b>不抛</b>：评审会因为找不到 skill 而失败，但那是一张有原因的 Error 票；
/// 让节点整个起不来则是更坏的结果。
/// </para>
/// </remarks>
public sealed class ReviewSkillDeployer(ConclaveOptions options, ILogger<ReviewSkillDeployer> logger)
{
    /// <summary>安装包里那份 plugin 的目录名，跟 <c>Conclave.App.csproj</c> 里的一致。</summary>
    internal const string BundledDirectoryName = "review-plugin";

    /// <summary>plugin 清单的相对路径；缺它 <c>--plugin-dir</c> 不认这个目录。</summary>
    internal const string ManifestRelativePath = ".claude-plugin/plugin.json";

    /// <summary>
    /// 铺一次。返回可以传给 <c>--plugin-dir</c> 的路径；铺不成返回 <c>null</c>。
    /// </summary>
    public string? Deploy()
    {
        var source = Path.Combine(AppContext.BaseDirectory, BundledDirectoryName);
        if (!File.Exists(Path.Combine(source, ManifestRelativePath)))
        {
            logger.LogWarning(
                "安装包里没有评审 skill（{Source} 下缺 {Manifest}），评审将拿不到 /az-pr-review",
                source, ManifestRelativePath);
            return null;
        }

        var target = options.SkillsDirectory;

        try
        {
            // 先删再铺：skill 里删掉一个文件（比如某个 references）时，只覆盖不清理会让
            // 那个文件永远留在目标目录里，而 SKILL.md 已经不再提它 —— 最难查的那种不一致。
            if (Directory.Exists(target))
            {
                GitWorkspace.DeleteTree(target, logger);
            }

            CopyTree(source, target);
            logger.LogInformation("评审 skill 已铺到 {Target}", target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "铺评审 skill 到 {Target} 失败，评审将拿不到 /az-pr-review", target);
            return null;
        }
    }

    /// <summary>
    /// 递归复制一棵目录树。
    /// </summary>
    /// <remarks>
    /// 复制后补一次可执行位：<c>az_pr.sh</c> 靠 <c>bash &lt;路径&gt;</c> 调用时其实不需要 +x，
    /// 但 skill 日后改成直接执行就会需要，而那种失败（Permission denied）跟「脚本不存在」
    /// 在日志里长得很像。非 Windows 才设 —— Windows 上没有这个位。
    /// </remarks>
    private static void CopyTree(string source, string target)
    {
        _ = Directory.CreateDirectory(target);

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            _ = Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            File.Copy(file, destination, overwrite: true);

            if (!OperatingSystem.IsWindows()
                && Path.GetExtension(destination).Equals(".sh", StringComparison.OrdinalIgnoreCase))
            {
                File.SetUnixFileMode(
                    destination,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }
}

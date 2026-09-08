using Conclave.Application;
using Conclave.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure;

/// <summary>
/// 在配置的若干根目录下找 git 工作副本。
/// </summary>
/// <remarks>
/// <para>
/// 只扫一层子目录：<c>~/projects/payment-center/.git</c> 算，
/// <c>~/projects/a/b/c/.git</c> 不算 —— 递归扫整个 home 又慢又容易撞上无关仓库。
/// </para>
/// <para>
/// 同时按**目录名**和 <c>origin</c> 里解析出的**远端仓库名**登记。只认目录名太脆：
/// 实测 <c>~/projects/user_svc</c> 的远端其实是
/// <c>.../liontrip-user/_git/liontrip-user</c>，只比目录名的话这个 clone 等于不存在，
/// 该节点会无声地失去入席资格。
/// </para>
/// </remarks>
public sealed class FileSystemRepoLocator(
    ConclaveOptions options,
    ILogger<FileSystemRepoLocator> logger) : IRepoLocator
{
    public IReadOnlyDictionary<string, string> Locate()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in options.RepoSearchRoots)
        {
            var expanded = ConclaveOptions.ExpandHome(root);
            if (!Directory.Exists(expanded))
            {
                continue;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(expanded);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "无法枚举 {Root}", expanded);
                continue;
            }

            foreach (var dir in children)
            {
                if (!Directory.Exists(Path.Combine(dir, ".git")) && !File.Exists(Path.Combine(dir, ".git")))
                {
                    continue;
                }

                // 同名 repo 出现在多个根目录时，先扫到的胜出。
                var dirName = Path.GetFileName(dir);
                if (!string.IsNullOrEmpty(dirName))
                {
                    _ = found.TryAdd(dirName, dir);
                }

                var remoteName = RemoteRepoName(dir);
                if (!string.IsNullOrEmpty(remoteName))
                {
                    _ = found.TryAdd(remoteName, dir);
                }
            }
        }

        return found;
    }

    /// <summary>从 <c>.git/config</c> 里读 origin 并取出仓库名。</summary>
    /// <remarks>
    /// 直接读文件而不是起 <c>git remote get-url</c> 子进程 —— 这个方法每轮轮询都会调，
    /// 为几十个目录各起一个进程不值得。
    /// </remarks>
    private string? RemoteRepoName(string repoDir)
    {
        try
        {
            var config = Path.Combine(repoDir, ".git", "config");
            if (!File.Exists(config))
            {
                return null;   // worktree 或 submodule 的 .git 是文件，不在此处处理
            }

            foreach (var line in File.ReadLines(config))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("url", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var eq = trimmed.IndexOf('=', StringComparison.Ordinal);
                if (eq < 0)
                {
                    continue;
                }

                var url = trimmed[(eq + 1)..].Trim().TrimEnd('/');
                if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                {
                    url = url[..^4];
                }

                var slash = url.LastIndexOf('/');
                var name = slash >= 0 && slash < url.Length - 1 ? url[(slash + 1)..] : null;
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "读不到 {Dir} 的 git config", repoDir);
        }

        return null;
    }
}

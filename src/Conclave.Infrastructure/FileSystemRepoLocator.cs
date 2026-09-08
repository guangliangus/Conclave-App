using Conclave.Application;
using Conclave.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure;

/// <summary>
/// 在配置的若干根目录下找 git 工作副本，用目录名当 repo 名。
/// </summary>
/// <remarks>
/// 只扫一层子目录：<c>~/projects/payment-center/.git</c> 算，
/// <c>~/projects/a/b/c/.git</c> 不算 —— 递归扫整个 home 又慢又容易撞上无关仓库。
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

                var name = Path.GetFileName(dir);
                if (!string.IsNullOrEmpty(name))
                {
                    // 同名 repo 出现在多个根目录时，先扫到的胜出。
                    _ = found.TryAdd(name, dir);
                }
            }
        }

        return found;
    }
}

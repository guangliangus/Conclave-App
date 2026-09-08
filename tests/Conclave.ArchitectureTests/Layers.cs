using System.Reflection;

namespace Conclave.ArchitectureTests;

internal static class Layers
{
    internal static readonly Assembly Domain = typeof(Domain.Revision).Assembly;
    internal static readonly Assembly Application = typeof(Application.ConclaveOptions).Assembly;
    internal static readonly Assembly Infrastructure = typeof(Infrastructure.SqliteActa).Assembly;

    /// <summary>
    /// 仓库根目录（含 <c>Conclave.slnx</c> 的那一层）。
    /// </summary>
    /// <remarks>
    /// 有几条规则只能扫源码 —— 比如「Domain 里不出现 UtcNow」，那是调用点的约束，
    /// 编译产物里看不出来。所以从测试程序集所在目录往上找根。
    /// </remarks>
    internal static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Conclave.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("从 " + AppContext.BaseDirectory + " 往上找不到 Conclave.slnx");
    }

    internal static IEnumerable<(string Path, string Text)> SourcesOf(string projectFolder)
    {
        var root = Path.Combine(RepoRoot, "src", projectFolder);
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (Path.GetRelativePath(RepoRoot, file), File.ReadAllText(file));
        }
    }
}

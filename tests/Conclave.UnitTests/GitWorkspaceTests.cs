using Conclave.Application;
using Conclave.Domain;
using Conclave.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

/// <summary>
/// 临时工作区的行为。
/// </summary>
/// <remarks>
/// 用本地裸仓库当 origin（<c>file://</c> URL），所以整套测试离线可跑、也不依赖 Azure DevOps。
/// 走 <c>file://</c> 而不是裸路径是必须的：git 对本地路径会忽略 <c>--depth</c>，
/// 那样就测不到浅历史下的行为，而浅历史正是这里唯一真正会出事的地方。
/// </remarks>
public sealed class GitWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "conclave-ws-" + Guid.NewGuid().ToString("N"));

    private string OriginPath => Path.Combine(_root, "origin.git");

    private string OriginUrl => "file://" + OriginPath;

    private ConclaveOptions Options(int depth = 50) => new()
    {
        HomeDirectory = Path.Combine(_root, "home"),
        FetchDepth = depth,
        CheckoutTimeout = TimeSpan.FromMinutes(2),
    };

    private GitWorkspaceFactory Factory(ConclaveOptions options) => new(
        options, new ExecutableResolver(options), NullLogger<GitWorkspaceFactory>.Instance);

    private static PrMeta Pr(string source, string target) => new()
    {
        PrId = 2880,
        Project = "edison-test",
        Repo = "edison-test",
        Title = "feat: x",
        Author = @"LIONMAIL\someone",
        SrcCommit = "0000000",
        SourceBranch = source,
        TargetBranch = target,
    };

    [Fact]
    public async Task Checkout_puts_the_source_branch_files_on_disk()
    {
        var options = Options();
        BuildOrigin(divergeAfter: 2);

        await using var ws = await Factory(options)
            .CheckoutAsync(Pr("feature", "develop"), OriginUrl, "2880@aaaaaaaa-r0", CancellationToken.None);

        // Read/Grep 这些工具要能在磁盘上看到文件，光有 git 对象不够。
        Assert.True(File.Exists(Path.Combine(ws.Path, "feature.txt")));
        Assert.Equal("feature", Git(ws.Path, "rev-parse", "--abbrev-ref", "HEAD").Trim());
    }

    [Fact]
    public async Task Checkout_yields_a_three_dot_diff_that_matches_the_full_history()
    {
        var options = Options(depth: 50);
        BuildOrigin(divergeAfter: 2);

        await using var ws = await Factory(options)
            .CheckoutAsync(Pr("feature", "develop"), OriginUrl, "pr", CancellationToken.None);

        // az_pr.sh 用的就是这条命令。它必须只报 feature 分支自己的改动，
        // 而不是把 develop 上后来的提交也算成 PR 的一部分。
        var changed = Git(ws.Path, "diff", "--name-only", "origin/develop...origin/feature")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToArray();

        Assert.Equal(["feature.txt"], changed);
    }

    [Fact]
    public async Task A_shallow_fetch_that_misses_the_merge_base_is_deepened_instead_of_breaking_the_diff()
    {
        // divergeAfter=6 配 depth=1：共同祖先落在浅边界之外。实测此时
        // git merge-base 退出码 1、输出为空，紧接着 az-pr-review 用的
        // git diff a...b 会以退出码 128 报 fatal: no merge base ——
        // 仓库白拉一遍、claude 白起一个，最后只落下一张 Error 票。
        var options = Options(depth: 1);
        BuildOrigin(divergeAfter: 6);

        await using var ws = await Factory(options)
            .CheckoutAsync(Pr("feature", "develop"), OriginUrl, "pr", CancellationToken.None);

        Assert.NotEmpty(Git(ws.Path, "merge-base", "origin/develop", "origin/feature").Trim());

        var changed = Git(ws.Path, "diff", "--name-only", "origin/develop...origin/feature")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(["feature.txt"], changed.Select(l => l.Trim()).ToArray());
    }

    [Fact]
    public async Task Disposing_the_workspace_deletes_it()
    {
        var options = Options();
        BuildOrigin(divergeAfter: 1);

        var ws = await Factory(options)
            .CheckoutAsync(Pr("feature", "develop"), OriginUrl, "pr", CancellationToken.None);
        var path = ws.Path;
        Assert.True(Directory.Exists(path));

        await ws.DisposeAsync();

        // git 的 pack 文件是 0444 建出来的，删除逻辑要能处理只读属性。
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task A_failed_checkout_leaves_no_half_built_directory()
    {
        var options = Options();
        BuildOrigin(divergeAfter: 1);

        _ = await Assert.ThrowsAnyAsync<Exception>(() => Factory(options).CheckoutAsync(
            Pr("no-such-branch", "develop"), OriginUrl, "pr", CancellationToken.None));

        Assert.Empty(Directory.EnumerateDirectories(options.WorkspaceRoot));
    }

    [Fact]
    public void Orphans_from_a_previous_crash_are_swept_at_startup()
    {
        var options = Options();
        var stale = Path.Combine(options.WorkspaceRoot, "2721@dc1d1d47-r0-deadbeef");
        _ = Directory.CreateDirectory(Path.Combine(stale, ".git", "objects", "pack"));
        File.WriteAllText(Path.Combine(stale, ".git", "objects", "pack", "p.pack"), "x");

        // 正常路径上目录由 DisposeAsync 删掉，但进程被 kill / 断电 / .app 强退时不会走到那里。
        // 工作区根目录整个是 Conclave 自己的，所以可以放心整目录清 ——
        // 这也是刻意不用系统临时目录的原因。
        _ = Factory(options);

        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public async Task A_pr_without_branch_names_fails_fast()
    {
        var options = Options();
        var pr = Pr("feature", "develop") with { SourceBranch = string.Empty };

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => Factory(options)
            .CheckoutAsync(pr, OriginUrl, "pr", CancellationToken.None));
    }

    /// <summary>
    /// 建一个裸仓库当 origin：<c>develop</c> 上若干提交，<c>feature</c> 从第
    /// <paramref name="divergeAfter"/> 个提交处分叉。
    /// </summary>
    /// <remarks>
    /// <paramref name="divergeAfter"/> 越大，两个分支的共同祖先离各自的尖端越远 ——
    /// 这正是「浅拉取拿不到 merge-base」的成因，用它来造那个场景。
    /// </remarks>
    private void BuildOrigin(int divergeAfter)
    {
        var work = Path.Combine(_root, "seed");
        _ = Directory.CreateDirectory(work);
        _ = Directory.CreateDirectory(OriginPath);

        Git(OriginPath, "init", "--bare", "--quiet", "--initial-branch=develop");

        Git(work, "init", "--quiet", "--initial-branch=develop");
        Git(work, "config", "user.email", "test@conclave.invalid");
        Git(work, "config", "user.name", "conclave test");
        Git(work, "remote", "add", "origin", OriginPath);

        Commit(work, "base.txt", "base");

        for (var i = 0; i < divergeAfter; i++)
        {
            Commit(work, "develop.txt", "develop " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // feature 从这里分叉，然后 develop 再往前走 —— 两边都得往回走才能碰到共同祖先。
        Git(work, "checkout", "--quiet", "-b", "feature");
        Commit(work, "feature.txt", "feature");

        Git(work, "checkout", "--quiet", "develop");
        for (var i = 0; i < divergeAfter; i++)
        {
            Commit(work, "develop.txt", "develop later " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Git(work, "push", "--quiet", "origin", "develop", "feature");
    }

    private void Commit(string dir, string file, string content)
    {
        File.WriteAllText(Path.Combine(dir, file), content + "\n");
        Git(dir, "add", ".");
        Git(dir, "commit", "--quiet", "-m", content);
    }

    private static string Git(string dir, params string[] args)
    {
        var result = ProcessRunner
            .RunAsync("git", ["-C", dir, .. args], workingDirectory: dir)
            .GetAwaiter().GetResult();

        return result.Success
            ? result.StdOut
            : throw new InvalidOperationException(
                $"git {string.Join(' ', args)} 失败（exit {result.ExitCode}）：{result.StdErr}");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(file);
                if (attrs.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // 临时目录清不掉不影响测试结论。
        }
    }
}

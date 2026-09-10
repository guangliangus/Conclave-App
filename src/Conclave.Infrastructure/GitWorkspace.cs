using System.Globalization;
using Conclave.Application;
using Conclave.Domain;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure;

/// <summary>
/// 一次评审用的临时 git 工作区。<see cref="DisposeAsync"/> 即删目录。
/// </summary>
/// <remarks>
/// 评审的代码不再依赖本机预先 clone 的仓库 —— 每次评审现拉一份、评完即删，
/// 于是任何节点都能评任何仓库，也不会碰到本机正在用的工作副本
/// （早先直接在用户的 clone 里跑 <c>git fetch</c>，会动到人家正在改的仓库）。
/// </remarks>
public sealed class GitWorkspace(string path, ILogger logger) : IAsyncDisposable
{
    /// <summary>工作区目录。<c>claude</c> 子进程的工作目录就是它。</summary>
    public string Path { get; } = path;

    public ValueTask DisposeAsync()
    {
        DeleteTree(Path, logger);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 递归删掉一棵目录树。
    /// </summary>
    /// <remarks>
    /// 直接 <c>Directory.Delete(recursive: true)</c> 在 git 仓库上并不总是成功：
    /// pack 文件是 0444 建出来的，某些平台上只读属性会让删除抛
    /// <see cref="UnauthorizedAccessException"/>。所以失败后清一遍只读属性再重试一次。
    /// 删不掉也只记日志 —— 残留目录会被下次启动的 <see cref="GitWorkspaceFactory"/> 扫走，
    /// 不该让一个清理失败把已经算出来的票丢掉。
    /// </remarks>
    internal static void DeleteTree(string path, ILogger logger)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "删除工作区 {Path} 失败，清只读属性后重试", path);
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(file);
                if (attrs.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "工作区 {Path} 清不掉，留给下次启动扫", path);
        }
    }
}

/// <summary>
/// 按 PR 拉出临时工作区。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不能只拉 <c>--depth=1</c>。</b> <c>az-pr-review</c> 靠
/// <c>git diff origin/target...origin/source</c> 取三点 diff，而三点 diff 需要 merge-base。
/// 实测浅到两个分支的共同祖先不在历史里时，<c>git merge-base</c> 退出码 1、输出为空，
/// 紧接着 <c>git diff a...b</c> 以退出码 128 报 <c>fatal: no merge base</c> ——
/// 评审在读 diff 那一步就死掉，白起一个 claude 子进程、只落下一张 Error 票。
/// 所以拉完必须校验 merge-base，不够就加深，真的拿不到才明确失败。
/// </para>
/// <para>
/// <b>部分克隆在这台服务器上没用。</b> 实测 <c>--filter=blob:none</c> 只会打印
/// <c>warning: filtering not recognized by server, ignoring</c> 然后退化成全量传输，
/// 所以省流量只能靠深度，不能靠 filter。
/// </para>
/// </remarks>
public sealed class GitWorkspaceFactory
{
    private readonly ConclaveOptions _options;
    private readonly ExecutableResolver _executables;
    private readonly ILogger<GitWorkspaceFactory> _logger;

    public GitWorkspaceFactory(
        ConclaveOptions options,
        ExecutableResolver executables,
        ILogger<GitWorkspaceFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _executables = executables;
        _logger = logger;

        SweepOrphans();
    }

    /// <summary>
    /// 清掉上次运行残留的工作区。
    /// </summary>
    /// <remarks>
    /// 正常路径上目录由 <see cref="GitWorkspace.DisposeAsync"/> 删掉，但进程被 kill、
    /// 断电、或者 <c>.app</c> 被强退时不会走到那里。工作区根目录整个是 Conclave 自己的
    /// （<c>~/.conclave/work</c>），所以可以放心整目录清 —— 这也是刻意不放系统临时目录的原因。
    /// </remarks>
    public int SweepOrphans()
    {
        var root = _options.WorkspaceRoot;
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var swept = 0;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            GitWorkspace.DeleteTree(dir, _logger);
            if (!Directory.Exists(dir))
            {
                swept++;
            }
        }

        if (swept > 0)
        {
            _logger.LogInformation("清掉 {Count} 个上次残留的临时工作区（{Root}）", swept, root);
        }

        return swept;
    }

    /// <summary>
    /// 把 <paramref name="pr"/> 的源分支拉进一个新的临时工作区。
    /// </summary>
    /// <param name="pr">PR 快照，用它的源/目标分支名。</param>
    /// <param name="cloneUrl">克隆地址，由 <c>IPrSource.GetCloneUrlAsync</c> 给出。</param>
    /// <param name="label">目录名前缀，人读的，例如 <c>2880@a6478fa5-r0</c>。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="InvalidOperationException">拉取失败，或拿不到 merge-base。</exception>
    public async Task<GitWorkspace> CheckoutAsync(
        PrMeta pr, string cloneUrl, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentException.ThrowIfNullOrWhiteSpace(cloneUrl);

        if (string.IsNullOrWhiteSpace(pr.SourceBranch) || string.IsNullOrWhiteSpace(pr.TargetBranch))
        {
            throw new InvalidOperationException(
                $"PR {pr.PrId} 缺源分支或目标分支，无法拉工作区（源={pr.SourceBranch}，目标={pr.TargetBranch}）");
        }

        _ = Directory.CreateDirectory(_options.WorkspaceRoot);

        // 目录名带随机后缀：同一个 (revision, round) 理论上不会并发两次，但重试路径上
        // 前一个目录可能还没删干净，撞名会让新一轮直接失败。
        var dir = System.IO.Path.Combine(
            _options.WorkspaceRoot,
            Sanitize(label) + "-" + Guid.NewGuid().ToString("N")[..8]);

        _ = Directory.CreateDirectory(dir);
        var workspace = new GitWorkspace(dir, _logger);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.CheckoutTimeout);
            var cct = timeout.Token;

            await GitAsync(dir, ["init", "--quiet"], cct).ConfigureAwait(false);

            // 用默认 refspec 建 remote，好让 `git fetch origin <branch>` 顺带更新
            // refs/remotes/origin/<branch> —— az_pr.sh 用的正是 origin/<branch> 这种写法。
            await GitAsync(dir, ["remote", "add", "origin", cloneUrl], cct).ConfigureAwait(false);

            await FetchAsync(dir, pr, _options.FetchDepth, cct).ConfigureAwait(false);
            await EnsureMergeBaseAsync(dir, pr, cct).ConfigureAwait(false);

            // 检出源分支：Read/Grep 这些工具要能在磁盘上看到文件，光有 git 对象不够。
            await GitAsync(
                dir,
                ["checkout", "--quiet", "-B", pr.SourceBranch, "origin/" + pr.SourceBranch],
                cct).ConfigureAwait(false);

            _logger.LogInformation(
                "工作区就绪 {Dir}（{Repo} {Source} → {Target}）", dir, pr.Repo, pr.SourceBranch, pr.TargetBranch);

            return workspace;
        }
        catch
        {
            // 半成品目录不能留：下一轮会以为它是残留而清掉，但在那之前它白占磁盘。
            await workspace.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task FetchAsync(string dir, PrMeta pr, int depth, CancellationToken ct)
    {
        List<string> args = ["fetch", "--quiet", "--no-tags"];
        if (depth > 0)
        {
            args.Add("--depth=" + depth.ToString(CultureInfo.InvariantCulture));
        }

        args.AddRange(["origin", pr.SourceBranch, pr.TargetBranch]);
        await GitAsync(dir, args, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 确认两个分支之间真的有 merge-base，没有就加深历史。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>git merge-base</c> 找不到共同祖先时退出码是 1、输出为空 —— 那不是错误，
    /// 是「拉得还不够深」，可以靠加深解决。所以这里刻意区分「命令失败」和「输出为空」。
    /// </para>
    /// <para>
    /// 不加深的后果是评审在读 diff 那一步硬失败（<c>git diff a...b</c> 退出码 128、
    /// <c>fatal: no merge base</c>）—— 已经拉完一整个仓库、起了一个 claude 子进程，
    /// 最后只落下一张 Error 票。宁可多两次网络往返也要在这里补上。
    /// </para>
    /// </remarks>
    private async Task EnsureMergeBaseAsync(string dir, PrMeta pr, CancellationToken ct)
    {
        if (await HasMergeBaseAsync(dir, pr, ct).ConfigureAwait(false))
        {
            return;
        }

        var deeper = Math.Max(200, _options.FetchDepth * 4);
        _logger.LogInformation(
            "PR {PrId} 的分支在深度 {Depth} 内没有共同祖先，加深到 {Deeper}",
            pr.PrId, _options.FetchDepth, deeper);

        await GitAsync(
            dir,
            ["fetch", "--quiet", "--no-tags", "--deepen=" + deeper.ToString(CultureInfo.InvariantCulture),
             "origin", pr.SourceBranch, pr.TargetBranch],
            ct).ConfigureAwait(false);

        if (await HasMergeBaseAsync(dir, pr, ct).ConfigureAwait(false))
        {
            return;
        }

        _logger.LogWarning(
            "PR {PrId} 加深到 {Deeper} 仍无共同祖先，退到全量历史（--unshallow）", pr.PrId, deeper);

        await GitAsync(dir, ["fetch", "--quiet", "--no-tags", "--unshallow", "origin"], ct)
            .ConfigureAwait(false);

        if (!await HasMergeBaseAsync(dir, pr, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"PR {pr.PrId} 的 {pr.SourceBranch} 与 {pr.TargetBranch} 即使在全量历史下也没有共同祖先，"
                + "三点 diff 无法成立，拒绝在错误的 diff 上评审");
        }
    }

    private async Task<bool> HasMergeBaseAsync(string dir, PrMeta pr, CancellationToken ct)
    {
        var result = await RunAsync(
            dir, ["merge-base", "origin/" + pr.TargetBranch, "origin/" + pr.SourceBranch], ct)
            .ConfigureAwait(false);

        // merge-base 找不到时退出码是 1 且 stdout 为空，那不是错误，是「还不够深」。
        return result.Success && result.StdOut.Trim().Length > 0;
    }

    private async Task GitAsync(string dir, IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await RunAsync(dir, args, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} 失败（exit {result.ExitCode}）：{result.StdErr.Trim()}");
        }
    }

    private Task<ProcessResult> RunAsync(string dir, IReadOnlyList<string> args, CancellationToken ct)
        => ProcessRunner.RunAsync(
            _executables.Resolve(_options.GitExecutable),
            ["-C", dir, .. args],
            workingDirectory: dir,
            // 交互式凭据提示会让子进程永远挂着等输入，而这里没有终端。
            // 认证要么由全局 git 凭据（credential helper / extraheader）自动完成，要么就明确失败。
            environment: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GIT_ASKPASS"] = "/usr/bin/true",
            },
            ct: ct);

    /// <summary>把 revision id 里的 <c>@</c>、<c>/</c> 之类换成目录名安全的字符。</summary>
    private static string Sanitize(string label)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var chars = label.Select(c => invalid.Contains(c) || c is '@' or ' ' ? '-' : c).ToArray();
        return new string(chars);
    }
}

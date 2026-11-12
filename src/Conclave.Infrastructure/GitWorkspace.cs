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
/// <b>首选：拉 ADO 预先算好的 PR 合并提交。</b> ADO 给每个无冲突的 PR 维护
/// <c>refs/pull/&lt;id&gt;/merge</c>，即「目标分支 + 源分支」的合并提交 M。只拉 M 和它的两个父提交
/// （<c>--depth=2</c>），<c>git diff M^1 M</c> 就是这个 PR 合进目标分支会带来的改动，不需要 merge-base。
/// 实测某前端仓库的一个 PR：按分支 <c>--depth=50</c> 要 220 MiB / 955 秒（线上 10 分钟超时），
/// 合并提交只要 23 MiB / 88 秒，diff 出的 49 个文件与 ADO 报的改动文件数一致。
/// 这么悬殊是因为这台 ADO Server 发 pack 几乎不做 delta（同样的对象本地 repack 能小 12 倍），
/// 而深度遇上合并提交会成倍展开（depth=50 实际拉进 539 个提交），传的历史越多亏得越多。
/// </para>
/// <para>
/// <b>退路：按分支浅拉取 + 校验 merge-base。</b> PR 有冲突时 ADO 不生成合并提交；推送之后
/// 它也可能还指着旧的源提交。这两种情况退回老路：按 <see cref="ConclaveOptions.FetchDepth"/>
/// 拉两个分支，<c>az-pr-review</c> 用 <c>git diff origin/target...origin/source</c> 取三点 diff。
/// 三点 diff 需要 merge-base：浅到两个分支的共同祖先不在历史里时，<c>git merge-base</c>
/// 退出码 1、输出为空，紧接着 <c>git diff a...b</c> 以退出码 128 报 <c>fatal: no merge base</c>
/// —— 评审在读 diff 那一步就死掉。所以拉完必须校验 merge-base，不够就逐级加深，真的拿不到才明确失败。
/// </para>
/// <para>
/// <b>部分克隆在这台服务器上没用。</b> 实测 <c>--filter=blob:none</c> 只会打印
/// <c>warning: filtering not recognized by server, ignoring</c> 然后退化成全量传输，
/// 所以省流量只能靠少拉历史，不能靠 filter。
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

            var viaMerge = await TryFetchPrMergeAsync(dir, pr, cct).ConfigureAwait(false);
            if (!viaMerge)
            {
                await FetchAsync(dir, pr, _options.FetchDepth, cct).ConfigureAwait(false);
                await EnsureMergeBaseAsync(dir, pr, cct).ConfigureAwait(false);
            }

            // 检出源分支：Read/Grep 这些工具要能在磁盘上看到文件，光有 git 对象不够。
            await GitAsync(
                dir,
                ["checkout", "--quiet", "-B", pr.SourceBranch, "origin/" + pr.SourceBranch],
                cct).ConfigureAwait(false);

            _logger.LogInformation(
                "工作区就绪 {Dir}（{Repo} {Source} → {Target}，{Mode}）",
                dir, pr.Repo, pr.SourceBranch, pr.TargetBranch,
                viaMerge ? "PR 合并提交" : "按分支浅拉取");

            return workspace;
        }
        catch
        {
            // 半成品目录不能留：下一轮会以为它是残留而清掉，但在那之前它白占磁盘。
            await workspace.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// ADO 为 PR 维护的合并提交的 ref 名，本地与远端同名。
    /// </summary>
    /// <remarks>
    /// <c>az_pr.sh</c> 认的就是这个名字：本地有它、且它的第二个父提交就是 HEAD，
    /// 就用 <c>git diff &lt;ref&gt;^1 &lt;ref&gt;</c> 取 diff，不再自己 fetch、也不算三点 diff。
    /// </remarks>
    internal static string PrMergeRef(int prId)
        => "refs/pull/" + prId.ToString(CultureInfo.InvariantCulture) + "/merge";

    /// <summary>
    /// ADO 明确表示「合并不出来」的 <see cref="PrMeta.MergeStatus"/>：这时不去试合并提交。
    /// </summary>
    /// <remarks>
    /// 只列确定失败的几种。<c>queued</c>、<c>notSet</c> 和空值（老 Summons 块）都照样尝试 ——
    /// 试错的代价是白拉一次，而合并提交对不对得上，由第二个父提交那道校验兜底。
    /// </remarks>
    private static readonly HashSet<string> NoMergeCommitStatuses =
        new(["conflicts", "failure", "rejectedByPolicy"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 尝试只拉 ADO 的 PR 合并提交及其两个父提交。
    /// </summary>
    /// <returns>
    /// 成功且合并提交对应的正是本次评审的源提交时返回 <see langword="true"/>，此时
    /// <c>origin/&lt;source&gt;</c> 指向源提交、<c>origin/&lt;target&gt;</c> 指向合并时的目标提交；
    /// 否则返回 <see langword="false"/>，调用方退回按分支拉取。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 拿不到合并提交不是错误：PR 有冲突时 ADO 根本不生成它。所以这里任何一步不对都只记日志、
    /// 返回 false，由老路兜底 —— 真正的网络或认证故障会在老路上以原样的错误再报一次。
    /// </para>
    /// <para>
    /// <b>必须核对第二个父提交等于 <see cref="PrMeta.SrcCommit"/>。</b> ADO 在推送后异步重算合并提交；
    /// PR 有冲突时 ref 也可能还留着上一次成功合并的结果。不核对就会在别的版本的代码上评审，
    /// 而票是记在 <c>SrcCommit</c> 这个 revision 名下的。对不上时把本地 ref 删掉，免得
    /// <c>az_pr.sh</c> 误用它。
    /// </para>
    /// <para>
    /// <c>origin/&lt;target&gt;</c> 指向合并提交的第一个父提交，也就是 ADO 计算合并时的目标分支，
    /// 不一定是目标分支此刻的最新提交；基准也就不是分叉点，而是那一刻的目标分支。
    /// </para>
    /// </remarks>
    private async Task<bool> TryFetchPrMergeAsync(string dir, PrMeta pr, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pr.SrcCommit))
        {
            return false;
        }

        if (NoMergeCommitStatuses.Contains(pr.MergeStatus))
        {
            _logger.LogInformation(
                "PR {PrId} 的 ADO 预合并结果是 {MergeStatus}，没有可用的合并提交，直接按分支拉取",
                pr.PrId, pr.MergeStatus);
            return false;
        }

        var mergeRef = PrMergeRef(pr.PrId);
        var fetch = await RunAsync(
            dir, ["fetch", "--quiet", "--no-tags", "--depth=2", "origin", "+" + mergeRef + ":" + mergeRef], ct)
            .ConfigureAwait(false);

        if (!fetch.Success)
        {
            _logger.LogInformation(
                "PR {PrId} 拿不到合并提交 {Ref}（多半是有冲突），退回按分支拉取：{Error}",
                pr.PrId, mergeRef, fetch.StdErr.Trim());
            return false;
        }

        var parents = await RunAsync(dir, ["rev-parse", mergeRef + "^1", mergeRef + "^2"], ct)
            .ConfigureAwait(false);
        var shas = parents.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!parents.Success || shas.Length != 2
            || !string.Equals(shas[1], pr.SrcCommit.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "PR {PrId} 的合并提交不对应本次源提交 {SrcCommit}（第二个父提交 {Actual}），退回按分支拉取",
                pr.PrId, pr.SrcCommit, shas.Length == 2 ? shas[1] : "无");
            _ = await RunAsync(dir, ["update-ref", "-d", mergeRef], ct).ConfigureAwait(false);
            return false;
        }

        await GitAsync(dir, ["update-ref", "refs/remotes/origin/" + pr.TargetBranch, shas[0]], ct)
            .ConfigureAwait(false);
        await GitAsync(dir, ["update-ref", "refs/remotes/origin/" + pr.SourceBranch, shas[1]], ct)
            .ConfigureAwait(false);

        return true;
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
    /// 最后只落下一张 Error 票。宁可多几次网络往返也要在这里补上。
    /// </para>
    /// <para>
    /// 加深按 <see cref="DeepenLadder"/> 逐级翻倍，而不是一步跳到很深。实测在已有的浅历史上
    /// 再加深是增量的（8→10 只收新增的 410 个对象），所以多一级只多一次往返；反过来一步跳到 200
    /// 在多语言大文件频繁改动的仓库上是几百 MB —— merge-base 往往近在深度 6，那一跳几乎全是白拉。
    /// </para>
    /// </remarks>
    private async Task EnsureMergeBaseAsync(string dir, PrMeta pr, CancellationToken ct)
    {
        if (await HasMergeBaseAsync(dir, pr, ct).ConfigureAwait(false))
        {
            return;
        }

        if (_options.FetchDepth <= 0)
        {
            // 本来就是全量历史，加深没有意义（对完整仓库 --unshallow 还会直接报错）。
            throw NoMergeBase(pr);
        }

        var reached = _options.FetchDepth;
        foreach (var deeper in DeepenLadder(_options.FetchDepth))
        {
            _logger.LogInformation(
                "PR {PrId} 的分支在深度 {Depth} 内没有共同祖先，加深到 {Deeper}",
                pr.PrId, reached, deeper);

            await GitAsync(dir, DeepenArgs(deeper, pr), ct).ConfigureAwait(false);
            reached = deeper;

            if (await HasMergeBaseAsync(dir, pr, ct).ConfigureAwait(false))
            {
                return;
            }
        }

        _logger.LogWarning(
            "PR {PrId} 加深到 {Deeper} 仍无共同祖先，退到全量历史（--unshallow）", pr.PrId, reached);

        await GitAsync(dir, ["fetch", "--quiet", "--no-tags", "--unshallow", "origin"], ct)
            .ConfigureAwait(false);

        if (!await HasMergeBaseAsync(dir, pr, ct).ConfigureAwait(false))
        {
            throw NoMergeBase(pr);
        }
    }

    private static InvalidOperationException NoMergeBase(PrMeta pr) => new(
        $"PR {pr.PrId} 的 {pr.SourceBranch} 与 {pr.TargetBranch} 即使在全量历史下也没有共同祖先，"
        + "三点 diff 无法成立，拒绝在错误的 diff 上评审");

    /// <summary>加深阶梯的上限；再找不到 merge-base 就直接 <c>--unshallow</c>。</summary>
    internal const int MaxLadderDepth = 256;

    /// <summary>
    /// 从 <paramref name="initialDepth"/> 起逐级翻倍的加深深度，不超过 <see cref="MaxLadderDepth"/>。
    /// </summary>
    /// <remarks>
    /// 例如初始 8 → 16、32、64、128、256。每级多一次往返（几秒），但总传输量只比刚好够用的深度
    /// 多不到一倍；不翻倍、一步跳深则可能多拉几十倍。
    /// </remarks>
    internal static IEnumerable<int> DeepenLadder(int initialDepth)
    {
        for (var depth = Math.Max(1, initialDepth) * 2; depth <= MaxLadderDepth; depth *= 2)
        {
            yield return depth;
        }
    }

    /// <summary>
    /// 加深到绝对深度 <paramref name="depth"/> 的 fetch 参数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>必须是 <c>--depth</c>（绝对深度），不能是 <c>--deepen</c>（在现有边界上再加）。</b>
    /// 相对加深要服务端广播 <c>deepen-relative</c>，而这里面对的 Azure DevOps Server 是协议 v0，
    /// 能力串只有 <c>multi_ack thin-pack side-band side-band-64k no-progress multi_ack_detailed
    /// no-done shallow allow-tip-sha1-in-want</c>。客户端一看没这项就直接
    /// <c>fatal: Server does not support --deepen</c>，请求都不发。
    /// </para>
    /// <para>
    /// 而 <see cref="GitAsync"/> 非零退出即抛 —— 于是下一级的 <c>--unshallow</c> 兜底永远执行不到，
    /// 本该救回来的评审变成一张 Error 票，还要按 <c>MaxReviewAttempts</c> 把整个拉取重试几遍。
    /// 三级台阶实际只有一级半。
    /// </para>
    /// <para>
    /// 用本地 <c>file://</c> 测照不出来：本地 git 是新版，这两项能力都有 —— 这也是它活到现在的原因。
    /// <c>--shallow-since</c> 同理（要 <c>deepen-since</c>，这台同样没有），一并别用。
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> DeepenArgs(int depth, PrMeta pr)
    {
        ArgumentNullException.ThrowIfNull(pr);

        return
        [
            "fetch", "--quiet", "--no-tags",
            "--depth=" + depth.ToString(CultureInfo.InvariantCulture),
            "origin", pr.SourceBranch, pr.TargetBranch,
        ];
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

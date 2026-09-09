using System.Globalization;
using System.Text;
using System.Text.Json;
using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure;

/// <summary>
/// 通过 <c>az</c> CLI 读写 Azure DevOps。
/// </summary>
/// <remarks>
/// <para>
/// 刻意走 CLI 而不是自己实现 REST 客户端：这套是 <b>Azure DevOps Server（自建）</b>，
/// 实测 AAD bearer token 打上去返回 TF400813（REST 需要 PAT + Basic），而 <c>az</c>
/// 已经把凭据管好了。顺带每个节点也不必各自维护 PAT。
/// </para>
/// <para>
/// azure-devops 扩展打到 Server 时每次会往 stderr 写一行
/// <c>WARNING: ... does not support Azure DevOps Server</c>，但功能正常 ——
/// 所以退出码为 0 时一律忽略 stderr。
/// </para>
/// </remarks>
public sealed class AzCliPrSource(
    ConclaveOptions options,
    IRepoLocator repos,
    ExecutableResolver executables,
    ILogger<AzCliPrSource> logger) : IPrSource
{
    public async Task<string> GetAuthenticatedIdentityAsync(CancellationToken ct)
    {
        // ADO Server 上没有 az ad signed-in-user，connectionData 是唯一可靠的取法。
        // api-version 必须是 5.0-preview：非 preview 会被拒，其他 area/resource 组合也不对。
        var json = await AzAsync([
            "devops", "invoke",
            "--area", "Location", "--resource", "connectionData",
            "--api-version", "5.0-preview", "-o", "json",
        ], ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("authenticatedUser", out var user))
        {
            if (user.TryGetProperty("properties", out var props)
                && props.TryGetProperty("Account", out var account)
                && account.TryGetProperty("$value", out var value))
            {
                var name = value.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }

            var display = Str(user, "providerDisplayName");
            if (display.Length > 0)
            {
                return display;
            }
        }

        throw new InvalidOperationException("connectionData 没有返回 authenticatedUser，请先跑 az devops login");
    }

    public async Task<IReadOnlyList<string>> ListProjectsAsync(CancellationToken ct)
    {
        var json = await AzAsync(["devops", "project", "list", "--query", "value[].name", "-o", "json"], ct)
            .ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray()
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)];
    }

    public async Task<IReadOnlyList<PrMeta>> ListActivePullRequestsAsync(string project, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);

        var json = await AzAsync(
            ["repos", "pr", "list", "--project", project, "--status", "active", "-o", "json"], ct)
            .ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 扩展有时返回裸数组，有时返回 { value: [...] }。
        var items = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("value", out var v) ? v : default;

        if (items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. items.EnumerateArray().Select(ParsePr).Where(p => p is not null).Select(p => p!)];
    }

    public async Task<PrMeta?> FindPullRequestAsync(int prId, CancellationToken ct)
    {
        // az repos pr show 不需要 --project，实测在 Server 上也能按 collection 级 ID 直接取。
        try
        {
            var json = await AzAsync(["repos", "pr", "show", "--id",
                prId.ToString(CultureInfo.InvariantCulture), "-o", "json"], ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var pr = ParsePr(doc.RootElement);
            return pr is null ? null : await EnrichWithDiffStatsAsync(pr, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "找不到 PR {PrId}", prId);
            return null;
        }
    }

    private static PrMeta? ParsePr(JsonElement e)
    {
        if (!e.TryGetProperty("pullRequestId", out var idProp))
        {
            return null;
        }

        var repository = e.TryGetProperty("repository", out var repo) ? repo : default;
        var projectName = repository.ValueKind == JsonValueKind.Object
            && repository.TryGetProperty("project", out var proj)
            && proj.TryGetProperty("name", out var projName)
                ? projName.GetString() ?? string.Empty
                : string.Empty;

        return new PrMeta
        {
            PrId = idProp.GetInt32(),
            Project = projectName,
            Repo = Str(repository, "name"),
            Title = Str(e, "title"),
            Author = e.TryGetProperty("createdBy", out var by) ? Str(by, "uniqueName") : string.Empty,
            SrcCommit = e.TryGetProperty("lastMergeSourceCommit", out var src) ? Str(src, "commitId") : string.Empty,
            IsDraft = e.TryGetProperty("isDraft", out var draft)
                && draft.ValueKind is JsonValueKind.True or JsonValueKind.False
                && draft.GetBoolean(),
            SourceBranch = ShortBranch(Str(e, "sourceRefName")),
            TargetBranch = ShortBranch(Str(e, "targetRefName")),
        };
    }

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
            ? p.GetString() ?? string.Empty
            : string.Empty;

    private static string ShortBranch(string refName)
        => refName.StartsWith("refs/heads/", StringComparison.Ordinal) ? refName["refs/heads/".Length..] : refName;

    public async Task<PrMeta> EnrichWithDiffStatsAsync(PrMeta pr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pr);

        if (!repos.Locate().TryGetValue(pr.Repo, out var repoPath))
        {
            // 本机没有这个 repo 的 clone。统计留 0 → quorum 退到 1，是安全的降级方向；
            // 而且本节点根本没有入席资格（见 SeatAssignment.Eligible），拿不到统计无妨。
            logger.LogDebug("本机没有 {Repo} 的 clone，跳过 diff 统计", pr.Repo);
            return pr;
        }

        try
        {
            var git = executables.Resolve(options.GitExecutable);

            _ = await ProcessRunner.RunAsync(
                git, ["-C", repoPath, "fetch", "--quiet", "origin", pr.SourceBranch, pr.TargetBranch],
                workingDirectory: repoPath, ct: ct).ConfigureAwait(false);

            // 三点语义：跟 PR 页面显示的一致（相对 merge-base 比较），而不是两个分支尖端直接 diff。
            var numstat = await ProcessRunner.RunAsync(
                git, ["-C", repoPath, "diff", "--numstat", $"origin/{pr.TargetBranch}...origin/{pr.SourceBranch}"],
                workingDirectory: repoPath, ct: ct).ConfigureAwait(false);

            if (!numstat.Success)
            {
                logger.LogDebug("git diff 失败（{Repo}）：{Err}", pr.Repo, numstat.StdErr.Trim());
                return pr;
            }

            var paths = new List<string>();
            var lines = 0;
            var files = 0;

            foreach (var row in numstat.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = row.Split('\t');
                if (parts.Length < 3)
                {
                    continue;
                }

                files++;
                paths.Add(parts[2].Trim());

                // 二进制文件的 numstat 是 "-"，跳过计数即可。
                if (int.TryParse(parts[0], CultureInfo.InvariantCulture, out var added))
                {
                    lines += added;
                }

                if (int.TryParse(parts[1], CultureInfo.InvariantCulture, out var removed))
                {
                    lines += removed;
                }
            }

            return pr with { FilesChanged = files, LinesChanged = lines, ChangedPaths = paths };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "算 {Repo} 的 diff 统计失败", pr.Repo);
            return pr;
        }
    }

    public async Task<int?> PostResultAsync(PrMeta pr, PromulgationPayload result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(result);

        var markdown = RenderComment(pr, result);
        var threadFile = Path.Combine(Path.GetTempPath(), $"conclave-thread-{pr.PrId}-{Guid.NewGuid():N}.json");
        var voteFile = false;

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                comments = new[] { new { parentCommentId = 0, commentType = "text", content = markdown } },
                status = "active",
            });
            await File.WriteAllTextAsync(threadFile, body, ct).ConfigureAwait(false);
            voteFile = true;

            // 与 az-pr-review skill 的 az_pr.sh 走同一条路径，行为保持一致。
            var json = await AzAsync([
                "devops", "invoke",
                "--area", "git", "--resource", "pullRequestThreads",
                "--route-parameters",
                    $"project={pr.Project}",
                    $"repositoryId={pr.Repo}",
                    $"pullRequestId={pr.PrId.ToString(CultureInfo.InvariantCulture)}",
                "--http-method", "POST",
                "--in-file", threadFile,
                "--api-version", "7.1",
                "-o", "json",
            ], ct).ConfigureAwait(false);

            int? threadId = null;
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("id", out var id) && id.TryGetInt32(out var parsed))
                {
                    threadId = parsed;
                }
            }

            var vote = result.Decision.ToAzVote();
            if (vote != "none")
            {
                _ = await AzAsync([
                    "repos", "pr", "set-vote",
                    "--id", pr.PrId.ToString(CultureInfo.InvariantCulture),
                    "--vote", vote, "-o", "json",
                ], ct).ConfigureAwait(false);
            }

            return threadId;
        }
        finally
        {
            if (voteFile)
            {
                try
                {
                    File.Delete(threadFile);
                }
                catch (IOException)
                {
                    // 临时文件删不掉不影响结果。
                }
            }
        }
    }

    /// <summary>把合并结论渲染成 PR 评论。Confidence 直接摊在读者面前，方便判断哪条值得看。</summary>
    private static string RenderComment(PrMeta pr, PromulgationPayload result)
    {
        var sb = new StringBuilder();
        _ = sb.Append("## Conclave 评审结论：").AppendLine(result.Decision.ToString());
        _ = sb.AppendLine();
        _ = sb.Append(CultureInfo.InvariantCulture, $"评审版本 `{result.RevisionId}`，")
              .Append(CultureInfo.InvariantCulture, $"{result.ActualQuorum}/{result.ExpectedQuorum} 个节点独立评审");
        _ = result.Degraded
            ? sb.AppendLine("（**降级：合格节点不足**）")
            : sb.AppendLine();
        _ = sb.AppendLine();

        if (result.Findings.Count == 0)
        {
            _ = sb.AppendLine("未发现问题。");
            return sb.ToString();
        }

        _ = sb.AppendLine("| 置信度 | 严重度 | 位置 | 问题 |");
        _ = sb.AppendLine("|---|---|---|---|");
        foreach (var f in result.Findings)
        {
            _ = sb.Append(CultureInfo.InvariantCulture, $"| {f.Confidence:P0} ({f.Mentions}/{result.ActualQuorum}) ")
                  .Append(CultureInfo.InvariantCulture, $"| {f.Best.Severity} ")
                  .Append(CultureInfo.InvariantCulture, $"| `{f.Best.File}:{f.Best.Line}` ")
                  .Append(CultureInfo.InvariantCulture, $"| {f.Best.Title} |")
                  .AppendLine();
        }

        _ = sb.AppendLine();
        foreach (var f in result.Findings.Where(x => x.Best.Detail.Length > 0))
        {
            _ = sb.Append("### ").AppendLine(f.Best.Title);
            _ = sb.Append('`').Append(f.Best.File).Append(':').Append(f.Best.Line).AppendLine("`");
            _ = sb.AppendLine().AppendLine(f.Best.Detail).AppendLine();
        }

        _ = sb.Append(CultureInfo.InvariantCulture,
            $"<sub>Conclave · PR {pr.PrId} · 置信度 = 独立提到该问题的节点占比</sub>");
        return sb.ToString();
    }

    private async Task<string> AzAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await ProcessRunner
            .RunAsync(executables.Resolve(options.AzExecutable), args, ct: ct)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"az {string.Join(' ', args)} 失败（exit {result.ExitCode}）：{result.StdErr.Trim()}");
        }

        // 退出码 0 时 stderr 里通常只有 ADO Server 的那句 WARNING，忽略。
        var stdout = result.StdOut.Trim();
        return string.IsNullOrEmpty(stdout) ? "[]" : stdout;
    }
}

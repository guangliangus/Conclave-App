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
/// <para>
/// 改动统计走 <c>pullRequestIterationChanges</c> 而不是本机 <c>git diff</c>：
/// 「本机有 clone」这条硬规则已经删掉，评审用的代码是评审时才临时拉的。代价是
/// ADO 不提供行数（实测确认），所以 quorum 的档位按文件数分。
/// </para>
/// </remarks>
public sealed class AzCliPrSource(
    ConclaveOptions options,
    ExecutableResolver executables,
    ILogger<AzCliPrSource> logger) : IPrSource
{
    /// <summary>组织地址只探一次 —— 它来自本机的 az 配置，一个进程生命周期内不会变。</summary>
    private string? _orgUrl;

    /// <summary>collection 级列举一次最多要多少条。</summary>
    private const int MaxPullRequests = 500;

    /// <summary>project 清单的缓存时长。</summary>
    private static readonly TimeSpan ProjectListTtl = TimeSpan.FromMinutes(10);

    private IReadOnlyList<string>? _projects;
    private DateTimeOffset _projectsAt;

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

    /// <summary>
    /// 列出有权限的 project。缓存 <see cref="ProjectListTtl"/>。
    /// </summary>
    /// <remarks>
    /// 缓存的理由跟别处一样是「一次 az 调用 = 一个 Python 进程 ≈ 1.25 秒」，而 project
    /// 清单几个月才变一次。它只有两个用途：给 mesh 分片（<c>DiscoveryShare</c>），
    /// 以及进心跳供 <see cref="Domain.Elector.HasProject"/> 判权限 —— 两者都容得下
    /// 十分钟的滞后。
    /// </remarks>
    public async Task<IReadOnlyList<string>> ListProjectsAsync(CancellationToken ct)
    {
        if (_projects is not null && DateTimeOffset.UtcNow - _projectsAt < ProjectListTtl)
        {
            return _projects;
        }

        var json = await AzAsync(["devops", "project", "list", "--query", "value[].name", "-o", "json"], ct)
            .ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        _projects = [.. doc.RootElement.EnumerateArray()
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)];
        _projectsAt = DateTimeOffset.UtcNow;
        return _projects;
    }

    /// <summary>
    /// 整个 collection 的活跃 PR，一次调用。
    /// </summary>
    /// <remarks>
    /// 走 <c>az devops invoke</c> 打 collection 级的 <c>_apis/git/pullrequests</c>：
    /// <c>az repos pr list</c> 必须带 <c>--project</c>，而这个路由不用 —— 实测这台
    /// ADO Server 上 1.5 秒返回全部 29 个，字段跟逐 project 列举完全一致。
    /// <para>
    /// 不分页：一次要 <see cref="MaxPullRequests"/> 条，取满了就警告而不是静默截断。
    /// 活跃 PR 上千的 collection 已经不是这个工具的场景（那时候队列本身就没法看）。
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<PrMeta>> ListAllActivePullRequestsAsync(CancellationToken ct)
    {
        var json = await AzAsync(
            [
                "devops", "invoke",
                "--area", "git", "--resource", "pullrequests",
                "--api-version", "5.0",
                "--query-parameters",
                "searchCriteria.status=active",
                "$top=" + MaxPullRequests.ToString(CultureInfo.InvariantCulture),
                "-o", "json",
            ], ct).ConfigureAwait(false);

        var prs = ParsePrList(json);

        if (prs.Count >= MaxPullRequests)
        {
            logger.LogWarning(
                "collection 级列举取到 {Count} 条，已达上限 —— 可能被截断，超出的 PR 这一轮不会被发现",
                prs.Count);
        }

        return prs;
    }

    public async Task<IReadOnlyList<PrMeta>> ListActivePullRequestsAsync(string project, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);

        var json = await AzAsync(
            ["repos", "pr", "list", "--project", project, "--status", "active", "-o", "json"], ct)
            .ConfigureAwait(false);

        return ParsePrList(json);
    }

    /// <summary>PR 列表的 JSON → <see cref="PrMeta"/>。两条列举路径共用。</summary>
    private static IReadOnlyList<PrMeta> ParsePrList(string json)
    {
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
            return pr is null ? null : await EnrichWithChangeStatsAsync(pr, ct).ConfigureAwait(false);
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
            // 实测 az repos pr list 把它返回成 null，只有 az repos pr show 才给真值。
            // 为空时由 GetCloneUrlAsync 按组织地址拼出来。
            RemoteUrl = Str(repository, "remoteUrl"),
        };
    }

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
            ? p.GetString() ?? string.Empty
            : string.Empty;

    private static string ShortBranch(string refName)
        => refName.StartsWith("refs/heads/", StringComparison.Ordinal) ? refName["refs/heads/".Length..] : refName;

    /// <summary>
    /// 结论转成 <c>az repos pr set-vote --vote</c> 接受的字符串。
    /// </summary>
    /// <remarks>
    /// 这个映射刻意放在这里而不是领域层：取值是 <c>az</c> 的命令行词汇，
    /// 换个 ADO 客户端就得跟着换，而 <see cref="ReviewDecision"/> 不该跟着动。
    /// <c>none</c> 表示不投票 —— <see cref="ReviewDecision.Error"/> 是本机跑挂了，
    /// 不是一个评审意见，不该在 PR 上留下任何一票。
    /// </remarks>
    private static string AzVote(ReviewDecision decision) => decision switch
    {
        ReviewDecision.Approve => "approve",
        ReviewDecision.ApproveWithSuggestions => "approve-with-suggestions",
        ReviewDecision.WaitForAuthor => "wait-for-author",
        ReviewDecision.Reject => "reject",
        _ => "none",
    };

    public async Task<PrMeta> EnrichWithChangeStatsAsync(PrMeta pr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pr);

        try
        {
            // 先问最后一个 iteration：作者每 push 一次就多一个 iteration，
            // 拿第一个（iterationId=1）只会看到首版的改动。
            var iteration = await LatestIterationAsync(pr, ct).ConfigureAwait(false);
            if (iteration is null)
            {
                logger.LogDebug("PR {PrId} 读不到 iteration，改动统计留 0", pr.PrId);
                return pr;
            }

            var json = await AzAsync([
                "devops", "invoke",
                "--area", "git", "--resource", "pullRequestIterationChanges",
                "--route-parameters",
                    $"project={pr.Project}",
                    $"repositoryId={pr.Repo}",
                    $"pullRequestId={pr.PrId.ToString(CultureInfo.InvariantCulture)}",
                    $"iterationId={iteration.Value.ToString(CultureInfo.InvariantCulture)}",
                "--api-version", "7.1", "-o", "json",
            ], ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("changeEntries", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                return pr;
            }

            var paths = new List<string>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("item", out var item))
                {
                    continue;
                }

                // 目录项也会出现在 changeEntries 里（isFolder=true），它们不算改动文件，
                // 也不该拿去撞 ReservedMatters 的路径模式。
                if (item.TryGetProperty("isFolder", out var isFolder)
                    && isFolder.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                var path = Str(item, "path").TrimStart('/');
                if (path.Length > 0)
                {
                    paths.Add(path);
                }
            }

            return pr with { FilesChanged = paths.Count, ChangedPaths = paths };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 统计留 0 → quorum 退到 1，是安全的降级方向：宁可少跑几遍，不要因为读不到
            // 统计就把 PR 整个漏掉。
            logger.LogWarning(ex, "读 PR {PrId} 的改动统计失败，quorum 将退到 1", pr.PrId);
            return pr;
        }
    }

    /// <summary>最后一个 iteration 的 id。</summary>
    private async Task<int?> LatestIterationAsync(PrMeta pr, CancellationToken ct)
    {
        var json = await AzAsync([
            "devops", "invoke",
            "--area", "git", "--resource", "pullRequestIterations",
            "--route-parameters",
                $"project={pr.Project}",
                $"repositoryId={pr.Repo}",
                $"pullRequestId={pr.PrId.ToString(CultureInfo.InvariantCulture)}",
            "--api-version", "7.1", "-o", "json",
        ], ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var items = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("value", out var v) ? v : default;

        if (items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        int? latest = null;
        foreach (var it in items.EnumerateArray())
        {
            if (it.TryGetProperty("id", out var id) && id.TryGetInt32(out var value))
            {
                latest = latest is null ? value : Math.Max(latest.Value, value);
            }
        }

        return latest;
    }

    public async Task<string> GetCloneUrlAsync(PrMeta pr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pr);

        if (!string.IsNullOrWhiteSpace(pr.RemoteUrl))
        {
            return pr.RemoteUrl;
        }

        var org = await GetOrgUrlAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(org))
        {
            throw new InvalidOperationException(
                $"PR {pr.PrId} 的快照里没有 remoteUrl，也读不到 az 的组织地址 —— "
                + "请配 Conclave:AzureDevOpsOrgUrl 或跑 az devops configure --defaults organization=<url>");
        }

        // 实测形态：https://host/Collection/<project>/_git/<repo>
        return $"{org.TrimEnd('/')}/{Uri.EscapeDataString(pr.Project)}/_git/{Uri.EscapeDataString(pr.Repo)}";
    }

    /// <summary>组织（collection）地址：配置优先，其次读 az 的默认值。</summary>
    public async Task<string> GetOrgUrlAsync(CancellationToken ct)
    {
        if (_orgUrl is not null)
        {
            return _orgUrl;
        }

        if (!string.IsNullOrWhiteSpace(options.AzureDevOpsOrgUrl))
        {
            _orgUrl = options.AzureDevOpsOrgUrl.Trim();
            return _orgUrl;
        }

        try
        {
            // configure --list 输出的是 INI 风格的纯文本，不是 JSON，所以不能走 AzAsync。
            var result = await ProcessRunner.RunAsync(
                executables.Resolve(options.AzExecutable), ["devops", "configure", "--list"], ct: ct)
                .ConfigureAwait(false);

            foreach (var line in result.StdOut.Split('\n'))
            {
                var idx = line.IndexOf('=', StringComparison.Ordinal);
                if (idx > 0 && line[..idx].Trim().Equals("organization", StringComparison.OrdinalIgnoreCase))
                {
                    _orgUrl = line[(idx + 1)..].Trim();
                    logger.LogInformation("Azure DevOps 组织地址 {Org}（来自 az 默认配置）", _orgUrl);
                    return _orgUrl;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "读不到 az 的组织地址");
        }

        _orgUrl = string.Empty;
        return _orgUrl;
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

            var vote = AzVote(result.Decision);
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

        // 这里拿到的整份 stdout 要当一个 JSON 文档解析，而截断保留的是<b>尾巴</b> ——
        // 少了开头的 `[` 或 `{`，解析器只会报一句「意外的字符」，看不出真正的原因。
        // 所以在这里就说清楚：不是 az 坏了，是输出超过了 ProcessRunner 的上限。
        if (result.Truncated)
        {
            throw new InvalidOperationException(
                $"az {string.Join(' ', args)} 的输出超过 {ProcessRunner.MaxCapturedChars} 字符已被截断，"
                + "解析不了。缩小查询范围（project 白名单 / $top）或调高上限");
        }

        // 退出码 0 时 stderr 里通常只有 ADO Server 的那句 WARNING，忽略。
        var stdout = result.StdOut.Trim();
        return string.IsNullOrEmpty(stdout) ? "[]" : stdout;
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure;

/// <summary>
/// 起 <c>claude -p</c> 子进程跑 <c>/az-pr-review</c>，把输出解析成一张 Ballot。
/// </summary>
/// <remarks>
/// <para>
/// <b>collect 模式</b>：环境变量 <c>REVIEW_MODE=collect</c> 要求 skill 只产出结论、
/// <b>不</b>发评论也不投票。quorum=3 时有 3 个节点跑同一个 PR，若各自都投递，
/// 一个 PR 会收到 3 条重复评论和 3 次投票。投递只由 round=0 的节点在收齐票后做一次。
/// </para>
/// <para>
/// <b>临时工作区</b>：每次评审在 <c>~/.conclave/work/</c> 下现拉一份源分支，子进程的工作目录
/// 就是它，评完（含失败与超时）立即删除。所以任何节点都能评任何仓库，不需要本机预先 clone，
/// 也不会动到本机正在用的工作副本。
/// </para>
/// <para>
/// <b>输出契约</b>：skill 需要在 <c>REVIEW_MODE=collect</c> 下于回复末尾输出一个
/// <c>```json</c> 代码块，形如
/// <c>{"decision":"reject","findings":[{"file":"a.cs","line":12,"severity":"major","title":"…","detail":"…"}]}</c>。
/// 解析不到就出 <see cref="ReviewDecision.Error"/> 票 —— 刻意不去猜结论：
/// 猜错方向会让一个该拦的 PR 被放过，而 Error 票只会让这一轮不计入多数决。
/// </para>
/// </remarks>
public sealed class ClaudeReviewRunner(
    ConclaveOptions options,
    IPrSource prSource,
    GitWorkspaceFactory workspaces,
    ExecutableResolver executables,
    ReviewProgressLog progress,
    ILogger<ClaudeReviewRunner> logger) : IReviewRunner
{
    public async Task<BallotPayload> RunAsync(
        Revision revision, PrMeta pr, int round, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(pr);

        // 现拉一份代码，评完即删。刻意不碰本机已有的 clone —— 早先直接在用户的工作副本里
        // 跑 git fetch/checkout，会动到人家正在改的仓库。
        progress.Begin(
            revision.Id,
            $"评审 {pr.Repo}#{pr.PrId.ToString(CultureInfo.InvariantCulture)} round={round.ToString(CultureInfo.InvariantCulture)}");

        GitWorkspace workspace;
        try
        {
            var cloneUrl = await prSource.GetCloneUrlAsync(pr, ct).ConfigureAwait(false);
            progress.Append(revision.Id, $"拉取工作区 {pr.SourceBranch} ← {cloneUrl}");
            workspace = await workspaces
                .CheckoutAsync(pr, cloneUrl, $"{revision.Id}-r{round}", ct)
                .ConfigureAwait(false);
            progress.Append(revision.Id, "工作区就绪（merge-base 已验证）");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 拉不下来就出 Error 票：计入 quorum 分母、链上留原因，PR 不会永远等下去。
            logger.LogError(ex, "拉 {Repo} 的临时工作区失败（{Revision} round={Round}）",
                pr.Repo, revision.Id, round);
            progress.End(revision.Id, "拉取工作区失败：" + ex.Message);
            return new BallotPayload(
                revision.Id, round, ReviewDecision.Error, [], "n/a", 0, ReviewUsage.None,
                Error: $"拉取 {pr.Repo} 的临时工作区失败：{ex.Message}");
        }

        await using (workspace.ConfigureAwait(false))
        {
            return await RunInAsync(workspace.Path, revision, pr, round, ct).ConfigureAwait(false);
        }
    }

    private async Task<BallotPayload> RunInAsync(
        string repoPath, Revision revision, PrMeta pr, int round, CancellationToken ct)
    {
        // 会话 ID 从 (revisionId, round) 确定性派生：日后要追某一票是怎么来的，
        // 直接 claude --resume <这个 ID> 就能翻出当时的完整会话。
        var sessionId = DeterministicUuid($"conclave:{revision.Id}#{round}");

        // stream-json 而不是 json：后者要等整场评审结束才吐一个大对象，
        // 期间界面上只有一个计时器 —— 卡住和正常慢分不出来。stream-json 是 NDJSON，
        // 每个事件（起会话、调工具、出文本、收尾）落地就是一行，正好喂给日志面板。
        // 最后那一行 type=result 跟 --output-format json 的输出是同一个对象，
        // 所以出票的解析路径一点没变（见 ResultLine）。
        var args = new List<string>
        {
            "-p", $"/az-pr-review {pr.PrId.ToString(CultureInfo.InvariantCulture)}",
            "--output-format", "stream-json",
            "--verbose",
            "--session-id", sessionId.ToString(),
            "--allowedTools", "Bash", "Read", "Grep", "Glob",
        };

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["REVIEW_MODE"] = "collect",
            ["CONCLAVE_REVISION"] = revision.Id,
            ["CONCLAVE_ROUND"] = round.ToString(CultureInfo.InvariantCulture),
        };

        progress.Append(revision.Id, "claude 启动，会话 " + sessionId.ToString()[..8]);

        var sw = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync(
            executables.Resolve(options.ClaudeExecutable), args, repoPath, env, ct,
            onOutputLine: line => progress.Append(revision.Id, Summarize(line)),
            onErrorLine: line => progress.Append(revision.Id, "stderr: " + Truncate(line, 300)))
            .ConfigureAwait(false);
        sw.Stop();

        if (!result.Success)
        {
            progress.End(
                revision.Id,
                $"claude 退出码 {result.ExitCode.ToString(CultureInfo.InvariantCulture)}");
            return new BallotPayload(
                revision.Id, round, ReviewDecision.Error, [], "n/a", sw.ElapsedMilliseconds,
                ReviewUsage.None,
                Error: $"claude 退出码 {result.ExitCode}：{Truncate(result.StdErr, 2000)}");
        }

        if (result.Truncated)
        {
            // 说出来而不是让它表现成「解析不到契约」。stream-json 要的那一行在末尾、
            // 所以多半还是解得出来的，但半年后回看这张票时得知道当时输出被截过。
            logger.LogWarning(
                "{Revision} round={Round} 的 claude 输出超过 {Max} 字符，开头已被丢弃",
                revision.Id, round, ProcessRunner.MaxCapturedChars);
            progress.Append(revision.Id, "⚠️ 输出过大，日志开头已被丢弃");
        }

        var ballot = Parse(revision, round, ResultLine(result.StdOut), sw.ElapsedMilliseconds);
        progress.End(
            revision.Id,
            string.Create(
                CultureInfo.InvariantCulture,
                $"完成：{ballot.Decision} · {ballot.Findings.Count} 条问题 · {sw.Elapsed.TotalMinutes:F1} 分钟"));

        return ballot;
    }

    /// <summary>
    /// 从 NDJSON 里挑出最后那个 <c>type=result</c> 的对象。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 它跟 <c>--output-format json</c> 的输出是同一个形状，所以 <see cref="Parse"/> 不用改。
    /// 一行都挑不出来时原样返回 —— 让 Parse 去报「不是合法 JSON」，那条错误信息比
    /// 这里另编一句更有用（它会带上原文）。
    /// </para>
    /// <para>
    /// <b>从后往前扫，而且不 <c>Split</c>。</b> 要找的那一行是最后一个事件，所以正着扫
    /// 意味着把整份 NDJSON 切成一个几十万元素的字符串数组（又一份完整拷贝），再对每一行
    /// 起一次 <see cref="JsonDocument"/> —— 而 <c>stream-json --verbose</c> 会把每个工具
    /// 返回体的全文都打出来，那份输出本身就能有几百 MB。倒着扫通常第一行就命中。
    /// </para>
    /// </remarks>
    internal static string ResultLine(string ndjson)
    {
        var end = ndjson.Length;

        while (end > 0)
        {
            var start = ndjson.LastIndexOf('\n', end - 1) + 1;
            var line = ndjson.AsSpan(start, end - start).Trim();

            if (line.Length > 0 && line[0] == '{' && IsResult(line))
            {
                return line.ToString();
            }

            end = start - 1;   // 跳过这一行的换行符；start 为 0 时变成 -1，循环结束
        }

        return ndjson;
    }

    /// <summary>这一行是不是 <c>type=result</c> 的事件。认不出（半行、非 JSON）一律 false。</summary>
    private static bool IsResult(ReadOnlySpan<char> line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line.ToString());
            return doc.RootElement.TryGetProperty("type", out var type) && type.ValueEquals("result");
        }
        catch (JsonException)
        {
            // 不是完整 JSON 的行（比如被截断的）直接跳过。
            return false;
        }
    }

    /// <summary>
    /// 把一条 stream-json 事件压成一行人话。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只认几种已知事件，认不出来的返回空串（<see cref="ReviewProgressLog.Append"/> 会忽略）——
    /// 事件的形状随 Claude Code 版本会变，而<b>日志面板认不出新事件的后果只是少显示一行</b>，
    /// 绝不该让评审本身出错。所以这里全程不抛。
    /// </para>
    /// <para>
    /// 工具调用带上参数里最能说明「在看什么」的那一项（Bash 的命令、Read 的文件路径），
    /// 截断到能读的长度 —— 完整参数动辄几千字符，刷屏之后反而看不出进度。
    /// </para>
    /// </remarks>
    internal static string Summarize(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "system":
                    return root.TryGetProperty("subtype", out var sub) && sub.GetString() == "init"
                        ? "会话已建立，开始读代码"
                        : string.Empty;

                case "assistant":
                    return Content(root);

                case "user":
                    // 工具的返回体又长又是给模型看的，只记「回来了」这件事。
                    return "  ← 工具返回";

                case "result":
                    var cost = root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDouble(out var usd)
                        ? usd
                        : 0;
                    var turns = root.TryGetProperty("num_turns", out var n) && n.TryGetInt32(out var count)
                        ? count
                        : 0;
                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"claude 收尾：{turns} 轮 · ${cost:F2}");

                default:
                    return string.Empty;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return string.Empty;
        }
    }

    /// <summary>助手消息里的文本与工具调用，压成一行。</summary>
    private static string Content(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>(2);

        foreach (var item in content.EnumerateArray())
        {
            var kind = item.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (kind == "text" && item.TryGetProperty("text", out var text))
            {
                var value = text.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    parts.Add(Truncate(value.Trim().ReplaceLineEndings(" "), 220));
                }
            }
            else if (kind == "tool_use")
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : "tool";
                parts.Add("▸ " + name + Argument(item));
            }
        }

        return string.Join(" ", parts);
    }

    /// <summary>工具调用里最能说明「在看什么」的那一个参数。</summary>
    private static string Argument(JsonElement toolUse)
    {
        if (!toolUse.TryGetProperty("input", out var input)
            || input.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var key in (string[])["command", "file_path", "pattern", "path", "prompt"])
        {
            if (input.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text)
            {
                return ": " + Truncate(text.ReplaceLineEndings(" "), 160);
            }
        }

        return string.Empty;
    }

    private BallotPayload Parse(Revision revision, int round, string stdout, long elapsedMs)
    {
        string text;
        var model = "unknown";
        var usage = ReviewUsage.None;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            text = root.TryGetProperty("result", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            usage = ExtractUsage(root);
            model = usage.Models.Count > 0
                ? usage.Models.OrderByDescending(m => m.OutputTokens).First().Model
                : "unknown";

            if (root.TryGetProperty("is_error", out var isError)
                && isError.ValueKind == JsonValueKind.True)
            {
                // 失败也要把计量记下来 —— 烧掉的 token 不会因为失败而退回。
                return new BallotPayload(
                    revision.Id, round, ReviewDecision.Error, [], model, elapsedMs, usage,
                    Error: Truncate(text, 2000));
            }
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "claude 的 --output-format json 输出解析失败");
            return new BallotPayload(
                revision.Id, round, ReviewDecision.Error, [], model, elapsedMs, usage,
                Error: "claude 输出不是合法 JSON：" + Truncate(stdout, 500));
        }

        var contract = ExtractJsonBlock(text);
        if (contract is null)
        {
            // 刻意不从自然语言里猜结论：猜错方向会放过该拦的 PR。
            logger.LogWarning(
                "{Revision} round={Round} 没有输出 collect 契约的 JSON 块 —— skill 需要加 REVIEW_MODE 分支",
                revision.Id, round);
            return new BallotPayload(
                revision.Id, round, ReviewDecision.Error, [], model, elapsedMs, usage,
                Error: "未找到 collect 契约的 JSON 块（skill 需要 REVIEW_MODE=collect 分支）。原文：\n"
                    + Truncate(text, 1500));
        }

        return contract with
        {
            RevisionId = revision.Id,
            Round = round,
            Model = model,
            DurationMs = elapsedMs,
            Usage = usage,
        };
    }

    /// <summary>
    /// 从 claude 的输出里取 token 与折算金额。
    /// </summary>
    /// <remarks>
    /// 聚合数刻意由 <c>modelUsage</c> 逐项相加，而不是读顶层的 <c>usage</c> 对象：
    /// 报表要能对上「总计 = 各模型之和」，读两个不同来源迟早对不上。
    /// 金额则取顶层 <c>total_cost_usd</c>，那是 claude 自己的权威汇总。
    /// </remarks>
    private static ReviewUsage ExtractUsage(JsonElement root)
    {
        var models = new List<ModelUsage>();

        if (root.TryGetProperty("modelUsage", out var modelUsage)
            && modelUsage.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in modelUsage.EnumerateObject())
            {
                var v = entry.Value;
                models.Add(new ModelUsage(
                    entry.Name,
                    Str(v, "canonicalModel", entry.Name),
                    Num(v, "inputTokens"),
                    Num(v, "outputTokens"),
                    Num(v, "cacheReadInputTokens"),
                    Num(v, "cacheCreationInputTokens"),
                    Num(v, "thinkingTokens"),
                    Money(v, "costUSD")));
            }
        }

        if (models.Count == 0)
        {
            return ReviewUsage.None;
        }

        var basis = "list";
        if (root.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in mu.EnumerateObject())
            {
                basis = Str(entry.Value, "costBasis", "list");
                break;
            }
        }

        return new ReviewUsage(
            models.Sum(m => m.InputTokens),
            models.Sum(m => m.OutputTokens),
            models.Sum(m => m.CacheReadTokens),
            models.Sum(m => m.CacheWriteTokens),
            models.Sum(m => m.ThinkingTokens),
            root.TryGetProperty("total_cost_usd", out var cost) && cost.TryGetDecimal(out var total)
                ? total
                : models.Sum(m => m.CostUsd),
            basis,
            root.TryGetProperty("num_turns", out var turns) && turns.TryGetInt32(out var t) ? t : 0,
            models);
    }

    private static long Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.TryGetInt64(out var v) ? v : 0;

    private static decimal Money(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.TryGetDecimal(out var v) ? v : 0m;

    private static string Str(JsonElement e, string name, string fallback)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? fallback
            : fallback;

    /// <summary>
    /// 从回复正文里抠出 collect 契约。优先找最后一个 <c>```json</c> 围栏，
    /// 找不到再看整段是否本身就是 JSON。
    /// </summary>
    private BallotPayload? ExtractJsonBlock(string text)
    {
        foreach (var candidate in JsonCandidates(text))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<CollectContract>(candidate, ContractJson);
                if (dto is null)
                {
                    continue;
                }

                var findings = (dto.Findings ?? [])
                    .Select(f => new Finding(
                        f.File ?? "(unknown)",
                        f.Line,
                        ParseSeverity(f.Severity),
                        f.Title ?? "(untitled)",
                        f.Detail ?? string.Empty))
                    .ToList();

                return new BallotPayload(
                    string.Empty, 0, ParseDecision(dto.Decision), findings, "unknown", 0);
            }
            catch (JsonException)
            {
                // 这一段不是契约，试下一段。
            }
        }

        return null;
    }

    /// <summary>从后往前扫围栏块，最后再拿整段兜底。</summary>
    private static IEnumerable<string> JsonCandidates(string text)
    {
        const string Fence = "```";
        var blocks = new List<string>();
        var i = 0;

        while (true)
        {
            var open = text.IndexOf(Fence, i, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            var afterFence = open + Fence.Length;
            var lineEnd = text.IndexOf('\n', afterFence);
            if (lineEnd < 0)
            {
                break;
            }

            var close = text.IndexOf(Fence, lineEnd, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            var lang = text[afterFence..lineEnd].Trim();
            if (lang.Length == 0 || lang.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                blocks.Add(text[(lineEnd + 1)..close]);
            }

            i = close + Fence.Length;
        }

        for (var b = blocks.Count - 1; b >= 0; b--)
        {
            yield return blocks[b];
        }

        yield return text;
    }

    private static readonly JsonSerializerOptions ContractJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static ReviewDecision ParseDecision(string? raw) => Normalize(raw) switch
    {
        "approve" => ReviewDecision.Approve,
        "approvewithsuggestions" or "approvewithsuggestion" or "suggest" => ReviewDecision.ApproveWithSuggestions,
        "waitforauthor" or "wait" => ReviewDecision.WaitForAuthor,
        "reject" => ReviewDecision.Reject,
        _ => ReviewDecision.Error,
    };

    private static Severity ParseSeverity(string? raw) => Normalize(raw) switch
    {
        "critical" or "blocker" => Severity.Critical,
        "major" or "high" => Severity.Major,
        "minor" or "medium" or "low" => Severity.Minor,
        _ => Severity.Info,
    };

    private static string Normalize(string? raw)
        => (raw ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal)
                                .Replace("_", string.Empty, StringComparison.Ordinal)
                                .Replace(" ", string.Empty, StringComparison.Ordinal)
                                .ToLowerInvariant();

    private static string Truncate(string s, int max)
        => s.Length <= max ? s.Trim() : string.Concat(s.AsSpan(0, max).Trim(), "…（已截断）");

    /// <summary>
    /// 由种子确定性派生一个 v4 形态的 UUID，供 <c>--session-id</c> 用。
    /// </summary>
    /// <remarks>
    /// 必须走 <c>bigEndian: true</c> 重载。<c>new Guid(byte[])</c> 把前三段按小端读，
    /// 于是 <c>bytes[6]</c> 落到字符串的第 8 个十六进制位而不是 version 位上 ——
    /// 那样派生出来的 UUID 版本号是错的。
    /// </remarks>
    internal static Guid DeterministicUuid(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);   // version 4
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);   // RFC 4122 variant
        return new Guid(bytes, bigEndian: true);
    }

    private sealed record CollectContract(string? Decision, List<ContractFinding>? Findings);

    private sealed record ContractFinding(
        string? File, int Line, string? Severity, string? Title, string? Detail);
}

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
/// <b>输出契约</b>：skill 需要在 <c>REVIEW_MODE=collect</c> 下于回复末尾输出一个
/// <c>```json</c> 代码块，形如
/// <c>{"decision":"reject","findings":[{"file":"a.cs","line":12,"severity":"major","title":"…","detail":"…"}]}</c>。
/// 解析不到就出 <see cref="ReviewDecision.Error"/> 票 —— 刻意不去猜结论：
/// 猜错方向会让一个该拦的 PR 被放过，而 Error 票只会让这一轮不计入多数决。
/// </para>
/// </remarks>
public sealed class ClaudeReviewRunner(
    ConclaveOptions options,
    IRepoLocator repos,
    ILogger<ClaudeReviewRunner> logger) : IReviewRunner
{
    public async Task<BallotPayload> RunAsync(
        Revision revision, PrMeta pr, int round, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(pr);

        if (!repos.Locate().TryGetValue(pr.Repo, out var repoPath))
        {
            // 入席前 SeatAssignment.Eligible 已经查过，走到这里说明 clone 在评审开始后被挪走了。
            return new BallotPayload(
                revision.Id, round, ReviewDecision.Error, [], "n/a", 0, ReviewUsage.None,
                Error: $"本机找不到 {pr.Repo} 的 clone");
        }

        // 会话 ID 从 (revisionId, round) 确定性派生：日后要追某一票是怎么来的，
        // 直接 claude --resume <这个 ID> 就能翻出当时的完整会话。
        var sessionId = DeterministicUuid($"conclave:{revision.Id}#{round}");

        var args = new List<string>
        {
            "-p", $"/az-pr-review {pr.PrId.ToString(CultureInfo.InvariantCulture)}",
            "--output-format", "json",
            "--session-id", sessionId.ToString(),
            "--allowedTools", "Bash", "Read", "Grep", "Glob",
        };

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["REVIEW_MODE"] = "collect",
            ["CONCLAVE_REVISION"] = revision.Id,
            ["CONCLAVE_ROUND"] = round.ToString(CultureInfo.InvariantCulture),
        };

        var sw = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync(
            options.ClaudeExecutable, args, repoPath, env, ct).ConfigureAwait(false);
        sw.Stop();

        if (!result.Success)
        {
            return new BallotPayload(
                revision.Id, round, ReviewDecision.Error, [], "n/a", sw.ElapsedMilliseconds,
                ReviewUsage.None,
                Error: $"claude 退出码 {result.ExitCode}：{Truncate(result.StdErr, 2000)}");
        }

        return Parse(revision, round, result.StdOut, sw.ElapsedMilliseconds);
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

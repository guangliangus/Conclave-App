using Conclave.Domain;
using Conclave.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

public class ClaudeReviewRunnerTests
{
    private static readonly Revision V1 = new("liontrip-cms", 2912, "81cc633a00000000");

    /// <summary>把一段回复正文包成 claude 的 <c>type=result</c> 那一行。</summary>
    private static string Result(string text, bool isError = false)
        => ActaJson.Serialize(new { type = "result", is_error = isError, result = text });

    private static BallotPayload Parse(string stdout)
        => ClaudeReviewRunner.Parse(V1, 0, stdout, 1234, NullLogger.Instance);

    [Fact]
    public void Session_id_is_stable_for_the_same_revision_and_round()
    {
        var a = ClaudeReviewRunner.DeterministicUuid("conclave:2721@dc1d1d47#0");
        var b = ClaudeReviewRunner.DeterministicUuid("conclave:2721@dc1d1d47#0");

        // 稳定才能事后 claude --resume <id> 翻出当时的完整会话。
        Assert.Equal(a, b);
    }

    [Fact]
    public void Session_id_differs_per_round_and_per_revision()
    {
        var r0 = ClaudeReviewRunner.DeterministicUuid("conclave:2721@dc1d1d47#0");
        var r1 = ClaudeReviewRunner.DeterministicUuid("conclave:2721@dc1d1d47#1");
        var other = ClaudeReviewRunner.DeterministicUuid("conclave:2722@dc1d1d47#0");

        Assert.NotEqual(r0, r1);
        Assert.NotEqual(r0, other);
    }

    [Fact]
    public void Session_id_is_a_well_formed_v4_uuid()
    {
        // claude --session-id 要求合法 UUID，版本位写错会被直接拒。
        var uuid = ClaudeReviewRunner.DeterministicUuid("seed");
        var s = uuid.ToString();

        Assert.Equal('4', s[14]);
        Assert.Contains(s[19], "89ab");
    }

    /// <summary>
    /// 退出码非零时，账本里要看得出为什么。
    /// </summary>
    /// <remarks>
    /// 原先只取 stderr，而 claude 把 API 层的错误打在 <b>stdout</b> 上 ——
    /// 于是真事故留在账本里的是 <c>claude 退出码 1：</c>，冒号后面空的。
    /// 三次评审全挂，事后翻账本只知道「挂了」。
    /// </remarks>
    [Fact]
    public void Stderr_is_the_reason_when_there_is_one()
    {
        var result = new ProcessResult(1, "{\"type\":\"result\",\"result\":\"别用我\"}", "段错误");

        Assert.Equal("段错误", ClaudeReviewRunner.FailureReason(result));
    }

    [Fact]
    public void An_api_error_on_stdout_is_still_found_when_stderr_is_empty()
    {
        var line = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "result",
            subtype = "error_during_execution",
            is_error = true,
            result = "API_ERROR_TEXT",
        });

        var reason = ClaudeReviewRunner.FailureReason(
            new ProcessResult(1, "{\"type\":\"system\"}\n" + line + "\n", string.Empty));

        Assert.Equal("API_ERROR_TEXT", reason);
    }

    [Fact]
    public void Without_a_result_event_the_last_line_is_better_than_nothing()
    {
        var reason = ClaudeReviewRunner.FailureReason(
            new ProcessResult(1, "起来了\nApI ErRoR\n\n", string.Empty));

        Assert.Equal("ApI ErRoR", reason);
    }

    [Fact]
    public void Nothing_on_either_stream_is_an_empty_reason_not_a_throw()
        => Assert.Equal(string.Empty, ClaudeReviewRunner.FailureReason(
            new ProcessResult(1, string.Empty, string.Empty)));

    /// <summary>
    /// 缺契约围栏是<b>确定性</b>失败，不该再烧一轮。
    /// </summary>
    /// <remarks>
    /// 实测的那次：PR 2912 连着两轮都是这个原因，两轮 claude 都把评审做完了 ——
    /// 原文里结论、finding、行号都在，只是最后那个 ```json 围栏没出来。
    /// 两轮合计 121 万 token、$2.79，产出为零，而按老规则它还会再跑第三轮。
    /// </remarks>
    [Fact]
    public void A_reply_without_the_contract_fence_is_a_fatal_error()
    {
        var ballot = Parse(Result(
            "Review complete.\n\n**Verdict: reject** — two blocking issues, both in the retry core:\n"
                + "- `retry.go:77` — `shouldRetry` falls through to `code >= 400`."));

        Assert.Equal(ReviewDecision.Error, ballot.Decision);
        Assert.True(ballot.IsFatal);

        // 原文要留在链上：这一轮的评审内容本身往往是好的，只是拿不到机器可读的结论。
        // 人看到它才知道该去修 skill，而不是以为 claude 挂了。
        Assert.Contains("Verdict: reject", ballot.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_that_is_not_even_json_is_a_fatal_error()
    {
        // claude 已经跑完、token 已经烧掉，它吐的不是 JSON 这件事再跑一遍还是一样。
        var ballot = Parse("Segmentation fault");

        Assert.Equal(ReviewDecision.Error, ballot.Decision);
        Assert.True(ballot.IsFatal);
    }

    /// <summary>
    /// claude 自己报的 <c>is_error</c> 仍然可重试。
    /// </summary>
    /// <remarks>
    /// 这一类是真·偶发：服务端 5xx、超时、子进程被杀。换一轮换一台机器很可能就过了，
    /// 那正是重试预算存在的理由 —— 不能因为要治「缺围栏」就把它们一起掐掉。
    /// </remarks>
    [Fact]
    public void An_error_reported_by_claude_itself_stays_retryable()
    {
        var ballot = Parse(Result("API Error: 500 internal", isError: true));

        Assert.Equal(ReviewDecision.Error, ballot.Decision);
        Assert.False(ballot.IsFatal);
    }

    [Fact]
    public void A_reply_with_the_contract_fence_parses_into_a_verdict()
    {
        var ballot = Parse(Result(
            "看完了。\n\n```json\n"
                + "{\"decision\":\"reject\",\"comment\":\"## 要改\","
                + "\"findings\":[{\"file\":\"retry.go\",\"line\":77,\"severity\":\"critical\","
                + "\"title\":\"4xx 也重试\",\"detail\":\"…\"}]}"
                + "\n```"));

        Assert.Equal(ReviewDecision.Reject, ballot.Decision);
        Assert.False(ballot.IsFatal);
        Assert.Equal("retry.go", Assert.Single(ballot.Findings).File);
        Assert.Equal("## 要改", ballot.Comment);
    }

    /// <summary>
    /// 出票契约要写进 prompt，不能只躺在 skill 文件末尾。
    /// </summary>
    /// <remarks>
    /// 解析它的是 runner，所以 runner 就该把它说出来。skill 是各机器各自装的、
    /// 一万多字，而这一条在最末尾 —— 押在那上面，失败起来是静默的：
    /// 评审做完了、结论也有，就是没围栏，而每一轮都是完整的账单。
    /// </remarks>
    [Fact]
    public void The_prompt_states_the_output_contract_itself()
    {
        var prompt = ClaudeReviewRunner.CollectPrompt(TestElectors.Pr(id: 2912));

        Assert.Contains("/az-pr-review 2912", prompt, StringComparison.Ordinal);
        Assert.Contains("```json", prompt, StringComparison.Ordinal);
        Assert.Contains("decision", prompt, StringComparison.Ordinal);
        Assert.Contains("findings", prompt, StringComparison.Ordinal);

        // 「拿不到足够信息也要出围栏」是最容易被忽略的那一条，恰恰也是实测踩到的那条。
        Assert.Contains("wait-for-author", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// 起草好的评论原文必须一路带到票上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 曾经漏掉过：<c>CollectContract</c> 那个 DTO 只有 <c>decision</c> 和 <c>findings</c>，
    /// 没列 <c>comment</c>，于是 <see cref="BallotPayload.Comment"/> 永远是 null。
    /// 每个 PR 收到的都是由 findings 现渲染的一张裸表格
    /// （<c>AzCliPrSource.CommentBody</c> 的兜底分支），
    /// 而评审里最有价值的部分 —— 为什么是问题、该怎么改、查过哪些地方是干净的 ——
    /// 全丢了，还没有任何报错。
    /// </para>
    /// <para>
    /// 两条断言缺一不可：只断言 <c>Comment</c> 非空的话，一个把 findings 拼成表格
    /// 塞进去的实现也能过。
    /// </para>
    /// </remarks>
    [Fact]
    public void The_drafted_comment_reaches_the_ballot_verbatim()
    {
        const string Drafted =
            "## 评审结论：需要修改\n\n### 4xx 也在重试\n`retry.go:77`\n\n"
                + "`shouldRetry` 落到 `code >= 400`，于是 401/404 会被重试 5 次。\n\n"
                + "已跑过的检查：`go build` ✅ `go vet` ✅ —— 这部分是干净的。";

        var ballot = Parse(Result(
            "看完了。\n\n```json\n"
                + ActaJson.Serialize(new
                {
                    decision = "reject",
                    comment = Drafted,
                    findings = new[]
                    {
                        new { file = "retry.go", line = 77, severity = "critical", title = "t", detail = "d" },
                    },
                })
                + "\n```"));

        Assert.Equal(Drafted, ballot.Comment);

        // 不是从 findings 现拼的：那条路会丢掉「查过哪些地方是干净的」这类上下文。
        Assert.Contains("go vet", ballot.Comment, StringComparison.Ordinal);
    }

    /// <summary>
    /// 评论原文里带 ```` ``` ```` 代码块时，契约照样解得出来。
    /// </summary>
    /// <remarks>
    /// <para>
    /// PR 2916 的真实事故：评审跑完了、结论 reject、18 条 finding 都在，起草好的评论
    /// 里给了七八段 <c>```go</c> 的修改示例 —— 那些反引号在 JSON 字符串<b>里面</b>，
    /// 而当时的扫法拿 <c>IndexOf("```")</c> 一路配对，把其中第一个当成了收尾围栏。
    /// 抠出来的是从中间截断的 JSON，解不出 → Error 票 → $1.31 全废，
    /// PR 上收到的是「评审没有跑出结论」那句兜底文案。
    /// </para>
    /// <para>
    /// 这是「一个节点评审就原样投递 claude 那条评论」这条路上最容易踩的一颗雷：
    /// 评论写得越好（越舍得给示例代码），越必然踩中。
    /// </para>
    /// </remarks>
    [Fact]
    public void A_drafted_comment_with_code_fences_inside_it_still_parses()
    {
        const string Drafted =
            "**Code Review** — 🔴 需要修改\n\n"
                + "**1. `query.go:104` — `Authenticate` SQL 注入**\n\n"
                + "```go\nq := fmt.Sprintf(\"… WHERE name = '%s'\", name)\n```\n\n"
                + "改成参数化：\n\n"
                + "```go\nrow := s.db.QueryRowContext(ctx, `… WHERE name = $1`, name)\n```\n\n"
                + "投票：**reject**。";

        var ballot = Parse(Result(
            "## 审阅完成\n\n### 结论：reject\n\n```json\n"
                + ActaJson.Serialize(new
                {
                    decision = "reject",
                    comment = Drafted,
                    findings = new[]
                    {
                        new { file = "query.go", line = 104, severity = "critical", title = "注入", detail = "d" },
                    },
                })
                + "\n```"));

        Assert.Equal(ReviewDecision.Reject, ballot.Decision);
        Assert.False(ballot.IsFatal);
        Assert.Equal("query.go", Assert.Single(ballot.Findings).File);

        // 一个字不改地带出来 —— 包括里面那两段围栏。
        Assert.Equal(Drafted, ballot.Comment);
    }

    /// <summary>
    /// 收尾围栏没出来时，花括号配平还能把契约救回来。
    /// </summary>
    /// <remarks>
    /// 模型漏写收尾围栏、或者输出被 max-tokens 截在收尾围栏之前都会走到这里。
    /// 这一轮的账单反正已经烧掉了，能救回来就不该作废。
    /// </remarks>
    [Fact]
    public void A_contract_fence_that_never_closed_is_still_salvaged()
    {
        var ballot = Parse(Result(
            "看完了。\n\n```json\n"
                + "{\"decision\":\"approve-with-suggestions\",\"comment\":\"看了 `{a}` 和 `}`，没大事\","
                + "\"findings\":[]}"));

        Assert.Equal(ReviewDecision.ApproveWithSuggestions, ballot.Decision);
        Assert.False(ballot.IsFatal);
        Assert.Equal("看了 `{a}` 和 `}`，没大事", ballot.Comment);
    }

    /// <summary>配平要认字符串和转义，不能光数括号。</summary>
    [Fact]
    public void Balancing_braces_ignores_the_ones_inside_strings()
    {
        const string Text = "前言 {\"a\":\"} 不算 { 也不算 \\\" 仍在串里 }\",\"b\":{\"c\":1}} 后记";

        Assert.Equal(
            "{\"a\":\"} 不算 { 也不算 \\\" 仍在串里 }\",\"b\":{\"c\":1}}",
            ClaudeReviewRunner.BalancedObject(Text, 0));

        // 配不平就是 null，不猜。
        Assert.Null(ClaudeReviewRunner.BalancedObject("{\"a\":1", 0));
        Assert.Null(ClaudeReviewRunner.BalancedObject("没有对象", 0));
    }

    /// <summary>缺 <c>comment</c> 不算失败 —— 投递方退回用 findings 渲染。</summary>
    [Fact]
    public void A_contract_without_a_comment_still_yields_a_verdict()
    {
        var ballot = Parse(Result(
            "```json\n{\"decision\":\"approve\",\"findings\":[]}\n```"));

        Assert.Equal(ReviewDecision.Approve, ballot.Decision);
        Assert.Null(ballot.Comment);
        Assert.False(ballot.IsFatal);
    }
}

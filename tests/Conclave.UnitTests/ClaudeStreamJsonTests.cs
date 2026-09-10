using Conclave.Infrastructure;

namespace Conclave.UnitTests;

/// <summary>
/// <c>claude -p --output-format stream-json</c> 的事件解析。
/// </summary>
/// <remarks>
/// <para>
/// 下面的原文都是<b>实测抓的</b>（claude 2.1.266），只删掉了与解析无关的长字段。
/// 这是整条链上第二处依赖 claude 输出格式的地方（另一处是额度探针），
/// 而它同时喂两个东西：出票要用最后那行 <c>type=result</c>，日志面板要用中间那些事件。
/// </para>
/// <para>
/// 格式随版本会变，所以两种失败方式都要钉住：认不出的事件必须<b>安静地</b>被忽略
/// （日志少一行，不影响评审），而 <c>result</c> 那行必须挑得出来（挑不出来就出不了票）。
/// </para>
/// </remarks>
public sealed class ClaudeStreamJsonTests
{
    private const string Init =
        """{"type":"system","subtype":"init","cwd":"/private/tmp","session_id":"75ffaf0e","model":"claude-opus-5[1m]","permissionMode":"auto"}""";

    private const string Text =
        """{"type":"assistant","message":{"model":"claude-opus-5","type":"message","role":"assistant","content":[{"type":"text","text":"ok"}],"usage":{"output_tokens":4}},"session_id":"75ffaf0e"}""";

    private const string ToolUse =
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_013z","name":"Bash","input":{"command":"echo hi","description":"Echo hi"},"caller":{"type":"direct"}}]},"session_id":"75ffaf0e"}""";

    /// <summary>2.1.266 新出现的事件类型。老代码不认识它，必须无声跳过。</summary>
    private const string RateLimit =
        """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","rateLimitType":"five_hour"},"session_id":"75ffaf0e"}""";

    private const string Result =
        """{"duration_api_ms":2659,"total_cost_usd":0.128616,"modelUsage":{"claude-opus-5[1m]":{"inputTokens":2,"outputTokens":4,"costUSD":0.128616,"canonicalModel":"claude-opus-5","costBasis":"list"}},"is_error":false,"num_turns":1,"subtype":"success","result":"ok","type":"result","duration_ms":4406}""";

    [Fact]
    public void The_result_line_is_picked_out_of_the_stream()
    {
        var ndjson = string.Join('\n', Init, Text, ToolUse, RateLimit, Result);

        // 出票走的就是这一行 —— 它跟 --output-format json 的整个输出是同一个对象，
        // 所以挑出来之后 Parse 一点没改。
        Assert.Equal(Result, ClaudeReviewRunner.ResultLine(ndjson));
    }

    [Fact]
    public void The_type_field_is_not_assumed_to_come_first()
    {
        // 实测 result 那行的 "type" 排在很后面（前面是 duration_api_ms、usage 等等）。
        // 按前缀匹配的写法在这里会漏掉整行 —— 然后就是「评审跑完了但出不了票」。
        Assert.DoesNotContain("{\"type\":\"result\"", Result[..40], StringComparison.Ordinal);
        Assert.Equal(Result, ClaudeReviewRunner.ResultLine(Result));
    }

    [Fact]
    public void Without_a_result_line_the_raw_text_comes_back()
    {
        // 让 Parse 去报「不是合法 JSON」并带上原文 —— 那条错误信息比在这里另编一句有用。
        var ndjson = string.Join('\n', Init, Text);

        Assert.Equal(ndjson, ClaudeReviewRunner.ResultLine(ndjson));
    }

    [Fact]
    public void Known_events_turn_into_one_readable_line_each()
    {
        Assert.Equal("会话已建立，开始读代码", ClaudeReviewRunner.Summarize(Init));
        Assert.Equal("ok", ClaudeReviewRunner.Summarize(Text));
        Assert.Equal("▸ Bash: echo hi", ClaudeReviewRunner.Summarize(ToolUse));
        Assert.Equal("claude 收尾：1 轮 · $0.13", ClaudeReviewRunner.Summarize(Result));
    }

    [Fact]
    public void Unknown_and_malformed_events_are_dropped_silently()
    {
        // 这几条都要返回空串（ReviewProgressLog.Append 会忽略空行）。
        // 抛异常的后果是把 claude 的输出读断，评审跟着失败 —— 日志面板不配有这个权力。
        Assert.Equal(string.Empty, ClaudeReviewRunner.Summarize(RateLimit));
        Assert.Equal(string.Empty, ClaudeReviewRunner.Summarize("这不是 JSON"));
        Assert.Equal(string.Empty, ClaudeReviewRunner.Summarize("""{"type":"assistant"}"""));
        Assert.Equal(string.Empty, ClaudeReviewRunner.Summarize("""{"type":"future_event"}"""));
        Assert.Equal(string.Empty, ClaudeReviewRunner.Summarize(string.Empty));
        Assert.Equal(string.Empty, ClaudeReviewRunner.Summarize("""{"broken":"""));
    }

    [Fact]
    public void A_long_tool_argument_is_truncated()
    {
        var pattern = new string('x', 400);
        var line = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Grep","input":{"pattern":"PATTERN"}}]}}"""
            .Replace("PATTERN", pattern, StringComparison.Ordinal);

        var summary = ClaudeReviewRunner.Summarize(line);

        // 完整参数动辄几千字符，刷屏之后反而看不出进度。
        Assert.StartsWith("▸ Grep: ", summary, StringComparison.Ordinal);
        Assert.True(summary.Length < 200, summary.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

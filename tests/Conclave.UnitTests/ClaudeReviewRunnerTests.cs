using Conclave.Infrastructure;

namespace Conclave.UnitTests;

public class ClaudeReviewRunnerTests
{
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
}

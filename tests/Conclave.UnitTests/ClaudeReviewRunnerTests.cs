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
}

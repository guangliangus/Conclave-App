using Conclave.Application.Ports;
using Conclave.Domain;
using Conclave.Infrastructure;

namespace Conclave.UnitTests;

/// <summary>
/// 评审途中「这一版还值不值得评下去」的判定（<see cref="AzCliPrSource.Liveness"/>）。
/// </summary>
/// <remarks>
/// 钉两件事：新推送要认出来（早先只看 status，旧版本会一路评完、票却没人公布）；
/// 任何一项读不到都不往坏的方向猜 —— 误杀一次评审就是白烧一整轮。
/// </remarks>
public sealed class PrLivenessTests
{
    private static readonly PrMeta Pr = TestElectors.Pr(id: 2721) with { SrcCommit = "1111aaaa2222bbbb" };

    [Fact]
    public void A_pr_that_is_no_longer_active_is_closed()
        => Assert.Equal(PrLiveness.Closed, AzCliPrSource.Liveness(Pr, "completed", "1111aaaa2222bbbb"));

    [Fact]
    public void A_new_source_commit_means_the_reviewed_revision_is_superseded()
        => Assert.Equal(PrLiveness.Superseded, AzCliPrSource.Liveness(Pr, "active", "3333cccc4444dddd"));

    [Fact]
    public void The_same_source_commit_is_still_active_regardless_of_case()
        => Assert.Equal(PrLiveness.Active, AzCliPrSource.Liveness(Pr, "Active", "1111AAAA2222BBBB"));

    [Theory]
    [InlineData("", "")]
    [InlineData("active", "")]
    [InlineData("", "1111aaaa2222bbbb")]
    public void Anything_unreadable_counts_as_still_active(string status, string src)
        => Assert.Equal(PrLiveness.Active, AzCliPrSource.Liveness(Pr, status, src));

    [Fact]
    public void Without_a_known_source_commit_a_push_cannot_be_told_apart()
        => Assert.Equal(
            PrLiveness.Active,
            AzCliPrSource.Liveness(Pr with { SrcCommit = string.Empty }, "active", "3333cccc4444dddd"));
}

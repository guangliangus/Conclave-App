using Conclave.Domain;
using Conclave.Infrastructure;

namespace Conclave.UnitTests;

/// <summary>
/// 投递到 Azure DevOps 的那条评论用的是评审节点起草的原文。
/// </summary>
/// <remarks>
/// <para>
/// 原先是把 <see cref="BallotPayload.Findings"/> 重新拼成一张
/// 「置信度 / 严重度 / 位置 / 标题」的表格再发。表格丢掉的正是评审里最值钱的部分：
/// 为什么是问题、该怎么改、哪些地方看过了没问题。
/// </para>
/// <para>
/// 这一组钉三件事：恰好一票时原样发（<b>一个字都不能加</b>）；
/// 拿不到原文时退回渲染而不是发空；quorum ≥ 2 时<b>不</b>原样发 ——
/// 那时候有 N 份各自起草的评论，没有唯一答案，合并渲染才是该说的话。
/// </para>
/// </remarks>
public sealed class VerbatimCommentTests
{
    private const string Rev = "2878@a8d427bb";

    private static readonly PrMeta Pr = TestElectors.Pr(id: 2878);

    private static BallotPayload Ballot(int round, string? comment, ReviewDecision decision = ReviewDecision.Reject)
        => new(Rev, round, decision, [new Finding("a.cs", 12, Severity.Major, "标题", "细节")],
            "claude-opus-5", 1000)
        { Comment = comment };

    [Fact]
    public void One_ballot_posts_its_comment_byte_for_byte()
    {
        const string drafted = "## 评审结论：需要修改\n\n### 退款金额未校验上限\n`a.cs:12`\n\n`amount` 直接取请求体。";

        var merged = QuorumEngine.Merge(Rev, [Ballot(0, drafted)], 1);
        var body = AzCliPrSource.CommentBody(Pr, merged);

        // 不是 Contains —— 前后各加一个字都算违约。
        Assert.Equal(drafted, body);
    }

    [Fact]
    public void No_comment_falls_back_to_the_rendered_table()
    {
        var merged = QuorumEngine.Merge(Rev, [Ballot(0, comment: null)], 1);
        var body = AzCliPrSource.CommentBody(Pr, merged);

        Assert.Contains("Conclave 评审结论", body, StringComparison.Ordinal);
        Assert.Contains("标题", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_comment_counts_as_no_comment()
    {
        var merged = QuorumEngine.Merge(Rev, [Ballot(0, "   \n  ")], 1);

        Assert.Null(merged.Comment);
        Assert.Contains("Conclave 评审结论", AzCliPrSource.CommentBody(Pr, merged), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_ballots_do_not_post_either_ones_comment()
    {
        var merged = QuorumEngine.Merge(Rev, [Ballot(0, "甲的评论"), Ballot(1, "乙的评论")], 2);

        Assert.Null(merged.Comment);
        var body = AzCliPrSource.CommentBody(Pr, merged);
        Assert.DoesNotContain("甲的评论", body, StringComparison.Ordinal);
        Assert.DoesNotContain("乙的评论", body, StringComparison.Ordinal);
    }

    /// <summary>Error 票不参与多数决，所以「一票有效」的判定不该被它带偏。</summary>
    [Fact]
    public void An_error_ballot_alongside_one_valid_ballot_still_posts_verbatim()
    {
        var merged = QuorumEngine.Merge(Rev, [
            Ballot(0, "起草的原文"),
            new BallotPayload(Rev, 1, ReviewDecision.Error, [], "n/a", 0, Error: "子进程挂了"),
        ], 2);

        Assert.Equal("起草的原文", merged.Comment);
    }

    [Fact]
    public void The_comment_is_capped_before_it_goes_on_chain()
    {
        var huge = new string('x', 200 * 1024);
        var capped = ClaudeReviewRunner.CapComment(huge);

        Assert.NotNull(capped);
        Assert.True(capped!.Length <= 64 * 1024, $"实际 {capped.Length} 字符");
        Assert.EndsWith("已截断）</sub>", capped, StringComparison.Ordinal);
    }

    [Fact]
    public void A_comment_within_the_cap_is_untouched_except_for_trimming()
    {
        Assert.Equal("## 正常长度", ClaudeReviewRunner.CapComment("  ## 正常长度\n "));
        Assert.Null(ClaudeReviewRunner.CapComment(null));
        Assert.Null(ClaudeReviewRunner.CapComment(" \t "));
    }
}

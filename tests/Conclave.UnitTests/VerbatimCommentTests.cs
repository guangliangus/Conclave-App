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

    /// <summary>
    /// 一个节点评审时，PR 上收到的就是 claude 那条评论本身 —— 不出现 Conclave。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 从 claude 的原始输出一路串到最终 markdown，中间不手搓 Ballot：
    /// 上面那几条都是从 <see cref="BallotPayload"/> 起步的，而 PR 2916 的事故恰好
    /// 死在更前面一步 —— 起草的评论里带 <c>```go</c> 示例代码，契约根本没解出来，
    /// 于是 <c>Comment</c> 是 null，作者收到的是兜底渲染的
    /// 「## Conclave 评审结论：Error」。评论写得越好越必然踩中。
    /// </para>
    /// <para>
    /// 所以这条从 <c>type=result</c> 那一行开始跑，钉的是整条链路的结果。
    /// </para>
    /// </remarks>
    [Fact]
    public void One_node_posts_claudes_own_comment_with_no_trace_of_conclave()
    {
        const string Drafted =
            "**Code Review** — 🔴 需要修改：`Authenticate` 可绕过登录\n\n"
                + "```go\nq := fmt.Sprintf(\"… WHERE name = '%s'\", name)\n```\n\n"
                + "改成参数化：\n\n"
                + "```go\nrow := s.db.QueryRowContext(ctx, `… WHERE name = $1`, name)\n```\n\n"
                + "投票：**reject**。";

        var stdout = ActaJson.Serialize(new
        {
            type = "result",
            is_error = false,
            result = "## 审阅完成\n\n### 结论：reject\n\n```json\n"
                + ActaJson.Serialize(new
                {
                    decision = "reject",
                    comment = Drafted,
                    findings = new[]
                    {
                        new { file = "query.go", line = 104, severity = "critical", title = "注入", detail = "d" },
                    },
                })
                + "\n```",
        });

        var ballot = ClaudeReviewRunner.Parse(
            new Revision("edison-test", 2916, "cbe34411d8d61994"), 0, stdout, 1234,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var merged = QuorumEngine.Merge(Rev, [ballot], 1);
        var body = AzCliPrSource.CommentBody(Pr, merged);

        Assert.Equal(ReviewDecision.Reject, merged.Decision);
        Assert.Equal(Drafted, body);
        Assert.DoesNotContain("Conclave", body, StringComparison.Ordinal);
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

    /// <summary>
    /// 执行失败的结论不能说成「未发现问题」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 一张有效票都没有时 <see cref="QuorumEngine.Merge"/> 出的是
    /// <see cref="ReviewDecision.Error"/> + 空 findings。而渲染那条路原先只看
    /// <c>Findings.Count == 0</c> 就写「未发现问题。」——「跑完了，是干净的」和
    /// 「压根没跑出结论」对作者的意义正好相反，走错分支等于告诉他可以合。
    /// </para>
    /// <para>
    /// 「确定性失败不再重试」之后这条路来得更快（一轮就收尾，不再烧满三轮），
    /// 所以这句话必须是对的。
    /// </para>
    /// </remarks>
    [Fact]
    public void A_failed_review_never_tells_the_author_the_pr_is_clean()
    {
        var merged = QuorumEngine.Merge(
            Rev,
            [Ballot(0, comment: null, decision: ReviewDecision.Error) with { Findings = [] }],
            1);

        var body = AzCliPrSource.CommentBody(Pr, merged);

        Assert.Equal(ReviewDecision.Error, merged.Decision);
        Assert.DoesNotContain("未发现问题", body, StringComparison.Ordinal);
        Assert.Contains("这不代表代码没有问题", body, StringComparison.Ordinal);
    }

    /// <summary>降级的两种原因要分开说。</summary>
    /// <remarks>
    /// 「合格节点不足」说的是评审是好的、只是票少；执行失败是压根没跑出结论。
    /// 原先两者共用一句硬编码的「降级：合格节点不足」。
    /// </remarks>
    [Fact]
    public void A_failed_review_says_why_it_is_degraded()
    {
        var merged = QuorumEngine.Merge(
            Rev,
            [Ballot(0, comment: null, decision: ReviewDecision.Error) with { Findings = [] }],
            1);

        var body = AzCliPrSource.CommentBody(Pr, merged);

        Assert.True(merged.Degraded);
        Assert.Contains("评审执行失败", body, StringComparison.Ordinal);
        Assert.DoesNotContain("合格节点不足", body, StringComparison.Ordinal);
    }

    /// <summary>真·干净的 PR 照旧那么说 —— 上面两条不能把这句话一起掐掉。</summary>
    [Fact]
    public void A_genuinely_clean_review_still_says_so()
    {
        var merged = QuorumEngine.Merge(
            Rev,
            [Ballot(0, comment: null, decision: ReviewDecision.Approve) with { Findings = [] }],
            1);

        var body = AzCliPrSource.CommentBody(Pr, merged);

        Assert.Contains("未发现问题", body, StringComparison.Ordinal);
    }
}

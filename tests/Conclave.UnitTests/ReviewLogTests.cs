using Conclave.Application;
using Conclave.Domain;
using Conclave.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

/// <summary>
/// 评审记录投影：谁、什么时候、什么状态、烧了多少 token、折合多少钱。
/// </summary>
public sealed class ReviewLogTests : IDisposable
{
    private readonly string _home;
    private readonly ElectorIdentity _identity;
    private readonly MutableAllowList _allowList;
    private readonly SqliteActa _acta;
    private readonly SqliteReviewLog _log;

    public ReviewLogTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "conclave-reviewlog-" + Guid.NewGuid().ToString("N"));
        var options = new ConclaveOptions { HomeDirectory = _home };
        _identity = ElectorIdentity.Create();
        _allowList = new MutableAllowList(_identity.Id);
        _acta = new SqliteActa(options, _identity, _allowList, NullLogger<SqliteActa>.Instance);
        _log = new SqliteReviewLog(options);
    }

    private static readonly Revision Rev = new("edison-test", 2878, "a8d427bbcccccccc");

    private static ReviewUsage Usage(decimal cost = 0.42m) => new(
        InputTokens: 1_200,
        OutputTokens: 3_400,
        CacheReadTokens: 120_000,
        CacheWriteTokens: 8_000,
        ThinkingTokens: 900,
        CostUsd: cost,
        CostBasis: "list",
        Turns: 7,
        Models: [
            new ModelUsage("claude-opus-5[1m]", "claude-opus-5", 1_000, 3_200, 120_000, 8_000, 900, cost - 0.02m),
            new ModelUsage("claude-haiku-4-5-20251001", "claude-haiku-4-5", 200, 200, 0, 0, 0, 0.02m),
        ]);

    private async Task SeedAsync(
        string reviewerAz = "alan",
        ReviewDecision decision = ReviewDecision.Reject,
        decimal cost = 0.42m,
        Revision? revision = null,
        string repo = "edison-test")
    {
        var ct = CancellationToken.None;
        var rev = revision ?? Rev;
        var pr = TestElectors.Pr(id: rev.PrId, author: "LIONMAIL\\edisonwei") with
        {
            Project = rev.Project,
            Repo = repo,
            Title = "feat(scripts): 带指数退避的重试包装",
            SrcCommit = rev.SrcCommit,
        };

        _ = await _acta.AppendAsync(
            rev.Id, BlockKind.Summons, new SummonsPayload(rev, pr, 1, "rules1"), ct);

        _ = await _acta.AppendAsync(
            rev.Id, BlockKind.Ballot,
            new BallotPayload(
                rev.Id, 0, decision,
                [new Finding("scripts/retry.sh", 9, Severity.Critical, "CI 假绿", "细节")],
                "claude-opus-5[1m]", 269_994, Usage(cost), reviewerAz),
            ct);
    }

    [Fact]
    public async Task Elector_usage_sums_only_this_nodes_own_reviews_inside_the_window()
    {
        var ct = CancellationToken.None;
        await SeedAsync(cost: 0.42m);
        await SeedAsync(cost: 0.30m, revision: new Revision("edison-test", 2879, "bbbbbbbb11111111"));

        var used = await _log.ReadElectorUsageAsync(_identity.Id, DateTimeOffset.UtcNow.AddDays(-7), ct);

        Assert.Equal(2, used.Reviews);
        Assert.Equal(2 * (1_200 + 3_400 + 120_000 + 8_000), used.TotalTokens);
        Assert.Equal(0.72m, used.CostUsd);

        // 别人的票也在同一张投影表里（gossip 进来的），但额度是按机器算的 ——
        // 同一个人在两台机器上是两份额度，所以必须按 electorId 过滤。
        var other = await _log.ReadElectorUsageAsync("ffffffffffffffff", DateTimeOffset.UtcNow.AddDays(-7), ct);
        Assert.Equal(0, other.Reviews);
        Assert.Equal(0, other.TotalTokens);
    }

    [Fact]
    public async Task Elector_usage_ignores_reviews_older_than_the_window()
    {
        await SeedAsync();

        // 窗口起点在未来 —— 所有已有记录都应落在窗口外。滚动窗口要是没生效，
        // 用量只会单调上涨，节点跑几天就永久出局。
        var used = await _log.ReadElectorUsageAsync(
            _identity.Id, DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        Assert.Equal(0, used.Reviews);
    }

    [Fact]
    public async Task Elector_usage_counts_error_ballots_too()
    {
        await SeedAsync(decision: ReviewDecision.Error);

        // 子进程失败前烧掉的 token 不会因为失败而退回。不计入会让额度用量系统性低报，
        // 而低报的方向恰恰是「继续给这台机器派活」。
        var used = await _log.ReadElectorUsageAsync(
            _identity.Id, DateTimeOffset.UtcNow.AddDays(-7), CancellationToken.None);

        Assert.Equal(1, used.Reviews);
        Assert.True(used.TotalTokens > 0);
    }

    [Fact]
    public async Task A_ballot_lands_in_the_review_log_with_who_when_and_what()
    {
        await SeedAsync();

        var records = await _log.ReadRecentAsync(10, CancellationToken.None);
        var r = Assert.Single(records);

        Assert.Equal("alan", r.ReviewerAz);                    // 谁
        Assert.Equal(_identity.Id, r.ReviewerId);              // 谁（密码学身份）
        Assert.True(r.ReviewedAt > DateTimeOffset.UtcNow.AddMinutes(-1));   // 什么时候
        Assert.Equal(ReviewDecision.Reject, r.Status);         // 什么状态
        Assert.Equal(1, r.Findings);

        // PR 元数据来自 Summons 块，不在 Ballot 里重复存一份
        Assert.Equal(2878, r.PrId);
        Assert.Equal("edison-test", r.Repo);
        Assert.Equal("LIONMAIL\\edisonwei", r.PrAuthor);
        Assert.Contains("指数退避", r.PrTitle);
        Assert.Equal(269_994, r.DurationMs);
    }

    [Fact]
    public async Task Token_counts_and_cost_come_through()
    {
        await SeedAsync(cost: 0.42m);

        var r = Assert.Single(await _log.ReadRecentAsync(10, CancellationToken.None));

        Assert.Equal(1_200, r.Usage.InputTokens);
        Assert.Equal(3_400, r.Usage.OutputTokens);
        Assert.Equal(120_000, r.Usage.CacheReadTokens);
        Assert.Equal(8_000, r.Usage.CacheWriteTokens);
        Assert.Equal(900, r.Usage.ThinkingTokens);
        Assert.Equal(0.42m, r.Usage.CostUsd);
        Assert.Equal(7, r.Usage.Turns);

        // costBasis 必须一起留着：list 是目录价折算，订阅用户边际成本其实是 0。
        Assert.Equal("list", r.Usage.CostBasis);

        Assert.Equal(132_600, r.Usage.TotalTokens);
        Assert.True(r.Usage.CacheHitRatio > 0.9, "缓存命中率应当很高");
    }

    [Fact]
    public async Task Per_model_detail_is_preserved_and_sums_to_the_total()
    {
        await SeedAsync(cost: 0.42m);

        var r = Assert.Single(await _log.ReadRecentAsync(10, CancellationToken.None));

        Assert.Equal(2, r.Usage.Models.Count);
        Assert.Equal(r.Usage.CostUsd, r.Usage.Models.Sum(m => m.CostUsd));

        // 子代理用的小模型必须能单独看见 —— 只看每票总计是看不出来的。
        var haiku = r.Usage.Models.Single(m => m.CanonicalModel == "claude-haiku-4-5");
        Assert.Equal(0.02m, haiku.CostUsd);
    }

    [Fact]
    public async Task Summaries_group_by_reviewer_repo_and_model()
    {
        var ct = CancellationToken.None;
        await SeedAsync(reviewerAz: "alan", cost: 0.40m);
        await SeedAsync(
            reviewerAz: "edisonwei", cost: 0.10m, repo: "payment-center",
            revision: new Revision("payment-center", 2853, "082fe735cccccccc"));

        var byReviewer = await _log.SummariseByReviewerAsync(null, null, ct);
        Assert.Equal(2, byReviewer.Count);
        Assert.Equal("alan", byReviewer[0].Key);              // 按金额倒序
        Assert.Equal(0.40m, byReviewer[0].CostUsd);

        var byRepo = await _log.SummariseByRepoAsync(null, null, ct);
        Assert.Contains(byRepo, x => x.Key == "payment-center" && x.CostUsd == 0.10m);

        // 按模型汇总读的是明细表，所以两票的同一个模型会合并
        var byModel = await _log.SummariseByModelAsync(null, null, ct);
        Assert.Equal(2, byModel.Count);
        Assert.Equal("claude-opus-5", byModel[0].Key);
        Assert.Equal(2, byModel[0].Reviews);

        var total = await _log.ReadTotalAsync(null, null, ct);
        Assert.Equal(2, total.Reviews);
        Assert.Equal(0.50m, total.CostUsd);
    }

    [Fact]
    public async Task Summaries_respect_the_time_range()
    {
        var ct = CancellationToken.None;
        await SeedAsync();

        var future = DateTimeOffset.UtcNow.AddDays(1);
        Assert.Empty(await _log.SummariseByReviewerAsync(future, null, ct));
        Assert.Equal(0, (await _log.ReadTotalAsync(future, null, ct)).Reviews);

        Assert.Single(await _log.SummariseByReviewerAsync(null, future, ct));
    }

    [Fact]
    public async Task Monthly_summary_buckets_by_utc_month()
    {
        await SeedAsync();

        var byMonth = await _log.SummariseByMonthAsync(CancellationToken.None);
        var expected = DateTimeOffset.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, Assert.Single(byMonth).Key);
    }

    [Fact]
    public async Task Every_row_points_back_to_a_block_that_still_exists()
    {
        var ct = CancellationToken.None;
        await SeedAsync();

        var chainHashes = (await _acta.ReadChainAsync(0, int.MaxValue, ct)).Select(b => b.Hash()).ToHashSet(StringComparer.Ordinal);

        // 链是唯一真相：投影行必须都能追回一个仍然存在的区块，
        // 否则让位重挂之后报表里会留下幽灵行、金额重复计。
        foreach (var record in await _log.ReadRecentAsync(100, ct))
        {
            Assert.Contains(record.BlockHash, chainHashes);
        }
    }

    [Fact]
    public async Task Reading_a_revision_returns_only_its_ballots()
    {
        var ct = CancellationToken.None;
        await SeedAsync();
        await SeedAsync(
            revision: new Revision("payment-center", 2853, "082fe735cccccccc"), repo: "payment-center");

        Assert.Single(await _log.ReadByRevisionAsync(Rev.Id, ct));
        Assert.Empty(await _log.ReadByRevisionAsync("9999@deadbeef", ct));
    }

    [Fact]
    public async Task An_error_ballot_is_recorded_with_zero_usage_rather_than_dropped()
    {
        var ct = CancellationToken.None;
        _ = await _acta.AppendAsync(
            Rev.Id, BlockKind.Summons,
            new SummonsPayload(Rev, TestElectors.Pr(id: Rev.PrId), 1, "r"), ct);
        _ = await _acta.AppendAsync(
            Rev.Id, BlockKind.Ballot,
            new BallotPayload(Rev.Id, 0, ReviewDecision.Error, [], "n/a", 0, null, "alan", "子进程挂了"),
            ct);

        // 执行失败也要在账上留一行：不然「这个月为什么只有 3 次评审」查不出来。
        var r = Assert.Single(await _log.ReadRecentAsync(10, ct));
        Assert.Equal(ReviewDecision.Error, r.Status);
        Assert.Equal(0m, r.Usage.CostUsd);
        Assert.Equal("unknown", r.Usage.CostBasis);
        Assert.Empty(r.Usage.Models);
    }

    public void Dispose()
    {
        _identity.Dispose();
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响结论
        }
    }
    [Fact]
    public async Task A_report_on_a_fresh_machine_creates_its_own_directory()
    {
        // conclave report 只解析 IReviewLog：它<b>不</b>构造 SqliteActa（原先建目录的地方），
        // 也不碰 ElectorIdentity 的 DI 工厂（另一处建目录的地方）。而 SQLite 只建文件不建
        // 父目录，于是全新机器上直接 SQLite Error 14: unable to open database file。
        //
        // 这个类其余用例都在构造函数里先建了 SqliteActa，顺手把目录建好了 —— 所以这条路
        // 一直没被走到。实测炸在 CI 的干净 runner 上（release 打包的自检），
        // 而开发机上 ~/.conclave 早就存在，本地永远复现不出来。
        var fresh = Path.Combine(
            Path.GetTempPath(), "conclave-fresh-" + Guid.NewGuid().ToString("N"), "dot-conclave");

        try
        {
            var log = new SqliteReviewLog(new ConclaveOptions { HomeDirectory = fresh });
            var total = await log.ReadTotalAsync(null, null, CancellationToken.None);

            Assert.Equal(0, total.Reviews);
            Assert.True(Directory.Exists(fresh), "读取侧自己没把目录建出来");
        }
        finally
        {
            var parent = Path.GetDirectoryName(fresh)!;
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }

}

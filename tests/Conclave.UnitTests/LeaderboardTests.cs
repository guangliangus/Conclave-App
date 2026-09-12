using Conclave.Application;
using Conclave.Domain;
using Conclave.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

/// <summary>
/// 排行榜：从真库端到端，而不是只测公式。
/// </summary>
/// <remarks>
/// <para>
/// 公式本身在 <see cref="PointsProjectionTests"/> 里钉。这一组钉的是<b>管道</b> ——
/// 写票时那几列有没有填上、读的时候是不是现算、Error 票会不会被算成产出。
/// 这几处每一处错了，公式再对，榜也是错的。
/// </para>
/// </remarks>
public sealed class LeaderboardTests : IDisposable
{
    private readonly string _home;
    private readonly ElectorIdentity _identity;
    private readonly SqliteActa _acta;
    private readonly SqliteReviewLog _log;

    public LeaderboardTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "conclave-board-" + Guid.NewGuid().ToString("N"));
        var options = new ConclaveOptions { HomeDirectory = _home };
        _identity = ElectorIdentity.Create();
        _acta = new SqliteActa(options, _identity, new MutableAllowList(_identity.Id),
            NullLogger<SqliteActa>.Instance);
        _log = new SqliteReviewLog(options);
    }

    private async Task BallotAsync(
        string who, int prId, int files, ReviewDecision decision, params Finding[] findings)
    {
        var ct = CancellationToken.None;
        var rev = new Revision("edison-test", prId, $"{prId:x8}cccccccc");
        var pr = TestElectors.Pr(id: prId, files: files) with
        {
            Project = rev.Project,
            SrcCommit = rev.SrcCommit,
        };

        _ = await _acta.AppendAsync(rev.Id, BlockKind.Summons,
            new SummonsPayload(rev, pr, 1, "rules1"), ct);
        _ = await _acta.AppendAsync(rev.Id, BlockKind.Ballot,
            new BallotPayload(rev.Id, 0, decision, findings, "claude-opus-5", 1000,
                ReviewUsage.None, who) { Pr = pr },
            ct);
    }

    private static Finding F(Severity s) => new("a.cs", 1, s, "标题", "细节");

    [Fact]
    public async Task The_board_ranks_by_points_and_counts_only_completed_reviews()
    {
        await BallotAsync("alan", 1, files: 1, ReviewDecision.Reject, F(Severity.Critical));
        await BallotAsync("alan", 2, files: 30, ReviewDecision.Approve);
        await BallotAsync("edison", 3, files: 1, ReviewDecision.ApproveWithSuggestions, F(Severity.Minor));
        // 没评成的不该出现在产出里。
        await BallotAsync("edison", 4, files: 50, ReviewDecision.Error);

        var board = await _log.ReadLeaderboardAsync(null, null, CancellationToken.None);

        Assert.Equal(2, board.Count);
        Assert.Equal("alan", board[0].Person);
        Assert.True(board[0].Points > board[1].Points);

        var edison = board[1];
        Assert.Equal(1, edison.Reviews);          // Error 那次不算
        Assert.Equal(0, edison.Catches);          // minor 不算「抓到」
        Assert.Equal(1, edison.FilesReviewed);    // Error 那 50 个文件也不算
    }

    [Fact]
    public async Task The_write_path_fills_in_what_the_score_needs()
    {
        // 难度与严重度是写入时从 Ballot 落下来的。漏填的话所有人都退化成基础分，
        // 榜看起来「能用」但其实已经不分难易了 —— 这种错最难发现，所以直接钉住。
        await BallotAsync("alan", 7, files: 30, ReviewDecision.Reject,
            F(Severity.Critical), F(Severity.Major), F(Severity.Minor));

        var board = await _log.ReadLeaderboardAsync(null, null, CancellationToken.None);
        var expected = PointsProjection.Score(ReviewDecision.Reject, 30, 1, 1, 1);

        Assert.Equal(Math.Round(expected, 1), board[0].Points);
        Assert.Equal(30, board[0].FilesReviewed);
        Assert.Equal(2, board[0].Catches);
    }

    [Fact]
    public async Task A_time_window_narrows_the_board()
    {
        await BallotAsync("alan", 11, files: 5, ReviewDecision.Reject, F(Severity.Major));

        var future = DateTimeOffset.UtcNow.AddDays(1);
        var empty = await _log.ReadLeaderboardAsync(future, null, CancellationToken.None);
        Assert.Empty(empty);

        var all = await _log.ReadLeaderboardAsync(
            DateTimeOffset.UtcNow.AddDays(-1), null, CancellationToken.None);
        Assert.Single(all);
    }

    [Fact]
    public async Task A_ballot_with_no_reviewer_name_stays_off_the_board()
    {
        // 认不出是谁的票挂在「未知」下面，等于把几个人的分并成一行。
        await BallotAsync(string.Empty, 21, files: 3, ReviewDecision.Reject, F(Severity.Major));

        Assert.Empty(await _log.ReadLeaderboardAsync(null, null, CancellationToken.None));
    }

    public void Dispose()
    {
        _identity.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }
}

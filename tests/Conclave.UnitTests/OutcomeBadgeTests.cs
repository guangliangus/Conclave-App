using Conclave.App.ViewModels;
using Conclave.Domain;

namespace Conclave.UnitTests;

/// <summary>
/// PR 队列最右边那个结论徽章。
/// </summary>
/// <remarks>
/// 这一组钉的是<b>那个数字说清楚了没有</b>。它曾经是光秃秃的「待作者 5」——
/// 而这个界面上同时存在问题数、票数（1/1 票）和轮次（round 0/1/2），三样都是个位数，
/// 读的人猜错了不会有任何提示。所以徽章带量词、tooltip 带名词。
/// </remarks>
public class OutcomeBadgeTests
{
    [Fact]
    public void The_number_on_the_badge_says_what_it_counts()
    {
        Assert.Equal("待作者 5 条", Badge.Outcome(ReviewDecision.WaitForAuthor, 5).Text);
        Assert.Equal("驳回 13 条", Badge.Outcome(ReviewDecision.Reject, 13).Text);
    }

    /// <summary>0 条不写数字 —— 「通过」本身已经说完了，再补个 0 只是噪音。</summary>
    [Fact]
    public void A_clean_verdict_carries_no_number_at_all()
    {
        Assert.Equal("通过", Badge.Outcome(ReviewDecision.Approve, 0).Text);
        Assert.Equal("通过 · 没有报出问题", Badge.OutcomeTip(ReviewDecision.Approve, 0));
    }

    /// <summary>名词放不进结论那一格，所以它必须在 tooltip 里。</summary>
    [Fact]
    public void The_tooltip_spells_out_the_noun_and_that_it_is_merged()
    {
        Assert.Equal("待作者 · 合并后 5 条问题", Badge.OutcomeTip(ReviewDecision.WaitForAuthor, 5));
    }

    /// <summary>还没评的行是个破折号，不是「0 条」—— 那两件事完全不同。</summary>
    [Fact]
    public void No_verdict_yet_is_a_dash_not_a_zero()
    {
        Assert.Equal("—", Badge.Outcome(null, 0).Text);
        Assert.True(Badge.Outcome(null, 0).IsBare);
        Assert.Equal("还没有结论", Badge.OutcomeTip(null, 0));
    }
}

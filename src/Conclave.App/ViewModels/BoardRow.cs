using System.Globalization;
using Conclave.Domain;

namespace Conclave.App.ViewModels;

/// <summary>排行榜里的一行。</summary>
/// <remarks>
/// <para>
/// 名次<b>由构造方传进来</b>而不是这里自己算：它是这一行在<b>这一次排序</b>里的位置，
/// 不是这个人的属性。同一个人在「总榜」和「近 7 天」里名次不同，而行对象是照表填的。
/// </para>
/// <para>
/// 分数在 <see cref="Conclave.Domain.PointsProjection"/> 里由账本现算，这里只负责
/// 把它变成能看的字。
/// </para>
/// </remarks>
public sealed class BoardRow
{
    /// <summary>领先度条的轨道宽度，像素。跟 MainWindow.axaml 里那一列的 Width 是同一个数。</summary>
    private const double BarTrack = 120;

    /// <summary>有分就至少画这么宽 —— 差距再大也不能让一个得了分的人看起来是空的。</summary>
    private const double MinBar = 3;

    public BoardRow(ScoreRow score, int rank, bool isSelf, double top, TableLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(score);

        Layout = layout ?? new TableLayout();

        Rank = rank.ToString(CultureInfo.InvariantCulture);
        Person = Labels.ShortAccount(score.Person);
        PersonFull = score.Person;
        IsSelf = isSelf;

        // 分数留一位小数。取整会把「20.6 和 20.7」压成同一个数，而那点差别正是
        // 难度与严重度带来的 —— 榜上看不出差别，公式里那些区分就白做了。
        Points = score.Points.ToString("F1", CultureInfo.InvariantCulture);
        HasPoints = score.Points > 0;

        // 0 显示成「—」而不是「0」：一列全是 0 的数字会被当成数据在读，
        // 破折号一眼就是「没有」。分数那一列例外 —— 它是这张表的排序键，
        // 排序键写成破折号会让人以为这一行没参与排序，而 0.0 本身就是个名次。
        Reviews = score.Reviews == 0
            ? "—"
            : score.Reviews.ToString(CultureInfo.InvariantCulture);

        Catches = score.Catches == 0
            ? "—"
            : score.Catches.ToString(CultureInfo.InvariantCulture);

        Files = score.FilesReviewed == 0
            ? "—"
            : score.FilesReviewed.ToString(CultureInfo.InvariantCulture);

        // 领先度条的长度。一列数字答不出排行榜真正被问的那个问题 ——
        // 「233.6 是遥遥领先，还是只比第二名多半分」，一条跟榜首比的横条一眼就能答。
        // 这里算成像素而不是比例：轨道是定宽的（MainWindow.axaml 里那 120），
        // XAML 做不了除法，而榜首的分数只有在编行的时候才在手边。
        BarWidth = top > 0 && score.Points > 0
            ? Math.Max(MinBar, Math.Round(score.Points / top * BarTrack))
            : 0;

        Tip = string.Create(
            CultureInfo.InvariantCulture,
            $"{score.Person}\n{score.Reviews} 次评审 · 抓到 {score.Catches} 条 Critical/Major · 看过 {score.FilesReviewed} 个改动文件\n分数 = 基础分 × 难度 + 发现加成，Error 票不计分");
    }

    /// <summary>按窗口宽度决定哪几列让位。行自己持有它，跟 ReviewRow 一致。</summary>
    public TableLayout Layout { get; }

    public string Rank { get; }

    public string Person { get; }

    public string PersonFull { get; }

    /// <summary>是不是本机这个人。金色只留给「跟本节点有关」的东西，见 Theme.axaml。</summary>
    public bool IsSelf { get; }

    public string Points { get; }

    /// <summary>分数是不是 0。0 分的人（票全是 Error）不该跟榜首一样用正文色。</summary>
    public bool HasPoints { get; }

    /// <summary>领先度条填充部分的宽度，像素；0 分是 0（只剩一条空轨道）。</summary>
    public double BarWidth { get; }

    public string Reviews { get; }

    public string Catches { get; }

    public string Files { get; }

    public string Tip { get; }
}

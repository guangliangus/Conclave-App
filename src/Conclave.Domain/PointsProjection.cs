namespace Conclave.Domain;

/// <summary>
/// 一个人在排行榜上的一行。
/// </summary>
/// <param name="Person">评审人（<see cref="BallotPayload.ReviewerAz"/>）。</param>
/// <param name="Points">总分。</param>
/// <param name="Reviews">计分的评审次数。</param>
/// <param name="Catches">抓到的 Critical + Major 条数。</param>
/// <param name="FilesReviewed">看过的改动文件数合计，难度的直观注脚。</param>
public sealed record ScoreRow(
    string Person,
    double Points,
    int Reviews,
    int Catches,
    int FilesReviewed);

/// <summary>
/// 评审积分：链的一个纯函数投影。
/// </summary>
/// <remarks>
/// <para>
/// <b>不新增块类型。</b> 积分不是转账，它由<b>已经在链上的事件</b>铸造，所以
/// 「余额 = f(链)」—— 每个节点从同一条链算出同样的分，共识是免费的：不需要挖矿、
/// 不需要投票，也不会因为积分而分叉。只有当分数要被<b>转让或消费</b>时才需要
/// 一个新的块类型，那时候价值才开始在任意双方之间移动。
/// </para>
/// <para>
/// <b>分数不落盘。</b> 落盘的是它的输入（评审次数、难度、严重度），分数在读取时现算。
/// 存分数等于把公式冻在写入的那一刻：改一次权重，老记录就跟新记录不是一把尺子，
/// 而排行榜最不能容忍的就是两把尺子。
/// </para>
/// <para>
/// <b>当前定位是纯荣誉。</b> 所以这里<b>没有</b>抽检与身份绑定：
/// quorum=1 时没有人复核，一次敷衍的「看起来没问题」和一次认真的评审同分；
/// 而 <see cref="BallotPayload.ReviewerAz"/> 是节点自报的，一个人跑两台机器就是两个身份。
/// 这两条在荣誉榜上可以接受（同事之间会自我纠正），但只要积分开始能兑换任何东西，
/// 它们就立刻变成必须先堵的窟窿 —— 别在没补这两条的情况下给分数赋予价值。
/// </para>
/// </remarks>
public static class PointsProjection
{
    /// <summary>完成一次评审的基础分。</summary>
    /// <remarks>
    /// 评一个干净的 PR 也是活：读完 diff、确认没问题，本身就值分。
    /// 所以基础分不看结论 —— 只看有没有真的评完。
    /// </remarks>
    public const double Base = 10.0;

    /// <summary>Critical 一条加几分。</summary>
    public const double CriticalWeight = 8.0;

    /// <summary>Major 一条加几分。</summary>
    public const double MajorWeight = 3.0;

    /// <summary>Minor 一条加几分。</summary>
    /// <remarks>
    /// 给分但给得少：小问题值得报，可是它<b>最容易堆</b> —— 命名、注释、空行，
    /// 一个 PR 想凑二十条不难。真正的护栏是 <see cref="Catch"/> 的封顶，不是这个权重。
    /// </remarks>
    public const double MinorWeight = 1.0;

    /// <summary>
    /// 难度系数：<c>1 + log2(1 + 改动文件数) / 4</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 评 3000 行的重构和评一个错别字不该同分，但也<b>不能线性</b> ——
    /// 线性的话一个 100 文件的 PR 顶 100 个小 PR，于是最优策略变成蹲守大 PR，
    /// 而小 PR 没人认领。取对数让它单调上升但很快变平：
    /// </para>
    /// <para>
    /// 1 个文件 → 1.25 · 5 个 → 1.65 · 20 个 → 2.10 · 100 个 → 2.67
    /// </para>
    /// </remarks>
    public static double Difficulty(int filesChanged)
        => 1.0 + (Math.Log2(1.0 + Math.Max(0, filesChanged)) / 4.0);

    /// <summary>
    /// 发现加成。<b>饱和</b>于基础分：<c>base × raw / (raw + base)</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 必须有界，否则最优策略是把每条能说的都写成 finding 并往高了标严重度 ——
    /// 那会同时污染排行榜和评审本身（作者得在三十条 nit 里找那一条真问题）。
    /// </para>
    /// <para>
    /// 但<b>不能用硬截断</b>。第一版写的是 <c>Math.Min(raw, base)</c>，拿真实链数据一跑就
    /// 露馅了：2879（2 个 critical）、2880（4 个 major）、2878（1 critical + 2 major）
    /// 三次完全不同的评审都顶到上限，得分<b>一模一样 25.0</b>。封顶一旦经常触发，
    /// 它就不再是护栏而是一把剪刀，把最该区分开的那一段信息剪平了。
    /// </para>
    /// <para>
    /// 饱和曲线两头都对：<c>raw</c> 小的时候近似线性（抓到就有回报），
    /// 越往上增益越小且<b>永远不超过 base</b>（堆 nit 没有意义），而且严格单调 ——
    /// 多抓一条、或把一条 minor 换成 critical，分数一定上升。同样一组数据：
    /// 25.0 / 25.0 / 25.0 变成 19.3 / 19.5 / 20.6。
    /// </para>
    /// </remarks>
    public static double Catch(int critical, int major, int minor, double ceiling)
    {
        var raw = (Math.Max(0, critical) * CriticalWeight)
                + (Math.Max(0, major) * MajorWeight)
                + (Math.Max(0, minor) * MinorWeight);

        return raw <= 0 || ceiling <= 0 ? 0 : ceiling * raw / (raw + ceiling);
    }

    /// <summary>
    /// 一次评审的得分。<paramref name="decision"/> 是 Error 时为 0。
    /// </summary>
    /// <remarks>
    /// Error 票不计分：它说明这一轮没评成（子进程挂了、工作区拉不下来）。
    /// 它仍然计入 <c>SpentRounds</c> 让出席位，但那是调度的事，不是贡献。
    /// </remarks>
    public static double Score(
        ReviewDecision decision, int filesChanged, int critical, int major, int minor)
    {
        if (!decision.CountsTowardMajority())
        {
            return 0;
        }

        var basePoints = Base * Difficulty(filesChanged);
        return basePoints + Catch(critical, major, minor, ceiling: basePoints);
    }
}

using Conclave.Application.Ports;
using Conclave.Domain;

namespace Conclave.Application;

/// <summary>一个 Revision 在 UI 上的一行。</summary>
/// <param name="Revision">幂等键。</param>
/// <param name="Pr">Summons 当时的 PR 快照。</param>
/// <param name="Stage">人读的阶段名：Summoned / Seated / Reviewing / Voted / Promulgated。</param>
/// <param name="Quorum">应有席位数。</param>
/// <param name="BallotCount">已收到的票数。</param>
/// <param name="Decision">已公布则是最终结论，否则 null。</param>
/// <param name="Findings">已公布则是合并后的问题数，否则 0。</param>
/// <param name="MySeat">本节点在这个 Revision 上的轮次；-1 表示没席位。</param>
public sealed record PrView(
    Revision Revision,
    PrMeta Pr,
    string Stage,
    int Quorum,
    int BallotCount,
    ReviewDecision? Decision,
    int Findings,
    int MySeat)
{
    /// <summary>正在评它的节点指纹；null 表示没人在评。</summary>
    /// <remarks>
    /// 「我的 PR 被哪个节点在评」是这个字段的用途。以下几个都是 init 属性而不是构造参数：
    /// 既有的构造点与 <c>with</c> 拷贝都不用改。
    /// </remarks>
    public string? ReviewingBy { get; init; }

    /// <summary>认领了但还没开跑的节点。</summary>
    public string? ClaimedBy { get; init; }

    /// <summary>开始评的时刻，用于显示「评了多久」。</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>
    /// mesh 里没有任何节点有资格评它。
    /// </summary>
    /// <remarks>
    /// 最常见的原因是<b>作者就是唯一的节点</b> —— 「不评审自己的 PR」是硬规则，
    /// 于是单节点上自己的 PR 永远没人评。那不是「在等」，是永远不会有人评，
    /// 界面上必须跟「待评审」区分开，否则人会一直等一件不会发生的事。
    /// 出路是把它指派给别的节点。
    /// </remarks>
    public bool NobodyEligible { get; init; }
}

/// <summary>通知的语气。</summary>
/// <remarks>
/// 刻意跟 UI 的徽章色分开定义：这一层只说「这件事是好是坏」，
/// 具体染成什么颜色是展示层的决定。
/// </remarks>
public enum NoticeKind
{
    /// <summary>就是一件事发生了。</summary>
    Info = 0,

    /// <summary>成了。</summary>
    Ok,

    /// <summary>需要人看一眼，但不是错误。</summary>
    Warn,

    /// <summary>出错了。</summary>
    Bad,
}

/// <summary>
/// 一条通知。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：mesh 里发生的事（有人请你评审、对方接受/拒绝、额度过线、评审出结论）
/// 原先只写进日志和状态栏，而<b>状态栏会被下一次轮询覆盖</b>（默认 60 秒一轮）。
/// 人一旦没在盯着窗口，那件事就永远看不到了。
/// </para>
/// <para>
/// 只在内存里，不进链：这些是「本机看到了什么」，不是需要全 mesh 共识的事实。
/// 重启即清空 —— 通知的价值在当场，隔天再看那条「节点掉线」没有意义。
/// </para>
/// </remarks>
/// <param name="At">发生时刻。</param>
/// <param name="Kind">语气。</param>
/// <param name="Title">一句话说清发生了什么。</param>
/// <param name="Detail">补充信息，可空 —— 比如拒绝的理由、失败的异常消息。</param>
public sealed record Notice(DateTimeOffset At, NoticeKind Kind, string Title, string? Detail = null);

/// <summary>
/// 本节点发出的一次指派及其结果。
/// </summary>
/// <param name="RequestId">指派请求的 id，答复靠它对上。</param>
/// <param name="RevisionId">指派的 PR 版本。</param>
/// <param name="PeerId">被指派的节点。</param>
/// <param name="Accepted">
/// <c>null</c> = 已送达、等答复；<c>true</c>/<c>false</c> = 对方已答复。
/// 三态而不是布尔：「还没回」和「拒绝了」在界面上必须分得开。
/// </param>
/// <param name="Reason">对方拒绝时给的理由，可空。</param>
/// <param name="At">最后一次变化的时刻。</param>
public sealed record AssignmentOutcome(
    string RequestId,
    string RevisionId,
    string PeerId,
    bool? Accepted,
    string? Reason,
    DateTimeOffset At);

/// <summary>
/// 后台服务与 UI 之间唯一的通信面。
/// </summary>
/// <remarks>
/// 后台写、UI 读，通过 <see cref="Changed"/> 通知。刻意不让 UI 直接调用后台服务的方法，
/// 也不让后台持有任何 Avalonia 类型 —— Application 层必须能在没有 UI 的进程里跑起来。
/// </remarks>
public sealed class NodeState
{
    private readonly Lock _gate = new();
    private IReadOnlyList<PrView> _prs = [];
    private IReadOnlyList<Block> _blocks = [];
    private string _status = "启动中";
    private bool _reviewing;
    private string? _toolProblem;
    private UsageReading? _usage;
    private readonly List<AssignmentOutcome> _assignments = [];
    private readonly List<Notice> _notices = [];
    private int _unreadNotices;
    private string _orgUrl = string.Empty;
    private ReleaseInfo? _update;
    private string _updateStatus = string.Empty;
    private bool _updating;

    /// <summary>队列、账本、状态、通知等「要重画表格」的变化。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 额度读数变了。跟 <see cref="Changed"/> 分开，是因为它的节奏和影响面都不一样。
    /// </summary>
    /// <remarks>
    /// 额度探针每 60 秒读一次，而 <see cref="UsageReading.ReadAt"/> 每次都不同 ——
    /// 界面上「上次刷新时刻」正是靠它把「没有变化」和「没在刷新」分开，所以这条
    /// <b>不能</b>像别的写入点那样去重。
    /// <para>
    /// 但它影响的只有额度那一块。混在 <see cref="Changed"/> 里的话，每分钟都会把 PR 队列、
    /// Acta、通知几张表整个 <c>Clear()</c> 重建一遍 —— 明明只有一个百分比变了。
    /// </para>
    /// </remarks>
    public event EventHandler? UsageChanged;

    /// <summary>
    /// 外部工具找不到时的说明；null 表示一切正常。
    /// </summary>
    /// <remarks>
    /// 与「az 没登录」区分开：从 Finder 启动 .app 时 PATH 里没有 az / claude，
    /// 那时提示「请跑 az devops login」会把人带到错误的方向。
    /// </remarks>
    /// <summary>
    /// 本节点发出去的指派及其结果，最近的在前。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>发起方原先什么都不记</b>，于是收到答复时只能写一行日志加一句状态栏文字 ——
    /// 而状态栏会被下一次轮询的状态覆盖（默认 60 秒一轮）。结果是「对方同意了吗」
    /// 这个问题在界面上无处可查：接受还能靠对方广播回来的认领间接看出来，
    /// <b>拒绝则完全无痕</b>。
    /// </para>
    /// <para>
    /// 只在内存里，不进链也不广播：这是「本机发过什么」的 UI 状态，别的节点不需要知道。
    /// 进程重启就没了 —— 代价是重启后收到的答复会被丢弃（见
    /// <see cref="TryCompleteAssignment"/>），那比让任何白名单节点伪造答复要好。
    /// </para>
    /// </remarks>
    public IReadOnlyList<AssignmentOutcome> Assignments
    {
        get { lock (_gate) { return [.. _assignments]; } }
    }

    /// <summary>通知，最近的在前。</summary>
    public IReadOnlyList<Notice> Notices
    {
        get { lock (_gate) { return [.. _notices]; } }
    }

    /// <summary>还没被看过的条数，用来在 tab 上显示角标。</summary>
    public int UnreadNotices
    {
        get { lock (_gate) { return _unreadNotices; } }
    }

    /// <summary>
    /// 记一条通知。
    /// </summary>
    /// <remarks>
    /// 一分钟内同样的内容不重复记：轮询失败、探针读不到额度这类问题会每轮复现一次，
    /// 不去重的话通知列表会被同一句话灌满，真正的事件反而被挤出去。
    /// </remarks>
    /// <summary>查到的可装新版本；null 表示没有（或最新的就是本机这一版）。</summary>
    public ReleaseInfo? UpdateAvailable
    {
        get { lock (_gate) { return _update; } }
    }

    /// <summary>更新进行到哪一步的一句话；没在更新时为空。</summary>
    public string UpdateStatus
    {
        get { lock (_gate) { return _updateStatus; } }
    }

    /// <summary>正在下载/替换。这期间不能再点一次「更新」。</summary>
    public bool UpdateInProgress
    {
        get { lock (_gate) { return _updating; } }
    }

    public void SetUpdateAvailable(ReleaseInfo? release)
    {
        lock (_gate)
        {
            if (Equals(_update, release))
            {
                return;
            }

            _update = release;
        }

        Raise();
    }

    public void SetUpdateProgress(bool inProgress, string status)
    {
        lock (_gate)
        {
            _updating = inProgress;
            _updateStatus = status;
        }

        Raise();
    }

    public void Notify(NoticeKind kind, string title, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_notices.Count > 0
                && _notices[0].Kind == kind
                && _notices[0].Title == title
                && _notices[0].Detail == detail
                && now - _notices[0].At < TimeSpan.FromMinutes(1))
            {
                return;
            }

            _notices.Insert(0, new Notice(now, kind, title, detail));
            _unreadNotices++;

            // 200 条足够回看一整天的联调，也不会让内存和界面无限长
            if (_notices.Count > 200)
            {
                _notices.RemoveRange(200, _notices.Count - 200);
            }
        }

        Raise();
    }

    /// <summary>
    /// 清空通知。
    /// </summary>
    /// <remarks>
    /// 只删本机这份内存列表，不影响链、账本和队列 —— 通知是「看过了」的收件箱，
    /// 清掉它不该丢掉任何可追溯的事实（那些都在 Acta 和评审记录里）。
    /// </remarks>
    public void ClearNotices()
    {
        lock (_gate)
        {
            if (_notices.Count == 0 && _unreadNotices == 0)
            {
                return;
            }

            _notices.Clear();
            _unreadNotices = 0;
        }

        Raise();
    }

    /// <summary>人打开通知页时清角标。</summary>
    public void MarkNoticesRead()
    {
        lock (_gate)
        {
            if (_unreadNotices == 0)
            {
                return;
            }

            _unreadNotices = 0;
        }

        Raise();
    }

    /// <summary>指派已送达对方，等答复。</summary>
    public void SetAssignmentSent(string requestId, string revisionId, string peerId)
    {
        lock (_gate)
        {
            // 同一个 revision 重新指派时覆盖旧记录：界面上只该有一条「当前状态」
            _assignments.RemoveAll(a => a.RevisionId == revisionId);
            _assignments.Insert(0, new AssignmentOutcome(
                requestId, revisionId, peerId, null, null, DateTimeOffset.UtcNow));

            // 只留最近 20 条，够界面回看，也不会无限长
            if (_assignments.Count > 20)
            {
                _assignments.RemoveRange(20, _assignments.Count - 20);
            }
        }

        Raise();
    }

    /// <summary>
    /// 收到答复，落到对应的记录上。
    /// </summary>
    /// <returns>
    /// 记录里那条指派的目标节点；<c>null</c> 表示本节点没发过这条指派 ——
    /// 调用方应当据此丢弃这条答复。
    /// </returns>
    public string? TryCompleteAssignment(string requestId, bool accepted, string? reason)
    {
        string? peerId;
        lock (_gate)
        {
            var index = _assignments.FindIndex(a => a.RequestId == requestId);
            if (index < 0)
            {
                return null;
            }

            peerId = _assignments[index].PeerId;
            _assignments[index] = _assignments[index] with
            {
                Accepted = accepted,
                Reason = reason,
                At = DateTimeOffset.UtcNow,
            };
        }

        Raise();
        return peerId;
    }

    public string? ToolProblem
    {
        get { lock (_gate) { return _toolProblem; } }
    }

    public void SetToolProblem(string? problem)
    {
        lock (_gate)
        {
            _toolProblem = problem;
        }

        Raise();
    }

    /// <summary>
    /// 最近一次额度读数；后台每轮轮询写一次，UI 只读。
    /// </summary>
    /// <remarks>
    /// 为什么额度也走这里，而不是让 UI 自己调 <see cref="IUsageMeter"/>：读一次额度要起一个
    /// <c>claude</c> 子进程（实测约 5 秒）。UI 再自己定时读，就是同一个进程每分钟起两次探针，
    /// 而且会出现「界面上显示的用量」和「心跳报给别的节点的用量」不是同一次读数 ——
    /// 那两个数必须是同一个，否则排查时根本说不清到底哪个决定了入席。
    /// </remarks>
    public UsageReading? Usage
    {
        get { lock (_gate) { return _usage; } }
    }

    public void SetUsage(UsageReading reading)
    {
        lock (_gate)
        {
            _usage = reading;
        }

        UsageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Azure DevOps 组织地址；后台轮询时写一次，UI 只读。
    /// </summary>
    /// <remarks>
    /// 界面拿它拼「点 PR 号跳浏览器」的地址。为什么不让 UI 自己读配置：这个值可能是
    /// <c>az devops configure --list</c> 给的（配置里留空时），而那是一次子进程调用 ——
    /// 归后台读、UI 只消费，跟额度读数同一个理由。
    /// </remarks>
    public string OrgUrl
    {
        get { lock (_gate) { return _orgUrl; } }
    }

    public void SetOrgUrl(string url)
    {
        lock (_gate)
        {
            if (_orgUrl == url)
            {
                return;
            }

            _orgUrl = url;
        }

        Raise();
    }

    public IReadOnlyList<PrView> Pipeline
    {
        get { lock (_gate) { return _prs; } }
    }

    public IReadOnlyList<Block> RecentBlocks
    {
        get { lock (_gate) { return _blocks; } }
    }

    public string Status
    {
        get { lock (_gate) { return _status; } }
    }

    /// <summary>
    /// 本节点此刻手上有没有评审在跑（含排在并发闸后面的）。
    /// </summary>
    /// <remarks>
    /// 菜单栏图标和顶栏那枚状态点都读它。原先它们只能从 <see cref="Pipeline"/> 里找
    /// <c>ReviewingBy == 自己</c> 的行 —— 那份投影每轮编排（15 秒）才重建一次，评审开跑
    /// 之后界面上要迟十几秒才有反应。这里由编排器在负载变化的那一刻直接设，立刻生效。
    /// </remarks>
    public bool Reviewing
    {
        get { lock (_gate) { return _reviewing; } }
    }

    /// <summary>
    /// 更新队列。内容没变就<b>不发事件</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 编排循环每 15 秒无条件调一次这个方法，而 UI 收到 <see cref="Changed"/> 就会把
    /// PR 队列、Acta、通知几张表整个 <c>Clear()</c> 重建。于是不管有没有事发生，
    /// 界面每 15 秒抖一次：滚动位置回到顶部、悬浮态消失、开着的菜单被关掉。
    /// </para>
    /// <para>
    /// <see cref="PrView"/> 是 record，值相等，所以逐项比一遍就能判断「真的变了没有」。
    /// 比较的代价远小于重建几十行控件。<see cref="SetOrgUrl"/> 本来就是这么做的，
    /// 这里只是把同一条规矩铺到其余几个写入点上。
    /// </para>
    /// <para>
    /// 判等偏差的方向是安全的：<see cref="PrMeta.ChangedPaths"/> 这类集合成员按引用比，
    /// 最坏情况是「其实没变却判成变了」—— 退化回原来的行为，多刷一次而已。
    /// </para>
    /// </remarks>
    public void SetPipeline(IReadOnlyList<PrView> prs)
    {
        ArgumentNullException.ThrowIfNull(prs);

        lock (_gate)
        {
            if (_prs.SequenceEqual(prs))
            {
                return;
            }

            _prs = prs;
        }

        Raise();
    }

    /// <summary>更新最近区块。内容没变就不发事件，理由同 <see cref="SetPipeline"/>。</summary>
    public void SetRecentBlocks(IReadOnlyList<Block> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        lock (_gate)
        {
            if (_blocks.SequenceEqual(blocks))
            {
                return;
            }

            _blocks = blocks;
        }

        Raise();
    }

    /// <summary>编排器在评审开跑/收尾时调。没变就不发事件。</summary>
    public void SetReviewing(bool reviewing)
    {
        lock (_gate)
        {
            if (_reviewing == reviewing)
            {
                return;
            }

            _reviewing = reviewing;
        }

        Raise();
    }

    public void SetStatus(string status)
    {
        lock (_gate)
        {
            if (_status == status)
            {
                return;
            }

            _status = status;
        }

        Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

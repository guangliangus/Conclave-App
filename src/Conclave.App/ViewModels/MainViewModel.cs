using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
using Microsoft.Extensions.Logging;

namespace Conclave.App.ViewModels;

/// <summary>主窗口的 ViewModel。只读 <see cref="NodeState"/>，不直接碰 Acta。</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly NodeState _state;
    private readonly IMesh _mesh;
    private readonly ReviewOrchestrator _orchestrator;
    private readonly ConclaveOptions _options;
    private readonly IReviewLog _reviewLog;
    private readonly IActaStore _acta;
    private readonly ReviewProgressLog _progress;
    private readonly IUpdateInstaller _installer;
    private readonly ILogger<MainViewModel> _logger;

    /// <summary>
    /// 下半区（额度 + 账单）的刷新节奏。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两块数据都<b>没有事件可订阅</b>，只能定时去看：额度真值是别的进程（交互式会话的
    /// <c>claude -p "/usage"</c>）按自己的节奏变的；账单则来自 <c>reviews</c>
    /// 投影表，而写它的可能是 gossip 进来的别人的票。
    /// </para>
    /// <para>
    /// <b>账单原先没有自己的节奏</b>，只搭 <see cref="NodeState.Changed"/> 的便车 ——
    /// 那个事件由编排循环每 15 秒发一次。于是编排循环一旦抛异常（它自己会 catch 住继续转，
    /// 但那一轮的 <c>SetPipeline</c> 就不会执行）或者整个后台空转，「今日/累计」就悄悄
    /// 冻在上一次的数字上，界面上看不出任何异常。定时刷新是为了把这条隐式依赖切断。
    /// </para>
    /// <para>
    /// 30 秒：一次读几百字节的文件加几条 SQLite 聚合，跟得上人在旁边开着 Claude Code
    /// 烧额度的速度，又不会白转。事件驱动的那条路径保留 —— 有动静时要立刻反映，
    /// 不能等下一个 tick。
    /// </para>
    /// </remarks>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 「随时间自己会变」的那些内容的重绘间隔。
    /// </summary>
    /// <remarks>
    /// 两处：PR 行的「评了多久」和节点行的「上次心跳多久前」。两者都走
    /// <see cref="Format.Elapsed"/>，精度是<b>分钟</b>（60 秒内一律显示「刚刚」），
    /// 所以 30 秒足够让它们看起来一直在走，最坏也就慢半格。
    /// </remarks>
    private static readonly TimeSpan LiveInterval = TimeSpan.FromSeconds(30);

    private readonly DispatcherTimer _refreshTimer;

    /// <summary>
    /// 只在<b>当前这一页确实有随时间变化的内容</b>时才转的重绘。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="NodeState"/> 现在只在数据真的变了才发 <see cref="NodeState.Changed"/>，
    /// 界面不再每 15 秒白抖一次。代价是两处从「现在几点」现算出来的文字会冻住：
    /// PR 行的「评了多久」（从 <c>StartedAt</c> 算）和节点行的「上次心跳多久前」
    /// （从 <c>LastHeartbeat</c> 算）。后者尤其隐蔽 —— 以前它是搭
    /// <c>SetStatus("mesh 内 N 个节点在线")</c> 的便车刷新的，而那句话在成员数不变时
    /// 现在已经不再发事件了。
    /// </para>
    /// <para>
    /// 所以留这一个定时器，但按<b>当前页</b>决定转不转、转的时候画什么：
    /// 停在队列页而且没有评审在跑，它就是停的；停在节点页,只重画那一张表。
    /// 静止的界面就真的静止。
    /// </para>
    /// </remarks>
    private readonly DispatcherTimer _liveTimer;

    /// <summary>
    /// 操作提示浮条的显示时长。
    /// </summary>
    /// <remarks>
    /// 3 秒：够看完一句话，又不会在下一次操作时还挂着上一条。
    /// </remarks>
    private static readonly TimeSpan ToastLifetime = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 乐观状态的有效期。
    /// </summary>
    /// <remarks>
    /// 略大于编排间隔（默认 15 秒）—— 过了这段时间后台状态一定已经反映出来了，
    /// 再留着就会盖住真实状态。
    /// </remarks>
    private static readonly TimeSpan JustDoneLifetime = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 指派发出去之后，那一行锁多久。
    /// </summary>
    /// <remarks>
    /// 锁是为了挡住「以为没点上再点一次」，但它不能没有尽头 —— 对方可能一直不按，
    /// 也可能接受之后机器就掉线了，而那时候人唯一的出路正是<b>改指派给别人</b>。
    /// 60 秒：够对方看见横幅按一下，又不至于把一行锁到只能重启。
    /// </remarks>
    private static readonly TimeSpan AssignAckWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 指派结果那句话在行上挂多久。
    /// </summary>
    /// <remarks>
    /// 以前不设过期，理由是「对方拒绝了必须一直挂着，否则完全无痕」。那条理由现在不成立了 ——
    /// 接受/拒绝都会进通知页，而通知页是永久的、还带角标。行上这句只是「刚发生」的提示，
    /// 一直挂着反而会盖住这一行真正的状态。
    /// <para>
    /// 3 分钟：够人从别的窗口切回来看见，也不至于第二天还挂在那儿。
    /// </para>
    /// </remarks>
    private static readonly TimeSpan AssignNoteLifetime = TimeSpan.FromMinutes(3);

    private readonly DispatcherTimer _toastTimer;

    /// <summary>刚点过但后台还没反映出来的操作：revisionId → 显示什么 + 灰掉时怎么解释 + 什么时候点的。</summary>
    private readonly Dictionary<string, (string Text, string Tip, DateTimeOffset At)> _justDone =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 正在答复的指派请求 id。
    /// </summary>
    /// <remarks>
    /// 答复要发一次 HTTP，最长等 Mesh.RequestTimeout。这期间「同意」「拒绝」两个按钮
    /// 都得灰掉 —— 它们是两个命令，只灰被点的那个的话，同意完还能点拒绝，
    /// 同一条请求就被答了两遍（第二次对方按 requestId 找不到记录，直接丢，
    /// 但界面上人以为自己刚刚改了主意）。
    /// </remarks>
    private readonly HashSet<string> _responding = new(StringComparer.Ordinal);

    /// <summary>
    /// 在 mesh 里撞了名的 az 身份。
    /// </summary>
    /// <remarks>
    /// 同一台机器上跑多个节点时，几个对端的 az 身份完全一样（都是本人），
    /// 于是「指派给」菜单里是两行一模一样的「guangliangli（额度 50%）」——
    /// 指给谁全靠蒙，而这两台的负载和额度可能差很多。撞名的补上指纹前 4 位，
    /// 没撞的保持干净：日常跨机器的 mesh 里没人想看指纹。
    /// </remarks>
    private HashSet<string> _ambiguousNames = new(StringComparer.Ordinal);

    /// <summary>已经弹过提示的最新一条指派答复的时刻，用来只弹一次。</summary>
    private DateTimeOffset _lastAssignmentSeen = DateTimeOffset.MinValue;
    private bool _readingLedger;

    /// <summary>主面板此刻在不在屏幕上。见 <see cref="SetVisible"/>。</summary>
    private bool _visible;

    /// <summary>公钥指纹全长；顶栏只显示前 8 位，完整值进 tooltip。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NodeIdShort), nameof(NodeIdTip))]
    public partial string NodeId { get; set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NodeIdTip))]
    public partial string AzIdentity { get; set; } = "—";

    /// <summary>
    /// 顶栏身份里「人」那一半：az 账号的短名。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 跟 <see cref="AzIdentity"/> 分开：那个是给 tooltip 的完整值
    /// （<c>LIONMAIL\guangliangli</c> 或者一个邮箱），这个是顶栏上要显示的那一截。
    /// </para>
    /// <para>
    /// 也跟 <c>AzName</c> 分开：那个在取不到 az 身份时退回指纹前 8 位，而顶栏这里
    /// 指纹本来就在冒号后面 —— 退回指纹会变成「35641f20:35641f20」。
    /// 取不到就明说「未登录」，顶上那条 <see cref="IdentityWarning"/> 横幅会解释后果。
    /// </para>
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NodeIdTip))]
    public partial string SelfAccount { get; set; } = "—";

    [ObservableProperty]
    public partial string Status { get; set; } = "启动中";

    [ObservableProperty]
    public partial bool AutoReview { get; set; }

    [ObservableProperty]
    public partial bool PostToAzureDevOps { get; set; }

    /// <summary>
    /// 顶栏下面那条操作提示。
    /// </summary>
    /// <remarks>
    /// 状态栏（<see cref="Status"/>）说的是「现在是什么状况」，在窗口最底部；
    /// 这条说的是「你刚才那一下的结果」，出现在离按钮最近的地方，几秒后自己消失。
    /// 两者不能合并：把操作结果写进状态栏，人根本不会往那儿看。
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToast))]
    public partial string ToastText { get; set; } = string.Empty;

    public bool HasToast => ToastText.Length > 0;

    /// <summary>
    /// 浮条的语气：完成 / 出错 / 进行中，各对应一个语义色点。
    /// </summary>
    /// <remarks>
    /// 只用一个点而不是给整条染色：整条变红会盖过页面上真正要看的东西，
    /// 而「刚才那一下成了还是没成」一个点就够说明。
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToastIsOk), nameof(ToastIsBad), nameof(ToastIsBusy))]
    public partial BadgeTone ToastTone { get; set; } = BadgeTone.Ok;

    public bool ToastIsOk => ToastTone == BadgeTone.Ok;

    public bool ToastIsBad => ToastTone == BadgeTone.Bad;

    public bool ToastIsBusy => ToastTone == BadgeTone.Gold;

    /// <summary>顶栏状态胶囊：「空闲」或「评审中 #1234」。</summary>
    [ObservableProperty]
    public partial string SelfActivityText { get; set; } = "空闲";

    [ObservableProperty]
    public partial string SelfActivityTip { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SelfIsBusy { get; set; }

    /// <summary>
    /// 单机还是组网。
    /// </summary>
    /// <remarks>
    /// 「在线节点 1/1」这一个数分不出两种完全不同的处境：mesh <b>关着</b>（要改配置重启），
    /// 还是 mesh 开着但<b>还没有邻居</b>（等对面开机，或者指纹没互相写进白名单）。
    /// 前者要动手，后者只要等 —— 所以模式必须单独说出来。
    /// </remarks>
    [ObservableProperty]
    public partial string MeshModeText { get; set; } = "单机";

    [ObservableProperty]
    public partial string MeshModeTip { get; set; } = string.Empty;

    /// <summary>组网模式（有没有别的节点无关，只看 mesh 开没开）。界面用它上色。</summary>
    [ObservableProperty]
    public partial bool MeshIsNetworked { get; set; }

    /// <summary>
    /// 顶部的更新横幅。有新版本才出现。
    /// </summary>
    /// <remarks>
    /// 默认<b>不需要人点</b>：查到新版就自动下载校验，等本节点评完手上的 PR 后替换并重启
    /// （见 <see cref="UpdateOptions.AutoInstall"/>）。所以这条横幅是「告诉你要发生什么」，
    /// 不是「问你要不要」—— 按钮只在关掉了自动更新时才出现（<see cref="CanUpdate"/>）。
    /// </remarks>
    [ObservableProperty]
    public partial bool HasUpdate { get; set; }

    [ObservableProperty]
    public partial string UpdateText { get; set; } = string.Empty;

    /// <summary>
    /// 横幅第二行：更新进行到哪一步，或者「接下来会怎样」。
    /// </summary>
    /// <remarks>
    /// 安装器在跑的时候用它的进度（下载百分比 / 等评审 / 替换中）；没在跑的时候不留空 ——
    /// 「有新版本」而下面什么都不说，人只会去找那个不存在的按钮。
    /// </remarks>
    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = string.Empty;

    /// <summary>
    /// 显示「立即更新」按钮。
    /// </summary>
    /// <remarks>
    /// 自动更新开着（且这台机器真能替换）时<b>不</b>显示：那个按钮什么也改变不了，
    /// 只会让人以为不点就不会更新。关掉自动更新才回到「人点」的老路子。
    /// </remarks>
    [ObservableProperty]
    public partial bool CanUpdate { get; set; }

    [ObservableProperty]
    public partial string UpdateTip { get; set; } = string.Empty;

    /// <summary>发布说明页；没有可装版本时为 null。code-behind 用它开浏览器。</summary>
    public Uri? UpdateReleasePage => _state.UpdateAvailable?.ReleasePage;

    /// <summary>az 身份缺失或外部工具找不到时给出的醒目警告。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial string? IdentityWarning { get; set; }

    /// <summary>下半区显示评审记录（否则显示 Acta 会议录）。</summary>
    /// <summary>
    /// 当前显示哪张表。
    /// </summary>
    /// <remarks>
    /// 六张表平级，用一个枚举而不是六个 bool 来记：六个互斥的 bool 需要六个
    /// <c>OnXChanged</c> 互相置反，再加一张时漏写一个就会出现两张表同时显示，
    /// 而那种错编译器不会拦。<see cref="ShowQueue"/> 那几个是它的投影，
    /// 只是为了让 RadioButton 能双向绑。
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowQueue))]
    [NotifyPropertyChangedFor(nameof(ShowMine))]
    [NotifyPropertyChangedFor(nameof(ShowReviews))]
    [NotifyPropertyChangedFor(nameof(ShowActa))]
    [NotifyPropertyChangedFor(nameof(ShowNodes))]
    [NotifyPropertyChangedFor(nameof(ShowNotices))]
    public partial MainTab Tab { get; set; } = MainTab.Queue;

    /// <summary>
    /// RadioButton 用的投影：置 true 就切到那张表，置 false（同组另一个被选中时）忽略。
    /// </summary>
    /// <remarks>
    /// 忽略 false 是关键。RadioButton 组切换时会先把旧的置 false 再把新的置 true，
    /// 如果这里跟着把 <see cref="Tab"/> 清掉，就会有一帧五张表全不显示。
    /// 通知不用自己发 —— <see cref="Tab"/> 上的 NotifyPropertyChangedFor 会一次性发全。
    /// </remarks>
    public bool ShowQueue
    {
        get => Tab == MainTab.Queue;
        set => Select(MainTab.Queue, value);
    }

    public bool ShowMine
    {
        get => Tab == MainTab.Mine;
        set => Select(MainTab.Mine, value);
    }

    public bool ShowReviews
    {
        get => Tab == MainTab.Reviews;
        set => Select(MainTab.Reviews, value);
    }

    public bool ShowActa
    {
        get => Tab == MainTab.Acta;
        set => Select(MainTab.Acta, value);
    }

    public bool ShowNodes
    {
        get => Tab == MainTab.Nodes;
        set => Select(MainTab.Nodes, value);
    }

    public bool ShowNotices
    {
        get => Tab == MainTab.Notices;
        set => Select(MainTab.Notices, value);
    }

    /// <summary>
    /// tab 名字后面那个上标数字。
    /// </summary>
    /// <remarks>
    /// 跟标题拆开而不是拼成一个字符串（原先是「我的 PR 13」）：数字要做成上标、要单独染色，
    /// 而拼在一起的话整段只能是同一种字号和颜色。为 0 时是空串，XAML 那边整个角标不占位。
    /// </remarks>
    [ObservableProperty]
    public partial string QueueCount { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MineCount { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReviewsCount { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ActaCount { get; set; } = string.Empty;

    /// <summary>在线节点是「活着的/总数」，所以不走 0 即隐藏那条规则。</summary>
    [ObservableProperty]
    public partial string NodesCount { get; set; } = string.Empty;

    /// <summary>通知那一格的角标是<b>未读数</b>，不是总条数 —— 总条数一直有，未读才需要人看。</summary>
    [ObservableProperty]
    public partial string NoticesCount { get; set; } = string.Empty;

    /// <summary>
    /// mesh 里所有节点都还活着。
    /// </summary>
    /// <remarks>
    /// 有节点掉线时「在线节点」那个角标要变色 —— 3/3 和 2/3 光看数字差别太小，
    /// 而后者意味着有 PR 会回到队列重新分配。
    /// </remarks>
    [ObservableProperty]
    public partial bool NodesAllAlive { get; set; } = true;

    /// <summary>
    /// 当前打开的评审日志；null 表示日志面板收着。
    /// </summary>
    /// <remarks>
    /// 日志开在表格下面而不是新窗口里：评审日志是「对着队列里那一行看」的东西，
    /// 单独一个窗口会挡住它自己要解释的那张表，而且多开几个就找不着了。
    /// 同时只留一份 —— 换一行看日志时上一份要 Dispose（它带着一个 2 秒的定时器）。
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLog))]
    public partial ReviewLogViewModel? Log { get; set; }

    public bool HasLog => Log is not null;

    /// <summary>
    /// 日志开着时把上面那张表收起来。
    /// </summary>
    /// <remarks>
    /// 不是「隐藏表格」而是「让日志占满」：屏幕不高的时候两块各半谁都看不清，
    /// 而看日志的那几分钟本来就不需要同时盯着队列。再点一下就回来。
    /// </remarks>
    [ObservableProperty]
    public partial bool TablesCollapsed { get; set; }

    /// <summary>三张表共用的一份「该显示几列」。窗口一改宽度，所有行跟着变。</summary>
    public TableLayout Layout { get; } = new();

    public ObservableCollection<PrRow> Pipeline { get; } = [];

    /// <summary>
    /// 队列里作者是本机 az 身份的那些。
    /// </summary>
    /// <remarks>
    /// 是队列的一个筛选视图，不从队列里挪走 —— 「PR 队列」要的是 mesh 的全貌，
    /// 少了自己那几条反而看不出「为什么这个 PR 没人评」（最常见的原因正是它是自己提的）。
    /// </remarks>
    public ObservableCollection<PrRow> Mine { get; } = [];

    /// <summary>mesh 里的节点，含判为离线的。</summary>
    public ObservableCollection<NodeRow> Nodes { get; } = [];

    /// <summary>
    /// 等本节点确认的指派请求。
    /// </summary>
    /// <remarks>
    /// 刻意不自动接受：别人的评审任务会在你的机器上跑 <c>Bash</c>、烧你的 Claude 额度，
    /// 那是要本人点头的事。
    /// </remarks>
    public ObservableCollection<PendingRow> Pending { get; } = [];

    [ObservableProperty]
    public partial bool HasPending { get; set; }

    public ObservableCollection<BlockRow> Blocks { get; } = [];

    public ObservableCollection<ReviewRow> Reviews { get; } = [];

    /// <summary>
    /// 通知，最近的在最上面。
    /// </summary>
    /// <remarks>
    /// 收件箱式的一张表，而不是一堆堆叠的浮层：mesh 里的事（有人请你评审、对方拒绝、
    /// 额度过线）常常是人不在屏幕前时发生的，浮层过几秒就没了，只有一个能回看的地方
    /// 才答得上「刚才到底发生了什么」。即时反馈仍然走那条 3 秒的 toast，两者互补。
    /// </remarks>
    public ObservableCollection<NoticeRow> Notices { get; } = [];

    /// <summary>
    /// 账单那一列的三行字。
    /// </summary>
    /// <remarks>
    /// 预拼成整句而不是「数字 + 单位」两个属性：这三行都是一句话（「今日 3 次 · $1.20」），
    /// 拆开只会让 XAML 里多三个 <c>StringFormat</c>，而排版一点都不会更灵活。
    /// <para>
    /// 原先这里有九个属性对应六个指标格。队列 / 累计 / 账本三个数已经写在 tab 标题上，
    /// TOKEN 与缓存命中是「累计」的分解 —— 全都收进这三行与它的 tooltip 里。
    /// </para>
    /// </remarks>
    [ObservableProperty]
    public partial string TodayLine { get; set; } = "今日 0 次 · $0";

    [ObservableProperty]
    public partial string TotalLine { get; set; } = "累计 0 次 · $0";

    /// <summary>累计 token 与缓存命中率 —— 命中缓存的输入计价远低于新输入，越高越省。</summary>
    [ObservableProperty]
    public partial string TokenLine { get; set; } = "0 token · 缓存 —";

    /// <summary>额度面板：按窗口拆开的用量，逐条画成进度条。</summary>
    public ObservableCollection<UsageWindowRow> UsageWindows { get; } = [];

    /// <summary>额度数字的来源（<c>rate-limits</c> / <c>budget</c> / …），排查时要看的第一眼。</summary>
    [ObservableProperty]
    public partial string UsageSource { get; set; } = "—";

    /// <summary>来源的一行说明，进 tooltip。</summary>
    [ObservableProperty]
    public partial string UsageDetail { get; set; } = string.Empty;

    /// <summary>一条窗口都没有 —— 此时改显示怎么把真值接上来。</summary>
    [ObservableProperty]
    public partial bool UsageEmpty { get; set; } = true;

    [ObservableProperty]
    public partial string UsageHint { get; set; } = "等下一轮轮询读取额度…";

    /// <summary>
    /// 上次刷新的时刻。
    /// </summary>
    /// <remarks>
    /// 定时刷新必须<b>看得见</b>。这次的问题就是数字冻住了而界面上毫无迹象 ——
    /// 一个时间戳能让「没有变化」和「没在刷新」一眼分开。
    /// </remarks>
    [ObservableProperty]
    public partial string UsageUpdatedText { get; set; } = "—";

    /// <summary>账本健康：索引冲突次数。放在状态栏右侧，平时应当一直是 0。</summary>
    [ObservableProperty]
    public partial string ActaSummary { get; set; } = "—";

    /// <summary>三张表各自的空状态：有数据时不占位置，没数据时给一句话说明下一步。</summary>
    [ObservableProperty]
    public partial bool PipelineEmpty { get; set; } = true;

    [ObservableProperty]
    public partial bool ReviewsEmpty { get; set; } = true;

    [ObservableProperty]
    public partial bool BlocksEmpty { get; set; } = true;

    [ObservableProperty]
    public partial bool MineEmpty { get; set; } = true;

    [ObservableProperty]
    public partial bool NodesEmpty { get; set; } = true;

    [ObservableProperty]
    public partial bool NoticesEmpty { get; set; } = true;

    public string NodeIdShort => NodeId.Length > 8 ? NodeId[..8] : NodeId;

    /// <summary>顶栏那串指纹的 tooltip。</summary>
    /// <remarks>
    /// <b>全长指纹必须在界面上能拿到。</b> 把一台机器加进 mesh 要做的事就是把它的全长
    /// 指纹写进对端的 <c>~/.conclave/electors.allow</c>，而顶栏只显示前 8 位。
    /// 这条 tooltip 原先只有 az 身份，于是全长指纹在整个界面上无处可查 ——
    /// 只能去翻启动日志里那行「节点身份 …」。
    /// </remarks>
    public string NodeIdTip => string.Join(
        '\n',
        "az 身份 " + AzIdentity,
        "elector " + NodeId,
        string.Empty,
        "冒号前是人、冒号后是机器：同一个人可以跑好几台节点，",
        "而席位、签名、认领、指派全都按 elector 指纹认人。",
        "要让别的机器接受本节点，把上面那串完整指纹写进它的 ~/.conclave/electors.allow");

    public bool HasWarning => !string.IsNullOrEmpty(IdentityWarning);

    public MainViewModel(
        NodeState state,
        IMesh mesh,
        ReviewOrchestrator orchestrator,
        ConclaveOptions options,
        IReviewLog reviewLog,
        IActaStore acta,
        ReviewProgressLog progress,
        IUpdateInstaller installer,
        ILogger<MainViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(options);

        _state = state;
        _mesh = mesh;
        _orchestrator = orchestrator;
        _options = options;
        _reviewLog = reviewLog;
        _acta = acta;
        _progress = progress;
        _installer = installer;
        _logger = logger;

        AutoReview = options.AutoReview;
        PostToAzureDevOps = options.PostToAzureDevOps;

        // 必须在首次 Refresh 之前建好 —— Refresh 结尾会按需启停它。
        _liveTimer = new DispatcherTimer { Interval = LiveInterval };
        _liveTimer.Tick += (_, _) => RenderLive();

        _state.Changed += OnStateChanged;

        // 额度单独一条路：探针每 60 秒读一次，而那只该重画额度那一块，
        // 不该把 PR 队列、Acta、通知几张表全拆了重建。
        _state.UsageChanged += OnUsageChanged;

        Refresh();

        // 只剩账单：它没有事件可订阅（写 reviews 投影表的可能是 gossip 进来的别人的票），
        // 只能定时去看。额度已经走 UsageChanged 了，不再挂在这里白转。
        // 刻意<b>不</b>在这里 Start —— 由 SetVisible 按窗口可见性启停。
        // 原先是无条件启动的，而这个 ViewModel 是单例、主面板关掉只是 Hide()，
        // 于是窗口不在屏幕上时它照样每 30 秒把几张表拆了重建。见 SetVisible 的注释。
        _refreshTimer = new DispatcherTimer { Interval = RefreshInterval };
        _refreshTimer.Tick += (_, _) => _ = LoadLedgerAsync();

        // 单次触发：每次 ShowToast 重新计时，所以连续操作只会看到最后一条
        _toastTimer = new DispatcherTimer { Interval = ToastLifetime };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastText = string.Empty;
        };
    }

    /// <summary>
    /// 主面板显示/隐藏时由 <c>MainWindow</c> 推进来。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这是把那次 40GB 摁住的那道闸。</b> 这个 ViewModel 是单例，而主面板的 x 只是
    /// <c>Hide()</c>（见 <c>App.OnDashboardClosing</c>）—— 所以在它之前，窗口不在屏幕上的
    /// 时候两个定时器加 <see cref="NodeState.Changed"/> 照样每 15/30 秒把 PR 队列、账本、
    /// 通知几张表整个 <c>Clear()</c> 重建一遍。而这个进程平时就是个菜单栏图标，
    /// 窗口一天也开不了一次。
    /// </para>
    /// <para>
    /// 实测（<c>dotnet-counters</c> + <c>vmmap</c> + 两次 heap 快照）：托管堆全程稳在
    /// 110MB、分配速率只有几百 KB/5s，而 <c>MALLOC_SMALL</c> 的脏页一路涨；每 100 秒
    /// 多出约 69 套 <c>TBaseFont</c> / <c>NSCTFont</c> / <c>TTrueTypeMemoryFont</c>，
    /// 堆里是成百上千个 96KB 与 624KB 的块 —— 正是「把整个字体文件读进内存」的尺寸。
    /// 也就是说漏的是 <b>Avalonia 12.1.2 macOS 后端的原生内存</b>，每重建一轮界面漏一批，
    /// 被「永不停的刷新」放大成每分钟几十 MB。本项目里没有任何自定义绘制，也没碰过
    /// <c>FormattedText</c> / <c>Typeface</c>，只有 <c>Program.BuildAvaloniaApp</c> 里
    /// 一次性的 <c>.WithInterFont()</c>。
    /// </para>
    /// <para>
    /// <b>这道闸只挡住了「没人在看的时候」，漏本身还在。</b> 后来又量了一轮：
    /// 面板收着时确实持平（2.7 分钟 +0.2MB），一打开就回到每分钟 10–15MB ——
    /// 光打开那一下的全量 <see cref="Refresh"/> 就是 33 秒 +48MB，一个进程跑了
    /// 五十几分钟就到 1.0GB。所以这里挡的是<b>放大系数</b>，不是原因。
    /// </para>
    /// <para>
    /// 真正的原因后来定位到了：字体缺少请求的字重时，Avalonia 会把字体字节读出来
    /// 再造一份带模拟效果的 typeface（<c>SkiaTypeface.TryGetStream</c> →
    /// <c>FontManagerImpl.TryCreateGlyphTypeface(Stream, FontSimulations)</c> →
    /// <c>SKTypeface.FromStream</c>），而那个 typeface 不释放也不复用，每次约 1.3MB。
    /// 修法是让默认字体同时覆盖用到的文字和请求的字重，见
    /// <c>Program.UiFontFamily</c> 与 <c>Controls.axaml</c> 里那段「字重上限」的注释；
    /// 上游已报 AvaloniaUI/Avalonia#22214。
    /// 菜单栏图标那条路<b>不受影响</b> —— 它订的是 <see cref="NodeState.Changed"/>
    /// 而不是这个 ViewModel（见 <c>App.WatchReviewingState</c>），
    /// 所以窗口没开时图标照样跟着评审状态换脸。
    /// </para>
    /// </remarks>
    public void SetVisible(bool visible)
    {
        if (_visible == visible)
        {
            return;
        }

        _visible = visible;

        if (visible)
        {
            _refreshTimer.Start();
            Log?.Resume();

            // 隐藏期间攒下的变化在这里一次补齐。Refresh 是全量重建、从 NodeState 现读，
            // 所以不需要记「欠了几次」—— 做一次就是最新的。
            Refresh();
            return;
        }

        _refreshTimer.Stop();
        _liveTimer.Stop();
        _toastTimer.Stop();
        ToastText = string.Empty;
        Log?.Suspend();
    }

    /// <summary>
    /// 把后台读到的额度铺到面板上。
    /// </summary>
    /// <remarks>
    /// <b>UI 刻意不自己调 <see cref="IUsageMeter"/>。</b> 探针改成起 <c>claude</c> 子进程之后
    /// 一次读要几秒（实测约 5 秒），UI 再定时读就是同一个进程每分钟起两次探针；更糟的是
    /// 界面上的百分比和心跳报给别的节点的那个会是两次不同的读数，排查时说不清到底哪个
    /// 决定了入席。改由后台每轮轮询读一次、写进 <see cref="NodeState.Usage"/>，这里只渲染。
    /// <para>
    /// 所以这个方法是同步的、可以随便多调 —— 它自己不产生任何 I/O。
    /// </para>
    /// </remarks>
    private void RenderUsage()
    {
        if (_state.Usage is not { } reading)
        {
            return;
        }

        try
        {
            var unbounded = string.Equals(
                reading.Source, UsageReading.Unbounded.Source, StringComparison.Ordinal);

            UsageSource = reading.Source;
            UsageDetail = reading.Detail;

            var now = DateTimeOffset.Now;
            // 这一块每 60 秒必刷（ReadAt 每次都不同），而百分比多半没变 ——
            // 就地对齐之后，没变的那几条一次布局都不会发生。
            List<UsageWindowRow> windows = reading.Windows.Count > 0
                ? [.. reading.Windows.Select(w => new UsageWindowRow(w, now))]
                : unbounded
                    ? []
                    // 折算来源拆不出窗口，但那个数照样决定入席，所以仍然画一条。
                    : [new UsageWindowRow("按预算折算", reading.Utilization, reading.Detail)];

            RowSync.Apply(UsageWindows, windows, static r => r.Label);

            UsageEmpty = UsageWindows.Count == 0;

            // Detail 现在自带「为什么没有真值」那句（见 ClaudeUsageMeter），
            // 所以这里不再重复拼提示，只在两个预算都没配时补一句后果。
            UsageHint = unbounded
                ? reading.Detail + "；也没配预算，所以额度规则当前不生效。"
                : reading.Detail;

            // 是「读到的时刻」而不是「画出来的时刻」：探针一旦卡住，这个数就该停住，
            // 否则「没有变化」和「没在刷新」又混成一样了。
            UsageUpdatedText = unbounded
                ? "—"
                : "读于 " + reading.ReadAt.ToLocalTime()
                    .ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            // 额度画不出来不该影响别的面板，也不该把已有的数字打空。
            _logger.LogWarning(ex, "渲染额度面板失败");
        }
    }

    /// <summary>
    /// 拉一次评审记录与账本计数。
    /// </summary>
    /// <remarks>
    /// 走投影表而不是重放整条链 —— 链会一直长，而面板每次状态变化都要刷。
    /// </remarks>
    private async Task LoadLedgerAsync()
    {
        // 现在有两条触发路径（定时器 + NodeState.Changed），可能叠在一起。
        // 重入会让下面那串 ObservableCollection 的清空/填充交错，界面闪成半截。
        if (_readingLedger)
        {
            return;
        }

        _readingLedger = true;
        try
        {
            var records = await _reviewLog.ReadRecentAsync(200, CancellationToken.None)
                .ConfigureAwait(true);
            var total = await _reviewLog.ReadTotalAsync(null, null, CancellationToken.None)
                .ConfigureAwait(true);

            // 「今日」按本地日切，不是 UTC 日 —— 人看的是自己这一天烧了多少。
            var midnight = DateTime.Today;
            var since = new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight));
            var today = await _reviewLog.ReadTotalAsync(since, null, CancellationToken.None)
                .ConfigureAwait(true);
            var health = await _acta.ReadHealthAsync(CancellationToken.None).ConfigureAwait(true);

            // 这张表最大（200 行 × 十几个单元格），而且每 30 秒刷一次 ——
            // Clear() 重建的代价最重的就是它。block_hash 是账本里的主键，天然唯一。
            RowSync.Apply(
                Reviews,
                [.. records.Select(r => new ReviewRow(r, OrgUrl, Layout))],
                static r => r.BlockHash);

            ReviewsEmpty = Reviews.Count == 0;

            TodayLine = string.Create(
                CultureInfo.InvariantCulture,
                $"今日 {today.Reviews} 次 · {Format.Money(today.CostUsd)}");
            TotalLine = string.Create(
                CultureInfo.InvariantCulture,
                $"累计 {total.Reviews} 次 · {Format.Money(total.CostUsd)}");
            TokenLine = string.Create(
                CultureInfo.InvariantCulture,
                $"{Format.Tokens(total.TotalTokens)} token · 缓存 {(total.Reviews == 0 ? "—" : Format.Percent(total.CacheHitRatio))}");

            // 链长与 revision 数原来各占一个指标格。它们是同一件事的两个数，
            // 又都不需要一直盯着，所以跟冲突计数一起收进状态栏右下角那一行。
            ActaSummary = string.Create(
                CultureInfo.InvariantCulture,
                $"链 {health.Blocks} 块 · {health.Revisions} revision · 索引冲突 {health.IndexConflicts} 次 · 让位 {health.ConflictsLost} 次");

            // 账本是异步读的，Refresh 那次算标题时 Reviews 还是空的 ——
            // 不在这里再算一次，「评审记录」那个 tab 上的条数会一直停在 0。
            RenderTabTitles();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读评审记录失败");
        }
        finally
        {
            _readingLedger = false;
        }
    }

    /// <summary>
    /// 额度读数变了 —— 只重画额度那一块。
    /// </summary>
    /// <remarks>
    /// 探针每 60 秒读一次,而 <c>ReadAt</c> 每次都不同,所以这条必然每分钟来一次。
    /// 走 <see cref="Refresh"/> 的话就是每分钟把整页拆了重建,而实际变的只有几个百分比。
    /// </remarks>
    private void OnUsageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RenderUsageIfVisible();
        }
        else
        {
            Dispatcher.UIThread.Post(RenderUsageIfVisible);
        }
    }

    /// <summary>后台线程触发，必须切回 UI 线程再动集合。</summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshIfVisible();
        }
        else
        {
            Dispatcher.UIThread.Post(RefreshIfVisible);
        }
    }

    /// <summary>看得见才重建。看不见时什么都不做，理由见 <see cref="SetVisible"/>。</summary>
    private void RefreshIfVisible()
    {
        if (_visible)
        {
            Refresh();
        }
    }

    /// <summary>同上，只是额度那一块。</summary>
    private void RenderUsageIfVisible()
    {
        if (_visible)
        {
            RenderUsage();
        }
    }

    private void Refresh()
    {
        var self = _mesh.Self;

        NodeId = self.Id;
        AzIdentity = string.IsNullOrEmpty(self.AzIdentity) ? "(未取到)" : self.AzIdentity;
        SelfAccount = string.IsNullOrWhiteSpace(self.AzIdentity)
            ? "未登录"
            : Labels.ShortAccount(self.AzIdentity);

        // 工具找不到与「没登录」是两回事，提示必须分开：从 Finder 启动 .app 时
        // LaunchServices 只给最小 PATH，az 根本不在里面，此时叫人去 az devops login
        // 只会把人带偏。
        IdentityWarning = _state.ToolProblem is { Length: > 0 } problem
            ? problem
            : string.IsNullOrEmpty(self.AzIdentity)
                ? "读不到 az 登录身份：「不评审自己的 PR」这条硬规则当前不生效。请跑 az devops login 后重启。"
                : null;
        Status = _state.Status;

        RenderUsage();

        var rowCommands = new PrRowCommands(
            ReviewCommand, ClaimCommand, ReleaseCommand, AssignCommand, ShowLogCommand);
        var now = DateTimeOffset.UtcNow;

        // 指派答复：对方同意/拒绝之后，这里是唯一能让人看见的地方 —— 状态栏那句话
        // 会被下一次轮询覆盖，而拒绝在别处完全没有痕迹。
        var assignments = _state.Assignments;
        foreach (var replied in assignments
            .Where(a => a.Accepted is not null && a.At > _lastAssignmentSeen)
            .OrderBy(a => a.At))
        {
            var who = PeerNameOf(replied.PeerId);
            ShowToast(
                replied.Accepted == true
                    ? $"{who} 接受了 {replied.RevisionId} 的评审指派"
                    : $"{who} 拒绝了 {replied.RevisionId} 的评审指派"
                        + (replied.Reason is { Length: > 0 } why ? $"：{why}" : string.Empty),
                replied.Accepted == true ? BadgeTone.Ok : BadgeTone.Bad);
        }

        if (assignments.Count > 0)
        {
            _lastAssignmentSeen = assignments.Max(a => a.At);
        }

        // 过期的乐观标记要清掉，否则会一直盖住真实状态
        foreach (var stale in _justDone
            .Where(kv => now - kv.Value.At > JustDoneLifetime)
            .Select(kv => kv.Key)
            .ToList())
        {
            _ = _justDone.Remove(stale);
        }
        _ambiguousNames = [.. _mesh.Members
            .GroupBy(AzName, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)];

        var peers = _mesh.Members
            .Where(m => m.Id != self.Id)
            .Select(m => new PeerInfo(
                m,
                PeerName(m),
                PeerLabel(m),
                IsIdle: m.IsAlive(now) && m.RunningJobs == 0 && m.HasHeadroom))
            .ToList();

        // 本节点认领了哪些：直接读 mesh 状态，而不是等队列投影转过来 ——
        // Claim/Release 都是<b>立刻</b>写进这里的，行上的「认领 / 撤销」也该立刻跟着换。
        var myClaims = _mesh.State.Claims
            .Select(c => c.RevisionId)
            .ToHashSet(StringComparer.Ordinal);

        // 本节点手上有没有活。一次只评一个 PR，所以有活的时候整张表的
        // 「评审 / 认领」都该点不动 —— 点了也只是排进本机队列，而排着的活
        // 对别的节点是不可见的，等于把那个 PR 从 mesh 里藏起来。
        // 认领也算「有活」：它就是「打算评」，领域层的席位分配同样这么算
        // （见 QueueProjection.AssignSeats）。
        var selfOccupied = myClaims.Count > 0
            || _state.Pipeline.Any(v => v.ReviewingBy == self.Id);

        // 先算出这一轮该有哪些行，最后交给 RowSync 就地对齐 —— 不再 Clear() 重建整张表，
        // 理由见 RowSync 的注释（每重建一轮界面会漏一批 CoreText 字体对象）。
        var queueRows = new List<PrRow>();
        var mineRows = new List<PrRow>();

        foreach (var view in _state.Pipeline
            .OrderBy(v => v.Decision is not null)
            .ThenByDescending(v => v.Pr.PrId))
        {
            // 后台一旦真的动起来（有人在评 / 已出结论），真实状态优先 —— 乐观标记只填
            // 「点了之后什么都还没发生」那段空白
            // 真实状态一旦动起来（有人认领了 / 在评了 / 出了结论），就不再盖任何临时文字
            var settled = view.Decision is not null
                || view.ClaimedBy is not null
                || view.Stage.StartsWith("评审中", StringComparison.Ordinal);

            var assignment = assignments.FirstOrDefault(a => a.RevisionId == view.Revision.Id);

            // 乐观标记可以顶替徽章：「已认领」本来就是这一行此刻的状态，只是后台还没确认。
            var pending = settled || _justDone.TryGetValue(view.Revision.Id, out var mark) is false
                ? null
                : new RowPending(mark.Text, mark.Tip);

            // 「等 X 确认 / X 已接受 / X 拒绝了」是<b>事件</b>，不是状态 —— 挂在徽章下面，
            // 而且会过期。以前它们直接顶替徽章，于是「状态」那一格里写着一句描述，
            // 真实状态（待评审 / 无人可评）反倒看不见了。
            var fresh = assignment is not null && now - assignment.At < AssignNoteLifetime;
            var note = settled || !fresh
                ? null
                : assignment switch
                {
                    { Accepted: null } a => new RowNote($"等 {PeerNameOf(a.PeerId)} 确认", BadgeTone.Info),
                    { Accepted: true } a => new RowNote($"{PeerNameOf(a.PeerId)} 已接受", BadgeTone.Ok),
                    { Accepted: false } a => new RowNote($"{PeerNameOf(a.PeerId)} 拒绝了", BadgeTone.Bad),
                    _ => null,
                };

            // 「送出去了还没回」要锁住这一行，别让人连点两次指派 —— 但只锁 AssignAckWindow
            // 那么久，之后要能改指派给别人。被拒绝<b>不锁</b>：那正是要人改指派的时刻。
            // 锁跟显示分开：note 过期消失了，锁该不该在只取决于时间窗。
            var blockedByAssignment = !settled
                && assignment is { Accepted: not false } a2
                && now - a2.At < AssignAckWindow;

            var row = new PrRow(
                view,
                rowCommands,
                self.Id,
                self.AzIdentity,
                peers,
                OrgUrl,
                Layout,
                pending,
                note,
                claimedLocally: myClaims.Contains(view.Revision.Id),
                selfOccupied: selfOccupied,
                blockedByAssignment: blockedByAssignment);

            // 自己的 PR 只进「我的 PR」那张表。留在队列里没有意义 ——
            // 本节点评不了它（硬规则），也认领不了，能做的只有指派给别人。
            if (row.IsMine)
            {
                mineRows.Add(row);
            }
            else
            {
                queueRows.Add(row);
            }
        }

        RowSync.Apply(Pipeline, queueRows, static r => r.RevisionId);
        RowSync.Apply(Mine, mineRows, static r => r.RevisionId);

        PipelineEmpty = Pipeline.Count == 0;
        MineEmpty = Mine.Count == 0;

        RenderNodes(self);
        RenderSelfActivity(self);
        RenderUpdate();

        RowSync.Apply(
            Pending,
            [.. _mesh.State.Pending
                .OrderBy(p => p.At)
                .Select(p => new PendingRow(
                    p, AcceptCommand, DeclineCommand, _responding.Contains(p.Id)))],
            static r => r.Id);

        HasPending = Pending.Count > 0;

        // 区块哈希天然唯一，而且链是 append-only —— 稳态下这张表一个事件都不会发。
        RowSync.Apply(
            Blocks,
            [.. _state.RecentBlocks.Select(b => new BlockRow(b))],
            static r => r.Hash);

        BlocksEmpty = Blocks.Count == 0;

        // 通知没有 id，用「发生时刻 + 标题」当身份：NodeState.Notify 已经把一分钟内
        // 完全相同的一条挡掉了，所以这个组合在列表里是唯一的。
        RowSync.Apply(
            Notices,
            [.. _state.Notices.Select(n => new NoticeRow(n))],
            static r => r.Key);

        NoticesEmpty = Notices.Count == 0;

        // 人正停在通知页上时就地清未读 —— 这一页上的东西已经看见了，tab 上还顶着
        // 未读数会让人以为别处还有没看的。清未读会再发一次 Changed、多刷一轮，
        // 但只在开着这一页且刚来了新通知时才会发生，代价可忽略。
        if (Tab == MainTab.Notices)
        {
            _state.MarkNoticesRead();
        }

        RenderTabTitles();
        SyncLiveTimer();

        // 账本读取是异步的，而 Refresh 由状态变化同步驱动 —— 不阻塞 UI 线程，
        // 失败也只记日志：账单面板刷不出来不该影响评审本身。
        _ = LoadLedgerAsync();
    }

    /// <summary>
    /// 当前这一页有没有随时间自己会变的内容，据此启停 <see cref="_liveTimer"/>。
    /// </summary>
    /// <remarks>
    /// 按<b>当前页</b>判而不是按「后台有没有事在发生」：看不见的东西不必重画。
    /// 队列页的判据用「有没有行真的显示了时长」而不是「有没有人在评」—— 那一列的内容是
    /// <see cref="PrRow.Elapsed"/> 算出来的，直接问它就不会出现「逻辑上该转但那一列
    /// 其实是空的」这种白转。
    /// </remarks>
    private void SyncLiveTimer()
    {
        var needed = _visible && Tab switch
        {
            // 节点页每行都显示「上次心跳多久前」，恒需要。
            MainTab.Nodes => true,
            // 除了「评了多久」，还有一类：指派那句话到点要自己消失，而消失也得有人来重画。
            MainTab.Queue => Pipeline.Any(r => r.Elapsed.Length > 0 || r.HasNote),
            MainTab.Mine => Mine.Any(r => r.Elapsed.Length > 0 || r.HasNote),
            _ => false,
        };

        if (needed == _liveTimer.IsEnabled)
        {
            return;
        }

        if (needed)
        {
            _liveTimer.Start();
        }
        else
        {
            _liveTimer.Stop();
        }
    }

    /// <summary>
    /// 只重画当前页上那些随时间变化的内容。
    /// </summary>
    /// <remarks>
    /// 节点页能精确到只重建那一张表；队列页不行 —— <see cref="PrRow.Elapsed"/> 是构造时
    /// 算好的字符串，要它变只能重建行。真要做到「只改那一格」得把 PrRow 改成可观察对象，
    /// 那是另一档改动。
    /// </remarks>
    private void RenderLive()
    {
        if (Tab == MainTab.Nodes)
        {
            RenderNodes(_mesh.Self);
            return;
        }

        Refresh();
    }

    /// <summary>
    /// 顶栏那枚「单机 / 组网」胶囊。
    /// </summary>
    /// <remarks>
    /// 台数含本机，跟「在线节点」那个 tab 的分母一致 —— 同一件事在两处显示成两个数
    /// 只会让人怀疑哪个是真的。白名单关掉（TrustAllElectors）是个联调开关，
    /// 它决定「谁能让你的机器起 claude 跑 Bash」，所以在 tooltip 里点名。
    /// </remarks>
    private void RenderMeshMode(DateTimeOffset now)
    {
        var mesh = _options.Mesh;

        if (!mesh.Enabled)
        {
            MeshIsNetworked = false;
            MeshModeText = "单机模式";
            MeshModeTip = "mesh 关着：不发也不收心跳，队列与席位只有本机。"
                + "要组网就把 Conclave:Mesh:Enabled 设为 true 后重启";
            return;
        }

        MeshIsNetworked = true;

        var alive = _mesh.Members.Count(m => m.IsAlive(now));

        MeshModeText = alive > 1
            ? string.Create(CultureInfo.InvariantCulture, $"组网 {alive} 台")
            : "组网（就本机）";

        MeshModeTip = string.Create(
            CultureInfo.InvariantCulture,
            $"心跳 {mesh.MulticastAddress}:{mesh.BeaconPort}（UDP 组播）· 载荷 HTTP :{mesh.HttpPort}")
            + (alive > 1
                ? "\n席位在这些节点间分配，每个节点同时只评一个 PR"
                : "\n还没收到别的节点：对面没开、不在同一个二层网段、或者指纹没互相写进 electors.allow")
            + (mesh.TrustAllElectors
                ? "\n⚠️ 白名单已关闭（TrustAllElectors）：任何能验签的节点都能把评审任务派到本机"
                : string.Empty);
    }

    /// <summary>
    /// 顶栏那枚状态胶囊：本节点此刻在评什么。
    /// </summary>
    /// <remarks>
    /// 一个节点一次只评一个 PR，所以这里最多只有一条。它是顶栏上唯一会动的东西 ——
    /// 「这台机器现在到底在干活还是闲着」是开着面板时最常问的一句。
    /// </remarks>
    private void RenderSelfActivity(Elector self)
    {
        var mine = _state.Pipeline.FirstOrDefault(v => v.ReviewingBy == self.Id);

        // 忙不忙看 NodeState.Reviewing（开跑那一刻就置位，跟菜单栏图标同源）；
        // 评的是哪个 PR 才看投影 —— 它最多迟一轮，那十几秒里先只说「评审中」
        SelfIsBusy = _state.Reviewing || mine is not null;
        SelfActivityText = !SelfIsBusy
            ? "空闲"
            : mine is null
                ? "评审中"
                : string.Format(CultureInfo.InvariantCulture, "评审中 #{0}", mine.Pr.PrId);
        SelfActivityTip = mine is null
            ? "本节点当前没有在评的 PR，下一轮编排会按队列现状取席位"
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0}/{1} · {2}\n一个节点一次只评一个 PR，评完才会接下一个",
                mine.Pr.Repo, mine.Pr.PrId, mine.Pr.Title);
    }

    /// <summary>
    /// 重建「在线节点」表。
    /// </summary>
    /// <remarks>
    /// 存活判定要一个「现在」，而它必须整张表共用一个值：逐行各取一次 <c>UtcNow</c> 时，
    /// 心跳正好落在 90 秒边界上的节点会在同一次刷新里一半算在线一半算离线。
    /// </remarks>
    private void RenderNodes(Elector self)
    {
        var now = DateTimeOffset.UtcNow;

        // 谁在评什么，来自实时状态投影出的队列，而不是各节点自报 ——
        // 后者拿不到「这台机器在评别的 project」这种信息。
        var reviewing = _state.Pipeline
            .Where(v => v.ReviewingBy is not null)
            .GroupBy(v => v.ReviewingBy!)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(v => v.Revision.Id)]);

        RowSync.Apply(
            Nodes,
            [.. _mesh.Members
                .OrderByDescending(m => m.Id == self.Id)
                .ThenByDescending(m => m.IsAlive(now))
                .ThenBy(m => m.AzIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(m => new NodeRow(
                    m,
                    m.Id == self.Id,
                    now,
                    reviewing.TryGetValue(m.Id, out var ids) ? ids : [],
                    Layout))],
            static r => r.Id);

        NodesEmpty = Nodes.Count == 0;

        RenderMeshMode(now);
    }

    /// <summary>
    /// tab 上的条数。
    /// </summary>
    /// <remarks>
    /// 在线节点那格给的是「在线/总数」：只报总数时，一台掉线的机器会一直算在里面，
    /// 而 mesh 从三台缩到一台恰恰是最该被看见的事。
    /// 通知那格给的是未读数而不是 <see cref="Notices"/> 的长度 —— 那个数只会一直涨。
    /// </remarks>
    private void RenderTabTitles()
    {
        var now = DateTimeOffset.UtcNow;
        var alive = _mesh.Members.Count(m => m.IsAlive(now));

        QueueCount = Count(Pipeline.Count);
        MineCount = Count(Mine.Count);
        ReviewsCount = Count(Reviews.Count);
        ActaCount = Count(Blocks.Count);
        NoticesCount = Count(_state.UnreadNotices);

        NodesCount = Nodes.Count == 0
            ? string.Empty
            : string.Format(CultureInfo.InvariantCulture, "{0}/{1}", alive, Nodes.Count);
        NodesAllAlive = alive == Nodes.Count;

        // 0 不显示：每个 tab 后面挂一个「0」只是噪音，而「有没有」本来就靠它有没有出现来读。
        static string Count(int n) => n == 0
            ? string.Empty
            : n.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 拼 PR 链接用的组织地址。
    /// </summary>
    /// <remarks>
    /// 优先用后台轮询读到的那个（配置留空时它来自 <c>az devops configure</c>），
    /// 还没轮询过时退回配置值 —— 否则刚启动那几秒里 PR 号是点不动的。
    /// </remarks>
    private string OrgUrl => _state.OrgUrl.Length > 0
        ? _state.OrgUrl
        : _options.AzureDevOpsOrgUrl;

    /// <summary>切到某张表；RadioButton 置 false 时什么都不做。</summary>
    private void Select(MainTab tab, bool selected)
    {
        if (!selected)
        {
            return;
        }

        Tab = tab;

        // 换了页，「这一页有没有随时间变化的内容」也就变了 —— 从节点页切走要停掉定时器，
        // 切过去要开起来。不在这里重算的话，它会一直保持上一页的判断。
        SyncLiveTimer();

        // 点进通知页当场清角标，不等下一轮刷新 —— 切过去还顶着「通知 3」
        // 会让人以为这一页没显示全。
        if (tab == MainTab.Notices)
        {
            _state.MarkNoticesRead();
        }
    }

    /// <summary>
    /// 指纹 → 人读的名字。
    /// </summary>
    /// <remarks>
    /// 对端已经掉线时成员表里查不到它，此时退回指纹前 8 位 —— 指派答复要能显示，
    /// 哪怕对方答完就下线了。
    /// </remarks>
    private string PeerNameOf(string electorId)
    {
        var peer = _mesh.Members.FirstOrDefault(m => m.Id == electorId);
        return peer is not null
            ? PeerName(peer)
            : electorId.Length > 8 ? electorId[..8] : electorId;
    }

    /// <summary>在顶栏下面弹一条几秒后自动消失的提示。</summary>
    private void ShowToast(string text, BadgeTone tone = BadgeTone.Ok)
    {
        ToastText = text;
        ToastTone = tone;

        // 先停再起：连续点两下时重新计时，而不是让第一条的计时把第二条提前清掉
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>
    /// 记一笔「刚点过」，让那一行立刻变样，不用等编排循环转一圈。
    /// </summary>
    /// <param name="revisionId">哪一行。</param>
    /// <param name="text">徽章上改显示什么。</param>
    /// <param name="tip">这一行的动作因此灰掉时，tooltip 里怎么解释。</param>
    private void MarkJustDone(string revisionId, string text, string tip)
    {
        _justDone[revisionId] = (text, tip, DateTimeOffset.UtcNow);
        Refresh();
    }

    /// <summary>编排循环最多多久转一圈，用在「再点也不会更快」那句解释里。</summary>
    private string TickWait => _options.OrchestratorInterval.TotalSeconds
        .ToString("F0", CultureInfo.InvariantCulture);

    private void RenderUpdate()
    {
        var release = _state.UpdateAvailable;
        var busy = _state.UpdateInProgress;

        // 「想自动装」和「这台机器装得了」是两件事：dotnet run 起的开发进程不在 .app 里，
        // 没有可替换的目标 —— 那时候要说清楚，不能显示成「即将自动更新」然后什么都不发生。
        var wants = _options.Update.AutoInstall;
        var auto = wants && _installer.CanInstall;

        HasUpdate = release is not null;
        UpdateText = release is null
            ? string.Empty
            : $"有新版本 v{release.Version}（本机 v{AppInfo.Version}）";
        CanUpdate = release is not null && !busy && !wants && _installer.CanInstall;

        // 安装器一动起来就用它的进度；还没动的时候说清楚「接下来会自己发生什么」。
        // 这条横幅是<b>通告</b>：三种措辞都不能出现「请你去下载」——
        // 自动更新的全部意义就是不必有人去做这件事。
        UpdateStatus = _state.UpdateStatus.Length > 0
            ? _state.UpdateStatus
            : auto
                ? "会自动下载校验，等本节点空闲后替换并重启，不用管"
                : wants
                    ? "开发态构建：进程不在 Conclave.app 里，不做就地替换"
                    : "自动更新已关掉（Conclave:Update:AutoInstall）";

        UpdateTip = busy
            ? "正在更新，别打断它"
            : string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"下载（约 {(release?.Bytes ?? 0) / 1_048_576.0:F0} MB）、校验、替换 Conclave.app 并重启。旧版留一份 .previous，新版起不来时能换回去");
    }

    /// <summary>
    /// 装更新。进度与结果都走 <see cref="NodeState"/>，这里只负责别让异常漏出去。
    /// </summary>
    /// <remarks>
    /// 正常完成时进程会被 launchd 重启，所以 await 之后基本不会再执行到什么。
    /// </remarks>
    [RelayCommand]
    private async Task UpdateAsync()
    {
        if (_state.UpdateAvailable is not { } release)
        {
            return;
        }

        try
        {
            await _installer.InstallAsync(release, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 安装器已经把失败写进状态和通知了；这里只是别让它成为未观察的异常。
            _logger.LogError(ex, "更新到 v{Version} 失败", release.Version);
            Status = "更新失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 打开某一行的评审日志。
    /// </summary>
    /// <remarks>
    /// 本节点在评就直读本地缓冲，别的节点在评就走 <c>GET /log</c> 现问 ——
    /// 这个分流在 <see cref="ReviewLogViewModel"/> 里，这里只管把面板换成它。
    /// </remarks>
    [RelayCommand]
    private void ShowLog(PrRow? row)
    {
        if (row is null)
        {
            return;
        }

        Log?.Dispose();
        Log = new ReviewLogViewModel(
            _mesh, _progress, row.RevisionId, row.ReviewerId, row.Subject);
    }

    [RelayCommand]
    private void CloseLog()
    {
        Log?.Dispose();
        Log = null;

        // 关掉日志就把表格放回来 —— 留一个「收起来了的空面板」没有意义。
        TablesCollapsed = false;
    }

    [RelayCommand]
    private void ToggleTables() => TablesCollapsed = !TablesCollapsed;

    /// <summary>清空通知收件箱。</summary>
    /// <remarks>
    /// 只清这份内存列表 —— 每条通知背后的事实都还在 Acta 或评审记录里查得到，
    /// 所以这个按钮不需要二次确认。
    /// </remarks>
    [RelayCommand]
    private void ClearNotices()
    {
        _state.ClearNotices();
        ShowToast("通知已清空");
    }

    /// <summary>手动开跑一个 Revision 的评审，绕过 <see cref="ConclaveOptions.AutoReview"/>。</summary>
    [RelayCommand]
    private void Review(PrRow? row)
    {
        if (row is null)
        {
            return;
        }

        _orchestrator.RequestReview(row.RevisionId);

        var wait = TickWait;
        Status = $"已排入评审 {row.RevisionId}，最多 {wait} 秒后开跑";
        ShowToast($"已排入评审 {row.RevisionId} —— 最多 {wait} 秒后开跑", BadgeTone.Gold);

        // 这里原先没有 Refresh，也没有任何行内变化：点完之后那 15 秒里界面一动不动。
        // 现在这一笔同时把行上的动作灰掉 —— toast 三秒就没了，灰按钮会一直挂到后台跟上。
        MarkJustDone(
            row.RevisionId,
            "已排队",
            $"已经排进本节点的评审队列，编排循环最多 {wait} 秒转一圈 —— 这期间再点不会更快");
    }

    /// <summary>
    /// 主动认领一个 PR：声明由本节点来评，优先于加权 HRW 的自动分配。
    /// </summary>
    /// <remarks>
    /// 认领本身就是「明确要评」的信号，所以不必再打开自动评审 —— 下一轮编排就会开跑。
    /// </remarks>
    [RelayCommand]
    private void Claim(PrRow? row)
    {
        if (row is null)
        {
            return;
        }

        _orchestrator.Claim(row.RevisionId);

        var wait = TickWait;
        Status = $"已认领 {row.RevisionId}，最多 {wait} 秒后开跑";
        ShowToast($"已认领 {row.RevisionId} —— 最多 {wait} 秒后开跑", BadgeTone.Gold);

        // 认领本身立刻生效，所以这一笔之后行上换的是「认领 → 撤销」，不是一个灰按钮
        MarkJustDone(
            row.RevisionId,
            "已认领",
            $"本节点已经认领，编排循环最多 {wait} 秒后开跑；想反悔就点「撤销」");
    }

    [RelayCommand]
    private void Release(PrRow? row)
    {
        if (row is null)
        {
            return;
        }

        _orchestrator.Release(row.RevisionId);
        Status = $"已撤销对 {row.RevisionId} 的认领，交回 mesh 自动分配";
        ShowToast($"已撤销认领 {row.RevisionId}，交回 mesh 自动分配");

        // 撤销是「取消刚才那一下」，所以乐观标记要一起撤掉
        _ = _justDone.Remove(row.RevisionId);
        Refresh();
    }

    /// <summary>
    /// 把一个 PR 指派给某个节点。对方同意才生效。
    /// </summary>
    /// <remarks>
    /// 主要用途是绕开「不评审自己的 PR」那条硬规则的死角：作者自己的 PR 没有任何合格节点，
    /// 席位表为空、永远没人评，指派给别人是它唯一的出路。
    /// </remarks>
    [RelayCommand]
    private async Task AssignAsync(AssignTarget? target)
    {
        if (target is null)
        {
            return;
        }

        // 这一条从界面到网络中间隔了好几层，出问题时（对方拒收、绑定没接上）现场
        // 什么都不剩 —— 所以命令一进来就记一行，日志里能直接答「那一下到底发出去了没有」
        _logger.LogInformation(
            "界面请求指派 {Revision} → {Elector}", target.RevisionId, target.ElectorId);

        // 这一条要发 HTTP，最长等 Mesh.RequestTimeout（默认 10 秒），而按钮在 Flyout 里、
        // 点完浮层就关了 —— 不先说一句「正在请求」，那 10 秒是完全的黑屏
        ShowToast($"正在请求 {target.Label} 评审 {target.RevisionId}…", BadgeTone.Gold);
        MarkJustDone(target.RevisionId, "指派中", $"正在把请求发给 {target.Label}，等它送达");
        // 送达之后行上的文字由 NodeState 里那条记录接管（「等 X 确认」→「X 已接受/拒绝了」）

        var sent = await _orchestrator
            .AssignAsync(target.RevisionId, target.ElectorId, null, CancellationToken.None)
            .ConfigureAwait(true);

        // 「送到了」不等于「对方同意了」—— 措辞上必须分清，否则人会以为已经安排好了。
        // 送不到有两种：连不上（离线），和对方回了 403（它把这条丢了 —— 最常见的是
        // electors.allow 只配了单向）。两种的处置完全不同，所以都点出来。
        Status = sent
            ? $"已请求 {target.Label} 评审 {target.RevisionId}，等对方确认"
            : $"请求没送到 {target.Label}：对方离线，或它拒收了（看它的日志：不在白名单 / 验签不过）";
        ShowToast(Status, sent ? BadgeTone.Gold : BadgeTone.Bad);

        // 送达了就交给记录去显示；送不到就把「指派中」撤掉，别留一个假的进行中状态
        if (!sent)
        {
            _ = _justDone.Remove(target.RevisionId);
        }

        Refresh();
    }

    [RelayCommand]
    private Task AcceptAsync(PendingRow? row) => RespondAsync(row, accepted: true);

    [RelayCommand]
    private Task DeclineAsync(PendingRow? row) => RespondAsync(row, accepted: false);

    /// <summary>
    /// 答复一条指派请求。
    /// </summary>
    /// <remarks>
    /// 「同意」「拒绝」共用一条路，是因为它们要共用同一把锁：答复要发 HTTP，这期间
    /// 两个按钮都得灰掉（见 <see cref="_responding"/>）。分成两个方法各锁各的话，
    /// 同意完还能点拒绝。
    /// </remarks>
    private async Task RespondAsync(PendingRow? row, bool accepted)
    {
        // 已经在答这一条了 —— 第二下直接丢掉，不再发一次请求
        if (row is null || !_responding.Add(row.Id))
        {
            return;
        }

        var what = accepted ? "接受" : "拒绝";

        // 先刷一次把两个按钮灰掉，再去发请求
        Refresh();
        try
        {
            await _orchestrator
                .RespondToAssignmentAsync(row.Id, accepted, accepted ? null : "手动拒绝", CancellationToken.None)
                .ConfigureAwait(true);

            Status = $"已{what} {row.FromShort} 的指派：{row.RevisionId}";
            ShowToast(Status);

            if (accepted)
            {
                MarkJustDone(row.RevisionId, "已接受", $"已经接受，编排循环最多 {TickWait} 秒后开跑");
            }
        }
        catch (Exception ex)
        {
            // 原先这里什么也没有：答复失败时那句成功的 toast 根本不会弹，
            // 横幅也还在 —— 看起来就是「点了没反应」。
            _logger.LogError(ex, "答复指派 {Request} 失败", row.Id);
            Status = $"{what}失败：{ex.Message}";
            ShowToast($"{what} {row.FromShort} 的指派失败：{ex.Message}", BadgeTone.Bad);
        }
        finally
        {
            // 失败时按钮要能再点一次；成功时这条横幅本来就没了
            _ = _responding.Remove(row.Id);
            Refresh();
        }
    }

    /// <summary>
    /// 指派菜单里怎么称呼一个节点。
    /// </summary>
    /// <remarks>
    /// 优先用 <c>az</c> 身份 —— 人记得住「edisonwei」，记不住 16 位指纹。
    /// 额度也带上：指派给一台快用满额度的机器，它会拒绝或者干脆不合格。
    /// </remarks>
    private string PeerLabel(Elector peer) => string.Format(
        CultureInfo.InvariantCulture,
        "{0}（额度 {1}）",
        PeerName(peer),
        Format.Percent(peer.Utilization));

    /// <summary>
    /// 节点的人读名字。
    /// </summary>
    /// <remarks>
    /// az 身份取不到时退回指纹前 8 位；跟别的节点撞了名（同机多节点联调）时
    /// 在后面补指纹前 4 位，否则菜单里两行长得一模一样，见 <see cref="_ambiguousNames"/>。
    /// </remarks>
    private string PeerName(Elector peer)
    {
        var name = AzName(peer);
        return _ambiguousNames.Contains(name)
            ? $"{name}·{peer.Id[..Math.Min(4, peer.Id.Length)]}"
            : name;
    }

    /// <summary>az 身份的短名；取不到时退回指纹前 8 位。</summary>
    private static string AzName(Elector peer)
        => string.IsNullOrWhiteSpace(peer.AzIdentity)
            ? peer.Id[..Math.Min(8, peer.Id.Length)]
            : Labels.ShortAccount(peer.AzIdentity);

    partial void OnAutoReviewChanged(bool value)
    {
        _options.AutoReview = value;
        Status = value
            ? "自动评审已开启：新发现的 PR 会自动开跑"
            : "自动评审已关闭：只有手动点「评审」才会跑";
    }

    partial void OnPostToAzureDevOpsChanged(bool value)
    {
        _options.PostToAzureDevOps = value;
        Status = value
            ? "投递已开启：公布结论时会往真实 PR 发评论并投票"
            : "投递已关闭：结论只写进本地 Acta";
    }

}

/// <summary>主窗口下半区六张平级的表。</summary>
public enum MainTab
{
    /// <summary>mesh 里所有活跃的 PR 版本。</summary>
    Queue = 0,

    /// <summary>其中作者是本机的那些。</summary>
    Mine,

    /// <summary>已完成评审的账本投影。</summary>
    Reviews,

    /// <summary>签名哈希链本体。</summary>
    Acta,

    /// <summary>mesh 成员与它们的席位权重。</summary>
    Nodes,

    /// <summary>本机看到的事件收件箱。</summary>
    Notices,
}

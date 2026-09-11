using System.Globalization;
using System.Windows.Input;
using Conclave.Application;
using Conclave.Domain;

namespace Conclave.App.ViewModels;

/// <summary>队列里可以把某个 PR 指派给谁。</summary>
/// <param name="RevisionId">要指派的 PR 版本。</param>
/// <param name="ElectorId">目标节点指纹。</param>
/// <param name="Label">菜单里显示的名字。</param>
/// <param name="Command">点它时执行的指派命令。</param>
/// <remarks>
/// <para>
/// 把「哪一行 + 指派给谁」打包成一个对象，是因为 Avalonia 的 <c>CommandParameter</c>
/// 只能带一个值 —— 拆成两个绑定就得在 code-behind 里拼，那更难读。
/// </para>
/// <para>
/// 命令也挂在这上面，而不是让菜单项去找外层的 <see cref="PrRow"/>：菜单在
/// <c>MenuFlyout</c> 里再嵌一层子菜单之后，<c>$parent[…]</c> 那种向上遍历的绑定
/// 既难写又容易在改布局时静默失效（绑不上不会报错，只是按钮点了没反应）。
/// </para>
/// </remarks>
public sealed record AssignTarget(
    string RevisionId, string ElectorId, string Label, ICommand Command);

/// <summary>mesh 里的一个对端节点，界面上怎么称呼它。</summary>
/// <param name="Node">
/// 节点本身。
/// <para>
/// 带着整个 <see cref="Elector"/> 而不只是几个字符串，是为了让「能不能指派给它」
/// 直接问 <see cref="SeatAssignment.Eligible"/> —— 那是领域层判定合格节点的<b>同一个</b>
/// 谓词。菜单要是自己另算一套，就会出现「菜单里能选、选了对方立刻拒绝」这种
/// 自相矛盾（实际踩到的形状：同一个 az 身份的第二台机器被列了出来，
/// 而作者不能评自己的 PR）。
/// </para>
/// </param>
/// <param name="Name">人读的名字：az 身份短名，取不到时退回指纹前 8 位。</param>
/// <param name="Label">指派菜单里的说法，名字后面还带着对方的额度。</param>
/// <param name="IsIdle">
/// 当前空闲：在线、手上没有在评的 PR、额度也没满。
/// <para>
/// 只有空闲的节点能被指派 —— 一个节点一次只评一个 PR，指给正在忙的那台只是让它排队，
/// 而请求方并不知道要等多久。
/// </para>
/// </param>
/// <remarks>
/// 两个称呼分开，是因为它们回答的是两个问题：菜单要「指派给谁划算」，所以带额度；
/// 而「我的 PR 被谁评了」只要一个名字 —— 那一格里塞进 <c>（额度 12%）</c> 反而读不出重点。
/// </remarks>
public sealed record PeerInfo(Elector Node, string Name, string Label, bool IsIdle = true)
{
    /// <summary>公钥指纹全长。</summary>
    public string Id => Node.Id;
}

/// <summary>队列一行上的四个动作。</summary>
/// <remarks>
/// 打成一包而不是四个构造参数：<see cref="PrRow"/> 每轮刷新都要新建一批，
/// 参数列表越长越容易在加动作时把顺序弄错，而那种错编译器不会拦。
/// </remarks>
public sealed record PrRowCommands(
    ICommand Review,
    ICommand Claim,
    ICommand Release,
    ICommand Assign,
    ICommand ShowLog);

/// <summary>一行上「刚点过、后台还没反映出来」的临时状态。</summary>
/// <param name="Text">盖在阶段徽章上的字，例如「已排队」「等 edison 确认」。</param>
/// <param name="Tip">动作因此灰掉时，tooltip 里的那句解释。</param>
/// <param name="Blocking">
/// 这一行会改状态的动作要不要跟着一起灰掉。
/// <para>
/// 默认要：编排循环最长 15 秒才转一圈，按钮在这段时间里还亮着的话，人只会以为没点上，
/// 于是再点一次，再看一条一模一样的 toast。唯一的例外是「对方拒绝了」——
/// 那正是要人改指派给别人的时刻，锁住反而没有出路。
/// </para>
/// </param>
public sealed record RowPending(string Text, string Tip, bool Blocking = true);

/// <summary>
/// 行上的一句话，说的是<b>刚发生了什么</b>，不是它现在处于什么状态。
/// </summary>
/// <remarks>
/// <para>
/// 「guangliangli·9db5 拒绝了」这类文字以前直接顶替 <see cref="PrRow.Stage"/> 显示 ——
/// 于是那一格明明写着「状态」，里面却是一条事件描述，真实状态（待评审 / 无人可评）
/// 反而被盖住看不见。
/// </para>
/// <para>
/// 拆出来之后：徽章永远是状态，这句话挂在它下面，而且<b>会过期消失</b>。
/// 过期不丢信息 —— 指派的接受/拒绝同时进了通知页，那里是永久的。
/// </para>
/// </remarks>
/// <param name="Text">一句话。</param>
/// <param name="Tone">语气，决定染什么色。</param>
public sealed record RowNote(string Text, BadgeTone Tone);

/// <summary>
/// PR 队列里的一行。所有字段都预格式化成字符串或徽章，让 XAML 里不必写转换器。
/// </summary>
public sealed class PrRow
{
    public PrRow(
        PrView view,
        PrRowCommands commands,
        string selfId,
        string selfAzIdentity,
        IReadOnlyList<PeerInfo> peers,
        string? azureDevOpsOrgUrl = null,
        TableLayout? layout = null,
        RowPending? pending = null,
        RowNote? note = null,
        bool claimedLocally = false,
        bool selfOccupied = false,
        bool blockedByAssignment = false)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(peers);

        Layout = layout ?? new TableLayout();
        View = view;
        ReviewCommand = commands.Review;
        ClaimCommand = commands.Claim;
        ReleaseCommand = commands.Release;
        AssignCommand = commands.Assign;
        ShowLogCommand = commands.ShowLog;

        RevisionId = view.Revision.Id;
        PrNumber = "#" + view.Pr.PrId.ToString(CultureInfo.InvariantCulture);

        // 点 PR 号在浏览器里打开它。拼不出地址时不隐藏这个数字，只是点了没反应 ——
        // 所以 tooltip 要说清为什么，否则会以为是界面坏了。
        Url = PrLink.For(view.Pr, azureDevOpsOrgUrl);
        UrlTip = Url ?? "拿不到 PR 网页地址：这一版的快照里没有 remoteUrl，也没配 Conclave:AzureDevOpsOrgUrl";
        Commit = view.Revision.ShortCommit;
        Repo = view.Pr.Repo;
        Project = view.Pr.Project;
        Title = view.Pr.Title;
        Author = Labels.ShortAccount(view.Pr.Author);
        AuthorFull = view.Pr.Author;
        Branches = $"{view.Pr.SourceBranch} → {view.Pr.TargetBranch}";

        // 描述列的第二行。仓库和作者已经各有一列，这里只补分支 —— PR 是往哪个分支合的
        // 平时不用看，但 hotfix 合错目标分支这种事只有这一行拦得住。
        Subtitle = Branches;

        // 跟「不评审自己的 PR」那条硬规则用同一个判定（AzIdentity.SamePerson），
        // 否则会出现「我的 PR」标签页里空着、而队列里那一行明明是我提的。
        IsMine = AzIdentity.SamePerson(selfAzIdentity, view.Pr.Author);

        // pending 是「刚点过、后台还没反映出来」的乐观状态：编排循环最长 15 秒才转一圈，
        // 那 15 秒里行上一点变化都没有，人会以为没点上然后再点一次。
        // pending 可以顶替徽章 —— 「已认领」本来就是这一行此刻的状态，只是后台还没确认。
        // 指派的接受/拒绝走的是 note：那些是事件不是状态，不该盖住徽章。
        Stage = pending is null ? Badge.Stage(view.Stage) : new Badge(pending.Text, BadgeTone.Gold);

        Note = note?.Text ?? string.Empty;
        HasNote = Note.Length > 0;
        NoteIsOk = note?.Tone == BadgeTone.Ok;
        NoteIsWarn = note?.Tone == BadgeTone.Warn;
        NoteIsBad = note?.Tone == BadgeTone.Bad;
        Outcome = Badge.Outcome(view.Decision, view.Findings);

        IsSeated = view.MySeat >= 0;
        var reviewingByMe = view.ReviewingBy == selfId;

        // 认领在本地是<b>立刻生效</b>的（直接写进 mesh 状态），慢的只是队列投影。
        // 所以这里不只看 view.ClaimedBy：不然点完「认领」之后，「撤销」得等下一轮编排
        // 才出得来 —— 而想反悔恰恰就在那十几秒里。
        var claimedByMe = view.ClaimedBy == selfId || claimedLocally;

        // 「谁在处理」只补徽章没说的那部分。徽章文案已经带了动作（评审中 / 已认领 /
        // 无人可评 / 待评审），这里再写一句完整的话就成了同一格里同样的词出现两遍 ——
        // 「无人可评」上下叠着两行「无人可评」，实测就是这么难看。所以：
        //   · 徽章已经点名「（本节点）」的，这里留空
        //   · 别的节点在处理，这里给它的名字（徽章只说「评审中」，不说是谁）
        //   · 本节点持席但还没开跑，这里说「待跑」—— 徽章的「待评审」看不出席位归谁
        Handler = view switch
        {
            { ReviewingBy: not null } => reviewingByMe
                ? string.Empty
                : Name(view.ReviewingBy, peers),
            { ClaimedBy: not null } => claimedByMe ? "本节点" : Name(view.ClaimedBy, peers),
            _ when view.Decision is not null || view.NobodyEligible => string.Empty,
            _ when IsSeated => "席位归本节点",
            _ => string.Empty,
        };

        HandlerIsSelf = reviewingByMe || claimedByMe || (view.ReviewingBy is null && IsSeated);

        // 队列分组：本节点手上的活 vs 别的节点手上的活 vs 谁都还没接。
        // 只给「真的有人在处理」的行上底色 —— 待评审与无人可评是常态（单节点上自己的
        // PR 全是后者），给它们也上色等于整张表都在喊，反而看不出哪几行有人在动。
        IsHandledHere = reviewingByMe || claimedByMe;
        IsHandledElsewhere =
            (view.ReviewingBy is not null && !reviewingByMe)
            || (view.ClaimedBy is not null && !claimedByMe);

        Elapsed = view.StartedAt is { } started
            ? Format.Elapsed(DateTimeOffset.Now - started)
            : string.Empty;

        VoteProgress = Math.Clamp(view.BallotCount / (double)Math.Max(1, view.Quorum), 0, 1) * 100;
        ShowProgress = view.Decision is null && view.BallotCount > 0;

        StageTip = BuildTip(view, selfId);

        var open = view.Decision is null && !view.Finished();

        // ── 按钮的三种状态 ──────────────────────────────────────────────────
        // 不画（IsVisible=false）：这一行<b>根本没有</b>这个动作，人也没有办法让它有 ——
        //     结论已经出了、席位归别的节点、别人的 PR 不给指派。表格是拿来扫的，
        //     画一排永远点不了的灰按钮只是噪音。
        // 灰掉（IsEnabled=false）：动作在，只是<b>此刻</b>点不了 —— 刚点过还在等后台，
        //     或者暂时没有可指派的对象。灰按钮必须连着一句「为什么」（tooltip），
        //     否则跟界面坏了长得一样。
        // 可点：真的能点。
        //
        // 「刚点过就整行灰掉」是这套东西的重点：编排循环最长 15 秒才转一圈，这期间队列
        // 投影一动不动，而按钮还亮着 —— toast 弹过就消失了，于是人再点一次，再弹一条
        // 一模一样的。灰掉 + 徽章改字（见 Stage）才是「点上了，在办了」的说法。
        // 锁跟显示分开：指派那句话（note）会过期消失，但「送出去了还没回、别连点两次」
        // 这条锁该不该在只取决于时间窗，不该跟着文字一起没。
        var busy = pending is { Blocking: true } || blockedByAssignment;
        var busyTip = pending?.Tip
            ?? (blockedByAssignment ? "指派已经送出去了，等对方按同意或拒绝" : string.Empty);

        // 本节点手上已经有活（在评、或已认领某个 PR）——「一次只评一个 PR」是硬规则
        // （见 ConclaveOptions.MaxConcurrent），所以这时候<b>整张表</b>的「评审」「认领」
        // 都点不动：点了也只是排进本机队列，而队列里排着的活对别的节点是不可见的，
        // 等于把它从 mesh 里藏起来。灰掉而不是不画 —— 动作还在，只是要等这一个跑完。
        var occupiedTip = "本节点一次只评一个 PR，手上这个跑完才能接下一个";

        // 本节点有席位、还没结论时才能手动开跑。
        ShowReview = open && IsSeated && view.ReviewingBy is null;
        CanReview = ShowReview && !busy && !selfOccupied;
        ReviewTip = busy ? busyTip
            : selfOccupied ? occupiedTip
            : "绕过「自动评审」开关，把这一版排进本节点的评审队列";

        // 认领：还没人在评、也没人认领过，而且不是自己的 PR。
        // 自己的 PR 认领了也没用 —— QueueProjection.SeatFor 会拿硬规则挡掉（不评自己的 PR），
        // 摆一个点了没反应的按钮比不摆更糟。它的出路是「指派给别人」。
        // 已经认领过的也不画：那一格该出现的是「撤销」。
        ShowClaim = open
            && !IsMine
            && view.ReviewingBy is null
            && view.ClaimedBy is null
            && !claimedByMe;
        CanClaim = ShowClaim && !busy && !selfOccupied;
        ClaimTip = busy ? busyTip
            : selfOccupied ? occupiedTip
            : "由本节点来评，优先于自动分配。两个节点同时认领时先到者胜";

        // 只能撤自己的认领，而且开跑之后就撤不了了 —— 那时候要停得靠超时。
        // 它<b>不跟着 busy 一起灰</b>：撤销撤的就是刚点下去的那一下，本地立刻生效，
        // 而想反悔的人正是在这十几秒里。
        ShowRelease = open && claimedByMe && view.ReviewingBy is null;
        CanRelease = ShowRelease;
        ReleaseTip = "交回 mesh 自动分配。开跑之后就撤不了了";

        // 指派<b>只对自己的 PR</b> 开放。它存在的理由就是绕开「不评审自己的 PR」那条
        // 硬规则的死角：作者自己的 PR 没有任何合格节点，席位表为空、永远没人评，
        // 指派给别人是它唯一的出路。别人的 PR 有 HRW 自动分席位，替它挑评审者
        // 既没必要、也是在替 mesh 做决定。
        //
        // 候选人过两道闸：
        //   · SeatAssignment.Eligible —— 领域层判定合格节点的同一个谓词。指派绕开的是
        //     「席位怎么分」，不是「谁有资格评」，所以<b>同一个人不能收自己的 PR</b>
        //     （同一个 az 身份的第二台机器也是自己）、没有这个 project 权限的不能收、
        //     额度满的和离线的不能收。菜单自己另算一套的下场是「能选、选了对方立刻拒绝」。
        //   · IsIdle —— 比 Eligible 更严一档：一个节点一次只评一个 PR，
        //     指给忙着的那台只是排进它的队列，而请求方并不知道要等多久。
        var others = peers.Where(p => p.Id != selfId).ToList();
        var qualified = others
            .Where(p => SeatAssignment.Eligible(p.Node, view.Pr, DateTimeOffset.UtcNow, view.AllowSelfReview))
            .ToList();

        AssignTargets = [.. qualified
            .Where(p => p.IsIdle)
            .Select(p => new AssignTarget(RevisionId, p.Id, p.Label, commands.Assign))];

        // 按钮画不画，只看「这是不是一个能指派的 PR」；有没有对象可指是「能不能点」。
        // 没对象就整段消失的话，这个功能看起来就像不存在。
        ShowAssign = open && IsMine && view.ReviewingBy is null;

        // 灰掉时那句解释。四种处置完全不同，不能糊成一句「不能指派」：
        //   没有别的节点   → 去把 mesh 开起来 / 等同事开机
        //   都是同一个人   → 这台机器上换不出评审者，得找别人
        //   没有一个合格   → 去开 project 权限，或者等额度回落
        //   合格但都在忙   → 等一会儿
        var samePerson = others.Count > 0 && others.All(
            p => AzIdentity.SamePerson(p.Node.AzIdentity, view.Pr.Author));

        AssignHint = !ShowAssign
            ? string.Empty
            : busy
                ? busyTip
                : AssignTargets.Count > 0
                    ? string.Empty
                    : others.Count == 0
                        ? "mesh 里还没有别的节点"
                        : qualified.Count == 0
                            ? samePerson
                                ? $"别的节点都是同一个 az 身份（{view.Pr.Author}）—— 不能把自己的 PR 指派给自己"
                                : "别的节点都不合格：没有这个 project 的权限、额度用满，或者已经离线"
                            : "能评这个 PR 的节点都在忙，等它们评完再指派";

        HasAssignHint = AssignHint.Length > 0;

        CanAssign = ShowAssign && !busy && AssignTargets.Count > 0;
        AssignTip = HasAssignHint ? AssignHint : "把这一版交给别的节点评，对方同意才生效";

        // 有人在评就能问它要日志 —— 评审是十几分钟的黑盒，「卡住了」和「正常慢」
        // 光看一个计时器分不出来。日志在评审节点的内存里，现问现给。
        // 它是只读的，所以不跟着 busy 一起灰 —— 恰恰是这一行正在办事的时候最该点它。
        ReviewerId = view.ReviewingBy;
        CanShowLog = view.ReviewingBy is not null;

        HasActions = ShowReview || ShowClaim || ShowRelease || ShowAssign || CanShowLog;
    }

    /// <summary>三张表共用的「该显示几列」。窄了就少显示几列，见 <see cref="TableLayout"/>。</summary>
    public TableLayout Layout { get; }

    public PrView View { get; }

    public ICommand ReviewCommand { get; }

    public ICommand ClaimCommand { get; }

    public ICommand ReleaseCommand { get; }

    public ICommand AssignCommand { get; }

    /// <summary>在表格下面开出日志面板。</summary>
    public ICommand ShowLogCommand { get; }

    public string RevisionId { get; }

    /// <summary>PR 号，带井号 —— 人在 Azure DevOps 里就是按这个数认 PR 的。</summary>
    public string PrNumber { get; }

    /// <summary>PR 页面地址；拼不出来时为 <c>null</c>。</summary>
    public string? Url { get; }

    public bool HasUrl => Url is not null;

    /// <summary>PR 号那一格的 tooltip：地址本身，或者「为什么没有地址」。</summary>
    public string UrlTip { get; }

    /// <summary>
    /// 源 commit 前 8 位。
    /// </summary>
    /// <remarks>
    /// 跟 PR 号叠在同一格里显示：一个 PR 会随作者 push 出现多行，光看 PR 号会以为界面重复了。
    /// </remarks>
    public string Commit { get; }

    public string Repo { get; }

    public string Project { get; }

    public string Title { get; }

    /// <summary>去掉域前缀的作者名。</summary>
    public string Author { get; }

    public string AuthorFull { get; }

    public string Branches { get; }

    /// <summary>描述下面那行小字：源分支 → 目标分支。</summary>
    public string Subtitle { get; }

    /// <summary>这一行有没有可做的动作 —— 没有就不画那个「⋯」按钮。</summary>
    public bool HasActions { get; }

    /// <summary>PR 的作者就是本机的 az 身份 —— 「我的 PR」那张表按它筛。</summary>
    public bool IsMine { get; }

    public Badge Stage { get; }

    public Badge Outcome { get; }

    /// <summary>
    /// 徽章之外还需要补的那半句：处理它的节点名，或「席位归本节点」；没有就空着。
    /// </summary>
    /// <remarks>
    /// 刻意说「席位归本节点」而不是「本节点待跑」——「待跑」是个预测，而它只在
    /// 自动评审开着的时候才成立。关着的时候那一行永远不会自己动，文案却在暗示它会，
    /// 于是人会一直等一件不会发生的事（跟「无人可评」当初的问题是同一种）。
    /// 「席位归本节点」是当下就为真的事实，怎么开跑写在 tooltip 里。
    /// </remarks>
    public string Handler { get; }

    /// <summary>刚发生了什么（指派被接受/拒绝之类）。见 <see cref="RowNote"/>，会过期消失。</summary>
    public string Note { get; }

    public bool HasNote { get; }

    public bool NoteIsOk { get; }

    public bool NoteIsWarn { get; }

    public bool NoteIsBad { get; }

    /// <summary>本节点正在评它、或已经认领了它 —— 队列里高亮的那几行。</summary>
    public bool IsHandledHere { get; }

    /// <summary>mesh 里别的节点正在评它、或已经认领了它。</summary>
    public bool IsHandledElsewhere { get; }

    /// <summary>处理者就是本节点 —— 界面上高亮它，人一眼能挑出自己的活。</summary>
    public bool HandlerIsSelf { get; }

    /// <summary>已经评了多久；没在评时为空。</summary>
    public string Elapsed { get; }

    /// <summary>正在评它的节点指纹；没人在评时为 null。</summary>
    public string? ReviewerId { get; }

    /// <summary>能不能看这次评审的实时日志 —— 有人在评就能。</summary>
    public bool CanShowLog { get; }

    /// <summary>PR 的一句话身份，日志窗口的标题用它。</summary>
    public string Subject => $"{Repo}#{PrNumber[1..]} · {Title}";

    /// <summary>本节点在这个 Revision 上有席位。</summary>
    public bool IsSeated { get; }

    /// <summary>0–100，喂给阶段格下面那条细进度条。</summary>
    public double VoteProgress { get; }

    /// <summary>只有「已投票 n/m」这种收票中的行才显示进度条。</summary>
    public bool ShowProgress { get; }

    public string StageTip { get; }

    /// <summary>「评审」这个动作对这一行适用 —— 画不画这个按钮。</summary>
    public bool ShowReview { get; }

    /// <summary>此刻点得动 —— 按钮灰不灰。</summary>
    public bool CanReview { get; }

    /// <summary>「评审」按钮的 tooltip；灰着的时候说的是为什么灰。</summary>
    public string ReviewTip { get; }

    public bool ShowClaim { get; }

    public bool CanClaim { get; }

    public string ClaimTip { get; }

    public bool ShowRelease { get; }

    public bool CanRelease { get; }

    public string ReleaseTip { get; }

    public bool ShowAssign { get; }

    public bool CanAssign { get; }

    public string AssignTip { get; }

    public IReadOnlyList<AssignTarget> AssignTargets { get; }

    /// <summary>「指派」灰着的原因，菜单里那行灰字用它；不灰时为空。</summary>
    public string AssignHint { get; }

    public bool HasAssignHint { get; }

    /// <summary>指纹太长，界面上只显示前 8 位；完整值进 tooltip。</summary>
    private static string Short(string electorId)
        => electorId.Length > 8 ? electorId[..8] : electorId;

    /// <summary>
    /// 把节点指纹换成人读的名字。
    /// </summary>
    /// <remarks>
    /// 查不到就退回指纹前 8 位：对端刚离线、心跳还没被清掉时会出现这种情况，
    /// 此时显示指纹也比显示「未知」有用 —— 至少能跟别的节点对上。
    /// </remarks>
    private static string Name(string electorId, IReadOnlyList<PeerInfo> peers)
        => peers.FirstOrDefault(p => p.Id == electorId)?.Name ?? Short(electorId);

    private static string BuildTip(PrView view, string selfId)
    {
        var lines = new List<string>(5)
        {
            string.Format(
                CultureInfo.InvariantCulture,
                "quorum {0} · 已出 {1} 票",
                view.Quorum,
                view.BallotCount),
        };

        if (view.ReviewingBy is { } reviewer)
        {
            lines.Add(
                $"正在评：{reviewer}{(reviewer == selfId ? "（本节点）" : string.Empty)}"
                + (view.StartedAt is { } at ? $"，自 {at.ToLocalTime():HH:mm:ss}" : string.Empty));
        }
        else if (view.ClaimedBy is { } claimer)
        {
            lines.Add($"已认领：{claimer}{(claimer == selfId ? "（本节点）" : string.Empty)}");
        }
        else if (view.NobodyEligible)
        {
            // 这一条最需要解释：它跟「待评审」长得像，但性质完全不同。
            lines.Add("mesh 里没有任何节点有资格评它（最常见的原因是作者就是唯一的节点）。");
            lines.Add("出路是把它指派给别的节点。");
        }
        else
        {
            lines.Add(view.MySeat >= 0
                ? "席位归本节点：自动评审开着就会在下一轮开跑；关着则要手动点「评审」"
                : "席位归 mesh 里的其他节点");
        }

        lines.Add(view.Pr.FilesChanged > 0
            ? $"改动 {view.Pr.FilesChanged.ToString(CultureInfo.InvariantCulture)} 个文件"
            : "改动 未知");

        return string.Join('\n', lines);
    }
}

/// <summary>把 <see cref="PrView"/> 上「已有结论」这件事读成一个名字。</summary>
internal static class PrViewExtensions
{
    internal static bool Finished(this PrView view) => view.Decision is not null;
}

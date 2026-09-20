using System.IO.Enumeration;
using Conclave.Domain;

namespace Conclave.Application;

/// <summary>节点配置。</summary>
public sealed class ConclaveOptions
{
    /// <summary>私钥、账本、临时工作区的落地目录。</summary>
    public string HomeDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".conclave");

    public string ActaPath => Path.Combine(HomeDirectory, "acta.db");

    public string KeyPath => Path.Combine(HomeDirectory, "elector.key");

    /// <summary>
    /// 评审 skill 的落地目录，也就是传给 <c>claude --plugin-dir</c> 的那个路径。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 内容由 <c>ReviewSkillDeployer</c> 每次启动从安装包里铺出来，所以这里<b>不是</b>
    /// 人手工维护的目录 —— 手改会在下次启动被覆盖。
    /// </para>
    /// <para>
    /// 刻意不直接把 <c>--plugin-dir</c> 指向 app bundle 里那份：更新时
    /// <c>MacUpdateInstaller</c> 会整个换掉 <c>.app</c>，正在跑的评审会被抽走脚本；
    /// 而且 <c>dotnet run</c> 起的开发进程根本不在任何 bundle 里。铺到这儿两边都成立。
    /// </para>
    /// </remarks>
    public string SkillsDirectory => Path.Combine(HomeDirectory, "skills");

    /// <summary>
    /// 临时工作区的根目录。每次评审在它下面开一个子目录，评完即删。
    /// </summary>
    /// <remarks>
    /// 刻意放在 <see cref="HomeDirectory"/> 下而不是系统临时目录：一是评审的仓库可能很大，
    /// 放在跟账本同一个卷上便于估算占用；二是崩溃后留下的残留目录要能被下次启动扫到并清掉，
    /// 而系统临时目录里混着别人的东西，不敢整目录清。
    /// </remarks>
    public string WorkspaceRoot => Path.Combine(HomeDirectory, "work");

    /// <summary>评审日志的落地目录。一次评审一个文件，见 <see cref="ReviewLogArchive"/>。</summary>
    public string LogDirectory => Path.Combine(HomeDirectory, "logs");

    /// <summary>
    /// 落盘的评审日志在本地留多久。
    /// </summary>
    /// <remarks>
    /// 一天。日志是「出了事当场翻」的东西，而它里面有源码路径、命令行和模型的分析原文，
    /// 长期堆在磁盘上既占地方也没必要。真要长期追溯，每一票的会话 ID 是从
    /// <c>(revisionId, round)</c> 确定性派生的，<c>claude --resume &lt;那个 ID&gt;</c>
    /// 能翻出完整会话，比留一堆文本可靠。
    /// </remarks>
    public TimeSpan LogRetention { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// 轮询 Azure DevOps 的间隔。
    /// </summary>
    /// <remarks>
    /// 30 秒是「一轮发现现在只要几秒」之后才敢配的。原先逐 project 列举，34 个 project
    /// 串行调 <c>az</c>（每次约 1.3 秒）就是 40 多秒一轮 —— 那时候配 30 秒等于让发现循环
    /// 永远在跑。换成 collection 级一次列举之后稳态一轮约 5 秒，间隔才是真的间隔。
    /// <para>
    /// 想更快就点界面上的「立即轮询」：把间隔压到 10 秒以下没意义，
    /// 因为一轮本身就要几秒，而作者 push 到 ADO 反映出来也不是即时的。
    /// </para>
    /// </remarks>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>编排循环的间隔：认领席位、跑评审、公布结论。</summary>
    public TimeSpan OrchestratorInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>认领席位后多久没出 Ballot 就判定弃权，席位让给下一轮。</summary>
    public TimeSpan SeatingTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>单个 claude 子进程的墙钟上限。</summary>
    public TimeSpan ReviewTimeout { get; set; } = TimeSpan.FromMinutes(45);

    /// <summary>
    /// 评审跑着的时候，多久问一次 ADO「这个 PR 还活着吗」。
    /// </summary>
    /// <remarks>
    /// 一次评审能跑 45 分钟，期间 PR 被合或被撤是常事，继续跑就是白烧额度。配成
    /// <see cref="TimeSpan.Zero"/> 或负数关掉这个检查。每次检查是一条 <c>az repos pr show</c>，
    /// 只在正在评审的那个节点上跑、一次只有一个 PR，所以一分钟一次的开销可以忽略。
    /// </remarks>
    public TimeSpan PrStatusCheckInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>拉取临时工作区的墙钟上限。大仓库的首次拉取会比较久。</summary>
    public TimeSpan CheckoutTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 本节点同时最多跑几个评审。<b>按规则是 1，别改大。</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 一个节点一次只评一个 PR。理由不是机器扛不住，而是<b>席位要按当时的队列现状分</b>：
    /// 并发跑的时候，一个节点会在同一轮里把好几个 PR 标成「我在评」，别的空闲节点看到
    /// 它们已经有人接手就不再插手 —— 于是活全堆在一台机器上排队，其余的干等。
    /// 评完一个再回队列里挑下一个，谁空谁接，整体反而快。
    /// </para>
    /// <para>
    /// 编排循环里还有一道更硬的闸：<c>_inFlight</c> 非空就这一轮不开新的
    /// （见 <c>ReviewOrchestrator.TickAsync</c>）。这个值只是信号量的上限，
    /// 配大了也不会真的并发起来。
    /// </para>
    /// </remarks>
    public int MaxConcurrent { get; set; } = 1;

    /// <summary>
    /// 发现阶段同时取几个 PR 的改动统计。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 每个新 PR 要两次 <c>az devops invoke</c>（iterations + iterationChanges），
    /// <b>实测单次约 1.56 秒、一个 PR 约 3 秒</b> —— 大头是 <c>az</c> 自己的启动开销，不是网络。
    /// 串行跑的话 26 个活跃 PR 就要 78 秒，34 个 project 上百个 PR 是好几分钟，
    /// 而这段时间里队列是空的。
    /// </para>
    /// <para>
    /// 8 路并发把它压到十几秒。再往上收益递减：瓶颈是 8 个 <c>az</c> 进程的启动，
    /// 而且并发太高会让 Azure DevOps 那边看起来像在扫描。
    /// </para>
    /// </remarks>
    public int DiscoveryConcurrency { get; set; } = 8;

    /// <summary>
    /// 拉取临时工作区时的初始深度。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 不能只拉 <c>--depth=1</c>：<c>az-pr-review</c> 靠 <c>git diff target...source</c>
    /// 取三点 diff，而三点 diff 要 merge-base。实测浅到拿不到共同祖先时，
    /// <c>git diff a...b</c> 直接以退出码 128 报 <c>fatal: no merge base</c>，
    /// 整次评审在读 diff 那一步就死掉、只落下一张 Error 票。
    /// </para>
    /// <para>
    /// 所以拉完必须校验 merge-base，不够就加深（见
    /// <c>Conclave.Infrastructure.GitWorkspaceFactory</c>）。这个值只是省流量的起点，
    /// 正确性由校验保证，配小了只会多一次加深往返。
    /// </para>
    /// </remarks>
    public int FetchDepth { get; set; } = 50;

    /// <summary>只轮询这些 project；留空表示全部。</summary>
    /// <remarks>
    /// ⚠️ 千万别给这个属性写默认值。.NET 的配置绑定对集合是<b>追加</b>语义（接口类型的集合
    /// 有没有 setter 都一样），「属性初始化器里带默认值 + 配置文件里再列一遍」会得到两份 ——
    /// 早先的 <c>RepoSearchRoots</c> 就这么在 .app 启动日志里重复过一遍。
    /// </remarks>
    public IList<string> ProjectAllowList { get; set; } = [];

    /// <summary>
    /// 排除这些 project，不轮询它们的 PR。支持 <c>*</c> / <c>?</c> 通配，大小写不敏感。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 典型用途是把测试/演示用的 project 挡掉：<c>["*test*", "*demo*", "*sandbox*"]</c>。
    /// 它在 <see cref="ProjectAllowList"/> <b>之后</b>生效 —— 白名单决定候选范围，
    /// 黑名单再从里面剔除，所以两个都配时黑名单说话。
    /// </para>
    /// <para>
    /// 留空表示不排除任何东西。同样<b>不给默认值</b>，理由见
    /// <see cref="ProjectAllowList"/> 上那条注释。
    /// </para>
    /// </remarks>
    public IList<string> ProjectDenyList { get; set; } = [];

    /// <summary>
    /// 扫描 project 时按<b>名字后缀</b>直接排除，分号分隔，大小写不敏感；留空表示不按后缀排除。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 默认 <c>-qa;-test</c>。这类 project 里的 PR 是给流程本身练手的，评了只是白烧额度，
    /// 而它们在一个 collection 里通常有几十个 —— 靠人往 <see cref="ProjectDenyList"/> 里
    /// 一个个列不现实。要加就写成 <c>-qa;-test;-uat;-sandbox</c>，<b>要关掉就留空串</b>。
    /// </para>
    /// <para>
    /// <b>为什么是分号分隔的字符串而不是数组。</b> 这一条是整个配置里唯一一个「带默认值的
    /// 排除规则」，而数组属性绝对不能带默认值：.NET 的配置绑定对集合是追加语义，
    /// 「属性初始化器里给一份 + 配置文件里再列一遍」会得到两份（见 <see cref="ProjectAllowList"/>
    /// 上那条注释记的事故）。标量属性没有这个问题 —— 后一层配置整个替换前一层，
    /// 于是「默认排除、要改就整条覆盖」这个语义是干净的。
    /// </para>
    /// <para>
    /// <b>为什么是后缀而不是通配。</b> <c>*test*</c> 会连 <c>contest-service</c> 一起命中
    /// （<see cref="ProjectDenyList"/> 的测试里专门钉了这个坑），而它是<b>静默</b>的 ——
    /// 日志上只看得出少轮询了几个 project。后缀匹配没有这种误伤。真要通配还有
    /// <see cref="ProjectDenyList"/>，两者是并集。
    /// </para>
    /// </remarks>
    public string ExcludedProjectSuffixes { get; set; } = "-qa;-test";

    /// <summary>
    /// 这个 project 是否被排除：<see cref="ExcludedProjectSuffixes"/> 或
    /// <see cref="ProjectDenyList"/> 命中任一即排除。
    /// </summary>
    /// <remarks>
    /// 通配用 <see cref="FileSystemName.MatchesSimpleExpression"/> 而不是自己写匹配 ——
    /// <see cref="Domain.ReservedMatters"/> 里的路径匹配用的就是它，同一个仓库里两套
    /// 通配语义只会让人猜错。
    /// </remarks>
    public bool IsProjectDenied(string project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (HasExcludedSuffix(project))
        {
            return true;
        }

        foreach (var pattern in ProjectDenyList)
        {
            if (!string.IsNullOrWhiteSpace(pattern)
                && FileSystemName.MatchesSimpleExpression(pattern.Trim(), project, ignoreCase: true))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>名字是不是以 <see cref="ExcludedProjectSuffixes"/> 里任一个后缀结尾。</summary>
    /// <remarks>
    /// 每次现拆而不是缓存：一轮扫描几十个 project，拆一个十来字符的串的代价可以忽略，
    /// 而缓存要跟着 setter 失效 —— 配置热改时忘了失效的那种 bug 不值得为这点开销去冒。
    /// </remarks>
    private bool HasExcludedSuffix(string project)
    {
        if (string.IsNullOrWhiteSpace(ExcludedProjectSuffixes))
        {
            return false;
        }

        foreach (var suffix in ExcludedProjectSuffixes.Split(
            ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (project.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把候选 project 过一遍黑名单。
    /// </summary>
    /// <remarks>
    /// 抽出来是因为有<b>两个</b>调用点：轮询范围，以及本节点心跳里报出去的 <c>Projects</c>。
    /// 后者会喂给 <see cref="Domain.SeatAssignment.Eligible"/>，所以漏掉它的话，
    /// 本节点虽然不轮询被排除的 project，却仍会去评审别的节点召集来的那些 PR ——
    /// 「不拉取」的意图就落空了一半。
    /// </remarks>
    public IReadOnlyList<string> FilterProjects(IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return [.. candidates.Where(p => !IsProjectDenied(p))];
    }

    /// <summary>
    /// quorum 档位：一个 PR 由几个节点独立评审。
    /// </summary>
    /// <remarks>
    /// 默认全 1 —— 每个 PR 只评一次。多节点独立评审能压掉 LLM 的方差，但那是成倍的额度，
    /// 所以合并逻辑完整保留、随时可以打开，默认不开。配置示例见
    /// <c>scripts/appsettings.example.json</c>。
    /// </remarks>
    public QuorumPolicy Quorum { get; set; } = new();

    /// <summary>
    /// 允许本节点发现的 PR 由作者自己评审。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>「不评审自己的 PR」是这套东西的地基</b>，这个开关是给联调用的逃生口 ——
    /// 本机起多个节点时 az 身份是同一个，拿自己提的 PR 做联调会所有节点都出局，
    /// PR 停在「已召集」不动。生产上打开等于让作者给自己盖章，评审就没有意义了。
    /// </para>
    /// <para>
    /// 它<b>不是</b>「本机怎么判定」的开关：席位分配是纯函数，各节点必须算出同一张表。
    /// 所以它由发现节点写进 <see cref="Domain.QueuedRevision.AllowSelfReview"/> 随条目
    /// 广播，并计入规则指纹 —— 谁发现的 PR 用谁的策略，全 mesh 读同一个值。
    /// </para>
    /// </remarks>
    public bool AllowSelfReview { get; set; }

    /// <summary>
    /// 一个 revision 最多让<b>几个不同节点</b>试（执行失败算一次）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 出错的节点不会被再抽到（见 <see cref="RetryOnSameNode"/>），所以每一轮都落在
    /// 一台没试过的机器上，这个数就是「最多换几台」。到顶之后写一个 degraded 的
    /// Error 结论、发一条通知，并且<b>不再分配席位</b> ——
    /// 否则「claude 在谁那儿都起不来」会一直排下去。
    /// </para>
    /// <para>
    /// 单节点 mesh 上把它设成 1，就是「一失败立刻收尾」。
    /// </para>
    /// </remarks>
    public int MaxReviewAttempts { get; set; } = 3;

    /// <summary>
    /// 执行失败之后，允许把席位再给同一个节点。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 默认<b>关</b>。出错的原因基本都不在这份代码上 —— claude 没额度、用户退出登录、
    /// az DevOps 连不上、token 到期 —— 在同一台机器上再跑一遍只会原样再错一次，
    /// 而每一轮都是一份完整的账单。关着的时候一次失败就把这一版放回队列，
    /// 由还没试过的节点重新获取席位。
    /// </para>
    /// <para>
    /// 打开则回到老行为：池子空了就重新蓄满，单节点 mesh 上会在同一台机器上
    /// 重试到 <see cref="MaxReviewAttempts"/> 为止。
    /// </para>
    /// <para>
    /// ⚠️ 它参与席位分配，而席位分配是纯函数、各节点必须算出同一张表 ——
    /// 所以<b>整个 mesh 要配成一样的</b>。跟 <see cref="MaxReviewAttempts"/> 同级：
    /// 两边不一致时，一个节点认为该给自己、另一个认为该换人，会短暂地各算各的
    /// （下一轮心跳把 <c>Reviewing</c> 广播出去就收敛）。真要按 PR 变的策略得像
    /// <see cref="AllowSelfReview"/> 那样随条目广播并计入规则指纹。
    /// </para>
    /// </remarks>
    public bool RetryOnSameNode { get; set; }

    /// <summary>
    /// 作者 push 修复之后，是否仍由上一版的评审者复审。
    /// </summary>
    /// <remarks>
    /// 默认开。那个节点已经读过这份代码、提过这些 finding，复审的边际成本远低于
    /// 换一个节点从头看；对作者来说也是同一个「评审者」在跟进，而不是每次换一套意见。
    /// 关掉则每个新 revision 都按加权 HRW 重新抽 —— 但种子是 PR 维度的
    /// （<see cref="Domain.Revision.SeatKey"/>），所以即便关掉，结果通常也还是同一个节点。
    /// </remarks>
    public bool StickyReviewer { get; set; } = true;

    /// <summary>P1 mesh 的参数。</summary>
    public MeshOptions Mesh { get; set; } = new();

    /// <summary>Claude 额度用量的判定参数。</summary>
    public ClaudeUsageOptions ClaudeUsage { get; set; } = new();

    /// <summary>结论出来后用飞书通知作者的参数。</summary>
    public LarkOptions Lark { get; set; } = new();

    /// <summary>mesh 配置同步：一处改、全组跟上。</summary>
    public ConfigSyncOptions ConfigSync { get; set; } = new();

    /// <summary>app 自身的更新：查 GitHub Release，人点了才装。</summary>
    public UpdateOptions Update { get; set; } = new();

    /// <summary>
    /// 覆盖本节点上报的 <c>az</c> 登录身份。留空则用 <c>az</c> 的真实身份。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只为本机多节点联调而存在。</b> 本机跑两个节点时它们共用同一个 <c>az</c> 登录，
    /// 于是「不评审自己的 PR」这条硬规则会把两个节点<b>同时</b>排除 —— 你自己的 PR 在
    /// 本地怎么测都没人评，指派功能也验证不了（被指派的那个节点同样不合格）。
    /// </para>
    /// <para>
    /// ⚠️ <b>生产环境不要配。</b> 它让节点谎报身份，而那条硬规则正是靠身份比对防「自己
    /// 批准自己的 PR」。配错了不会报错，只会让自评审静默地重新变成可能。
    /// 所以启动日志里会为它单独打一条警告。
    /// </para>
    /// </remarks>
    public string AzIdentityOverride { get; set; } = string.Empty;

    /// <summary>
    /// Azure DevOps 组织（collection）地址，形如 <c>https://host/Collection</c>。
    /// </summary>
    /// <remarks>
    /// 留空则从 <c>az devops configure --list</c> 的 <c>organization</c> 读。它用来在
    /// <c>az repos pr list</c> 没给 <c>remoteUrl</c>（实测返回 null）时拼出克隆地址。
    /// </remarks>
    public string AzureDevOpsOrgUrl { get; set; } = string.Empty;

    /// <summary>
    /// 发现 PR 后是否自动开跑评审。
    /// </summary>
    /// <remarks>
    /// 默认 false：一开机就把 34 个 project 的活跃 PR 全评一遍会烧掉可观的额度。
    /// 先在 UI 里手动点几个确认链路正常，再打开。
    /// </remarks>
    public bool AutoReview { get; set; }

    /// <summary>
    /// 是否把结论投递回 Azure DevOps（发评论 + 投票）。
    /// </summary>
    /// <remarks>
    /// 默认 false，只在本地 Acta 上留结论。确认合并结果质量之前不要往真实 PR 上写。
    /// </remarks>
    public bool PostToAzureDevOps { get; set; }

    /// <summary><c>claude</c> 的命令名或绝对路径。</summary>
    public string ClaudeExecutable { get; set; } = "claude";

    /// <summary>
    /// 评审用哪个模型，例如 <c>claude-opus-5</c>。留空 = 用那台机器 <c>claude</c> 的默认模型。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>每台机器自己配，不随 mesh 同步。</b> 各台的订阅和各模型额度并不一样
    /// （<c>/usage</c> 里 Fable 就有自己独立的周额度桶），全组统一指定一个模型，会让那个
    /// 模型额度打满的机器直接失效 —— 而它本可以换一个模型继续干活。
    /// </para>
    /// <para>
    /// 代价要知道：各节点用不同模型时，quorum 压方差这件事只剩一半。「被 3 个节点都提到
    /// 几乎必然是真问题」仍然成立；但「只被 1 个提到 = 大概率是噪音」不再成立 ——
    /// 它也可能是「只有能力强的那台看出来了」，而
    /// <c>Confidence &lt; 0.5 只作提示不作阻塞</c> 这条规则正建立在前一种解释上。
    /// 所以每一票用的是哪个模型要看得见，别只看 Confidence。
    /// </para>
    /// <para>
    /// 留空是安全的默认：不给 <c>--model</c>，行为跟这个选项加进来之前一模一样。
    /// 每一票<b>实际</b>用的模型照旧从 <c>modelUsage</c> 里事后读出来记进票里 ——
    /// 配了这一项也不改那条路径，因为「配的」和「真跑的」不一定一致（模型下线、别名解析）。
    /// </para>
    /// </remarks>
    public string ClaudeModel { get; set; } = string.Empty;

    /// <summary><c>az</c> 的命令名或绝对路径。</summary>
    public string AzExecutable { get; set; } = "az";

    /// <summary><c>git</c> 的命令名或绝对路径。</summary>
    public string GitExecutable { get; set; } = "git";

    /// <summary>
    /// 找外部工具时优先看这些目录。
    /// </summary>
    /// <remarks>
    /// 从 Finder 启动 <c>.app</c> 时 LaunchServices 只给一个最小 PATH，
    /// <c>az</c> 和 <c>claude</c> 都不在里面。解析器自带常见安装目录的兜底，
    /// 装在非常规位置时用这个补。
    /// </remarks>
    public IList<string> ExtraToolPaths { get; set; } = [];

    /// <summary>展开 <c>~</c> 前缀。</summary>
    public static string ExpandHome(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.StartsWith('~'))
        {
            return path;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, path.TrimStart('~', '/', '\\'));
    }
}

namespace Conclave.Application;

/// <summary>
/// app 自身的更新：去哪查、多久查一次、装到哪。
/// </summary>
/// <remarks>
/// <para>
/// 更新源是 GitHub Release（仓库公开，不需要 token）。CI 在打 tag 时把两个架构的
/// <c>Conclave.app</c> zip 连同 <c>manifest.json</c>（每个包的 sha256）发成 Release，
/// 见 <c>.github/workflows/release.yml</c>。app 只查 <c>releases/latest</c>、比版本、
/// 校验和对上才装。
/// </para>
/// <para>
/// <b>查</b>有两个触发口：每 <see cref="CheckInterval"/> 一次的定时，加上「组里有人
/// 已经在跑更新的版本」—— 心跳里带着每个节点的 app 版本，看见比自己新的就立刻查一次，
/// 不必等满一个小时。同一批机器往往是同时该升的，让先升的那台顺手把消息带给其余的。
/// </para>
/// <para>
/// <b>装</b>是自动的（<see cref="AutoInstall"/>），不问人：下载、校验都可以随时做，
/// 只有替换二进制那一步要等本节点评完手上的 PR —— 一次评审十几分钟、烧订阅额度，
/// 中途换二进制等于白烧。所以「等空闲」是安装器的事，不是让人守着点按钮。
/// </para>
/// </remarks>
public sealed class UpdateOptions
{
    /// <summary>关掉就不查也不提示。装的入口仍在（比如手动拿到包）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>GitHub 仓库，<c>owner/name</c>。</summary>
    public string Repository { get; set; } = "guangliangus/Conclave-App";

    /// <summary>多久查一次。GitHub 匿名 API 每小时 60 次，一小时一次连零头都不到。</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// 多久看一眼「组里有没有人在跑更新的版本」。
    /// </summary>
    /// <remarks>
    /// 这一步只读内存里的成员表（心跳带着各节点的 app 版本），不发任何请求，
    /// 所以配得比 <see cref="CheckInterval"/> 密得多。只有真看见更新的版本才会
    /// 去问一次 GitHub，而且同一个版本只问一次 —— 否则对面是个开发态的构建
    /// （版本号比 Release 还新）就会把配额耗在无意义的重复查询上。
    /// </remarks>
    public TimeSpan PeerScanInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 查到新版本就自动装，不等人点。
    /// </summary>
    /// <remarks>
    /// 替换那一步仍然要等本节点空闲（见 <c>IUpdateInstaller</c>），所以「自动」
    /// 不会打断在跑的评审。关掉则退回「只提示，横幅上有按钮」。
    /// 进程不在 <c>.app</c> 里（<c>dotnet run</c> 的开发态）时这个开关无效 ——
    /// 那时没有可替换的目标，只提示。
    /// </remarks>
    public bool AutoInstall { get; set; } = true;

    /// <summary>启动后等多久再查第一次 —— 让轮询和 mesh 先起来，别跟它们抢启动那几秒。</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 要替换的 <c>.app</c> 路径。留空则从当前进程的可执行文件往上找 <c>.app</c>。
    /// </summary>
    /// <remarks>
    /// 留空是正确默认：装在 <c>/Applications</c> 还是 <c>~/Applications</c> 不该由配置猜，
    /// 进程自己知道自己从哪起来的。写死这个值只在「进程不在 .app 里但要更新那个 .app」时有用。
    /// </remarks>
    public string BundlePath { get; set; } = string.Empty;

    /// <summary>LaunchAgent 的 label。装好之后用 <c>launchctl kickstart -k</c> 让 launchd 拉起新版。</summary>
    public string LaunchAgentLabel { get; set; } = "com.liontravel.conclave";
}

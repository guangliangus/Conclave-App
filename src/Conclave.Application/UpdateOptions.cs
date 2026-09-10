namespace Conclave.Application;

/// <summary>
/// app 自身的更新：去哪查、多久查一次、装到哪。
/// </summary>
/// <remarks>
/// 更新源是 GitHub Release（仓库公开，不需要 token）。CI 在打 tag 时把两个架构的
/// <c>Conclave.app</c> zip 连同 <c>manifest.json</c>（每个包的 sha256）发成 Release，
/// 见 <c>.github/workflows/release.yml</c>。app 只查 <c>releases/latest</c>、比版本、
/// 校验和对上才装 —— <b>不自动装</b>，装是人点的：一个节点一次只评一个 PR，
/// 换二进制的时机由人挑，而且要等手上那个评完。
/// </remarks>
public sealed class UpdateOptions
{
    /// <summary>关掉就不查也不提示。装的入口仍在（比如手动拿到包）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>GitHub 仓库，<c>owner/name</c>。</summary>
    public string Repository { get; set; } = "guangliangus/Conclave-App";

    /// <summary>多久查一次。GitHub 匿名 API 每小时 60 次，一小时一次连零头都不到。</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(1);

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

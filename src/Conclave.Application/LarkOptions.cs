using Conclave.Domain;

namespace Conclave.Application;

/// <summary>
/// 结论出来后用飞书私聊通知 PR 作者。
/// </summary>
/// <remarks>
/// <para>
/// 走自建应用的 <c>tenant_access_token</c> 直接打 OpenAPI，而不是 shell 出去调 <c>lark-cli</c>：
/// 那会给每个节点再加一个必须装、必须在 PATH 里、必须各自登录的外部二进制 ——
/// 从 Finder 启动 <c>.app</c> 时连 <c>az</c> 和 <c>claude</c> 都找不到（见 <c>ExecutableResolver</c>），
/// 没理由再踩一次同样的坑。
/// </para>
/// <para>
/// 也刻意不用群自定义机器人的 webhook：那个只能往群里发，@ 到人靠 open_id 或邮箱，
/// 而且一个 PR 的结论刷进公共群里，看的人比该看的人多得多。
/// </para>
/// </remarks>
public sealed class LarkOptions
{
    /// <summary>关掉就完全不发通知。</summary>
    public bool Enabled { get; set; }

    /// <summary>自建应用的 App ID，形如 <c>cli_xxx</c>。</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// 自建应用的 App Secret。
    /// </summary>
    /// <remarks>
    /// ⚠️ 别写进仓库里的 appsettings.json。用 <c>~/.conclave/appsettings.json</c>（每台机器自己的）
    /// 或环境变量 <c>CONCLAVE_Conclave__Lark__AppSecret</c> —— 后者优先级更高，
    /// 见 <c>Program.CreateBuilder</c> 的配置叠加顺序。
    /// </remarks>
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>开放平台地址。国际版 <c>open.larksuite.com</c>，中国版 <c>open.feishu.cn</c>。</summary>
    public string BaseUrl { get; set; } = "https://open.larksuite.com";

    /// <summary>
    /// 把 az 账号拼成邮箱用的域名，例如 <c>liontravel.com</c>；留空表示只认 <see cref="UserMap"/>。
    /// </summary>
    /// <remarks>
    /// Azure DevOps 侧<b>拿不到邮箱</b>：<c>createdBy</c> 只有 <c>LIONMAIL\tobeyhuang</c>、
    /// displayName 和一个 identity id，而这台 ADO Server 的 <c>IMS/Identities</c> 接口
    /// 对 <c>az</c> 的认证方式直接返回 401（实测）。所以这里按
    /// 「<c>AzIdentity.Normalize(uniqueName)</c> + @域名」拼 —— 实测本组织的
    /// commit author email 正是这个形状（<c>LIONMAIL\tobeyhuang</c> → <c>tobeyhuang@liontravel.com</c>）。
    /// 拼不对的人用 <see cref="UserMap"/> 单独兜。
    /// </remarks>
    public string EmailDomain { get; set; } = string.Empty;

    /// <summary>
    /// az 账号 → 飞书 open_id 或邮箱的显式覆盖。键不区分大小写，值以 <c>ou_</c> 开头视为 open_id。
    /// </summary>
    /// <remarks>
    /// 邮箱拼接规则总有人不吻合（改过名、外包账号、离职复用）。这里刻意<b>不</b>给默认值：
    /// .NET 的配置绑定对集合是追加语义，属性初始化器里塞条目会跟配置文件里的叠成两份，
    /// 跟 <see cref="ConclaveOptions.ProjectAllowList"/> 是同一个坑。
    /// </remarks>
    public IDictionary<string, string> UserMap { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>单次 OpenAPI 调用的超时。</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>开关开着而且凭据齐了。</summary>
    public bool IsConfigured =>
        Enabled && AppId.Length > 0 && AppSecret.Length > 0;

    /// <summary>
    /// 把一个 az 身份换成飞书收件人（<c>ou_</c> 开头的 open_id，或一个邮箱）。
    /// </summary>
    /// <returns>换不出来返回 <c>null</c> —— 调用方要把这种情况说出来，不要静默不发。</returns>
    public string? RecipientFor(string? azIdentity)
    {
        var account = AzIdentity.Normalize(azIdentity);
        if (account.Length == 0)
        {
            return null;
        }

        var mapped = Mapped(account);
        if (mapped is not null)
        {
            return mapped;
        }

        var domain = EmailDomain.Trim().TrimStart('@');
        return domain.Length > 0 ? $"{account}@{domain}" : null;
    }

    /// <summary>
    /// 在 <see cref="UserMap"/> 里找这个账号。
    /// </summary>
    /// <remarks>
    /// 键允许写成 az 里看到的<b>任何</b>形态。直接命中不了就按
    /// <see cref="AzIdentity.Normalize"/> 扫一遍 —— 面板上复制出来的是
    /// <c>LIONMAIL\tobeyhuang</c>，要求人先手工剥掉域前缀纯属给自己挖坑，
    /// 而挖坑的结果是「配了 UserMap 但没生效」，跟没配一模一样。
    /// 表就几十条，一次线性扫远比一个静默不命中的字典便宜。
    /// </remarks>
    private string? Mapped(string account)
    {
        if (UserMap.TryGetValue(account, out var direct) && !string.IsNullOrWhiteSpace(direct))
        {
            return direct.Trim();
        }

        foreach (var (key, value) in UserMap)
        {
            if (!string.IsNullOrWhiteSpace(value) && AzIdentity.Normalize(key) == account)
            {
                return value.Trim();
            }
        }

        return null;
    }
}

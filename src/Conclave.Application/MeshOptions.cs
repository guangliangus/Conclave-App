namespace Conclave.Application;

/// <summary>
/// P1 mesh 的参数。
/// </summary>
/// <remarks>
/// 发现走 UDP 多播而不是 mDNS：这里需要的只是「谁在线 + 它的 HTTP 端点」，
/// 不需要 DNS-SD 的服务记录那一整套，自己发一个 JSON 信标反而更少依赖也更好排查。
/// </remarks>
public sealed class MeshOptions
{
    /// <summary>关掉就是单机模式（P0 行为）。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 信任所有能通过验签的节点，不再读 <c>electors.allow</c>。
    /// </summary>
    /// <remarks>
    /// <b>只该用于本机联调。</b> 白名单挡的不是「谁能看数据」而是
    /// <b>谁能让你的机器起 claude 跑 Bash</b> —— 别人的 Seating 块落进你的账本，
    /// 对应的评审任务就会在你这里执行。关掉之后，同一多播域里任何持有合法密钥对的
    /// 节点都能派活给你（签名只证明「这台机器是它自称的那台」，不证明「它可信」）。
    /// <para>
    /// 存在的理由是联调成本：本机起两个节点时，白名单要求先各起一次拿指纹、
    /// 互相写进对方的 <c>electors.allow</c>、再重启。开了这个开关就只剩「改端口」一步。
    /// 跟 <see cref="ConclaveOptions.AzIdentityOverride"/> 同一性质，也同样会大声警告。
    /// </para>
    /// </remarks>
    public bool TrustAllElectors { get; set; }

    /// <summary>
    /// 多播组地址。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 默认值落在 <b>239.255.0.0/16（RFC 2365 的 IPv4 Local Scope）</b>—— 组播里的
    /// 「192.168.x」：路由器不会把它转出管理边界。不能用 224.0.0.0/24（链路本地控制组，
    /// 224.0.0.251 就是 mDNS）也不该用 224.0.1.0–238.255.255.255（理论上可全网路由）。
    /// </para>
    /// <para>
    /// 主机位（<c>42.7</c>）是随便挑的，协议不依赖它。只有三条要求：在 239/8 里、
    /// 同一个 mesh 的所有节点写同一个值、别撞局域网里已经在用的组 ——
    /// 比较容易撞的是 <c>239.255.255.250</c>（SSDP/UPnP，家用路由器和电视都在发）。
    /// </para>
    /// <para>
    /// 改这个值（或 <see cref="BeaconPort"/>）就是换一个频道：<b>全 mesh 必须一起改</b>，
    /// 不然彼此收不到心跳。反过来，这也是让同一网段上两套互不相干的 mesh 互相看不见的
    /// 办法之一 —— 另一个办法是各自的 <c>electors.allow</c>，那种情况下包照收，
    /// 只是验签后被丢掉（所以「不在白名单」只记 Debug，不然会刷屏）。
    /// </para>
    /// </remarks>
    public string MulticastAddress { get; set; } = "239.255.42.7";

    /// <summary>信标端口。IANA 未分配的高位端口，跟地址一样只要求全 mesh 一致。</summary>
    public int BeaconPort { get; set; } = 47707;

    /// <summary>本节点 HTTP 端点监听端口。0 表示由系统分配。</summary>
    public int HttpPort { get; set; } = 47708;

    /// <summary>信标广播间隔。要明显小于 <see cref="Domain.Elector.HeartbeatWindow"/>。</summary>
    public TimeSpan BeaconInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>一个区块 gossip 给几个邻居。</summary>
    public int Fanout { get; set; } = 3;

    /// <summary>对端 HTTP 请求超时。</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

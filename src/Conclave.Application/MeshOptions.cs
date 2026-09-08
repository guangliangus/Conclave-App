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

    /// <summary>多播组地址。默认是管理性局域网范围（IPv4 organization-local）。</summary>
    public string MulticastAddress { get; set; } = "239.255.42.7";

    /// <summary>信标端口。</summary>
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

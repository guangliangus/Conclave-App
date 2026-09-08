using System.Net;
using System.Net.Sockets;
using System.Text;
using Conclave.Application;
using Conclave.Domain;

namespace Conclave.Infrastructure.Mesh;

/// <summary>
/// UDP 多播上的心跳收发。
/// </summary>
/// <remarks>
/// <para>
/// 用裸多播而不是 mDNS：这里需要的只是「谁在线 + 它的 HTTP 端点」，
/// 不需要 DNS-SD 的服务/实例/TXT 那一整套；自己发一个签名过的 JSON 反而更少依赖也更好排查
/// （<c>nc -ul 47707</c> 就能看）。
/// </para>
/// <para>
/// <c>ReuseAddress</c> 必须在 Bind 之前设：否则同一台机器上起第二个节点会直接
/// 「地址已在使用」，而本机跑两个节点正是最方便的联调方式。
/// </para>
/// </remarks>
internal sealed class MeshBeaconSocket : IDisposable
{
    private readonly UdpClient _socket;
    private readonly IPEndPoint _group;

    internal MeshBeaconSocket(MeshOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var address = IPAddress.Parse(options.MulticastAddress);
        _group = new IPEndPoint(address, options.BeaconPort);

        _socket = new UdpClient(AddressFamily.InterNetwork);
        _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Client.Bind(new IPEndPoint(IPAddress.Any, options.BeaconPort));
        _socket.JoinMulticastGroup(address);
        _socket.MulticastLoopback = true;   // 本机多节点联调要能收到自己发的
    }

    internal async Task SendAsync(Beacon beacon, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(ActaJson.Serialize(beacon));
        _ = await _socket.SendAsync(payload, _group, ct).ConfigureAwait(false);
    }

    /// <summary>收一个心跳。解析不了就返回 null —— 多播组里可能有别的东西在发包。</summary>
    internal async Task<Beacon?> ReceiveAsync(CancellationToken ct)
    {
        var result = await _socket.ReceiveAsync(ct).ConfigureAwait(false);
        try
        {
            return ActaJson.Deserialize<Beacon>(Encoding.UTF8.GetString(result.Buffer));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _socket.Dispose();
}

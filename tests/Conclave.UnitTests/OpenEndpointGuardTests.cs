using System.Net;
using Conclave.Infrastructure.Mesh;

namespace Conclave.UnitTests;

/// <summary>
/// <c>GET /open</c> 的来源判断。
/// </summary>
/// <remarks>
/// 这是 mesh 上唯一一个会造成<b>本机 UI 动作</b>的接口 —— 其余几个 GET 最多泄露信息，
/// 而它能让别人把你的面板弹出来。卡片上的链接永远是 <c>127.0.0.1</c>，
/// 所以除了回环之外一律拒绝，不损失任何功能。
/// </remarks>
public class OpenEndpointGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]   // 整个 127.0.0.0/8 都是回环
    [InlineData("::1")]
    public void Loopback_is_allowed(string address)
        => Assert.True(MeshHttpServer.IsLoopback(IPAddress.Parse(address)), address);

    /// <summary>
    /// IPv4 映射进 IPv6 的回环也要放行。
    /// </summary>
    /// <remarks>
    /// 双栈机器上浏览器连 127.0.0.1 时，请求可能以 <c>::ffff:127.0.0.1</c> 的形态到达。
    /// 不拆回去判的话，按钮在那些机器上会稳定地 403 —— 而那种失败只会表现成
    /// 「点了没反应」，跟这套东西一路踩过的坑是同一个。
    /// </remarks>
    [Fact]
    public void An_ipv4_mapped_loopback_is_still_loopback()
        => Assert.True(MeshHttpServer.IsLoopback(IPAddress.Parse("::ffff:127.0.0.1")));

    /// <summary>
    /// 本机的内网地址<b>也要拒</b>。
    /// </summary>
    /// <remarks>
    /// 这正是不用 <c>HttpListenerRequest.IsLocal</c> 的原因：它的语义是
    /// 「回环<b>或者</b>等于本机任意一个地址」，于是 10.x 那一档也算 local。
    /// 卡片永远指 127.0.0.1，放宽换不来功能，只是把面放大。
    /// </remarks>
    [Theory]
    [InlineData("10.19.8.67")]
    [InlineData("192.168.1.5")]
    [InlineData("8.8.8.8")]
    public void Anything_else_is_refused(string address)
        => Assert.False(MeshHttpServer.IsLoopback(IPAddress.Parse(address)), address);

    /// <summary>拿不到远端地址时默认拒绝 —— 这里的默认值必须是「不放行」。</summary>
    [Fact]
    public void An_unknown_peer_is_refused() => Assert.False(MeshHttpServer.IsLoopback(null));
}

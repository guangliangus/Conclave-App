using Conclave.Application;
using Conclave.Domain;
using Conclave.Infrastructure.Mesh;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

public class HttpMeshTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-09-08T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record Peer(ElectorIdentity Identity, Elector Elector);

    private static Peer MakePeer(DateTimeOffset? heartbeat = null)
    {
        var identity = ElectorIdentity.Create();
        return new Peer(identity, new Elector
        {
            Id = identity.Id,
            PublicKey = identity.PublicKey,
            Endpoint = "http://10.0.0.9:47708",
            AzIdentity = "peer",
            Projects = ["edison-test"],
            LastHeartbeat = heartbeat ?? Now,
            ProtocolVersion = Beacon.ProtocolVersion,
            AppVersion = "1.0.0-test",
        });
    }

    private static Beacon BeaconOf(Peer peer)
        => new(peer.Elector, 0, peer.Identity.Sign(Beacon.SigningPayload(peer.Elector, 0)));

    /// <summary>
    /// 一个只会失败的 handler：模拟「HTTP 也不通」。
    /// </summary>
    /// <remarks>
    /// 默认不传 handler 的话，确认那一步会真去连 10.0.0.9 —— 那要等满
    /// <c>RequestTimeout</c>（10 秒）才失败，测试就变慢了。
    /// </remarks>
    private sealed class DeadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }

    /// <summary>答得上话的 handler：返回对端签名的实时状态，模拟「组播丢了但机器好着」。</summary>
    private sealed class AliveHandler(Peer peer) : HttpMessageHandler
    {
        internal int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;

            var signed = SignedLiveState.Sign(peer.Identity, LiveState.Empty with { Version = 7 });

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    ActaJson.Serialize(signed), System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>把请求的 URL 记下来，并回一个空链。</summary>
    private sealed class ChainHandler : HttpMessageHandler
    {
        internal List<Uri> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is { } uri)
            {
                Urls.Add(uri);
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (HttpMesh Mesh, MutableAllowList Allow, ElectorIdentity Self) Build(
        HttpMessageHandler? handler = null)
    {
        var self = ElectorIdentity.Create();
        var allow = new MutableAllowList(self.Id);
        var elector = new Elector
        {
            Id = self.Id,
            PublicKey = self.PublicKey,
            AzIdentity = "alan",
            LastHeartbeat = Now,
        };

        var mesh = new HttpMesh(
            elector, self, allow, new ConclaveOptions(), NullLogger<HttpMesh>.Instance,
            handler ?? new DeadHandler());
        return (mesh, allow, self);
    }

    [Fact]
    public async Task Catching_up_asks_for_one_page_at_a_time()
    {
        // 「从 N 起全都给我」会让对端把那一段链同时以 List<Block> 和一个完整 byte[]
        // 驻留，而链是永远在长的。页大小必须真的发出去。
        var handler = new ChainHandler();
        var (mesh, _, _) = Build(handler);
        var peer = new Elector
        {
            Id = "peer",
            PublicKey = "k",
            AzIdentity = "peer",
            Endpoint = "http://10.0.0.9:47707",
            LastHeartbeat = Now,
        };

        _ = await mesh.PullChainAsync(peer, 42, 500, CancellationToken.None).ConfigureAwait(true);

        var query = Assert.Single(handler.Urls).Query;
        Assert.Contains("from=42", query, StringComparison.Ordinal);
        Assert.Contains("take=500", query, StringComparison.Ordinal);
    }

    [Fact]
    public void A_whitelisted_signed_beacon_is_accepted()
    {
        var (mesh, allow, self) = Build();
        using (self)
        {
            var peer = MakePeer();
            using (peer.Identity)
            {
                allow.Allow(peer.Elector.Id);

                Assert.True(mesh.AcceptBeacon(BeaconOf(peer), Now));
                Assert.Equal(2, mesh.Members.Count);           // 自己 + 它
                Assert.Contains(mesh.Members, e => e.Id == peer.Elector.Id);
                Assert.All(mesh.Members, e => Assert.True(e.IsAlive(Now)));
            }
        }
    }

    [Fact]
    public void A_beacon_from_outside_the_whitelist_is_dropped()
    {
        var (mesh, _, self) = Build();
        using (self)
        {
            var peer = MakePeer();
            using (peer.Identity)
            {
                // 这道闸决定了「谁的评审任务能在你机器上跑 Bash」。
                Assert.False(mesh.AcceptBeacon(BeaconOf(peer), Now));
                Assert.Single(mesh.Members);
            }
        }
    }

    [Fact]
    public void A_tampered_beacon_is_dropped_even_if_whitelisted()
    {
        var (mesh, allow, self) = Build();
        using (self)
        {
            var peer = MakePeer();
            using (peer.Identity)
            {
                allow.Allow(peer.Elector.Id);
                var forged = BeaconOf(peer) with
                {
                    Elector = peer.Elector with { Projects = ["payment-center"] },
                };

                Assert.False(mesh.AcceptBeacon(forged, Now));
            }
        }
    }

    [Fact]
    public void A_stale_beacon_is_dropped()
    {
        var (mesh, allow, self) = Build();
        using (self)
        {
            var peer = MakePeer(heartbeat: Now.AddMinutes(-10));
            using (peer.Identity)
            {
                allow.Allow(peer.Elector.Id);
                Assert.False(mesh.AcceptBeacon(BeaconOf(peer), Now));
            }
        }
    }

    [Fact]
    public void Our_own_beacon_looped_back_by_multicast_is_ignored()
    {
        var (mesh, _, self) = Build();
        using (self)
        {
            var mine = mesh.Self;
            var beacon = new Beacon(mine, 0, self.Sign(Beacon.SigningPayload(mine, 0)));

            // MulticastLoopback 开着（本机多节点联调需要），所以会收到自己发的。
            Assert.False(mesh.AcceptBeacon(beacon, Now));
            Assert.Single(mesh.Members);
        }
    }

    [Fact]
    public async Task Peers_that_stop_beaconing_fall_out_of_the_mesh()
    {
        var (mesh, allow, self) = Build();
        using (self)
        {
            var peer = MakePeer();
            using (peer.Identity)
            {
                allow.Allow(peer.Elector.Id);
                _ = mesh.AcceptBeacon(BeaconOf(peer), Now);
                Assert.Equal(2, mesh.Members.Count);

                var later = Now + Elector.HeartbeatWindow + TimeSpan.FromSeconds(1);

                // 自己的心跳由发信循环每个 BeaconInterval 刷一次，这里照做，
                // 否则「窗口过了」会把自己也算成掉线。
                mesh.UpdateSelf(self => self with { LastHeartbeat = later });

                // 对端窗口一过，席位分配就不再把它算作合格（Eligible 里查 IsAlive），
                // 哪怕它还挂在成员表上 —— 存活判定用的是调用方的那个 now。
                Assert.Single(mesh.Members, e => e.IsAlive(later));

                // 心跳过期 + HTTP 也不通 → 真的移出成员表，免得表无限增长。
                Assert.Single(await mesh.ConfirmAndPruneAsync(later, CancellationToken.None));
                Assert.Empty(await mesh.ConfirmAndPruneAsync(later, CancellationToken.None));
            }
        }
    }

    [Fact]
    public void A_fresh_beacon_refreshes_an_existing_peer()
    {
        var (mesh, allow, self) = Build();
        using (self)
        {
            var peer = MakePeer();
            using (peer.Identity)
            {
                allow.Allow(peer.Elector.Id);
                _ = mesh.AcceptBeacon(BeaconOf(peer), Now);

                var later = Now.AddSeconds(40);
                var refreshed = peer.Elector with { LastHeartbeat = later, RunningJobs = 2 };
                var beacon = new Beacon(refreshed, 0, peer.Identity.Sign(Beacon.SigningPayload(refreshed, 0)));

                Assert.True(mesh.AcceptBeacon(beacon, later));
                Assert.Equal(2, mesh.Members.Single(e => e.Id == peer.Elector.Id).RunningJobs);
            }
        }
    }
    [Fact]
    public async Task A_peer_whose_multicast_is_lost_but_http_answers_stays_in_the_mesh()
    {
        var peer = MakePeer();
        using var handler = new AliveHandler(peer);
        var (mesh, allow, self) = Build(handler);

        using (self)
        using (peer.Identity)
        {
            allow.Allow(peer.Elector.Id);
            _ = mesh.AcceptBeacon(BeaconOf(peer), Now);

            var later = Now + Elector.HeartbeatWindow + TimeSpan.FromSeconds(1);
            mesh.UpdateSelf(x => x with { LastHeartbeat = later });

            // 心跳走 UDP 组播，最容易被网络设备静默丢掉；HTTP 是单播 TCP，两者的失败不相关。
            // 只看心跳就宣布掉线，会在「组播断了但机器好着」时把它正在评的 PR 判给别人 ——
            // 那个 PR 会被评两遍，多烧一份额度。
            Assert.Empty(await mesh.ConfirmAndPruneAsync(later, CancellationToken.None));
            Assert.Equal(2, mesh.Members.Count);
            Assert.Equal(1, handler.Calls);

            // 心跳时刻要刷到当下，否则所有 IsAlive 的判定点（席位资格、队列合并里的
            // alive 集合）照样把它当死的 —— 确认了也等于没确认。
            Assert.Single(mesh.Members, e => e.Id == peer.Elector.Id && e.IsAlive(later));

            // 而且接下来一个心跳窗口内不再问它 —— 这不是轮询开销。
            Assert.Empty(await mesh.ConfirmAndPruneAsync(
                later + TimeSpan.FromSeconds(30), CancellationToken.None));
            Assert.Equal(1, handler.Calls);

            // 顺带把它的实时状态收编了，那正好是接管判定要用的「它在评什么」。
            Assert.True(mesh.PeerStates.ContainsKey(peer.Elector.Id));
        }
    }

    [Fact]
    public void A_beacon_from_another_protocol_version_is_reported_not_silently_dropped()
    {
        var (mesh, allow, self) = Build();
        var peer = MakePeer();
        using (self)
        using (peer.Identity)
        {
            allow.Allow(peer.Elector.Id);

            // 签名是对的（对端按它自己的格式签），只是协议版本不同。
            var old = peer.Elector with { ProtocolVersion = Beacon.ProtocolVersion - 1, AppVersion = "0.9.0" };
            var beacon = new Beacon(old, 0, peer.Identity.Sign(Beacon.SigningPayload(old, 0)));

            Elector? reported = null;
            mesh.ProtocolMismatch += e => reported = e;

            // 以前这条路的结果是一句「验签失败」外加从成员表里消失，跟真掉线长得一模一样。
            Assert.False(mesh.AcceptBeacon(beacon, Now));
            Assert.Equal(old.Id, reported?.Id);
            Assert.Equal("0.9.0", reported?.AppVersion);
            Assert.Single(mesh.Members);   // 没进成员表

            // 十分钟内同一台机器不重复报。
            reported = null;
            Assert.False(mesh.AcceptBeacon(beacon, Now));
            Assert.Null(reported);
        }
    }

}

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
            Repos = ["edison-test"],
            Projects = ["edison-test"],
            LastHeartbeat = heartbeat ?? Now,
        });
    }

    private static Beacon BeaconOf(Peer peer)
        => new(peer.Elector, peer.Identity.Sign(Beacon.SigningPayload(peer.Elector)));

    private static (HttpMesh Mesh, MutableAllowList Allow, ElectorIdentity Self) Build()
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
            elector, allow, new ConclaveOptions(), NullLogger<HttpMesh>.Instance);
        return (mesh, allow, self);
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
                    Elector = peer.Elector with { Repos = ["payment-center"] },
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
            var beacon = new Beacon(mine, self.Sign(Beacon.SigningPayload(mine)));

            // MulticastLoopback 开着（本机多节点联调需要），所以会收到自己发的。
            Assert.False(mesh.AcceptBeacon(beacon, Now));
            Assert.Single(mesh.Members);
        }
    }

    [Fact]
    public void Peers_that_stop_beaconing_fall_out_of_the_mesh()
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

                // 之后再被清理出成员表，免得表无限增长。
                Assert.Equal(1, mesh.PruneDeadPeers(later));
                Assert.Equal(0, mesh.PruneDeadPeers(later));
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
                var beacon = new Beacon(refreshed, peer.Identity.Sign(Beacon.SigningPayload(refreshed)));

                Assert.True(mesh.AcceptBeacon(beacon, later));
                Assert.Equal(2, mesh.Members.Single(e => e.Id == peer.Elector.Id).RunningJobs);
            }
        }
    }
}

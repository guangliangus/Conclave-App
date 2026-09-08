using Conclave.Domain;

namespace Conclave.UnitTests;

public class BeaconTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-09-08T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static (Beacon Beacon, ElectorIdentity Identity) Sign(
        DateTimeOffset? heartbeat = null, string[]? repos = null)
    {
        var identity = ElectorIdentity.Create();
        var elector = new Elector
        {
            Id = identity.Id,
            PublicKey = identity.PublicKey,
            Endpoint = "http://10.0.0.5:47708",
            AzIdentity = "edisonwei",
            Repos = repos ?? ["edison-test"],
            Projects = ["edison-test"],
            LastHeartbeat = heartbeat ?? Now,
        };

        return (new Beacon(elector, identity.Sign(Beacon.SigningPayload(elector))), identity);
    }

    [Fact]
    public void A_signed_beacon_verifies()
    {
        var (beacon, identity) = Sign();
        using (identity)
        {
            Assert.True(beacon.VerifySignature());
            Assert.True(beacon.IsFresh(Now));
        }
    }

    [Fact]
    public void Claiming_extra_repos_breaks_the_signature()
    {
        var (beacon, identity) = Sign();
        using (identity)
        {
            // 伪造「什么 repo 都有」的心跳就能把席位全吸走然后永不出票，
            // 让所有 PR 卡在弃权重试的循环里。这条必须拦住。
            var forged = beacon with
            {
                Elector = beacon.Elector with { Repos = ["edison-test", "payment-center"] },
            };

            Assert.False(forged.VerifySignature());
        }
    }

    [Fact]
    public void Claiming_to_be_idle_breaks_the_signature()
    {
        using var identity = ElectorIdentity.Create();
        var busy = new Elector
        {
            Id = identity.Id,
            PublicKey = identity.PublicKey,
            AzIdentity = "edisonwei",
            Repos = ["edison-test"],
            Projects = ["edison-test"],
            RunningJobs = 3,
            Reviews24h = 40,
            LastHeartbeat = Now,
        };

        var beacon = new Beacon(busy, identity.Sign(Beacon.SigningPayload(busy)));
        Assert.True(beacon.VerifySignature());

        // 负载和近期票数都进了加权 HRW —— 谎报空闲就能把席位吸过来。
        var forged = beacon with { Elector = busy with { RunningJobs = 0, Reviews24h = 0 } };
        Assert.False(forged.VerifySignature());
    }

    [Fact]
    public void Swapping_in_another_public_key_breaks_the_fingerprint()
    {
        var (beacon, identity) = Sign();
        using var other = ElectorIdentity.Create();
        using (identity)
        {
            var forged = beacon with { Elector = beacon.Elector with { PublicKey = other.PublicKey } };
            Assert.False(forged.VerifySignature());
        }
    }

    [Fact]
    public void A_replayed_beacon_is_stale()
    {
        var (beacon, identity) = Sign(heartbeat: Now.AddMinutes(-10));
        using (identity)
        {
            // LastHeartbeat 在签名范围内，所以重放旧心跳签名是有效的 —— 靠新鲜度拦。
            Assert.True(beacon.VerifySignature());
            Assert.False(beacon.IsFresh(Now));
        }
    }

    [Fact]
    public void A_beacon_dated_far_in_the_future_is_not_fresh()
    {
        var (beacon, identity) = Sign(heartbeat: Now.AddMinutes(10));
        using (identity)
        {
            // 否则把时间戳设到很远的未来就能让自己永远「在线」。
            Assert.False(beacon.IsFresh(Now));
        }
    }

    [Fact]
    public void A_small_clock_skew_is_tolerated()
    {
        var (beacon, identity) = Sign(heartbeat: Now.AddSeconds(10));
        using (identity)
        {
            Assert.True(beacon.IsFresh(Now));
        }
    }
}

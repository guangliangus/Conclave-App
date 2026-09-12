using Conclave.Domain;

namespace Conclave.UnitTests;

public class BeaconTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-09-08T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static (Beacon Beacon, ElectorIdentity Identity) Sign(
        DateTimeOffset? heartbeat = null, double utilization = 0)
    {
        var identity = ElectorIdentity.Create();
        var elector = new Elector
        {
            Id = identity.Id,
            PublicKey = identity.PublicKey,
            Endpoint = "http://10.0.0.5:47708",
            AzIdentity = "edisonwei",
            Projects = ["edison-test"],
            Utilization = utilization,
            LastHeartbeat = heartbeat ?? Now,
        };

        return (new Beacon(elector, 0, identity.Sign(Beacon.SigningPayload(elector, 0))), identity);
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
    public void Claiming_extra_projects_breaks_the_signature()
    {
        var (beacon, identity) = Sign();
        using (identity)
        {
            // 伪造「什么 project 都有权限」的心跳就能把席位全吸走然后永不出票，
            // 让所有 PR 卡在弃权重试的循环里。这条必须拦住。
            var forged = beacon with
            {
                Elector = beacon.Elector with { Projects = ["edison-test", "liontrip-cms"] },
            };

            Assert.False(forged.VerifySignature());
        }
    }

    [Fact]
    public void Understating_claude_usage_breaks_the_signature()
    {
        var (beacon, identity) = Sign(utilization: 0.95);
        using (identity)
        {
            // 额度用量既是硬规则（超 MaxUtilization 不入席）又是权重因子，
            // 所以谎报「我还很空」是最有收益的伪造方向 —— 必须落在签名范围内。
            Assert.True(beacon.VerifySignature());

            var forged = beacon with { Elector = beacon.Elector with { Utilization = 0.0 } };
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
            Projects = ["edison-test"],
            RunningJobs = 3,
            Reviews24h = 40,
            LastHeartbeat = Now,
        };

        var beacon = new Beacon(busy, 0, identity.Sign(Beacon.SigningPayload(busy, 0)));
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

    /// <summary>
    /// 心跳装得进一个以太网帧，而且余量已经不多了。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 心跳走 UDP 多播，超过 MTU 就要 IP 分片，而分片是网络设备最爱静默丢的那种包 ——
    /// 表现是整台机器从 mesh 里消失，没有任何报错指向包大小。
    /// </para>
    /// <para>
    /// 这条用例是加「对端也要画 5h / 7d」时立的：那份窗口明细两条就是 175 字节，
    /// 而下面这个实测形状（35 个 project）已经 1418 字节、离线只剩 54 字节 ——
    /// 挂到心跳上直接就过线了。所以明细改走 <see cref="LiveState.UsageWindows"/>，
    /// 心跳一个字节都没长。
    /// </para>
    /// <para>
    /// ⚠️ <b>撑满心跳的是 <see cref="Elector.Projects"/></b>：35 个名字占了半个包还多，
    /// 而它随组织规模线性增长 —— 实测 50 个 project 就是 1748 字节，已经在分片了。
    /// 往心跳里加任何字段之前先跑这条。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(29)]
    [InlineData(35)]
    public void The_heartbeat_still_fits_in_one_datagram(int projects)
    {
        using var identity = ElectorIdentity.Create();
        var elector = new Elector
        {
            Id = identity.Id,
            PublicKey = identity.PublicKey,
            Endpoint = "http://10.19.8.118:47708",
            AzIdentity = "LIONMAIL\\edisonwei",
            Projects = [.. Enumerable.Range(0, projects).Select(i => $"liontrip-project-{i:D2}")],
            Utilization = 0.26,
            AppVersion = "1.0.5",
            ClaudeVersion = "2.1.268",
            ProtocolVersion = Beacon.ProtocolVersion,
            LastHeartbeat = Now,
        };

        var beacon = new Beacon(elector, 42, identity.Sign(Beacon.SigningPayload(elector, 42)));
        var bytes = System.Text.Encoding.UTF8.GetByteCount(ActaJson.Serialize(beacon));

        // 1472 = 1500 MTU - 20 字节 IP 头 - 8 字节 UDP 头。
        Assert.True(bytes <= 1472, $"心跳 {bytes} 字节，已经会被分片");
    }
}
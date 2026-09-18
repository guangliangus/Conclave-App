using Conclave.Application;
using Conclave.Domain;

namespace Conclave.UnitTests;

/// <summary>
/// 可传播的配置文档：验签、取舍、以及机密在不在签名范围内。
/// </summary>
public class ConfigDocumentTests
{
    private const string Json = """{"Conclave":{"AutoReview":true}}""";

    [Fact]
    public void A_signed_document_verifies()
    {
        using var publisher = ElectorIdentity.Create();

        Assert.True(ConfigDocument.Sign(publisher, 7, Json).VerifySignature());
    }

    [Fact]
    public void Tampering_with_the_body_breaks_the_signature()
    {
        using var publisher = ElectorIdentity.Create();

        var forged = ConfigDocument.Sign(publisher, 7, Json)
            with { Json = """{"Conclave":{"ClaudeExecutable":"/tmp/evil"}}""" };

        Assert.False(forged.VerifySignature());
    }

    /// <summary>
    /// 一份文档在转发链上任何一跳都验得过。
    /// </summary>
    /// <remarks>
    /// 这是原样转发的全部依据：B 从 A 那里拿到、原封不动给 C，C 验的是 <b>A</b> 的签名，
    /// 跟经手人无关。反过来如果 B 重签一份，同一份内容就会以两个 <c>PublisherId</c> 流动，
    /// <see cref="ConfigDocument.Supersedes"/> 的同版本抢占开始互相覆盖，永远收敛不了。
    /// </remarks>
    [Fact]
    public void A_relayed_document_still_verifies()
    {
        using var author = ElectorIdentity.Create();

        var original = ConfigDocument.Sign(author, 7, Json);

        // 经过一跳：序列化、传输、反序列化，字节原样。
        var relayed = ActaJson.Deserialize<ConfigDocument>(ActaJson.Serialize(original));

        Assert.NotNull(relayed);
        Assert.True(relayed.VerifySignature());
        Assert.Equal(author.Id, relayed.PublisherId);
    }

    /// <summary>
    /// 机密不在签名范围内 —— 它是每一跳现封的。
    /// </summary>
    /// <remarks>
    /// 签进去就没法原样转发了：转发方没有原作者的私钥，重新封一份机密就得重签整个文档。
    /// 机密自身的完整性由 AES-GCM 的标签保证，「是不是给我的」由 ECDH 保证。
    /// </remarks>
    [Fact]
    public void The_offer_carries_secrets_outside_the_signed_document()
    {
        using var author = ElectorIdentity.Create();
        using var node = ElectorIdentity.Create();

        var document = ConfigDocument.Sign(author, 7, Json);

        var offer = new ConfigOffer(
            document,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Lark.AppSecret"] = SecretSealing.Seal("s", node.PublicKey, author, "Lark.AppSecret"),
            });

        // 换掉机密，正文的签名不受影响 —— 正是「谁应答谁现封」要的性质。
        var reoffered = offer with
        {
            Secrets = new Dictionary<string, string>(StringComparer.Ordinal) { ["Lark.AppSecret"] = "AAAA" },
        };

        Assert.True(reoffered.Document.VerifySignature());
    }

    /// <summary>自称的发布者 id 跟公钥指纹对不上，直接判无效。</summary>
    [Fact]
    public void A_publisher_id_that_does_not_match_the_key_is_rejected()
    {
        using var publisher = ElectorIdentity.Create();

        Assert.False((ConfigDocument.Sign(publisher, 7, Json) with { PublisherId = "0000000000000000" })
            .VerifySignature());
    }

    /// <summary>版本大的赢；同版本比发布者指纹，字典序小的赢（跟链上冲突收敛同一套规则）。</summary>
    [Fact]
    public void The_highest_version_wins_and_ties_break_on_the_publisher_id()
    {
        using var a = ElectorIdentity.Create();
        using var b = ElectorIdentity.Create();

        var older = ConfigDocument.Sign(a, 6, Json);
        var newer = ConfigDocument.Sign(b, 7, Json);

        Assert.True(newer.Supersedes(older));
        Assert.False(older.Supersedes(newer));
        Assert.True(newer.Supersedes(null));

        var (low, high) = string.CompareOrdinal(a.Id, b.Id) < 0 ? (a, b) : (b, a);
        Assert.True(ConfigDocument.Sign(low, 7, Json).Supersedes(ConfigDocument.Sign(high, 7, Json)));
        Assert.False(ConfigDocument.Sign(high, 7, Json).Supersedes(ConfigDocument.Sign(low, 7, Json)));
    }
}

using Conclave.Domain;

namespace Conclave.UnitTests;

public class BlockTests
{
    private static Block Sign(ElectorIdentity id, string payloadJson, long index = 0, string? prev = null)
    {
        var unsigned = new Block
        {
            ChainId = "pr:p:1",
            Index = index,
            PrevHash = prev ?? Block.GenesisPrevHash,
            At = DateTimeOffset.Parse("2026-09-08T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            Kind = BlockKind.Summons,
            PayloadJson = payloadJson,
            ElectorId = id.Id,
            PublicKey = id.PublicKey,
            Signature = string.Empty,
        };

        return unsigned with { Signature = id.Sign(unsigned.SigningPayload()) };
    }

    [Fact]
    public void Signed_block_verifies()
    {
        using var id = ElectorIdentity.CreateEphemeral();
        Assert.True(Sign(id, """{"a":1}""").VerifySignature());
    }

    [Fact]
    public void Tampering_with_the_payload_breaks_verification()
    {
        using var id = ElectorIdentity.CreateEphemeral();
        var block = Sign(id, """{"a":1}""");

        var tampered = block with { PayloadJson = """{"a":2}""" };

        Assert.False(tampered.VerifySignature());
        Assert.NotEqual(block.Hash(), tampered.Hash());
    }

    [Fact]
    public void Swapping_in_another_public_key_breaks_the_elector_fingerprint()
    {
        using var mine = ElectorIdentity.CreateEphemeral();
        using var theirs = ElectorIdentity.CreateEphemeral();

        var block = Sign(mine, """{"a":1}""");
        var forged = block with { PublicKey = theirs.PublicKey };

        // ElectorId 是公钥指纹，换了公钥就对不上 —— 冒名写块拦在这里。
        Assert.False(forged.VerifySignature());
    }

    [Fact]
    public void Hash_excludes_the_signature_so_it_is_a_function_of_content_only()
    {
        using var id = ElectorIdentity.CreateEphemeral();
        var a = Sign(id, """{"a":1}""");
        var b = Sign(id, """{"a":1}""");

        // ECDSA 是随机化签名，两次签同一内容签名不同……
        Assert.NotEqual(a.Signature, b.Signature);

        // ……但哈希必须相同，否则「同 index 取 blockHash 小者」这条冲突规则就不确定了。
        Assert.Equal(a.Hash(), b.Hash());
    }

    [Fact]
    public void Prev_hash_links_blocks_into_a_chain()
    {
        using var id = ElectorIdentity.CreateEphemeral();
        var first = Sign(id, """{"n":1}""");
        var second = Sign(id, """{"n":2}""", index: 1, prev: first.Hash());

        Assert.Equal(first.Hash(), second.PrevHash);
        Assert.NotEqual(first.Hash(), second.Hash());
    }
}

using Conclave.Application;
using Conclave.Domain;

namespace Conclave.UnitTests;

/// <summary>
/// 把机密封给指定节点。
/// </summary>
/// <remarks>
/// 它存在的全部理由是 mesh 走明文 HTTP、而 GET 接口按现有约定不校验请求方身份。所以要钉的
/// 不只是「能封能拆」，更是那几种<b>挪用</b>：别人的信封、换了用途的信封、改过一个字节的
/// 信封，全都必须打不开 —— 而且是返回 null，不是抛异常（配置同步不能被一个坏信封带倒）。
/// </remarks>
public class SecretSealingTests
{
    private const string Purpose = "Lark.AppSecret";
    private const string Secret = "cli-secret-9f3a2b";

    [Fact]
    public void The_intended_recipient_can_open_it()
    {
        using var publisher = ElectorIdentity.Create();
        using var node = ElectorIdentity.Create();

        var envelope = SecretSealing.Seal(Secret, node.PublicKey, publisher, Purpose);

        Assert.Equal(Secret, SecretSealing.Open(envelope, publisher.PublicKey, node, Purpose));
    }

    /// <summary>封给别人的信封，自己打不开 —— 这是「端口开着也没关系」的全部依据。</summary>
    [Fact]
    public void Someone_else_cannot_open_it()
    {
        using var publisher = ElectorIdentity.Create();
        using var node = ElectorIdentity.Create();
        using var eavesdropper = ElectorIdentity.Create();

        var envelope = SecretSealing.Seal(Secret, node.PublicKey, publisher, Purpose);

        Assert.Null(SecretSealing.Open(envelope, publisher.PublicKey, eavesdropper, Purpose));
    }

    /// <summary>换个用途拆不开。共享密钥是静态的，不把用途绑进 AAD 的话信封能被挪用。</summary>
    [Fact]
    public void An_envelope_cannot_be_reused_for_another_purpose()
    {
        using var publisher = ElectorIdentity.Create();
        using var node = ElectorIdentity.Create();

        var envelope = SecretSealing.Seal(Secret, node.PublicKey, publisher, Purpose);

        Assert.Null(SecretSealing.Open(envelope, publisher.PublicKey, node, "Something.Else"));
    }

    /// <summary>发信方对不上就拆不开 —— 冒充发布者发一份配置，机密解不出来。</summary>
    [Fact]
    public void A_forged_sender_cannot_be_opened()
    {
        using var publisher = ElectorIdentity.Create();
        using var node = ElectorIdentity.Create();
        using var impostor = ElectorIdentity.Create();

        var envelope = SecretSealing.Seal(Secret, node.PublicKey, publisher, Purpose);

        Assert.Null(SecretSealing.Open(envelope, impostor.PublicKey, node, Purpose));
    }

    [Fact]
    public void A_tampered_envelope_is_rejected()
    {
        using var publisher = ElectorIdentity.Create();
        using var node = ElectorIdentity.Create();

        var bytes = Convert.FromBase64String(
            SecretSealing.Seal(Secret, node.PublicKey, publisher, Purpose));
        bytes[^1] ^= 0xFF;

        Assert.Null(SecretSealing.Open(Convert.ToBase64String(bytes), publisher.PublicKey, node, Purpose));
    }

    /// <summary>坏输入返回 null 而不是抛 —— 一个畸形信封不该把配置同步带倒。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not base64!!")]
    [InlineData("YWJj")]
    public void Garbage_returns_null_instead_of_throwing(string envelope)
    {
        using var publisher = ElectorIdentity.Create();
        using var node = ElectorIdentity.Create();

        Assert.Null(SecretSealing.Open(envelope, publisher.PublicKey, node, Purpose));
    }

    /// <summary>每次封出来的密文都不同 —— 静态共享密钥下，nonce 必须每次新取。</summary>
    /// <remarks>
    /// GCM 在同一个密钥上重用 nonce 是灾难性的（泄漏明文异或、可伪造标签）。而这里两端
    /// 都是长期密钥、协商出的密钥永远一样，所以唯一的保护就是 nonce。
    /// </remarks>
    [Fact]
    public void Every_sealing_uses_a_fresh_nonce()
    {
        using var publisher = ElectorIdentity.Create();
        using var node = ElectorIdentity.Create();

        var first = SecretSealing.Seal(Secret, node.PublicKey, publisher, Purpose);
        var second = SecretSealing.Seal(Secret, node.PublicKey, publisher, Purpose);

        Assert.NotEqual(first, second);
        Assert.Equal(Secret, SecretSealing.Open(first, publisher.PublicKey, node, Purpose));
        Assert.Equal(Secret, SecretSealing.Open(second, publisher.PublicKey, node, Purpose));
    }
}

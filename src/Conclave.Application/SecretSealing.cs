using System.Security.Cryptography;
using System.Text;
using Conclave.Domain;

namespace Conclave.Application;

/// <summary>
/// 把一小段机密封给<b>某一个指定节点</b>，只有它解得开。
/// </summary>
/// <remarks>
/// <para>
/// 为配置同步而存在：<c>Lark.AppSecret</c> 要能随配置散到全 mesh，但 mesh 的 HTTP 是明文
/// （<c>MeshHttpServer</c> 监听的是 <c>http://</c>），而且 <c>GET</c> 那几个接口按现有约定
/// <b>不校验请求方身份</b>——「同网段能连上这个端口的都读得到」。所以机密不能就那么摆在
/// 响应体里：那等于任何一台能连上 47708 的机器 curl 一下就拿到，不需要嗅探、不需要在链路上。
/// </para>
/// <para>
/// 封装之后这两个问题一起消失：线上是密文，端口开着也没关系，只有持对应私钥的那个节点
/// 解得开。代价是<b>没有前向保密</b>——两边都是静态长期密钥，协商出来的共享密钥永远一样，
/// 私钥泄漏能解开此前抓到的所有信封。对「内网 + 一个只有 im:message 权限的应用」够用，
/// 但别拿它封更值钱的东西。
/// </para>
/// <para>
/// 信封格式：<c>nonce(12) | tag(16) | 密文</c>，整体 base64。
/// </para>
/// </remarks>
public static class SecretSealing
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <summary>
    /// 把 <paramref name="plaintext"/> 封给 <paramref name="recipientPublicKey"/> 那个节点。
    /// </summary>
    /// <param name="plaintext">机密原文。</param>
    /// <param name="recipientPublicKey">收件节点的公钥（SubjectPublicKeyInfo 的 base64）。</param>
    /// <param name="sender">发信节点的身份，用它的私钥参与协商。</param>
    /// <param name="purpose">
    /// 这个信封是干什么用的，例如 <c>Lark.AppSecret</c>。它进 AAD，所以<b>封和拆必须一致</b>。
    /// </param>
    public static string Seal(
        string plaintext, string recipientPublicKey, ElectorIdentity sender, string purpose)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(recipientPublicKey);
        ArgumentNullException.ThrowIfNull(sender);

        var key = Derive(sender, recipientPublicKey);
        var aad = Aad(purpose, sender.Id, ElectorIdentity.FingerprintOf(recipientPublicKey));

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var envelope = new byte[NonceBytes + TagBytes + plain.Length];

        nonce.CopyTo(envelope.AsSpan(0, NonceBytes));

        using (var gcm = new AesGcm(key, TagBytes))
        {
            gcm.Encrypt(
                nonce,
                plain,
                envelope.AsSpan(NonceBytes + TagBytes),
                envelope.AsSpan(NonceBytes, TagBytes),
                aad);
        }

        CryptographicOperations.ZeroMemory(key);
        return Convert.ToBase64String(envelope);
    }

    /// <summary>
    /// 拆一个封给自己的信封。
    /// </summary>
    /// <returns>
    /// 解不开返回 <c>null</c> —— 不是给自己的、被改过、发信方对不上、格式坏了，全都走这条。
    /// 调用方要把它当「这条配置里的机密取不到」处理，而不是崩掉。
    /// </returns>
    public static string? Open(
        string envelopeBase64, string senderPublicKey, ElectorIdentity recipient, string purpose)
    {
        ArgumentNullException.ThrowIfNull(recipient);

        if (string.IsNullOrEmpty(envelopeBase64) || string.IsNullOrEmpty(senderPublicKey))
        {
            return null;
        }

        byte[]? key = null;
        try
        {
            var envelope = Convert.FromBase64String(envelopeBase64);
            if (envelope.Length < NonceBytes + TagBytes)
            {
                return null;
            }

            key = Derive(recipient, senderPublicKey);
            var aad = Aad(purpose, ElectorIdentity.FingerprintOf(senderPublicKey), recipient.Id);
            var plain = new byte[envelope.Length - NonceBytes - TagBytes];

            using var gcm = new AesGcm(key, TagBytes);
            gcm.Decrypt(
                envelope.AsSpan(0, NonceBytes),
                envelope.AsSpan(NonceBytes + TagBytes),
                envelope.AsSpan(NonceBytes, TagBytes),
                plain,
                aad);

            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    /// <summary>ECDH 协商出 32 字节的 AES 密钥。两边算出来的必然相同。</summary>
    private static byte[] Derive(ElectorIdentity self, string otherPublicKeyBase64)
    {
        using var mine = self.CreateAgreement();
        using var theirs = ECDiffieHellman.Create();
        theirs.ImportSubjectPublicKeyInfo(Convert.FromBase64String(otherPublicKeyBase64), out _);
        return mine.DeriveKeyFromHash(theirs.PublicKey, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// 附加认证数据：把用途和两端身份绑进密文。
    /// </summary>
    /// <remarks>
    /// 共享密钥是静态的，不绑的话一个信封可以被挪用 —— 换个用途重放（拿
    /// <c>Lark.AppSecret</c> 的信封冒充另一个字段），或者转发给第三个节点。绑上之后
    /// 这两种挪用都会在 GCM 的认证标签上失败。
    /// </remarks>
    private static byte[] Aad(string purpose, string senderId, string recipientId)
        => Encoding.UTF8.GetBytes($"{purpose}|{senderId}|{recipientId}");
}

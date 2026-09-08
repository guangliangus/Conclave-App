using System.Security.Cryptography;
using System.Text;

namespace Conclave.Domain;

/// <summary>
/// Acta（会议录）上的一个区块。
/// </summary>
/// <remarks>
/// 刻意不做全局单链 —— 每个 PR 一条独立链（<see cref="ChainId"/>），
/// 于是不需要全局排序，也就不需要 PoW/BFT 那一套。见 docs/DESIGN.md §5。
/// </remarks>
public sealed record Block
{
    /// <summary>创世块的 PrevHash。64 个 0，与 SHA-256 十六进制长度一致。</summary>
    public const string GenesisPrevHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public required string ChainId { get; init; }

    public required long Index { get; init; }

    public required string PrevHash { get; init; }

    public required DateTimeOffset At { get; init; }

    public required BlockKind Kind { get; init; }

    public required string PayloadJson { get; init; }

    public required string ElectorId { get; init; }

    public required string PublicKey { get; init; }

    /// <summary>对 <see cref="SigningPayload"/> 的 ECDsa P-256 签名，base64。</summary>
    public required string Signature { get; init; }

    /// <summary>
    /// 参与哈希与签名的规范化文本。
    /// </summary>
    /// <remarks>
    /// 刻意不含 <see cref="Signature"/>：ECDSA 签名是随机化的，同一节点对同一内容
    /// 重签会得到不同签名。把签名排除在外，区块哈希才是内容的确定性函数 ——
    /// 这是 §5 冲突解决（同 index 取 blockHash 字典序小者）能成立的前提。
    /// </remarks>
    public string SigningPayload() => string.Join('|',
        ChainId,
        Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PrevHash,
        At.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        Kind.ToString(),
        PayloadJson,
        ElectorId);

    /// <summary>区块哈希，小写十六进制。</summary>
    public string Hash() => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(SigningPayload()))).ToLowerInvariant();

    /// <summary>验证签名确实出自 <see cref="PublicKey"/>，且公钥指纹与 <see cref="ElectorId"/> 一致。</summary>
    public bool VerifySignature()
        => ElectorIdentity.FingerprintOf(PublicKey) == ElectorId
        && ElectorIdentity.Verify(PublicKey, SigningPayload(), Signature);

    public T? Payload<T>() => ActaJson.Deserialize<T>(PayloadJson);
}

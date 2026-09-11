using System.Security.Cryptography;
using System.Text;

namespace Conclave.Domain;

/// <summary>
/// Acta（会议录）上的一个区块。
/// </summary>
/// <remarks>
/// <para>
/// 全 mesh 共用一条链，<see cref="ChainId"/> 恒为 <see cref="Acta.ChainId"/> ——
/// 不是每个 PR 一条。早期确实是每 PR 一条（那样单写者居多、撞索引是罕见情况），
/// 改成单链之后并发写<b>必然</b>争同一个 index，让位重挂因此从异常路径变成常态路径，
/// 见 <see cref="Acta.ChainId"/> 与 <c>SqliteActa.RebaseAsync</c>。
/// </para>
/// <para>
/// 换来的是一条真正的全局时间线：谁在什么时候评审了什么，按链序读一遍就是。
/// 仍然不需要 PoW/BFT —— 冲突解决靠「同 index 取 <see cref="Hash"/> 字典序小者」，
/// 是纯函数、确定性收敛。见 docs/DESIGN.md §5。
/// </para>
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

    /// <summary>
    /// 内容身份：同一块内容不论落在哪个索引上都是这个值。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不能拿 <see cref="Hash"/> 当身份。</b> 它含 <see cref="Index"/> 与
    /// <see cref="PrevHash"/>，而让位重挂（<c>SqliteActa.RebaseAsync</c>）改的正好是这两个 ——
    /// 同一张票每重挂一次就换一个块哈希。于是「这块我是不是已经有了」这个判断认不出
    /// 重挂过的自己：对端把重挂前那一版推回来，本地当成新块又挂一遍，还顺带再触发一次
    /// 索引冲突、再重挂一次，循环放大。
    /// </para>
    /// <para>
    /// 实测代价：一张票在链上挂了 41 遍，账单的次数与金额被放大 8 倍，
    /// 单节点一天打出 622 次索引冲突。
    /// </para>
    /// <para>
    /// 所以身份只取<b>重挂动不了</b>的那几个字段。<see cref="Signature"/> 同样排除在外，
    /// 理由跟 <see cref="SigningPayload"/> 一样：ECDSA 重签会得到不同签名。
    /// <see cref="PublicKey"/> 也不必进来 —— 它的指纹就是 <see cref="ElectorId"/>。
    /// </para>
    /// </remarks>
    public string ContentId() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('|',
            ChainId,
            At.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Kind.ToString(),
            PayloadJson,
            ElectorId)))).ToLowerInvariant();

    /// <summary>验证签名确实出自 <see cref="PublicKey"/>，且公钥指纹与 <see cref="ElectorId"/> 一致。</summary>
    public bool VerifySignature()
        => ElectorIdentity.FingerprintOf(PublicKey) == ElectorId
        && ElectorIdentity.Verify(PublicKey, SigningPayload(), Signature);

    public T? Payload<T>() => ActaJson.Deserialize<T>(PayloadJson);
}

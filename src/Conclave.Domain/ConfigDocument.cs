using System.Globalization;

namespace Conclave.Domain;

/// <summary>
/// 一份签过名的、可以在 mesh 里传播的配置。
/// </summary>
/// <remarks>
/// <para>
/// 版本号单调递增，收方取<b>版本最大的</b>那份。同版本冲突时比发布者指纹的字典序 ——
/// 跟链上「同 index 冲突取哈希字典序小者」是同一套收敛规则：纯函数、确定性，
/// 不需要任何协商消息。
/// </para>
/// <para>
/// <b>这份文档是不可变的，而且要原样转发。</b> 节点 B 从 A 同步到 v7 之后，C 来问 B，
/// B 给出去的必须还是 A 签的那一份，不能以自己的身份重签 —— 重签会让同一份内容以不同
/// <see cref="PublisherId"/> 在网里流动，<see cref="Supersedes"/> 的同版本抢占就不再收敛，
/// 两个节点会反复互相覆盖。原样转发则身份是 <c>(Version, PublisherId)</c>，全网取最大，
/// 确定性收敛。
/// </para>
/// <para>
/// <b>机密不在这里。</b> 信封是用 ECDH 封给<b>某一把公钥</b>的，A 封给 B 的那份 C 拿到也
/// 解不开 —— 塞进可转发的文档里没有意义。机密由<b>当前应答的那个节点</b>按请求方的公钥
/// 现封一份，见 <see cref="ConfigOffer"/>。
/// </para>
/// </remarks>
public sealed record ConfigDocument
{
    /// <summary>配置版本，单调递增。</summary>
    public required long Version { get; init; }

    /// <summary>发布节点的公钥指纹。</summary>
    public required string PublisherId { get; init; }

    /// <summary>发布节点的公钥，收方用它验签。</summary>
    public required string PublicKey { get; init; }

    /// <summary>
    /// 可同步的那些键，序列化成 <c>{"Conclave":{…}}</c> 的形状。
    /// </summary>
    /// <remarks>
    /// 刻意是<b>字符串</b>而不是结构化对象：签名要对着传输的原文算，先反序列化再重新序列化
    /// 一遍会因为属性顺序或格式的细微差别让验签失败 —— <see cref="SignedLiveState"/>
    /// 用的是同一个办法，理由也一样。
    /// </remarks>
    public required string Json { get; init; }

    public required string Signature { get; init; }

    /// <summary>签名有效，且公钥指纹跟自称的发布者 id 一致。</summary>
    /// <remarks>
    /// 转发链上<b>每一跳都要验一次</b>：中间那台可能是恶意的，也可能只是把文件改坏了。
    /// 验的是<b>原作者</b>的签名，跟从谁手里拿到的无关 —— 这正是原样转发换来的好处。
    /// </remarks>
    public bool VerifySignature()
        => ElectorIdentity.FingerprintOf(PublicKey) == PublisherId
        && ElectorIdentity.Verify(PublicKey, SigningPayload(Version, PublisherId, PublicKey, Json), Signature);

    /// <summary>
    /// 参与签名的规范化文本。
    /// </summary>
    /// <remarks>
    /// 只覆盖可转发的那部分。机密刻意<b>不</b>在签名范围内 —— 它是每一跳现封的，签进去就
    /// 没法原样转发了。机密自身的完整性由 AES-GCM 的认证标签保证，「这段密文是不是给我的」
    /// 由 ECDH 保证：不是给我的就解不开。
    /// </remarks>
    public static string SigningPayload(
        long version,
        string publisherId,
        string publicKey,
        string json)
        => string.Join(
            '|',
            version.ToString(CultureInfo.InvariantCulture),
            publisherId,
            publicKey,
            json);

    /// <summary>
    /// 本节点签发一份<b>新</b>配置。
    /// </summary>
    /// <remarks>
    /// 只在本机作者了一份新的时才调 —— 也就是人把 <c>meshsettings.json</c> 里的版本号调高了。
    /// 从 mesh 同步来的那份要原样存、原样转发，绝不能过这里重签。
    /// </remarks>
    public static ConfigDocument Sign(ElectorIdentity publisher, long version, string json)
    {
        ArgumentNullException.ThrowIfNull(publisher);

        return new ConfigDocument
        {
            Version = version,
            PublisherId = publisher.Id,
            PublicKey = publisher.PublicKey,
            Json = json,
            Signature = publisher.Sign(
                SigningPayload(version, publisher.Id, publisher.PublicKey, json)),
        };
    }

    /// <summary>
    /// 这一份比 <paramref name="other"/> 更该采用。
    /// </summary>
    /// <remarks>
    /// 版本大的赢；同版本比发布者指纹，字典序小的赢。<c>null</c> 视为「本地什么都没有」。
    /// </remarks>
    public bool Supersedes(ConfigDocument? other)
        => other is null
        || Version > other.Version
        || (Version == other.Version
            && string.CompareOrdinal(PublisherId, other.PublisherId) < 0);
}

/// <summary>
/// 一次 <c>GET /config</c> 的应答：可转发的配置正文 + 现封给请求方的机密。
/// </summary>
/// <remarks>
/// 拆成两半是 gossip 模型的必然结果。<see cref="Document"/> 是原作者签的、不可变的，谁手上有
/// 就原样给出去；<see cref="Secrets"/> 是<b>应答方</b>拿自己手上的明文、按请求方的公钥现封的，
/// 每次应答都不同，也没法被转发 —— 那恰恰是要的效果：想要机密就自己去问，
/// 别人替你要来的那份你也解不开。
/// </remarks>
/// <param name="Document">配置正文，用原作者的公钥验签。</param>
/// <param name="Secrets">键是用途（如 <c>Lark.AppSecret</c>），值是封给请求方的信封。</param>
public sealed record ConfigOffer(
    ConfigDocument Document,
    IReadOnlyDictionary<string, string> Secrets)
{
    /// <summary>不带机密的应答。</summary>
    public static ConfigOffer Plain(ConfigDocument document)
        => new(document, new Dictionary<string, string>(StringComparer.Ordinal));
}

namespace Conclave.Domain;

/// <summary>
/// 一个节点向 mesh 广播的签名心跳。
/// </summary>
/// <remarks>
/// <para>
/// 心跳<b>必须</b>签名。席位分配完全由心跳里的字段决定（有哪些 repo、有哪些 project、
/// 当前负载），伪造一个「空闲、什么 repo 都有」的心跳就能把席位全吸到自己名下然后永不出票 ——
/// 那会让所有 PR 卡在弃权重试的循环里。签名 + 白名单一起把这条路堵掉。
/// </para>
/// <para>
/// <see cref="Elector.LastHeartbeat"/> 在签名范围内，所以重放旧心跳会在
/// <see cref="IsFresh"/> 处被判过期。
/// </para>
/// </remarks>
/// <param name="Elector">发信节点的公开状态。</param>
/// <param name="Signature">对 <see cref="SigningPayload"/> 的签名，base64。</param>
public sealed record Beacon(Elector Elector, string Signature)
{
    /// <summary>允许的时钟偏差。心跳落在未来超过这个量就当作可疑。</summary>
    public static TimeSpan ClockSkew => TimeSpan.FromSeconds(30);

    /// <summary>
    /// 参与签名的规范化文本。
    /// </summary>
    /// <remarks>
    /// 显式拼字符串而不是序列化整个对象：JSON 的属性顺序依赖反射顺序，
    /// 换个编译器版本或加个字段就可能让老节点验不过新节点的签名。
    /// </remarks>
    public static string SigningPayload(Elector elector)
    {
        ArgumentNullException.ThrowIfNull(elector);
        return string.Join('|',
            elector.Id,
            elector.PublicKey,
            elector.Endpoint,
            elector.AzIdentity,
            string.Join(',', elector.Repos),
            string.Join(',', elector.Projects),
            elector.RunningJobs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            elector.MaxConcurrent.ToString(System.Globalization.CultureInfo.InvariantCulture),
            elector.Reviews24h.ToString(System.Globalization.CultureInfo.InvariantCulture),
            elector.LastHeartbeat.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>签名有效，且公钥指纹与自称的 Id 一致。</summary>
    public bool VerifySignature()
        => ElectorIdentity.FingerprintOf(Elector.PublicKey) == Elector.Id
        && ElectorIdentity.Verify(Elector.PublicKey, SigningPayload(Elector), Signature);

    /// <summary>心跳既没过期、也没有落在未来太远。</summary>
    public bool IsFresh(DateTimeOffset now)
        => Elector.LastHeartbeat <= now + ClockSkew && Elector.IsAlive(now);
}

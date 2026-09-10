namespace Conclave.Domain;

/// <summary>
/// 一个节点向 mesh 广播的签名心跳。
/// </summary>
/// <remarks>
/// <para>
/// 心跳<b>必须</b>签名。席位分配完全由心跳里的字段决定（有哪些 project、当前负载、
/// 近期出票数），伪造一个「空闲、什么 project 都有权限」的心跳就能把席位全吸到自己名下
/// 然后永不出票 —— 那会让所有 PR 卡在弃权重试的循环里。签名 + 白名单一起把这条路堵掉。
/// </para>
/// <para>
/// <see cref="Elector.LastHeartbeat"/> 在签名范围内，所以重放旧心跳会在
/// <see cref="IsFresh"/> 处被判过期。
/// </para>
/// </remarks>
/// <param name="Elector">发信节点的公开状态。</param>
/// <param name="StateVersion">
/// 发信节点实时状态（<see cref="LiveState"/>）的版本号。
/// <para>
/// 队列、正在评审、认领这些东西塞不进心跳 —— 实测心跳本体已 1085 字节，一条队列项约 279
/// 字节，34 条就 10.5KB，远超 MTU。所以心跳只带这个版本号，对端看到它变了才去拉一次
/// <c>GET /state</c>：稳态下零额外流量，跟「区块走 HTTP、存在感走 UDP」是同一套分工。
/// </para>
/// </param>
/// <param name="Signature">对 <see cref="SigningPayload"/> 的签名，base64。</param>
public sealed record Beacon(Elector Elector, long StateVersion, string Signature)
{
    /// <summary>
    /// 当前心跳协议版本。改 <see cref="SigningPayload"/> 的格式就 +1。
    /// </summary>
    /// <remarks>
    /// 历史：1 = 初版（带 Repos）；2 = 去掉 Repos、加 StateVersion 与 Utilization；
    /// 3 = 加 AppVersion 与 ProtocolVersion 本身。每一次都是「整个 mesh 必须同时升级」，
    /// 区别是从 3 开始不一致会被明确说出来，而不是表现成掉线。
    /// </remarks>
    public const int ProtocolVersion = 3;

    /// <summary>对端心跳的协议版本跟本机一致。不一致的连签名都不必验 —— 载荷格式就不同。</summary>
    public bool IsCompatible => Elector.ProtocolVersion == ProtocolVersion;

    /// <summary>允许的时钟偏差。心跳落在未来超过这个量就当作可疑。</summary>
    public static TimeSpan ClockSkew => TimeSpan.FromSeconds(30);

    /// <summary>
    /// 参与签名的规范化文本。
    /// </summary>
    /// <remarks>
    /// 显式拼字符串而不是序列化整个对象：JSON 的属性顺序依赖反射顺序，
    /// 换个编译器版本或加个字段就可能让老节点验不过新节点的签名。
    /// <para>
    /// ⚠️ 改这个方法就是改线上协议：新旧节点会互相验签失败，整个 mesh 必须同时升级。
    /// 去掉 <c>Repos</c>、加上 <c>StateVersion</c> 都是这样的破坏性变更。
    /// </para>
    /// </remarks>
    public static string SigningPayload(Elector elector, long stateVersion)
    {
        ArgumentNullException.ThrowIfNull(elector);
        return string.Join('|',
            stateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            elector.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            elector.AppVersion,
            elector.Id,
            elector.PublicKey,
            elector.Endpoint,
            elector.AzIdentity,
            string.Join(',', elector.Projects),
            elector.RunningJobs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            elector.MaxConcurrent.ToString(System.Globalization.CultureInfo.InvariantCulture),
            elector.Reviews24h.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // 固定 4 位小数的定点格式：double 的默认 ToString 在不同运行时/文化下位数可能不同，
            // 而这段文本要逐字节参与签名 —— 格式一漂移，对端就验不过签。
            elector.Utilization.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            elector.LastHeartbeat.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>签名有效，且公钥指纹与自称的 Id 一致。</summary>
    public bool VerifySignature()
        => ElectorIdentity.FingerprintOf(Elector.PublicKey) == Elector.Id
        && ElectorIdentity.Verify(Elector.PublicKey, SigningPayload(Elector, StateVersion), Signature);

    /// <summary>心跳既没过期、也没有落在未来太远。</summary>
    public bool IsFresh(DateTimeOffset now)
        => Elector.LastHeartbeat <= now + ClockSkew && Elector.IsAlive(now);
}

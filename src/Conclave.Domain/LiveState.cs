using System.Security.Cryptography;
using System.Text;

namespace Conclave.Domain;

/// <summary>
/// 队列里的一项：某个节点发现的、在 Azure DevOps 上仍然活跃的 PR 版本。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是「排队中」的唯一来源。</b> 以前队列是从链上推出来的（有 Summons、无 Promulgation），
/// 那是一条纯历史查询 —— PR 在 ADO 上被 merge 之后只是从活跃列表里消失，链上那条
/// Summons 没有任何人去清，于是它永远留在队列里（实测积到 41 个 revision 里 36 个是僵尸）。
/// </para>
/// <para>
/// 改成实时状态之后，队列 = 各节点当前上报的这些项的并集。PR 一关闭就从发现节点的上报里
/// 消失，队列自动干净 —— 不需要任何清理逻辑，也不需要新增区块类型。
/// </para>
/// </remarks>
/// <param name="Revision">幂等键。</param>
/// <param name="Pr">发现时读到的 PR 元数据快照。</param>
/// <param name="Quorum">
/// 应有席位数，由<b>发现节点</b>算出来带上，而不是各节点各自算。quorum 策略是每台机器
/// 自己配的，各算一次可能得出不同的数，那样席位表就不一致了。以前这个数写在 Summons
/// 块上，现在改由实时状态携带，一致性的保证方式没变。
/// </param>
/// <param name="RulesFingerprint">当时生效的规则指纹（敏感路径清单 + quorum 策略）。</param>
public sealed record QueuedRevision(
    Revision Revision,
    PrMeta Pr,
    int Quorum,
    string RulesFingerprint)
{
    /// <summary>
    /// 这一项允许作者评自己的 PR。
    /// </summary>
    /// <remarks>
    /// 跟 <see cref="Quorum"/> 完全同一个道理：由<b>发现节点</b>按自己的配置决定并带上，
    /// 各节点读同一个值，所以席位表不会分叉。各自读本机配置的话，一台允许、一台不允许，
    /// 两边算出的合格节点集就不同 —— 那种分叉不报错，只会变成重复评审或集体旁观。
    /// <para>
    /// 是 init 属性而不是构造参数：老节点上报的 JSON 里没有这个字段，反序列化成 false，
    /// 正好等于「保持原来的硬规则」，是安全的默认。
    /// </para>
    /// </remarks>
    public bool AllowSelfReview { get; init; }
}

/// <summary>某个节点正在进行的一次评审。</summary>
/// <remarks>
/// 「正在评审」列表就是各节点这个字段的并集 —— 谁在评你的 PR、评了多久，一眼可见。
/// <para>
/// 节点崩掉之后它不再广播，别的节点在 <see cref="Elector.HeartbeatWindow"/> 内发现它掉线，
/// 这个 PR 自动回到队列。<b>不需要写 Recess 块，也不需要 10 分钟的席位超时</b>。
/// </para>
/// </remarks>
public sealed record ActiveReview(string RevisionId, int Round, DateTimeOffset StartedAt);

/// <summary>主动认领：某个节点声明它要评这个 PR，但还没开跑。</summary>
/// <remarks>
/// 手动认领<b>优先于</b>加权 HRW 的自动分配 —— 人明确要评的，规则不该抢走。
/// 冲突解决见 <see cref="ReviewClaim.Winner"/>。
/// </remarks>
public sealed record ReviewClaim(string RevisionId, string ElectorId, DateTimeOffset At)
{
    /// <summary>
    /// 同一个 revision 上多个认领时谁胜出：先到者，同刻取 electorId 字典序小者。
    /// </summary>
    /// <remarks>
    /// 纯函数，所以各节点独立算出同一结果，不需要协商 —— 跟席位表和索引冲突让位是同一个路子。
    /// 输的一方自己撤销。
    /// <para>
    /// 时刻相同时必须再比 id：只按时间会在两个节点同一秒认领时永远分不出胜负，
    /// 两边都以为自己赢了，于是同一个 PR 被评两遍。
    /// </para>
    /// </remarks>
    public static ReviewClaim? Winner(IEnumerable<ReviewClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        return claims
            .OrderBy(c => c.At)
            .ThenBy(c => c.ElectorId, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}

/// <summary>把自己的 PR 指派给某个节点评审的请求。对方同意才生效。</summary>
/// <param name="Id">请求 id，回复时用它对上。</param>
/// <param name="RevisionId">要评的 PR 版本。</param>
/// <param name="From">发起者（一般是 PR 作者所在的节点）。</param>
/// <param name="To">被请求的节点。</param>
/// <param name="At">发起时刻。</param>
/// <param name="Note">给对方看的一句话，可空。</param>
/// <remarks>
/// 这条路顺带解决一个硬规则带来的死角：<b>作者自己的 PR 在单节点 mesh 上永远出不去</b>
/// —— <see cref="SeatAssignment.Eligible"/> 排除了作者本人，于是席位表为空、永远没人评。
/// 指派给别的节点正是它的出路。
/// </remarks>
public sealed record AssignmentRequest(
    string Id,
    string RevisionId,
    string From,
    string To,
    DateTimeOffset At,
    string? Note = null);

/// <summary>指派请求的答复。</summary>
public sealed record AssignmentReply(string Id, string RevisionId, bool Accepted, string? Reason = null);

/// <summary>
/// 带签名的指派消息，<c>POST /assignments</c> 与 <c>POST /assignments/reply</c> 的请求体。
/// </summary>
/// <remarks>
/// <para>
/// 必须签名，理由跟心跳和实时状态一样：没有签名的话，同网段任何人都能伪造一条
/// 「某某请你评这个 PR」，或者伪造一条「我同意了」把活推给别人。
/// 收方验四件事：签名有效、公钥指纹与自称 id 一致、在白名单内、<see cref="From"/> 与
/// 发布者一致。
/// </para>
/// <para>
/// 跟 <see cref="SignedLiveState"/> 一样<b>签传输的 JSON 原文</b>，收方就着收到的字节验，
/// 中间不做任何重排 —— 嵌套结构做规范化序列化太容易在加字段时悄悄改变字节序。
/// </para>
/// </remarks>
/// <param name="From">发布者的公钥指纹。</param>
/// <param name="PublicKey">发布者公钥。</param>
/// <param name="BodyJson">消息本体的 JSON 原文（<see cref="AssignmentRequest"/> 或 <see cref="AssignmentReply"/>）。</param>
/// <param name="Signature">对 <see cref="BodyJson"/> 的签名，base64。</param>
public sealed record SignedAssignment(
    string From,
    string PublicKey,
    string BodyJson,
    string Signature)
{
    public bool VerifySignature()
        => ElectorIdentity.FingerprintOf(PublicKey) == From
        && ElectorIdentity.Verify(PublicKey, BodyJson, Signature);

    public T? Body<T>()
    {
        try
        {
            return ActaJson.Deserialize<T>(BodyJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return default;
        }
    }

    public static SignedAssignment Sign<T>(ElectorIdentity identity, T body)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var json = ActaJson.Serialize(body);
        return new SignedAssignment(identity.Id, identity.PublicKey, json, identity.Sign(json));
    }
}

/// <summary>
/// 一个节点向 mesh 广播的实时状态。
/// </summary>
/// <remarks>
/// <para>
/// <b>刻意不上链。</b> 链（Acta）只记<b>已经完成</b>的评审（Ballot / Promulgation）——
/// 那是需要不可篡改、可审计的部分。而「队列里有什么、谁正在评、谁认领了」是随时在变的
/// 协调状态，写进不可变的链里只会留下一地永远不会被清掉的历史。
/// </para>
/// <para>
/// <b>为什么不塞进心跳。</b> 实测心跳本体（34 个 project）已经 1085 字节，一条队列项约
/// 279 字节 —— 10 条就 3.8KB、34 条 10.5KB，远超以太网 MTU，会分片甚至丢包。所以走
/// 「UDP 心跳只带 <see cref="Version"/>，HTTP 拉完整状态」：跟现在「区块走 HTTP、
/// 存在感走 UDP」是同一套分工。
/// </para>
/// </remarks>
public sealed record LiveState
{
    public static LiveState Empty { get; } = new();

    /// <summary>
    /// 状态版本，单调递增。
    /// </summary>
    /// <remarks>
    /// 对端在心跳里看到这个数变了才去拉一次 <c>GET /state</c> —— 稳态下不产生任何 HTTP 流量。
    /// 用计数而不是内容哈希：哈希要先把状态序列化一遍才能算，而这个数每次改状态都要更新。
    /// </remarks>
    public long Version { get; init; }

    /// <summary>本节点这一片轮询到的、仍然活跃的 PR。</summary>
    public IReadOnlyList<QueuedRevision> Discovered { get; init; } = [];

    /// <summary>本节点正在跑的评审。</summary>
    public IReadOnlyList<ActiveReview> Reviewing { get; init; } = [];

    /// <summary>本节点认领了但还没开跑的。</summary>
    public IReadOnlyList<ReviewClaim> Claims { get; init; } = [];

    /// <summary>等本节点确认的指派请求。</summary>
    public IReadOnlyList<AssignmentRequest> Pending { get; init; } = [];

    /// <summary>
    /// 本节点各个额度窗口的原始读数（会话 5h / 周 7d），只带参与入席判定的那些；
    /// 真值读不到（退到按预算折算）或节点太旧没上报时为空。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Elector.Utilization"/> 是这些窗口经 <c>UsagePressure</c> 折算出来的<b>一个</b>
    /// 标量，席位规则只认它，它也只需要一个数。但「到底是会话额度快满了还是周额度快满了」
    /// 压不进一个数里 —— 而看别人那一行时想知道的恰恰是这个。以前这份明细只有本节点自己有，
    /// 于是同一张表上本机画「5h 31% / 7d 77%」、别人画「压力 26%」，两种不是一回事的数并排摆着。
    /// </para>
    /// <para>
    /// <b>为什么在这里而不是挂到 <see cref="Elector"/> 上随心跳走。</b> 心跳走 UDP，而它
    /// <b>已经没有余量了</b>：实测 35 个 project 的心跳 1418 字节，离以太网 MTU（1472 =
    /// 1500 - 20 IP - 8 UDP）只剩 54 字节，两条窗口明细就是 175 字节，直接把它推过线。
    /// 分片的 UDP 是网络设备最爱静默丢的那种包，表现是整台机器从 mesh 里消失、没有任何报错。
    /// 走这条 HTTP 通道则一点 MTU 预算都不占，还顺带被
    /// <see cref="SignedLiveState"/> 签名保护 —— 挂在心跳上就只能像
    /// <see cref="Elector.ClaudeVersion"/> 那样裸奔（加进签名载荷 = 全 mesh 同时升级）。
    /// 这跟队列当初为什么不塞进心跳是同一个理由、同一套分工。
    /// </para>
    /// <para>
    /// 代价是它跟着 <see cref="Version"/> 走：额度读数一变，对端下一轮就会拉一次
    /// <c>GET /state</c>。所以写它的那一处要先比一比再决定改不改 —— 额度每轮都会被重新
    /// 算一遍，不比就等于每轮都在改。
    /// </para>
    /// <para>
    /// 写 <see cref="Discovered"/> 的 <c>DiscoveryService.Publish</c> 也是同一套做法，
    /// 理由相同 —— 它原先每轮无条件重写，于是队列一动没动，版本也每轮都涨。
    /// </para>
    /// </remarks>
    public IReadOnlyList<UsageWindow> UsageWindows { get; init; } = [];

    /// <summary>本节点占着某个 revision（正在评或已认领）。</summary>
    public bool Holds(string revisionId)
        => Reviewing.Any(r => r.RevisionId == revisionId)
        || Claims.Any(c => c.RevisionId == revisionId);
}

/// <summary>
/// 带签名的实时状态，<c>GET /state</c> 的响应体。
/// </summary>
/// <remarks>
/// <para>
/// <b>必须签名，理由跟心跳一样。</b> 伪造一份「我正在评所有 PR」的状态就能让别的节点全部
/// 旁观，所有 PR 卡死；伪造 <see cref="LiveState.Discovered"/> 则能往队列里塞不存在的 PR。
/// 收方验四件事：签名有效、公钥指纹与自称 id 一致、在白名单内、版本不回退。
/// </para>
/// <para>
/// <b>签的是传输的 JSON 原文</b>（<see cref="StateJson"/>），不是反序列化后再重新拼的规范化
/// 文本。嵌套列表要做规范化序列化很容易在「加个字段」时悄悄改变字节序，从而让新旧节点
/// 互相验不过签 —— <see cref="Beacon.SigningPayload"/> 那套显式拼字符串的做法在这种形状上
/// 不划算。直接签原文，收方就着收到的那串字节验，中间不做任何重排。
/// </para>
/// </remarks>
/// <param name="ElectorId">发布者的公钥指纹。</param>
/// <param name="PublicKey">发布者公钥，SubjectPublicKeyInfo 的 base64。</param>
/// <param name="StateJson"><see cref="LiveState"/> 的 JSON 原文 —— 签名与验签都针对它。</param>
/// <param name="Signature">对 <see cref="StateJson"/> 的签名，base64。</param>
public sealed record SignedLiveState(
    string ElectorId,
    string PublicKey,
    string StateJson,
    string Signature)
{
    /// <summary>签名有效，且公钥指纹与自称的 id 一致。</summary>
    public bool VerifySignature()
        => ElectorIdentity.FingerprintOf(PublicKey) == ElectorId
        && ElectorIdentity.Verify(PublicKey, StateJson, Signature);

    /// <summary>反序列化出状态本体；内容不合法时返回 null。</summary>
    public LiveState? Payload()
    {
        try
        {
            return ActaJson.Deserialize<LiveState>(StateJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>本节点签一份自己的状态。</summary>
    public static SignedLiveState Sign(ElectorIdentity identity, LiveState state)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var json = ActaJson.Serialize(state);
        return new SignedLiveState(identity.Id, identity.PublicKey, json, identity.Sign(json));
    }

    /// <summary>状态内容的短指纹，只用于日志排查。</summary>
    public string Digest()
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StateJson)).AsSpan(0, 4))
            .ToLowerInvariant();
}

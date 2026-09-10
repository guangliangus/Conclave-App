namespace Conclave.Domain;

/// <summary>
/// 一个节点在 mesh 里的公开状态，由签名心跳广播；mesh 未启用时成员只有自己。
/// </summary>
/// <remarks>
/// 刻意<b>不</b>登记「本机有哪些 repo」。评审用的代码是评审时拉进
/// <c>~/.conclave/work/</c> 下的临时工作区、结束即删，所以任何节点都能评任何仓库 ——
/// 本机预先 clone 了什么与入席资格无关。剩下的 <see cref="Projects"/> 是另一回事：
/// 它反映 <c>az</c> 身份的读权限，没权限连克隆和发评论都做不到，是真实约束。
/// </remarks>
public sealed record Elector
{
    /// <summary>公钥指纹，见 <see cref="ElectorIdentity.Id"/>。</summary>
    public required string Id { get; init; }

    public required string PublicKey { get; init; }

    /// <summary>本节点对外的 HTTP 端点；mesh 未启用时为空。</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>本节点 <c>az</c> 登录身份，用于「不评审自己的 PR」。</summary>
    public required string AzIdentity { get; init; }

    /// <summary>本节点 <c>az</c> 身份有读权限的 project。</summary>
    public IReadOnlyList<string> Projects { get; init; } = [];

    public int RunningJobs { get; init; }

    public int MaxConcurrent { get; init; } = 2;

    /// <summary>近 24 小时已出的 Ballot 数。用于公平性权重。</summary>
    public int Reviews24h { get; init; }

    /// <summary>
    /// 本节点 Claude 额度的用量比例，0 = 全新，1 = 用满。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 实测 <c>claude -p --output-format json</c> 的返回里<b>没有</b>任何额度/限流字段
    /// （只有这一次调用的 token 与折算金额），磁盘上也没有可读的缓存。所以这个数只能自己记账：
    /// 由基础设施层按滚动窗口汇总本节点自己出过的票，除以配置的预算，见
    /// <c>Conclave.Infrastructure.ClaudeUsageMeter</c>。
    /// </para>
    /// <para>
    /// 因此它<b>只覆盖 Conclave 自己烧掉的额度</b>，看不见节点主人交互式用 Claude 的部分。
    /// 专职 worker 机器上这个数就是全部；日常办公机上会低报 —— 那种机器可以用
    /// <c>~/.conclave/usage</c> 覆盖文件把真实数字喂进来。
    /// </para>
    /// </remarks>
    public double Utilization { get; init; }

    public DateTimeOffset LastHeartbeat { get; init; }

    /// <summary>本节点跑的 app 版本（打包时 <c>-p:Version</c> 写入；开发态是 1.0.0）。</summary>
    public string AppVersion { get; init; } = string.Empty;

    /// <summary>
    /// 心跳协议版本，见 <see cref="Beacon.ProtocolVersion"/>。
    /// </summary>
    /// <remarks>
    /// 不一致的节点互相之间连签名都验不过（载荷格式不同），以前那就是静默地「它掉线了」。
    /// 有了这个字段，收方能在验签<b>之前</b>认出「是版本不对，不是坏包」，并把它说出来。
    /// 老版本的心跳里没有这个字段，反序列化成 0 —— 同样被判为不一致。
    /// </remarks>
    public int ProtocolVersion { get; init; }

    /// <summary>心跳窗口。90 秒 = 60 秒轮询间隔 + 30 秒余量。</summary>
    public static TimeSpan HeartbeatWindow => TimeSpan.FromSeconds(90);

    /// <summary>
    /// 用量超过这个比例就不再入席。
    /// </summary>
    /// <remarks>
    /// <b>刻意是领域常量而不是配置项。</b> 席位表的一致性要求所有节点用同一个阈值算 ——
    /// 一台配 0.8、一台配 0.9，两边算出的合格节点集就不同，于是同一个 PR 被两个节点同时
    /// 评审、或者所有节点都以为该别人干。这跟 <see cref="HeartbeatWindow"/> 是同一性质：
    /// 属于节点间协议，改它就得全 mesh 同时升级。
    /// </remarks>
    public const double MaxUtilization = 0.8;

    public bool IsAlive(DateTimeOffset now) => now - LastHeartbeat <= HeartbeatWindow;

    public bool HasProject(string project)
        => Projects.Contains(project, StringComparer.OrdinalIgnoreCase);

    /// <summary>额度还够，可以接评审任务。</summary>
    public bool HasHeadroom => Utilization < MaxUtilization;

    /// <summary>
    /// 加权 HRW 里的权重：忙的少拿、最近干得多的少拿、额度快用完的少拿。
    /// </summary>
    /// <remarks>
    /// 三个因子刻意都是「除掉」或「乘以一个 &lt;1 的系数」而不是加权求和 —— 求和要调一组
    /// 量纲不同的系数，而连乘只要每个因子各自单调就行，加一个新维度不必重新配平旧的。
    /// <para>
    /// <c>(1 - Utilization)</c> 用乘法而不是 <c>1/(1+u)</c>：它在 <c>u → 1</c> 时趋于 0，
    /// 正好和 <see cref="MaxUtilization"/> 的硬截断同向收口 —— 越接近上限拿到的席位越少，
    /// 到了上限直接出局，中间没有突变。合格节点的这个因子恒 &gt; 0.2，不会退化成零权重。
    /// </para>
    /// </remarks>
    public double Weight =>
        1.0 / (1 + RunningJobs)                              // 负载均衡：正在跑的少拿
            / (1 + (Reviews24h * 0.1))                       // 长期公平：最近出票多的少拿
            * Math.Max(0, 1 - Utilization);                  // 额度：快用完的少拿
}

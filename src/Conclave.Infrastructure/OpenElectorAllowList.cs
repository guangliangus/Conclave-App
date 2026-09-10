using Conclave.Application.Ports;
using Conclave.Domain;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure;

/// <summary>
/// 放行所有节点的白名单实现，由 <c>Mesh.TrustAllElectors</c> 启用。
/// </summary>
/// <remarks>
/// <para>
/// 单独一个类而不是在 <see cref="FileElectorAllowList"/> 里加一个 if：这道闸的两种行为
/// （按文件放行 / 全部放行）差别太大，混在一个类里，读代码的人得先想清楚当前是哪一种。
/// 装配处选实现，调用处不用关心。
/// </para>
/// <para>
/// ⚠️ 只用于本机联调。见 <see cref="Application.MeshOptions.TrustAllElectors"/> 里
/// 关于「谁能让你的机器跑 Bash」那段。
/// </para>
/// </remarks>
public sealed class OpenElectorAllowList : IElectorAllowList
{
    private readonly string _selfId;

    public OpenElectorAllowList(ElectorIdentity identity, ILogger<OpenElectorAllowList> logger)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(logger);

        _selfId = identity.Id;

        // 跟 AzIdentityOverride 一样单独警告一次：这种失效是静默的，
        // 藏在那行「生效配置」里没人会注意到。
        logger.LogWarning(
            "白名单已关闭（Mesh.TrustAllElectors）—— 任何能通过验签的节点都可以让本机执行"
            + "评审任务。只该用于本机联调，别在日常机器上留着这个开关");
    }

    public bool IsAllowed(string electorId) => !string.IsNullOrEmpty(electorId);

    /// <summary>没有名单可展示，只报自己。</summary>
    public IReadOnlyCollection<string> Allowed => [_selfId];
}

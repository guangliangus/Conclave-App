using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Conclave.Domain;

/// <summary>
/// quorum 自适应的档位。
/// </summary>
/// <remarks>
/// <para>
/// <b>默认全 1 —— 每个 PR 只评一次。</b> 多节点独立评审同一个 PR 能压掉 LLM 的输出方差
/// （被多个节点独立提到的 finding 几乎必然是真问题），但代价是成倍的额度。
/// 所以合并逻辑（<see cref="QuorumEngine"/>）完整保留、随时可以打开，
/// 但默认不开：先把「不漏、不重复」这件事做好，方差是第二优先。
/// </para>
/// <para>
/// 想恢复原来的「敏感路径和大改动跑 3 遍」，把
/// <see cref="ReservedMatters"/> 与 <see cref="LargeChange"/> 配成 3 即可。
/// </para>
/// <para>
/// <b>这份策略应当全 mesh 保持一致。</b> 它不像
/// <see cref="Elector.MaxUtilization"/> 那样必须一致 —— quorum 由发现节点算出来、
/// 写进 Summons 块，之后所有节点都读链上那个值，所以配置不同不会让席位表分叉。
/// 但会变成「同一个 PR 由谁发现，决定了它跑几遍」，那种不确定性没有意义。
/// 为了让分歧至少可见，策略指纹会跟敏感路径清单一起写进 Summons。
/// </para>
/// </remarks>
public sealed record QuorumPolicy
{
    /// <summary>默认策略：所有 PR 都只评一次。</summary>
    public static QuorumPolicy Single { get; } = new();

    /// <summary>不命中任何升级条件时跑几遍。</summary>
    public int Default { get; init; } = 1;

    /// <summary>触及 <see cref="Domain.ReservedMatters"/> 时跑几遍。</summary>
    public int ReservedMatters { get; init; } = 1;

    /// <summary>改动文件数超过 <see cref="MediumChangeFiles"/> 时跑几遍。</summary>
    public int MediumChange { get; init; } = 1;

    public int MediumChangeFiles { get; init; } = 5;

    /// <summary>改动文件数超过 <see cref="LargeChangeFiles"/> 时跑几遍。</summary>
    public int LargeChange { get; init; } = 1;

    public int LargeChangeFiles { get; init; } = 20;

    /// <summary>
    /// 策略指纹，前 12 个十六进制字符。
    /// </summary>
    /// <remarks>
    /// 跟 <see cref="Domain.ReservedMatters.Fingerprint"/> 一起写进 Summons 块 ——
    /// 半年后回看「这个 PR 为什么只跑了 1 个节点」时不必猜当时的配置。
    /// </remarks>
    public string Fingerprint()
    {
        var canonical = string.Join('|',
            Default.ToString(CultureInfo.InvariantCulture),
            ReservedMatters.ToString(CultureInfo.InvariantCulture),
            MediumChange.ToString(CultureInfo.InvariantCulture),
            MediumChangeFiles.ToString(CultureInfo.InvariantCulture),
            LargeChange.ToString(CultureInfo.InvariantCulture),
            LargeChangeFiles.ToString(CultureInfo.InvariantCulture));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }

    /// <summary>全部档位都是 1 —— 即「永远只评一次」。</summary>
    public bool IsSingleReview
        => Default <= 1 && ReservedMatters <= 1 && MediumChange <= 1 && LargeChange <= 1;
}

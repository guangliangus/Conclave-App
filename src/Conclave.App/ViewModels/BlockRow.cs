using System.Globalization;
using Conclave.Domain;

namespace Conclave.App.ViewModels;

/// <summary>Acta 账本浏览器里的一行。</summary>
public sealed class BlockRow
{
    public BlockRow(Block block)
    {
        ArgumentNullException.ThrowIfNull(block);

        At = block.At.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        // 全局单链之后链名恒为 "acta"，没有信息量；改显示这块属于哪个 PR 版本。
        Revision = Conclave.Domain.Acta.RevisionIdOf(block) ?? "—";
        Index = block.Index.ToString(CultureInfo.InvariantCulture);
        Kind = Badge.Of(block.Kind);
        Elector = block.ElectorId[..8];
        ElectorFull = block.ElectorId;
        Hash = block.Hash()[..12];
        Summary = Describe(block);
    }

    public string At { get; }

    public string Revision { get; }

    public string Index { get; }

    public Badge Kind { get; }

    /// <summary>签名节点公钥指纹前 8 位。</summary>
    public string Elector { get; }

    public string ElectorFull { get; }

    public string Hash { get; }

    public string Summary { get; }

    /// <summary>
    /// payload 里真正有信息量的那几个字段。
    /// </summary>
    /// <remarks>
    /// 刻意不再重复 revision id 和签名节点 —— 两者都已经是独立的列，
    /// 摘要里再抄一遍会把这一列挤成一串重复的十六进制。
    /// </remarks>
    private static string Describe(Block block)
    {
        try
        {
            return block.Kind switch
            {
                BlockKind.Summons => block.Payload<SummonsPayload>() is { } s
                    ? $"{s.Pr.Repo} · quorum {s.Quorum} · rules {s.RulesFingerprint}"
                    : string.Empty,
                BlockKind.Seating => block.Payload<SeatingPayload>() is { } t
                    ? $"round {t.Round} → {Short(t.ElectorId)}"
                    : string.Empty,
                BlockKind.Ballot => block.Payload<BallotPayload>() is { } b
                    ? $"round {b.Round} · {Labels.Decision(b.Decision)} · {b.Findings.Count} 条 · "
                        + $"{b.DurationMs / 1000} 秒 · {b.Model}"
                    : string.Empty,
                BlockKind.Promulgation => block.Payload<PromulgationPayload>() is { } p
                    ? $"{Labels.Decision(p.Decision)} · {p.Findings.Count} 条 · "
                        + $"{p.ActualQuorum}/{p.ExpectedQuorum} 票"
                        + (p.Degraded ? " · 降级" : string.Empty)
                    : string.Empty,
                BlockKind.Recess => block.Payload<RecessPayload>() is { } r
                    ? $"round {r.Round} · {r.Reason}"
                    : string.Empty,
                _ => string.Empty,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            // 账本浏览器不该因为一个坏 payload 就整块崩掉。
            return "(payload 解析失败)";
        }
    }

    private static string Short(string electorId)
        => electorId.Length > 8 ? electorId[..8] : electorId;
}

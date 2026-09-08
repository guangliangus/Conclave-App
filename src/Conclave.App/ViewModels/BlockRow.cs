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
        Chain = block.ChainId;
        Index = block.Index.ToString(CultureInfo.InvariantCulture);
        Kind = block.Kind.ToString();
        Elector = block.ElectorId;
        Hash = block.Hash()[..12];
        Summary = Describe(block);
    }

    public string At { get; }

    public string Chain { get; }

    public string Index { get; }

    public string Kind { get; }

    public string Elector { get; }

    public string Hash { get; }

    public string Summary { get; }

    private static string Describe(Block block)
    {
        try
        {
            return block.Kind switch
            {
                BlockKind.Summons => block.Payload<SummonsPayload>() is { } s
                    ? $"{s.Revision.Id} {s.Pr.Repo} quorum={s.Quorum} rules={s.RulesFingerprint}"
                    : string.Empty,
                BlockKind.Seating => block.Payload<SeatingPayload>() is { } t
                    ? $"{t.RevisionId} round={t.Round} → {t.ElectorId}"
                    : string.Empty,
                BlockKind.Ballot => block.Payload<BallotPayload>() is { } b
                    ? $"{b.RevisionId} round={b.Round} {b.Decision} {b.Findings.Count} 条 {b.DurationMs}ms {b.Model}"
                    : string.Empty,
                BlockKind.Promulgation => block.Payload<PromulgationPayload>() is { } p
                    ? $"{p.RevisionId} {p.Decision} {p.Findings.Count} 条 {p.ActualQuorum}/{p.ExpectedQuorum}"
                        + (p.Degraded ? " 降级" : string.Empty)
                    : string.Empty,
                BlockKind.Recess => block.Payload<RecessPayload>() is { } r
                    ? $"{r.RevisionId} round={r.Round} {r.Reason}"
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
}

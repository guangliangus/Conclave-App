namespace Conclave.Domain;

/// <summary>Acta 的常量与载荷助手。</summary>
public static class Acta
{
    /// <summary>
    /// 全局唯一一条链的 ID。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 早期设计是「每个 PR 一条链」，那样单写者居多、索引冲突是罕见情况。改成全局单链之后
    /// <b>并发写必然撞索引</b>：两个节点只要在听到彼此之前各写一块，就会争同一个 index。
    /// 让位重挂（<c>SqliteActa.RebaseAsync</c>）因此从异常路径变成常态路径 ——
    /// 它是纯函数、确定性收敛，功能上没问题，但代价要看得见，所以账本记了冲突计数。
    /// </para>
    /// <para>
    /// 换来的是一条真正的全局时间线：谁在什么时候评审了什么，按链序读一遍就是。
    /// </para>
    /// </remarks>
    public const string ChainId = "acta";

    /// <summary>
    /// 从任意区块载荷里取出它所属的 revision id。
    /// </summary>
    /// <remarks>
    /// 全局单链之后「按 PR 查」不能再靠 ChainId，只能靠这个值冗余成一列并建索引。
    /// 放在领域层是因为它只依赖载荷的形状 —— 加一种区块类型时这里必须同步，
    /// 漏了会让那种块查不到，所以刻意用 switch 而不是反射兜底。
    /// </remarks>
    public static string? RevisionIdOf(Block block)
    {
        ArgumentNullException.ThrowIfNull(block);

        try
        {
            return block.Kind switch
            {
                BlockKind.Summons => block.Payload<SummonsPayload>()?.Revision.Id,
                BlockKind.Seating => block.Payload<SeatingPayload>()?.RevisionId,
                BlockKind.Ballot => block.Payload<BallotPayload>()?.RevisionId,
                BlockKind.Promulgation => block.Payload<PromulgationPayload>()?.RevisionId,
                BlockKind.Recess => block.Payload<RecessPayload>()?.RevisionId,
                _ => null,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

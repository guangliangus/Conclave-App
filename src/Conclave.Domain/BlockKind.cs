namespace Conclave.Domain;

/// <summary>Acta 上的区块类型。命名沿用 Conclave 的会议隐喻，见 docs/DESIGN.md §3。</summary>
public enum BlockKind
{
    /// <summary>发现 PR，召集评议。由轮询到该 project 的节点写入。</summary>
    Summons = 0,

    /// <summary>认领席位。各节点独立算出席位分配，算到自己才写。</summary>
    Seating = 1,

    /// <summary>签名的评审结论。</summary>
    Ballot = 2,

    /// <summary>公布最终结论并投递回 Azure DevOps。只有 round=0 的节点写。</summary>
    Promulgation = 3,

    /// <summary>超时弃权，席位重新分配到下一轮。</summary>
    Recess = 4,
}

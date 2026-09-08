using Conclave.Domain;

namespace Conclave.Application.Ports;

/// <summary>Acta 账本的持久化。实现见 <c>Conclave.Infrastructure.SqliteActa</c>。</summary>
public interface IActaStore
{
    /// <summary>由本节点签名并追加一个区块到 <paramref name="chainId"/> 的链尾。</summary>
    Task<Block> AppendAsync<TPayload>(string chainId, BlockKind kind, TPayload payload, CancellationToken ct);

    /// <summary>
    /// 应用一个来自其他节点的区块。验签失败、PrevHash 不匹配、或索引冲突且己方哈希更小时返回 false。
    /// </summary>
    Task<bool> TryApplyAsync(Block block, CancellationToken ct);

    /// <summary>读一条完整的链，按 Index 升序。</summary>
    Task<IReadOnlyList<Block>> ReadChainAsync(string chainId, CancellationToken ct);

    /// <summary>最近写入的若干区块，跨链，按时间倒序。供 UI 的账本浏览器用。</summary>
    Task<IReadOnlyList<Block>> ReadRecentAsync(int limit, CancellationToken ct);

    /// <summary>链上出现过 Summons 但尚无 Promulgation 的链 ID。</summary>
    Task<IReadOnlyList<string>> ReadOpenChainsAsync(CancellationToken ct);

    /// <summary>本节点近 24 小时出过多少张 Ballot。用于公平性权重。</summary>
    Task<int> CountRecentBallotsAsync(string electorId, CancellationToken ct);
}

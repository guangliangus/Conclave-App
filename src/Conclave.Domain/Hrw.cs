using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Conclave.Domain;

/// <summary>
/// 加权 rendezvous 哈希（HRW / highest random weight）。
/// </summary>
/// <remarks>
/// <para>
/// 用它而不是 <c>hash % count</c>：节点上下线时，<c>%</c> 会让几乎所有分配重排，
/// HRW 只重排掉线节点原本那一份，其余保持稳定。
/// </para>
/// <para>
/// 全部是纯函数，这是整个设计成立的前提 —— 每个节点各自算，算出同一个席位表，
/// 于是「谁来评审」不需要任何协商消息。
/// </para>
/// </remarks>
public static class Hrw
{
    /// <summary>
    /// 加权 HRW 打分：<c>weight / -ln(u)</c>，其中 u 是 (key, round, memberId) 映射到 (0,1) 的均匀值。
    /// 取分数最大者。这是加权 HRW 的标准形式，权重翻倍即中签概率翻倍。
    /// </summary>
    public static double Score(string key, int round, string memberId, double weight)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(memberId);

        if (weight <= 0)
        {
            return double.NegativeInfinity;
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{key}#{round}#{memberId}"));
        var raw = BinaryPrimitives.ReadUInt64BigEndian(digest);

        // 夹到开区间 (0,1)：u=0 会让 ln 发散，u=1 会让分母为 0。
        var u = Math.Clamp(raw / (double)ulong.MaxValue, 1e-12, 1 - 1e-12);
        return weight / -Math.Log(u);
    }

    /// <summary>在一组等权成员里选一个。用于把 project 的轮询责任分片到节点。</summary>
    public static string? PickId(string key, int round, IEnumerable<string> memberIds)
    {
        ArgumentNullException.ThrowIfNull(memberIds);
        return memberIds
            .OrderByDescending(id => Score(key, round, id, 1.0))
            .ThenBy(id => id, StringComparer.Ordinal)   // 分数相同（极罕见）时保持确定性
            .FirstOrDefault();
    }

    /// <summary>在一组带权节点里选一个。</summary>
    public static Elector? Pick(string key, int round, IEnumerable<Elector> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .OrderByDescending(e => Score(key, round, e.Id, e.Weight))
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}

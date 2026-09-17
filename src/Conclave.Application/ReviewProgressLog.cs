using System.Globalization;

namespace Conclave.Application;

/// <summary>
/// 一段评审日志。
/// </summary>
/// <param name="RevisionId">哪一版的评审。</param>
/// <param name="From">请求方已经拿到的序号。</param>
/// <param name="Next">下次带这个序号来要增量。</param>
/// <param name="Lines">从 <paramref name="From"/> 之后的行。</param>
/// <param name="Running">这次评审还在跑；false 表示已经收尾（日志不再增长）。</param>
public sealed record LogChunk(
    string RevisionId,
    long From,
    long Next,
    IReadOnlyList<string> Lines,
    bool Running);

/// <summary>
/// 正在跑的评审留下的实时日志。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由是「我的 PR 被某个节点评着，我想看它到哪一步了」。评审是十几分钟的黑盒，
/// 期间界面上只有一个「评审中」徽章和一个计时器 —— 卡住了和正常慢分不出来。
/// </para>
/// <para>
/// 按序号（<see cref="LogChunk.Next"/>）取增量而不是每次全量：一次评审的日志能有几百行，
/// 而面板是每两秒轮询一次的。序号是单调递增的全局计数，被环形缓冲挤掉的行不会重来。
/// </para>
/// <para>
/// <b>内存里这份是给人当场看的</b>，所以有上限、会被挤掉、进程一退就没。
/// 而人想翻日志的时刻往往晚于它还在的时刻（评审失败了要看为什么、进程重启过要看上一轮），
/// 所以每一行同时交给 <see cref="ReviewLogArchive"/> 落一份盘，留一天。
/// 两者分工明确：内存管「实时、按序号取增量」，盘上那份管「事后、要全文」。
/// </para>
/// </remarks>
/// <param name="archive">
/// 落盘的那一份。给 <c>null</c> 就是纯内存 —— 单测默认走这条，免得每个用例都碰磁盘。
/// </param>
public sealed class ReviewProgressLog(ReviewLogArchive? archive = null)
{
    /// <summary>
    /// 单个 revision 最多留几行。超了从头挤掉。
    /// </summary>
    /// <remarks>
    /// 公开是因为<b>取日志的那一头必须用同一个数</b>：这里是环形缓冲，而
    /// <see cref="Read"/> 给的是增量，于是读的人要是只加不减，它手上的行数会一路涨到
    /// 「这次评审总共出过多少行」，而不是「现在还留着多少行」。
    /// </remarks>
    public const int MaxLines = 600;

    /// <summary>最多留几个 revision 的日志。评完不立刻丢 —— 人常常是评完才想看。</summary>
    private const int MaxRevisions = 8;

    private readonly Lock _gate = new();

    /// <summary>插入顺序即淘汰顺序，所以用 List 而不是 Dictionary 记 LRU。</summary>
    private readonly List<string> _order = [];

    private readonly Dictionary<string, Buffer> _buffers = new(StringComparer.Ordinal);

    /// <summary>记一行。<paramref name="line"/> 会被去掉首尾空白，空行忽略。</summary>
    public void Append(string revisionId, string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);

        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var stamped = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:HH:mm:ss} {line.Trim()}");

        lock (_gate)
        {
            Slot(revisionId).Add(stamped);

            // 在锁里写盘：盘上的行序必须跟内存里一致，否则事后翻出来的日志跟当时看到的
            // 不是一回事。IO 失败由 archive 自己吞掉并记一条 —— 落盘绝不该让评审出错。
            archive?.Append(revisionId, stamped);
        }
    }

    /// <summary>
    /// 标记这次评审开始 —— 会清掉同一版上一次的残留。
    /// </summary>
    /// <remarks>
    /// 清的只是<b>内存</b>里那份：实时面板要看的是这一轮。盘上那份继续往后追加，
    /// 于是同一版重试了几次，几次的日志都在同一个文件里（见 <see cref="ReviewLogArchive.Append"/>）。
    /// <para>
    /// 顺手清一次过期日志。挂在这里而不是另起一个定时器：评审是这个目录唯一的增长来源，
    /// 不评审就不会长，一天扫不到几次。
    /// </para>
    /// </remarks>
    public void Begin(string revisionId, string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);

        archive?.Sweep();

        lock (_gate)
        {
            _ = _buffers.Remove(revisionId);
            _ = _order.Remove(revisionId);
            Slot(revisionId).Running = true;
        }

        Append(revisionId, line);
    }

    /// <summary>标记这次评审收尾。日志留着，但面板据此停止轮询。</summary>
    public void End(string revisionId, string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);

        Append(revisionId, line);

        lock (_gate)
        {
            if (_buffers.TryGetValue(revisionId, out var buffer))
            {
                buffer.Running = false;
            }
        }
    }

    /// <summary>取 <paramref name="from"/> 之后的行。没有这个 revision 时返回空块。</summary>
    public LogChunk Read(string revisionId, long from)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);

        lock (_gate)
        {
            if (!_buffers.TryGetValue(revisionId, out var buffer))
            {
                return new LogChunk(revisionId, from, from, [], Running: false);
            }

            return buffer.Read(revisionId, from);
        }
    }

    /// <summary>本机是否留着这一版的日志（内存或盘上）。</summary>
    public bool Has(string revisionId)
    {
        lock (_gate)
        {
            if (_buffers.ContainsKey(revisionId))
            {
                return true;
            }
        }

        return archive?.Has(revisionId) == true;
    }

    /// <summary>
    /// 这一版日志的全文，供「拉到本地存一份」用。
    /// </summary>
    /// <remarks>
    /// 优先给盘上那份：它<b>没有 <see cref="MaxLines"/> 这个上限</b>，而内存里那份是环形缓冲，
    /// 一次长评审的开头早被挤掉了 —— 事后要看的恰恰常是开头（拉代码、起会话那几步）。
    /// 盘上没有（比如对端关掉了落盘）才退回内存里现有的那些行。
    /// </remarks>
    public string? ReadAll(string revisionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);

        if (archive?.Read(revisionId) is { } archived)
        {
            return archived.Text;
        }

        lock (_gate)
        {
            return _buffers.TryGetValue(revisionId, out var buffer)
                ? buffer.Snapshot()
                : null;
        }
    }

    private Buffer Slot(string revisionId)
    {
        if (_buffers.TryGetValue(revisionId, out var existing))
        {
            return existing;
        }

        // 淘汰最老的那个 revision，而不是最老的行 —— 一次评审的日志要么完整要么不留，
        // 掐掉一半的日志比没有更容易误判。
        while (_order.Count >= MaxRevisions)
        {
            _ = _buffers.Remove(_order[0]);
            _order.RemoveAt(0);
        }

        var buffer = new Buffer();
        _buffers[revisionId] = buffer;
        _order.Add(revisionId);
        return buffer;
    }

    /// <summary>一个 revision 的环形缓冲。<c>_base</c> 是第 0 行对应的全局序号。</summary>
    private sealed class Buffer
    {
        private readonly Queue<string> _lines = new();
        private long _base;

        internal bool Running { get; set; } = true;

        internal void Add(string line)
        {
            _lines.Enqueue(line);
            while (_lines.Count > MaxLines)
            {
                _ = _lines.Dequeue();
                _base++;
            }
        }

        /// <summary>现有的行拼成一整份文本。</summary>
        internal string Snapshot() => string.Join(Environment.NewLine, _lines);

        internal LogChunk Read(string revisionId, long from)
        {
            var next = _base + _lines.Count;

            // 序号落在缓冲之外时一律从头给，而不是给空：
            //   · 比 _base 小 —— 那几行已经被挤掉了，给现有的最早一行起；
            //   · 比 next 大 —— 对端重启过、或者同一版重试过（序号从 0 重新开始），
            //     给空的话面板会白等一轮才恢复。
            var inRange = from >= _base && from <= next;
            var skip = inRange ? (int)(from - _base) : 0;

            return new LogChunk(revisionId, _base + skip, next, [.. _lines.Skip(skip)], Running);
        }
    }
}

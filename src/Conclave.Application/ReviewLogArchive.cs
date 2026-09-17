using Microsoft.Extensions.Logging;

namespace Conclave.Application;

/// <summary>落盘的一份评审日志。</summary>
/// <param name="Path">文件路径。要给人看的，所以是全路径。</param>
/// <param name="Text">全文。</param>
public sealed record ArchivedLog(string Path, string Text);

/// <summary>
/// 评审日志的落地与保留。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReviewProgressLog"/> 只在内存里，而人想看日志的时刻往往<b>晚于</b>它还在的时刻：
/// 评审失败了要看为什么，进程重启过要看上一轮跑到哪 —— 而那两种情况下内存里什么都没有了
/// （环形缓冲只留最近几个 revision，进程一退全没）。
/// 所以每一行在进内存的同时也落一份盘。
/// </para>
/// <para>
/// <b>只留 <see cref="ConclaveOptions.LogRetention"/>（默认一天）。</b> 日志里有源码路径、
/// 命令行和模型的分析原文，长期堆在磁盘上既占地方也没必要。要长期追溯的话，每一票的会话 ID
/// 是从 <c>(revisionId, round)</c> 确定性派生的，<c>claude --resume</c> 能翻出完整会话。
/// </para>
/// <para>
/// <b>两种文件，靠文件名分开：</b><c>&lt;rev&gt;.log</c> 是本机跑那次评审时一行行写下来的；
/// <c>&lt;rev&gt;.&lt;对端指纹前 8 位&gt;.log</c> 是从别的节点拉回来的。分开的理由是重复拉取
/// 要能覆盖同一个文件（幂等），同时绝不能覆盖掉本机自己那份 —— 本机那份是逐行落的，
/// 对端给的是它当时的快照，两者不是同一个东西。
/// </para>
/// <para>
/// <b>任何一处 IO 失败都只记一条日志，绝不往外抛。</b> 写盘的调用点在评审子进程的输出回调上，
/// 磁盘满了、目录没权限，代价必须是「少一份日志」而不是「评审挂了」——
/// 跟 <c>ClaudeReviewRunner.Summarize</c> 认不出事件时的取舍是同一条。
/// </para>
/// </remarks>
public sealed class ReviewLogArchive(ConclaveOptions options, ILogger<ReviewLogArchive> logger)
{
    /// <summary>落盘目录。</summary>
    public string Root => options.LogDirectory;

    /// <summary>本机跑评审时逐行写的那份。</summary>
    public string LivePathOf(string revisionId)
        => Path.Combine(Root, Safe(revisionId) + ".log");

    /// <summary>
    /// 追加一行。
    /// </summary>
    /// <remarks>
    /// <b>刻意不在开新一轮时清空文件。</b> 同一版 PR 重试时内存缓冲会被
    /// <see cref="ReviewProgressLog.Begin"/> 丢掉（实时面板要的是这一轮），但盘上那份要留全 ——
    /// 「上一轮为什么失败」正是最常要翻的东西，而 <c>Begin</c> 本身会写一行
    /// <c>评审 repo#123 round=2</c>，天然就是轮与轮之间的分隔。
    /// </remarks>
    public void Append(string revisionId, string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);

        try
        {
            EnsureRoot();
            File.AppendAllText(LivePathOf(revisionId), line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn(ex, revisionId);
        }
    }

    /// <summary>
    /// 盘上有没有本机自己写的那份留档。
    /// </summary>
    /// <remarks>
    /// <b>队列表上每一行、每次刷新都会问一次</b>（界面两秒重建一遍），所以这里只做一次
    /// <c>File.Exists</c>，不去遍历目录找拉回来的那些文件。代价是「本机没评过、只拉过一次」
    /// 的那一版重启后按钮会消失 —— 而那一版链上必然有票（不然没人会去拉它的日志），
    /// <c>PrRow</c> 那条 <c>BallotCount &gt; 0</c> 已经把它管住了。
    /// </remarks>
    public bool Has(string revisionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);
        return File.Exists(LivePathOf(revisionId));
    }

    /// <summary>
    /// 盘上这一版的日志：优先本机那份，没有就取最新拉回来的那份。
    /// </summary>
    /// <returns>一份都没有时为 <c>null</c>。</returns>
    public ArchivedLog? Read(string revisionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);

        try
        {
            var live = LivePathOf(revisionId);
            if (File.Exists(live))
            {
                return new ArchivedLog(live, File.ReadAllText(live));
            }

            // 拉回来的可能有好几份（问过好几个节点），取最新的那个。
            var pulled = System.IO.Directory.Exists(Root)
                ? System.IO.Directory.EnumerateFiles(Root, Safe(revisionId) + ".*.log")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;

            return pulled is null ? null : new ArchivedLog(pulled, File.ReadAllText(pulled));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn(ex, revisionId);
            return null;
        }
    }

    /// <summary>
    /// 存一份从别的节点拉回来的日志。
    /// </summary>
    /// <returns>存到了哪；存不下时为 <c>null</c>。</returns>
    /// <remarks>
    /// 按 <paramref name="peerId"/> 命名所以重复拉取是覆盖同一个文件，不会越拉越多。
    /// </remarks>
    public string? SavePulled(string revisionId, string peerId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);

        try
        {
            EnsureRoot();
            var path = Path.Combine(
                Root,
                Safe(revisionId) + "." + Safe(peerId[..Math.Min(8, peerId.Length)]) + ".log");

            File.WriteAllText(path, text);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn(ex, revisionId);
            return null;
        }
    }

    /// <summary>
    /// 删掉过期的日志文件。
    /// </summary>
    /// <remarks>
    /// 按<b>最后写入时间</b>而不是创建时间判：一次评审能跑 45 分钟
    /// （<see cref="ConclaveOptions.ReviewTimeout"/>），按创建时间的话跨过保留期的那一刻
    /// 会把还在写的那份删掉。
    /// </remarks>
    public void Sweep()
    {
        if (options.LogRetention <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            if (!System.IO.Directory.Exists(Root))
            {
                return;
            }

            var deadline = DateTime.UtcNow - options.LogRetention;
            var swept = 0;

            foreach (var path in System.IO.Directory.EnumerateFiles(Root, "*.log"))
            {
                if (File.GetLastWriteTimeUtc(path) >= deadline)
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                    swept++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 正被别人打开着之类。下一轮再说。
                    logger.LogDebug(ex, "删不掉过期的评审日志 {Path}", path);
                }
            }

            if (swept > 0)
            {
                logger.LogInformation(
                    "清掉 {Count} 份超过 {Retention} 的评审日志（{Root}）",
                    swept, options.LogRetention, Root);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "清理评审日志目录 {Root} 失败", Root);
        }
    }

    private void EnsureRoot() => System.IO.Directory.CreateDirectory(Root);

    /// <summary>
    /// 把一个 id 变成能当文件名的样子。
    /// </summary>
    /// <remarks>
    /// revision id 现在是 <c>2916@a1b2c3d</c> 这种形状，本来就是安全的；这一步是为了
    /// 日后 id 的构成变了也不至于写出个路径穿越来 —— 它是拼在 <see cref="Root"/> 后面的。
    /// </remarks>
    private static string Safe(string id)
    {
        var chars = id.ToCharArray();
        var invalid = Path.GetInvalidFileNameChars();

        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == '.')
            {
                chars[i] = '_';
            }
        }

        var safe = new string(chars);
        return safe.Length <= 120 ? safe : safe[..120];
    }

    private void Warn(Exception ex, string revisionId) => logger.LogWarning(
        ex,
        "{Revision} 的评审日志读写失败（{Root}）—— 只影响能不能事后翻日志，不影响评审",
        revisionId,
        Root);
}

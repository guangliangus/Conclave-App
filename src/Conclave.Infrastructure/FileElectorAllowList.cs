using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure;

/// <summary>
/// 从 <c>~/.conclave/electors.allow</c> 读白名单，每行一个 elector id，<c>#</c> 起注释。
/// </summary>
/// <remarks>
/// <para>
/// 刻意用一个纯文本文件而不是配置节：加一台机器时要做的事就是把对方 UI 上显示的
/// 那串 16 位指纹粘进来，不必碰 JSON 也不必重启别的东西。
/// </para>
/// <para>
/// 文件不存在时名单里只有自己 —— 即单机模式，是安全的默认。
/// </para>
/// </remarks>
public sealed class FileElectorAllowList : IElectorAllowList
{
    private readonly string _path;
    private readonly string _selfId;
    private readonly ILogger<FileElectorAllowList> _logger;
    private readonly Lock _gate = new();
    private HashSet<string> _allowed;
    private DateTime _loadedAt;

    public FileElectorAllowList(
        ConclaveOptions options, ElectorIdentity identity, ILogger<FileElectorAllowList> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(identity);

        _path = Path.Combine(options.HomeDirectory, "electors.allow");
        _selfId = identity.Id;
        _logger = logger;
        _allowed = Load();
    }

    public IReadOnlyCollection<string> Allowed
    {
        get
        {
            Refresh();
            lock (_gate)
            {
                return [.. _allowed];
            }
        }
    }

    public bool IsAllowed(string electorId)
    {
        if (string.IsNullOrEmpty(electorId))
        {
            return false;
        }

        if (electorId == _selfId)
        {
            return true;
        }

        Refresh();
        lock (_gate)
        {
            return _allowed.Contains(electorId);
        }
    }

    /// <summary>
    /// 文件改动后自动生效，不用重启节点。
    /// </summary>
    /// <remarks>
    /// 按写入时间判断而不是上 FileSystemWatcher：这个方法调用频率不高，
    /// 一次 stat 比维护一个 watcher 的生命周期便宜得多。
    /// </remarks>
    private void Refresh()
    {
        try
        {
            var stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
            lock (_gate)
            {
                if (stamp == _loadedAt)
                {
                    return;
                }
            }

            var reloaded = Load();
            lock (_gate)
            {
                _allowed = reloaded;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "读不到白名单 {Path}，沿用上一次载入的名单", _path);
        }
    }

    private HashSet<string> Load()
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { _selfId };
        _loadedAt = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;

        if (!File.Exists(_path))
        {
            _logger.LogInformation("白名单 {Path} 不存在，只信任本节点（单机模式）", _path);
            return set;
        }

        foreach (var raw in File.ReadLines(_path))
        {
            var hash = raw.IndexOf('#', StringComparison.Ordinal);
            var line = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (line.Length > 0)
            {
                _ = set.Add(line);
            }
        }

        _logger.LogInformation("白名单载入 {Count} 个节点（含自己）", set.Count);
        return set;
    }
}

using System.Collections.Concurrent;
using System.Net.Http.Json;
using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure.Mesh;

/// <summary>
/// P1 的 mesh：UDP 多播发现 + HTTP 推送区块。
/// </summary>
/// <remarks>
/// 传输走 BCL 的 <c>HttpListener</c> / <c>HttpClient</c> 而不是 gRPC：区块本来就是 JSON，
/// 上 gRPC 要多一个 <c>.proto</c> 并让它跟 <see cref="Block"/> 这个 record 保持同步，
/// 而内网里这点流量用 HTTP 完全够，还能直接 curl。
/// </remarks>
public sealed class HttpMesh : IMesh, IDisposable
{
    private readonly ConcurrentDictionary<string, Elector> _peers = new(StringComparer.Ordinal);
    private readonly IElectorAllowList _allowList;
    private readonly MeshOptions _options;
    private readonly ILogger<HttpMesh> _logger;
    private readonly HttpClient _http;
    private readonly Lock _gate = new();
    private Elector _self;

    public HttpMesh(
        Elector self,
        IElectorAllowList allowList,
        ConclaveOptions options,
        ILogger<HttpMesh> logger)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(options);

        _self = self;
        _allowList = allowList;
        _options = options.Mesh;
        _logger = logger;
        _http = new HttpClient { Timeout = _options.RequestTimeout };
    }

    public Elector Self
    {
        get
        {
            lock (_gate)
            {
                return _self;
            }
        }
    }

    public IReadOnlyList<Elector> Members => [Self, .. _peers.Values];

    public void UpdateSelf(Func<Elector, Elector> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            _self = mutate(_self);
        }
    }

    /// <summary>
    /// 收编一个心跳。验签、验指纹、验白名单、验新鲜度，任一不过就丢掉。
    /// </summary>
    /// <returns>被收编返回 true。</returns>
    public bool AcceptBeacon(Beacon beacon, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(beacon);

        if (beacon.Elector.Id == Self.Id)
        {
            return false;   // 多播回环会收到自己发的
        }

        if (!beacon.VerifySignature())
        {
            _logger.LogWarning("丢弃心跳：{Elector} 验签失败", beacon.Elector.Id);
            return false;
        }

        if (!_allowList.IsAllowed(beacon.Elector.Id))
        {
            // 不用 Warning：同网段有别人的 mesh 时这会刷屏，而这属于正常情况。
            _logger.LogDebug("丢弃心跳：{Elector} 不在白名单", beacon.Elector.Id);
            return false;
        }

        if (!beacon.IsFresh(now))
        {
            _logger.LogWarning("丢弃心跳：{Elector} 的时间戳不新鲜（可能是重放）", beacon.Elector.Id);
            return false;
        }

        var known = _peers.ContainsKey(beacon.Elector.Id);
        _peers[beacon.Elector.Id] = beacon.Elector;

        if (!known)
        {
            _logger.LogInformation(
                "发现节点 {Elector} @ {Endpoint}（az={Az}，{Repos} 个 repo）",
                beacon.Elector.Id, beacon.Elector.Endpoint,
                beacon.Elector.AzIdentity, beacon.Elector.Repos.Count);
        }

        return true;
    }

    /// <summary>清掉心跳过期的节点，返回被清掉的数量。</summary>
    public int PruneDeadPeers(DateTimeOffset now)
    {
        var dead = _peers.Where(kv => !kv.Value.IsAlive(now)).Select(kv => kv.Key).ToList();
        foreach (var id in dead)
        {
            if (_peers.TryRemove(id, out _))
            {
                _logger.LogInformation("节点 {Elector} 心跳超时，已移出 mesh", id);
            }
        }

        return dead.Count;
    }

    /// <summary>
    /// 把区块推给若干个邻居。
    /// </summary>
    /// <remarks>
    /// 只推给 <see cref="MeshOptions.Fanout"/> 个随机邻居，靠对方继续转发扩散。
    /// 单个对端失败只记日志：本地已经落链了，收敛靠 P2 的补链兜底，
    /// 不能因为一台机器离线就让写入失败。
    /// </remarks>
    public async Task BroadcastAsync(Block block, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(block);

        var targets = _peers.Values
            .Where(p => p.IsAlive(DateTimeOffset.UtcNow) && p.Endpoint.Length > 0)
            .OrderBy(_ => Random.Shared.Next())
            .Take(Math.Max(1, _options.Fanout))
            .ToList();

        if (targets.Count == 0)
        {
            return;
        }

        await Task.WhenAll(targets.Select(peer => PushAsync(peer, block, ct))).ConfigureAwait(false);
    }

    private async Task PushAsync(Elector peer, Block block, CancellationToken ct)
    {
        try
        {
            using var response = await _http
                .PostAsJsonAsync($"{peer.Endpoint}/blocks", block, ActaJson.Options, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "推区块给 {Elector} 返回 {Status}（对方可能需要补链）",
                    peer.Id, (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "推区块给 {Elector} 失败", peer.Id);
        }
    }

    /// <summary>从某个节点回拉 <paramref name="fromIndex"/> 起的链段，用于补链。</summary>
    public async Task<IReadOnlyList<Block>> PullChainAsync(
        Elector peer, long fromIndex, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);

        try
        {
            var url = $"{peer.Endpoint}/chain?from={fromIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var blocks = await _http
                .GetFromJsonAsync<List<Block>>(url, ActaJson.Options, ct)
                .ConfigureAwait(false);
            return blocks ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or System.Text.Json.JsonException)
        {
            _logger.LogDebug(ex, "从 {Elector} 补链（#{From} 起）失败", peer.Id, fromIndex);
            return [];
        }
    }

    public void Dispose() => _http.Dispose();
}

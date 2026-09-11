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
    private readonly ConcurrentDictionary<string, LiveState> _peerStates = new(StringComparer.Ordinal);
    private readonly ElectorIdentity _identity;
    private readonly Lock _gate = new();
    private Elector _self;
    private LiveState _state = LiveState.Empty;

    /// <param name="self">本节点的公开状态。</param>
    /// <param name="identity">本节点的密钥对，用来签心跳与实时状态。</param>
    /// <param name="allowList">信任哪些 elector。</param>
    /// <param name="options">mesh 端口、超时、fanout 等。</param>
    /// <param name="logger">日志。</param>
    /// <param name="handler">
    /// 只给测试用：传 null 走默认 handler。传进来的<b>不</b>由本类释放 ——
    /// 测试要自己持有它才能在断言里读到发出去的请求。
    /// </param>
    public HttpMesh(
        Elector self,
        ElectorIdentity identity,
        IElectorAllowList allowList,
        ConclaveOptions options,
        ILogger<HttpMesh> logger,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(options);

        _identity = identity;
        _self = self;
        _allowList = allowList;
        _options = options.Mesh;
        _logger = logger;
        _http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = _options.RequestTimeout;
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

    public LiveState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public void UpdateState(Func<LiveState, LiveState> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            var next = mutate(_state);

            // 原样返回的当作空操作。调用方里到处是 `s.Claims.Any(...) ? s : s with {...}`
            // 这样的守卫，而无条件递增会让它们全部形同虚设 —— 版本一变，mesh 里每个节点
            // 都为一次什么都没改的调用拉一遍 GET /state。
            if (ReferenceEquals(next, _state))
            {
                return;
            }

            // 版本号由这里统一递增，调用方不用管。漏加一次会让对端永远不来拉新状态，
            // 而那种失效是静默的 —— 界面上看是「别人的队列一直不更新」。
            _state = next with { Version = _state.Version + 1 };
        }
    }

    public IReadOnlyDictionary<string, LiveState> PeerStates
    {
        get
        {
            var all = new Dictionary<string, LiveState>(_peerStates, StringComparer.Ordinal);
            all[Self.Id] = State;
            return all;
        }
    }

    /// <summary>本节点状态的签名快照，供 <c>GET /state</c> 返回。</summary>
    public SignedLiveState SignedState() => SignedLiveState.Sign(_identity, State);

    /// <summary>某个对端上报的状态版本，用来判断要不要去拉。</summary>
    private readonly ConcurrentDictionary<string, long> _peerVersions = new(StringComparer.Ordinal);

    /// <summary>
    /// 对端的实时状态是否比本地已有的更新，需要拉一次。
    /// </summary>
    /// <remarks>
    /// 心跳里只带版本号（状态本体塞不进 UDP：实测心跳已 1085 字节，一条队列项约 279 字节，
    /// 34 条就 10.5KB，远超 MTU）。版本没变就不发 HTTP 请求 —— 稳态下零额外流量。
    /// </remarks>
    public bool NeedsStatePull(string electorId, long advertisedVersion)
        => advertisedVersion > 0
        && (!_peerVersions.TryGetValue(electorId, out var known) || advertisedVersion > known);

    /// <summary>
    /// 从对端拉一次实时状态并收编。
    /// </summary>
    /// <remarks>
    /// 验四件事，跟收心跳同一套：签名有效、公钥指纹与自称 id 一致、在白名单内、版本不回退。
    /// <b>状态必须验签</b>：伪造一份「我正在评所有 PR」就能让别的节点全部旁观、所有 PR 卡死；
    /// 伪造 Discovered 则能往队列里塞不存在的 PR。
    /// </remarks>
    /// <summary>
    /// 向对端要评审日志。
    /// </summary>
    /// <remarks>
    /// 拿不到就返回 null，一律只记 Debug：对端是旧版本（没有这个接口）、刚好评完把日志
    /// 换掉了、网络抖一下 —— 都不是错误，界面上显示「拿不到日志」就够了。
    /// </remarks>
    public async Task<LogChunk?> FetchLogAsync(
        Elector peer, string revisionId, long from, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);

        if (peer.Endpoint.Length == 0)
        {
            return null;
        }

        var url = $"{peer.Endpoint}/log"
            + $"?revision={Uri.EscapeDataString(revisionId)}"
            + $"&from={Math.Max(0, from).ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        try
        {
            return await _http.GetFromJsonAsync<LogChunk>(url, ActaJson.Options, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or System.Text.Json.JsonException)
        {
            _logger.LogDebug(ex, "从 {Elector} 取 {Revision} 的评审日志失败", peer.Id, revisionId);
            return null;
        }
    }

    public async Task<bool> PullStateAsync(Elector peer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);

        if (peer.Endpoint.Length == 0)
        {
            return false;
        }

        SignedLiveState? signed;
        try
        {
            signed = await _http
                .GetFromJsonAsync<SignedLiveState>($"{peer.Endpoint}/state", ActaJson.Options, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or System.Text.Json.JsonException)
        {
            _logger.LogDebug(ex, "从 {Elector} 拉实时状态失败", peer.Id);
            return false;
        }

        if (signed is null)
        {
            return false;
        }

        if (signed.ElectorId != peer.Id)
        {
            _logger.LogWarning(
                "丢弃 {Elector} 的实时状态：自称 {Claimed}", peer.Id, signed.ElectorId);
            return false;
        }

        if (!_allowList.IsAllowed(signed.ElectorId))
        {
            _logger.LogDebug("丢弃实时状态：{Elector} 不在白名单", signed.ElectorId);
            return false;
        }

        if (!signed.VerifySignature())
        {
            _logger.LogWarning("丢弃 {Elector} 的实时状态：验签失败", signed.ElectorId);
            return false;
        }

        var payload = signed.Payload();
        if (payload is null)
        {
            _logger.LogWarning("丢弃 {Elector} 的实时状态：内容不是合法 JSON", signed.ElectorId);
            return false;
        }

        // 版本回退当作重放丢掉。节点重启后版本会从 0 重新计数，那时它的心跳也会带上小版本号，
        // 于是这里会拒 —— 但节点重启同时也换不了身份，下一轮它的版本涨过旧值就恢复正常。
        if (_peerVersions.TryGetValue(signed.ElectorId, out var known) && payload.Version < known)
        {
            _logger.LogDebug(
                "丢弃 {Elector} 的实时状态：版本回退（{New} < {Known}）",
                signed.ElectorId, payload.Version, known);
            return false;
        }

        _peerStates[signed.ElectorId] = payload;
        _peerVersions[signed.ElectorId] = payload.Version;

        _logger.LogDebug(
            "收编 {Elector} 的实时状态 v{Version}（队列 {Queue}、在评 {Reviewing}）",
            signed.ElectorId, payload.Version, payload.Discovered.Count, payload.Reviewing.Count);

        return true;
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

        // 协议版本不同的先挑出来、说出来。它们的载荷格式就不一样，往下验签必然失败 ——
        // 以前这条路的结果是一句「验签失败」外加从成员表里消失，跟真掉线长得一模一样。
        if (!beacon.IsCompatible)
        {
            NoteIncompatible(beacon.Elector);
            return false;
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
                "发现节点 {Elector} @ {Endpoint}（az={Az}，{Projects} 个 project，额度已用 {Used:P0}）",
                beacon.Elector.Id, beacon.Elector.Endpoint,
                beacon.Elector.AzIdentity, beacon.Elector.Projects.Count, beacon.Elector.Utilization);
        }

        return true;
    }

    /// <summary>
    /// 有节点跑着协议版本不同的 Conclave。参数是它自报的 Elector（未验签，只用来显示）。
    /// </summary>
    /// <remarks>
    /// 每个节点十分钟最多触发一次：心跳 20 秒一发，不限的话通知会被同一台机器灌满。
    /// </remarks>
    public event Action<Elector>? ProtocolMismatch;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _mismatchNoted = new(StringComparer.Ordinal);

    private void NoteIncompatible(Elector peer)
    {
        var now = DateTimeOffset.UtcNow;
        if (_mismatchNoted.TryGetValue(peer.Id, out var last) && now - last < TimeSpan.FromMinutes(10))
        {
            return;
        }

        _mismatchNoted[peer.Id] = now;
        _logger.LogWarning(
            "节点 {Elector}（v{App}）跑的是心跳协议 v{Theirs}，本机 v{Ours} —— 忽略它的心跳；整个 mesh 要一起升级",
            peer.Id, peer.AppVersion.Length > 0 ? peer.AppVersion : "?", peer.ProtocolVersion, Beacon.ProtocolVersion);
        ProtocolMismatch?.Invoke(peer);
    }

    /// <summary>
    /// 心跳过期的节点，逐个按 HTTP 确认一次再决定是否移出 mesh。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>心跳过期只是「疑似掉线」。</b> 心跳走 UDP 组播，而组播是最容易被网络设备
    /// 静默丢掉的那种流量（交换机的 IGMP snooping、无线漫游、VPN 都会）；HTTP 是单播 TCP，
    /// 两者的失败是不相关的。只看心跳就宣布掉线，会在「组播断了但机器好着」时把它正在评的
    /// PR 判给别人 —— 那个 PR 会被评两遍，多烧一份额度，还可能提前凑够 quorum。
    /// </para>
    /// <para>
    /// 所以这里先打一次 <c>GET /state</c>：<b>答得上话就算活着</b>，把心跳时刻刷到当下
    /// （于是接下来 90 秒不用再问），顺带把它的实时状态也收编了 —— 那正好是接管判定要用的
    /// 「它在评什么」。答不上才真的移出去。
    /// </para>
    /// <para>
    /// 一次心跳窗口内每个疑似节点只会被问一次（确认成功就把窗口重置，失败就已经移出），
    /// 所以这不是轮询开销。几个疑似节点并发问，免得串起来超过心跳周期。
    /// </para>
    /// <para>
    /// ⚠️ 这条确认路径的抗重放比心跳弱：心跳的签名覆盖了 <c>LastHeartbeat</c>，重放会被
    /// <see cref="Beacon.IsFresh"/> 判过期；而 <c>/state</c> 的签名里没有时刻，只靠
    /// <see cref="PullStateAsync"/> 里的版本号不回退。也就是说，能冒充对端端点、并原样重放
    /// 它<b>最新</b>那份状态的攻击者可以让一个已死的节点看起来还活着。代价是接管被推迟
    /// （PR 停在「评审中」），拿不到任何席位 —— 席位仍要求接管方自己合格。
    /// 要彻底堵掉得让确认走一次带挑战的签名，那是协议改动，暂不做。
    /// </para>
    /// </remarks>
    /// <param name="now">这一轮的「现在」，整批疑似节点共用一个值。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>
    /// 真正被移出去的那些节点本体（不只是个数）—— 调用方要拿它们发掉线通知，
    /// 而节点一被移出成员表就再也查不到它的 az 身份了，只剩一串指纹。
    /// </returns>
    public async Task<IReadOnlyList<Elector>> ConfirmAndPruneAsync(
        DateTimeOffset now, CancellationToken ct)
    {
        var suspects = _peers.Values.Where(e => !e.IsAlive(now)).ToList();
        if (suspects.Count == 0)
        {
            return [];
        }

        var alive = await Task.WhenAll(suspects.Select(async peer =>
        {
            // 没广播过端点的问不着（老版本或起不来 HTTP 的节点），直接按掉线处理。
            if (peer.Endpoint.Length == 0)
            {
                return (peer, Confirmed: false);
            }

            var ok = await PullStateAsync(peer, ct).ConfigureAwait(false);
            return (peer, Confirmed: ok);
        })).ConfigureAwait(false);

        var removed = new List<Elector>();

        foreach (var (peer, confirmed) in alive)
        {
            if (confirmed)
            {
                // 答得上话就算活着。刷新心跳时刻 —— 所有 IsAlive 的判定点（席位资格、
                // 队列合并里的 alive 集合）读的都是它，不刷的话确认了也等于没确认。
                _ = _peers.TryUpdate(peer.Id, peer with { LastHeartbeat = now }, peer);
                _logger.LogInformation(
                    "节点 {Elector} 心跳超时但 HTTP 答得上话（组播可能被丢），继续算在线", peer.Id);
                continue;
            }

            if (_peers.TryRemove(peer.Id, out var gone))
            {
                // 状态一起清掉：它正在评的 PR 由此从全局视图消失、自动回到队列。
                // 这一条就是接管机制的全部，不需要写任何区块。
                _ = _peerStates.TryRemove(peer.Id, out _);
                _ = _peerVersions.TryRemove(peer.Id, out _);
                _logger.LogInformation(
                    "节点 {Elector} 心跳超时且 HTTP 也不通，已移出 mesh（其在评任务回队列）", peer.Id);
                removed.Add(gone);
            }
        }

        return removed;
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

    /// <summary>
    /// 请求某个节点评审一个 PR。对方同意才生效。
    /// </summary>
    /// <remarks>
    /// 顺带解决一个硬规则带来的死角：作者自己的 PR 在单节点 mesh 上永远出不去
    /// （<see cref="SeatAssignment.Eligible"/> 排除了作者本人），指派给别的节点是它的出路。
    /// </remarks>
    public async Task<bool> SendAssignmentAsync(
        Elector peer, AssignmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(request);

        return await PostSignedAsync(peer, "/assignments", request, ct).ConfigureAwait(false);
    }

    /// <summary>答复一个指派请求。</summary>
    public async Task<bool> SendAssignmentReplyAsync(
        Elector peer, AssignmentReply reply, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(reply);

        return await PostSignedAsync(peer, "/assignments/reply", reply, ct).ConfigureAwait(false);
    }

    private async Task<bool> PostSignedAsync<T>(
        Elector peer, string path, T body, CancellationToken ct)
    {
        if (peer.Endpoint.Length == 0)
        {
            _logger.LogWarning("节点 {Elector} 没有端点，发不出 {Path}", peer.Id, path);
            return false;
        }

        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"{peer.Endpoint}{path}",
                SignedAssignment.Sign(_identity, body),
                ActaJson.Options,
                ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "发 {Path} 给 {Elector} 返回 {Status}", path, peer.Id, (int)response.StatusCode);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "发 {Path} 给 {Elector} 失败", path, peer.Id);
            return false;
        }
    }

    /// <summary>
    /// 校验一条收到的指派消息，并取出本体。
    /// </summary>
    /// <returns>校验不过返回 default。</returns>
    /// <remarks>
    /// <paramref name="expectedFrom"/> 让答复能对上：只有被指派方本人的答复才算数，
    /// 否则任何白名单内的节点都能替别人「同意」，把活推给一个根本没答应的机器。
    /// </remarks>
    public T? AcceptAssignment<T>(SignedAssignment signed, string? expectedFrom = null)
    {
        ArgumentNullException.ThrowIfNull(signed);

        if (!_allowList.IsAllowed(signed.From))
        {
            // Warning 而不是 Debug：这条是「对方点了指派，而我这边什么都没出现」的
            // 唯一线索，默认日志级别下必须看得见（最常见的原因是两台机器的
            // electors.allow 只配了单向）。
            _logger.LogWarning("丢弃指派消息：{Elector} 不在白名单", signed.From);
            return default;
        }

        if (!signed.VerifySignature())
        {
            _logger.LogWarning("丢弃 {Elector} 的指派消息：验签失败", signed.From);
            return default;
        }

        if (expectedFrom is not null && signed.From != expectedFrom)
        {
            _logger.LogWarning(
                "丢弃指派答复：来自 {Actual}，但这条请求发给的是 {Expected}", signed.From, expectedFrom);
            return default;
        }

        return signed.Body<T>();
    }

    /// <summary>从某个节点回拉 <paramref name="fromIndex"/> 起的一页链段，用于补链。</summary>
    /// <remarks>
    /// <paramref name="take"/> 是<b>请求</b>的页大小，对端会按自己的
    /// <c>MeshHttpServer.MaxChainPage</c> 再夹一次 —— 所以拿回来的可能比要的少，
    /// 调用方要按「实际拿到的最大索引」往前挪，不能按页号算偏移。
    /// </remarks>
    public async Task<IReadOnlyList<Block>> PullChainAsync(
        Elector peer, long fromIndex, int take, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);

        try
        {
            var url = $"{peer.Endpoint}/chain"
                + $"?from={fromIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                + $"&take={Math.Max(1, take).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
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

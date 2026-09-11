using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure.Mesh;

/// <summary>
/// 节点之间的 HTTP 接口。
/// </summary>
/// <remarks>
/// <para>
/// 用 BCL 的 <see cref="HttpListener"/> 而不是 Kestrel：Kestrel 要把 ASP.NET Core 的
/// 框架引用拖进这个 Avalonia 进程，还要把 host 从 <c>HostApplicationBuilder</c>
/// 改造成 <c>WebApplicationBuilder</c>。内网三个 JSON 接口不值得这个代价。
/// </para>
/// <para>
/// 接口：<c>POST /blocks</c> 收区块、<c>GET /chain?from=N</c> 供对方补链、
/// <c>GET /elector</c> 方便 curl 排查。
/// </para>
/// </remarks>
public sealed class MeshHttpServer : IDisposable
{
    /// <summary>
    /// 同时处理几个请求。
    /// </summary>
    /// <remarks>
    /// 原先是每来一个请求就 <c>Task.Run</c> 一个，没有任何上限 —— 而 <c>GET /chain</c>
    /// 单次就要把一段链读进内存再整个序列化一遍。补链风暴（多个节点同时发现自己缺块）
    /// 下这是「并发数 × 单次峰值」，没有封顶。
    /// <para>
    /// 闸在 <c>GetContextAsync</c> <b>之前</b>：不收新请求，压力就退到 TCP 连接队列上，
    /// 由对端的超时去自然退避，而不是在本进程里排成一堆各自攥着请求体的 Task。
    /// </para>
    /// </remarks>
    private const int MaxConcurrentRequests = 16;

    /// <summary>
    /// 请求体最大字节数。
    /// </summary>
    /// <remarks>
    /// <c>POST /blocks</c> 原先是 <c>ReadToEndAsync</c>，对端塞多大就吃多大 ——
    /// 一个区块正常是几 KB，八兆已经是三个数量级的余量。超了回 413，
    /// 而不是先把它读进内存再判断。
    /// </remarks>
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    /// <summary>
    /// 一次 <c>GET /chain</c> 最多给几块。
    /// </summary>
    /// <remarks>
    /// 补链方按 <c>?take=</c> 分页往前挪（见 <c>MeshService.CatchUpAsync</c>）。
    /// 一块含 finding 全文时能有几 KB，500 块的响应体量级在几 MB，可控。
    /// </remarks>
    internal const int MaxChainPage = 500;

    private readonly SemaphoreSlim _slots = new(MaxConcurrentRequests, MaxConcurrentRequests);
    private readonly HttpListener _listener = new();
    private readonly IActaStore _acta;
    private readonly Func<Elector> _self;
    private readonly Func<SignedLiveState> _signedState;
    private readonly Func<SignedAssignment, bool, CancellationToken, Task<bool>> _onAssignment;
    private readonly Func<Block, CancellationToken, Task> _onBlock;
    private readonly Func<string, long, LogChunk> _readLog;
    private readonly ILogger<MeshHttpServer> _logger;

    public MeshHttpServer(
        MeshOptions options,
        IActaStore acta,
        Func<Elector> self,
        Func<SignedLiveState> signedState,
        Func<SignedAssignment, bool, CancellationToken, Task<bool>> onAssignment,
        Func<Block, CancellationToken, Task> onBlock,
        Func<string, long, LogChunk> readLog,
        ILogger<MeshHttpServer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _acta = acta;
        _self = self;
        _signedState = signedState;
        _onAssignment = onAssignment;
        _onBlock = onBlock;
        _readLog = readLog;
        _logger = logger;

        Port = options.HttpPort;
        _listener.Prefixes.Add($"http://+:{Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}/");
    }

    public int Port { get; }

    /// <summary>本节点对外的基地址，写进心跳供对端回连。</summary>
    public string Endpoint => $"http://{LocalAddress()}:{Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public void Start()
    {
        _listener.Start();
        _logger.LogInformation("mesh 接口监听 {Endpoint}", Endpoint);
    }

    /// <summary>接受请求直到取消。</summary>
    public async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // 手上已经有 MaxConcurrentRequests 个在处理就先不收新的，见那个常量的说明。
            try
            {
                await _slots.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException
                                          or HttpListenerException or ObjectDisposedException)
            {
                _ = _slots.Release();
                return;   // 取消了，或者监听器被关掉了
            }

            // 单个请求出错不能让接受循环退出，否则一个坏包就把节点从 mesh 里摘掉了。
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await HandleSafelyAsync(context, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        _ = _slots.Release();
                    }
                },
                CancellationToken.None);
        }
    }

    private async Task HandleSafelyAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            await HandleAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理 {Method} {Path} 出错", context.Request.HttpMethod, context.Request.RawUrl);
            TrySetStatus(context, HttpStatusCode.InternalServerError);
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                // 对端已经断了
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var method = context.Request.HttpMethod;

        if (method == "POST" && path == "/blocks")
        {
            var body = await ReadBodyAsync(context, ct).ConfigureAwait(false);
            if (body is null)
            {
                TrySetStatus(context, HttpStatusCode.RequestEntityTooLarge);
                return;
            }

            var block = ActaJson.Deserialize<Block>(body);

            if (block is null)
            {
                TrySetStatus(context, HttpStatusCode.BadRequest);
                return;
            }

            await _onBlock(block, ct).ConfigureAwait(false);
            TrySetStatus(context, HttpStatusCode.Accepted);
            return;
        }

        if (method == "GET" && path == "/chain")
        {
            // 全局单链之后补链是「从我这个索引往后给我」，而不是整链重传。
            var from = 0L;
            var raw = context.Request.QueryString["from"];
            if (!string.IsNullOrEmpty(raw)
                && long.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                from = Math.Max(0, parsed);
            }

            // 一次最多给 MaxChainPage 块。对端要更多就带着新的 from 再来一次。
            var take = MaxChainPage;
            var rawTake = context.Request.QueryString["take"];
            if (!string.IsNullOrEmpty(rawTake)
                && int.TryParse(rawTake, System.Globalization.CultureInfo.InvariantCulture, out var parsedTake))
            {
                take = Math.Clamp(parsedTake, 1, MaxChainPage);
            }

            var chain = await _acta.ReadChainAsync(from, take, ct).ConfigureAwait(false);
            await WriteJsonAsync(context, chain, ct).ConfigureAwait(false);
            return;
        }

        // 指派：A→B 请求评审，B→A 答复同意/拒绝。两条都要签名 + 白名单校验。
        if (method == "POST" && (path == "/assignments" || path == "/assignments/reply"))
        {
            var body = await ReadBodyAsync(context, ct).ConfigureAwait(false);
            if (body is null)
            {
                TrySetStatus(context, HttpStatusCode.RequestEntityTooLarge);
                return;
            }

            SignedAssignment? signed;
            try
            {
                signed = ActaJson.Deserialize<SignedAssignment>(body);
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "指派消息不是合法 JSON");
                signed = null;
            }

            if (signed is null)
            {
                TrySetStatus(context, HttpStatusCode.BadRequest);
                return;
            }

            // 收下才回 202。丢掉（不在白名单 / 验签不过 / 收件人不是本节点）要回 403 ——
            // 原先无论如何都回 202，于是发起方显示「已请求，等对方确认」然后永远等下去，
            // 而接收方那边一条痕迹都没有。「送到了」和「收下了」必须分开说。
            var taken = await _onAssignment(
                signed, path.EndsWith("/reply", StringComparison.Ordinal), ct).ConfigureAwait(false);

            TrySetStatus(context, taken ? HttpStatusCode.Accepted : HttpStatusCode.Forbidden);
            return;
        }

        if (method == "GET" && path == "/state")
        {
            // 实时状态：队列、谁在评什么、认领、待确认的指派。
            // 心跳只带版本号（状态本体远超 UDP 的 MTU），对端看到版本变了才来拉这里。
            // 响应体自带签名，收方逐项校验，见 HttpMesh.PullStateAsync。
            await WriteJsonAsync(context, _signedState(), ct).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path == "/log")
        {
            // 本节点正在跑（或刚跑完）的那次评审的实时日志。作者想知道自己的 PR
            // 评到哪一步了，而评审是十几分钟的黑盒 —— 见 ReviewProgressLog。
            //
            // ⚠️ 跟 /chain、/state 一样<b>不校验请求方身份</b>：同网段能连上这个端口的
            // 都读得到。日志里有源码路径、命令行和模型的分析文字，敏感度高于 /state，
            // 但低于 /chain（链上已经有完整的 finding 正文）。真要收紧的话，
            // 三个 GET 接口应该一起加签名校验，只给 electors.allow 里的节点，
            // 而不是单独给这一个 —— 那样只会造成「以为收紧了」的错觉。
            var revision = context.Request.QueryString["revision"];
            if (string.IsNullOrWhiteSpace(revision))
            {
                TrySetStatus(context, HttpStatusCode.BadRequest);
                return;
            }

            var from = 0L;
            var rawFrom = context.Request.QueryString["from"];
            if (!string.IsNullOrEmpty(rawFrom)
                && long.TryParse(rawFrom, System.Globalization.CultureInfo.InvariantCulture, out var parsedFrom))
            {
                from = Math.Max(0, parsedFrom);
            }

            await WriteJsonAsync(context, _readLog(revision, from), ct).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path == "/elector")
        {
            await WriteJsonAsync(context, _self(), ct).ConfigureAwait(false);
            return;
        }

        TrySetStatus(context, HttpStatusCode.NotFound);
    }

    /// <summary>
    /// 把请求体读成字符串，超过 <see cref="MaxBodyBytes"/> 就放弃。
    /// </summary>
    /// <returns>超限返回 <c>null</c>，调用方回 413。</returns>
    /// <remarks>
    /// 先看 <c>Content-Length</c>，但<b>不能只看它</b> —— 那个头是对端自报的，可以撒谎或者
    /// 干脆不给（chunked）。所以边读边数，越线立刻停手。
    /// </remarks>
    private static async Task<string?> ReadBodyAsync(HttpListenerContext context, CancellationToken ct)
    {
        if (context.Request.ContentLength64 > MaxBodyBytes)
        {
            return null;
        }

        var buffer = new byte[16 * 1024];
        using var body = new MemoryStream();
        int read;

        while ((read = await context.Request.InputStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (body.Length + read > MaxBodyBytes)
            {
                return null;
            }

            body.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
    }

    private static async Task WriteJsonAsync<T>(HttpListenerContext context, T value, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(ActaJson.Serialize(value));
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static void TrySetStatus(HttpListenerContext context, HttpStatusCode status)
    {
        try
        {
            context.Response.StatusCode = (int)status;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // 响应已经开始写了
        }
    }

    /// <summary>第一个非回环的 IPv4 地址。对端要靠它回连，所以不能用 localhost。</summary>
    private static string LocalAddress()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(addr.Address))
                {
                    return addr.Address.ToString();
                }
            }
        }

        return "127.0.0.1";
    }

    public void Dispose()
    {
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        _listener.Close();
        _slots.Dispose();
    }
}

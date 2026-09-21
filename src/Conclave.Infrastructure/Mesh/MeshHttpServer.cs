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
/// <c>GET /config?pk=…</c> 发配置、<c>GET /elector</c> 方便 curl 排查。
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
    private readonly Func<string, string?> _readFullLog;

    /// <summary>浏览器点了「在 Conclave 里打开」。</summary>
    private readonly Action<string, DeepLinkTarget> _onOpen;

    /// <summary>按请求方的公钥应答一份配置；本机手上还没有配置时返回 null。</summary>
    private readonly Func<string, ConfigOffer?> _offerConfig;

    private readonly ILogger<MeshHttpServer> _logger;

    public MeshHttpServer(
        MeshOptions options,
        IActaStore acta,
        Func<Elector> self,
        Func<SignedLiveState> signedState,
        Func<SignedAssignment, bool, CancellationToken, Task<bool>> onAssignment,
        Func<Block, CancellationToken, Task> onBlock,
        Func<string, long, LogChunk> readLog,
        Func<string, string?> readFullLog,
        Action<string, DeepLinkTarget> onOpen,
        Func<string, ConfigOffer?> offerConfig,
        ILogger<MeshHttpServer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _acta = acta;
        _self = self;
        _signedState = signedState;
        _onAssignment = onAssignment;
        _onBlock = onBlock;
        _readLog = readLog;
        _readFullLog = readFullLog;
        _onOpen = onOpen;
        _offerConfig = offerConfig;
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

        if (method == "GET" && path == LocalOpenLink.Path)
        {
            // 飞书卡片上那个「在 Conclave 里打开」。走 http 而不是 conclave:// ——
            // 飞书客户端把自定义协议静默丢掉（三种写法都实测过），见 LocalOpenLink。
            //
            // ⚠️ 只接回环，这是唯一一个会造成<b>本机 UI 动作</b>的接口。其余几个 GET
            // 最多泄露信息，而这个能让别人把你的面板弹出来。
            //
            // 用回环而不是 HttpListenerRequest.IsLocal：后者的语义是「回环<b>或者</b>
            // 等于本机任意一个地址」，于是从本机打自己的内网 IP 也算数。而卡片上的链接
            // 永远是 127.0.0.1，那一档放宽换不来任何功能，只是把面放大。
            //
            // 单独监听 127.0.0.1 做不到：mesh 要对邻居可达，HttpListener 已经绑了 +:47708
            // （即 0.0.0.0），再绑 127.0.0.1:47708 是冲突的；隔离就得再开一个端口，
            // 多一个要配、要同步、要过防火墙的东西。一次判断换同样的保证，值。
            if (!IsLoopback(context.Request.RemoteEndPoint?.Address))
            {
                TrySetStatus(context, HttpStatusCode.Forbidden);
                return;
            }

            var wantedOpen = context.Request.QueryString[LocalOpenLink.RevisionKey];
            if (string.IsNullOrWhiteSpace(wantedOpen))
            {
                TrySetStatus(context, HttpStatusCode.BadRequest);
                return;
            }

            _onOpen(wantedOpen, LocalOpenLink.ViewFrom(context.Request.QueryString[LocalOpenLink.ViewKey]));

            // 不回显 revision：这一页不需要它，而不回显就不存在转义对不对的问题。
            await WriteHtmlAsync(context, OpenedPage, ct).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path == "/log/live")
        {
            // /log 的人读版：一页会自己轮询 /log 的 HTML，给飞书「开始评审」卡片上那个按钮用。
            // 作者不必装 Conclave —— 评审跑在别人的机器上，要求他先装个客户端才能看
            // 自己 PR 的进度，这个门槛比它解决的问题还高。见 LiveLogPage。
            var wantedLive = context.Request.QueryString["revision"];
            if (string.IsNullOrWhiteSpace(wantedLive))
            {
                TrySetStatus(context, HttpStatusCode.BadRequest);
                return;
            }

            await WriteHtmlAsync(context, LiveLogPage.Html(wantedLive), ct).ConfigureAwait(false);
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

        if (method == "GET" && path == "/log/full")
        {
            // 落盘那份的全文（见 ReviewLogArchive）。跟 /log 的分工：那条是实时的、按序号
            // 给增量、只有内存里那几百行；这条是事后的 —— 评审失败了或者没跑完，
            // 作者要把日志整份拉回本地看，而那时候本节点早就不在评了，增量接口给不出东西。
            //
            // ⚠️ 身份校验同 /log：没有。敏感度也同 /log（源码路径、命令行、模型的分析原文），
            // 只是一次给得更多。要收紧就三个 GET 接口一起加签名校验。
            var wanted = context.Request.QueryString["revision"];
            if (string.IsNullOrWhiteSpace(wanted))
            {
                TrySetStatus(context, HttpStatusCode.BadRequest);
                return;
            }

            var full = _readFullLog(wanted);
            if (full is null)
            {
                TrySetStatus(context, HttpStatusCode.NotFound);
                return;
            }

            await WriteTextAsync(context, full, ct).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path == "/config")
        {
            // 请求方报上自己的公钥，本机把机密封给这把公钥（见 Domain.SecretSealing）。
            //
            // ⚠️ 刻意不要求请求方签名：签名在这里不起作用。公钥本来就是公开的，拿别人的
            // 公钥来要，换回去的信封自己也解不开；拿自己的来要，签名一样过得了。
            //
            // 也就是说加密保护的是<b>线路</b>，不是授权。本设计里授权边界就是 mesh 本身：
            // 能连上这个端口的都拿得到（跟 /state、/chain 同一姿态）。挡住损失的是
            // SyncableConfig 那张白名单，不是这里。
            var requester = context.Request.QueryString["pk"];
            if (string.IsNullOrWhiteSpace(requester))
            {
                TrySetStatus(context, HttpStatusCode.BadRequest);
                return;
            }

            var offer = _offerConfig(requester);
            if (offer is null)
            {
                // 本机手上还没有配置。给 404 而不是 200 空体：对端据此就跳过这台。
                TrySetStatus(context, HttpStatusCode.NotFound);
                return;
            }

            await WriteJsonAsync(context, offer, ct).ConfigureAwait(false);
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

    /// <summary>
    /// 这个请求是不是从本机回环来的。
    /// </summary>
    /// <remarks>
    /// 拿地址而不是 <c>HttpListenerRequest</c> 当参数，纯粹是为了能测 ——
    /// <c>HttpListenerRequest</c> 造不出来。
    /// <para>
    /// 拿不到远端地址时判否：这里的默认值必须是「拒绝」。
    /// </para>
    /// </remarks>
    internal static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        // IPv4 映射进 IPv6 的形态（::ffff:127.0.0.1）要先拆回去再判，
        // 否则回环从 IPv6 栈进来会被当成外部请求挡掉。
        var plain = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        return IPAddress.IsLoopback(plain);
    }

    /// <summary>
    /// <c>GET /open</c> 的回执。
    /// </summary>
    /// <remarks>
    /// 人要看的东西已经在面板上了，这一页只是浏览器不得不显示的那个落点 ——
    /// 所以它只说一句话，并且不回显任何来自请求的内容。
    /// </remarks>
    private const string OpenedPage = """
<!DOCTYPE html>
<html lang="zh">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>已在 Conclave 中打开</title>
<style>
  :root { color-scheme: light dark; }
  body { margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center;
         font:15px/1.7 -apple-system,BlinkMacSystemFont,"PingFang SC",sans-serif;
         background:#f6f7f9; color:#24292f; }
  @media (prefers-color-scheme: dark) { body { background:#12141a; color:#d7dae0; } }
  main { text-align:center; padding:24px; }
  h1 { font-size:17px; margin:0 0 8px; }
  p { margin:0; opacity:.65; font-size:13px; }
</style>
</head>
<body>
<main>
  <h1>已在 Conclave 中打开</h1>
  <p>可以关掉这个标签页了。</p>
</main>
</body>
</html>
""";

    private static Task WriteHtmlAsync(HttpListenerContext context, string html, CancellationToken ct)
        => WriteBodyAsync(context, html, "text/html; charset=utf-8", ct);

    /// <summary>纯文本原样发。日志不是 JSON —— 包一层只会让 curl 出来的东西没法直接读。</summary>
    private static Task WriteTextAsync(HttpListenerContext context, string text, CancellationToken ct)
        => WriteBodyAsync(context, text, "text/plain; charset=utf-8", ct);

    /// <summary>
    /// 把一段文本当响应体发出去。
    /// </summary>
    /// <remarks>
    /// <c>nosniff</c> 是为 <c>/log/live</c> 加的：那条路不校验身份，而 revision 参数由请求方给 ——
    /// 浏览器自己去猜类型的话，一个精心构造的参数能让本该是纯文本的响应被当成 HTML 执行。
    /// 三条路共用一个写入口，这类响应头加一次就都有了，不必逐条记得。
    /// </remarks>
    private static async Task WriteBodyAsync(
        HttpListenerContext context, string body, string contentType, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = contentType;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
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

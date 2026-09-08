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
    private readonly HttpListener _listener = new();
    private readonly IActaStore _acta;
    private readonly Func<Elector> _self;
    private readonly Func<Block, CancellationToken, Task> _onBlock;
    private readonly ILogger<MeshHttpServer> _logger;

    public MeshHttpServer(
        MeshOptions options,
        IActaStore acta,
        Func<Elector> self,
        Func<Block, CancellationToken, Task> onBlock,
        ILogger<MeshHttpServer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _acta = acta;
        _self = self;
        _onBlock = onBlock;
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
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;   // 监听器被关掉了
            }

            // 单个请求出错不能让接受循环退出，否则一个坏包就把节点从 mesh 里摘掉了。
            _ = Task.Run(() => HandleSafelyAsync(context, ct), CancellationToken.None);
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
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
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

            var chain = await _acta.ReadChainAsync(from, ct).ConfigureAwait(false);
            await WriteJsonAsync(context, chain, ct).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path == "/elector")
        {
            await WriteJsonAsync(context, _self(), ct).ConfigureAwait(false);
            return;
        }

        TrySetStatus(context, HttpStatusCode.NotFound);
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
    }
}

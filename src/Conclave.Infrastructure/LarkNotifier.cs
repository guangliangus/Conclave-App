using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure;

/// <summary>
/// 用飞书自建应用的 <c>tenant_access_token</c> 私聊 PR 作者。
/// </summary>
/// <remarks>
/// <para>
/// 收件人有三种来源，按可靠性排序：<see cref="LarkOptions.UserMap"/> 里直接写的
/// <c>open_id</c>、邮箱换来的 <c>open_id</c>、以及按邮箱直投。
/// </para>
/// <para>
/// <b>实测只有 open_id 真的能发。</b> <c>receive_id_type=email</c> 在本租户上一律
/// 99992402 —— 因为应用看不到通讯录里的邮箱字段（<c>contact/v3/users/{id}</c> 回来的记录里
/// 连 <c>email</c> 键都没有，那归 <c>contact:user.email:readonly</c> 管），于是收件人解析不出来。
/// 同一个 body 换成 open_id 立刻成功。所以按邮箱直投只当最后一跳的尽力而为，
/// 别当成「不需要权限的那条路」——它需要，只是需要的是另一个 scope。
/// </para>
/// <para>
/// 结论：要么在 <c>UserMap</c> 里写死 <c>ou_</c>（零权限，今天就能用），
/// 要么开 <c>contact:user.id:readonly</c> 让邮箱能换成 open_id。
/// </para>
/// <para>
/// 缺通讯录权限只探一次：飞书对未申请的 scope 返回 99991672，而这个状态在进程生命周期内
/// 不会变（开权限要去开发者后台，改完得重启节点）。每条通知都白打一次请求没有意义。
/// </para>
/// </remarks>
public sealed class LarkNotifier : INotifier, IDisposable
{
    /// <summary>飞书的「应用未申请该 scope」。</summary>
    private const int ScopeNotApplied = 99991672;

    /// <summary>
    /// 飞书的「参数校验失败」。
    /// </summary>
    /// <remarks>
    /// 收件人不存在和收件人<b>解析不了</b>共用这一个码，所以它单独看没有信息量 ——
    /// 见 <see cref="BuildSendError"/>。
    /// </remarks>
    private const int FieldValidationFailed = 99992402;

    /// <summary>正文里最多列几条 finding。再多就该去看 PR 本身了。</summary>
    private const int MaxListedFindings = 3;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly LarkOptions _options;

    /// <summary>Azure DevOps 组织地址。只用来兜底拼 PR 链接（老区块里没有 RemoteUrl）。</summary>
    private readonly string _orgUrl;

    private readonly ILogger<LarkNotifier> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    /// <summary>邮箱 → open_id。查一次就够，人不会换 open_id。</summary>
    private readonly ConcurrentDictionary<string, string> _openIds = new(StringComparer.OrdinalIgnoreCase);

    private string _token = string.Empty;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private bool _canResolveOpenId = true;

    /// <remarks>
    /// <c>handler</c> 只给测试用：传 null 走默认 handler，传进来的<b>不</b>由本类释放 ——
    /// 测试要自己持有它才能在断言里读到发出去的请求。
    /// </remarks>
    public LarkNotifier(
        ConclaveOptions options,
        ILogger<LarkNotifier> logger,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Lark;
        _orgUrl = options.AzureDevOpsOrgUrl;
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _http.Timeout = _options.RequestTimeout;
    }

    public async Task NotifyPromulgationAsync(PrMeta pr, PromulgationPayload result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(result);

        if (!_options.IsConfigured)
        {
            return;
        }

        var recipient = _options.RecipientFor(pr.Author);
        if (recipient is null)
        {
            // 不要静默跳过：配错域名或漏配 UserMap 的表现就是「结论出了但没人收到」，
            // 而那跟「通知功能没开」在外部看来一模一样。
            _logger.LogWarning(
                "PR {PrId} 的作者 {Author} 换不出飞书收件人（UserMap 没有，EmailDomain 也没配），跳过通知",
                pr.PrId, pr.Author);
            return;
        }

        var (idType, id) = await ResolveRecipientAsync(recipient, ct).ConfigureAwait(false);
        var body = new
        {
            ReceiveId = id,
            MsgType = "text",
            Content = JsonSerializer.Serialize(new { text = RenderText(pr, result, _orgUrl) }, Json),
        };

        var (code, msg, doc) = await PostAsync(
            $"open-apis/im/v1/messages?receive_id_type={idType}", body, authorize: true, ct)
            .ConfigureAwait(false);

        using (doc)
        {
            if (code != 0)
            {
                throw new InvalidOperationException(BuildSendError(recipient, idType, code, msg));
            }
        }

        _logger.LogInformation("已飞书通知 PR {PrId} 的作者 {Recipient}（{IdType}）", pr.PrId, recipient, idType);
    }

    /// <summary>
    /// 发送失败的说明。
    /// </summary>
    /// <remarks>
    /// 按邮箱发失败时要额外指路。实测：应用看不到通讯录里的邮箱字段
    /// （<c>contact/v3/users/{id}</c> 回来的记录里连 <c>email</c> 键都没有）时，
    /// <c>receive_id_type=email</c> 一律 99992402 —— 跟「这个人不存在」同一个码，
    /// 而同一个 body 换成 open_id 立刻成功。让人从这个码自己反推一遍不合理。
    /// </remarks>
    private static string BuildSendError(string recipient, string idType, int code, string msg)
    {
        var head = $"飞书发送失败（收件人 {recipient}，{idType}）：{code} {msg}";

        return idType == "email" && code == FieldValidationFailed
            ? head + "。按邮箱发要求应用能看到通讯录里的邮箱；"
                + "要么在 UserMap 里直接写 ou_ 开头的 open_id（不需要任何通讯录权限），"
                + "要么开通 contact:user.id:readonly 走邮箱换 open_id"
            : head;
    }

    /// <summary>
    /// 决定这条消息按什么身份发。
    /// </summary>
    /// <returns><c>receive_id_type</c> 与对应的 ID。</returns>
    private async Task<(string IdType, string Id)> ResolveRecipientAsync(string recipient, CancellationToken ct)
    {
        if (recipient.StartsWith("ou_", StringComparison.Ordinal))
        {
            return ("open_id", recipient);
        }

        if (_openIds.TryGetValue(recipient, out var cached))
        {
            return ("open_id", cached);
        }

        if (_canResolveOpenId)
        {
            var openId = await LookupOpenIdAsync(recipient, ct).ConfigureAwait(false);
            if (openId is not null)
            {
                _openIds[recipient] = openId;
                return ("open_id", openId);
            }
        }

        return ("email", recipient);
    }

    /// <summary>邮箱换 open_id。换不到（含没权限）返回 null，由调用方退回按邮箱直投。</summary>
    private async Task<string?> LookupOpenIdAsync(string email, CancellationToken ct)
    {
        var (code, msg, doc) = await PostAsync(
            "open-apis/contact/v3/users/batch_get_id?user_id_type=open_id",
            new { Emails = new[] { email } }, authorize: true, ct).ConfigureAwait(false);

        using (doc)
        {
            if (code == ScopeNotApplied)
            {
                _canResolveOpenId = false;
                _logger.LogWarning(
                    "飞书应用未申请 contact:user.id:readonly，本进程改为按邮箱直投；"
                    + "到开发者后台开通后重启节点即可用 open_id（{Msg}）", msg);
                return null;
            }

            if (code != 0)
            {
                _logger.LogWarning("查 {Email} 的 open_id 失败（{Code} {Msg}），改为按邮箱直投", email, code, msg);
                return null;
            }

            if (!doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("user_list", out var users)
                || users.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var user in users.EnumerateArray())
            {
                // user_id 这个字段名跟着 user_id_type 变形：请求里问的是 open_id，回的就是 ou_ 开头的值。
                if (user.TryGetProperty("user_id", out var uid)
                    && uid.GetString() is { Length: > 0 } openId)
                {
                    return openId;
                }
            }

            _logger.LogWarning("{Email} 在飞书通讯录里查无此人，仍按邮箱试投一次", email);
            return null;
        }
    }

    /// <summary>
    /// <c>tenant_access_token</c>，带缓存。
    /// </summary>
    /// <remarks>
    /// 提前 5 分钟作废：飞书给的有效期是 2 小时，卡着边界用会偶发 99991663，
    /// 而那种偶发失败在「一天几条通知」的频率下极难复现。
    /// </remarks>
    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_tokenExpiresAt > DateTimeOffset.UtcNow)
        {
            return _token;
        }

        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_tokenExpiresAt > DateTimeOffset.UtcNow)
            {
                return _token;
            }

            var (code, msg, doc) = await PostAsync(
                "open-apis/auth/v3/tenant_access_token/internal",
                new { AppId = _options.AppId, AppSecret = _options.AppSecret },
                authorize: false, ct).ConfigureAwait(false);

            using (doc)
            {
                if (code != 0)
                {
                    throw new InvalidOperationException($"换 tenant_access_token 失败：{code} {msg}");
                }

                _token = doc.RootElement.TryGetProperty("tenant_access_token", out var t)
                    ? t.GetString() ?? string.Empty
                    : string.Empty;

                var expire = doc.RootElement.TryGetProperty("expire", out var e)
                    && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 7200;

                _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expire - 300));
            }

            return _token;
        }
        finally
        {
            _ = _tokenGate.Release();
        }
    }

    /// <summary>
    /// 打一次 OpenAPI。
    /// </summary>
    /// <remarks>
    /// 刻意不走 <c>EnsureSuccessStatusCode</c>：权限不足这类错误飞书是带着 JSON body
    /// 用非 2xx 返回的，先抛 <c>HttpRequestException</c> 就把 <c>code</c> 丢了，
    /// 而 99991672 正是需要据以降级的那一个。
    /// </remarks>
    private async Task<(int Code, string Msg, JsonDocument Doc)> PostAsync(
        string path, object body, bool authorize, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, body.GetType(), Json), Encoding.UTF8, "application/json"),
        };

        if (authorize)
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", await TokenAsync(ct).ConfigureAwait(false));
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"飞书 {path} 返回了非 JSON（HTTP {(int)response.StatusCode}）", ex);
        }

        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetInt32()
            : -1;
        var msg = root.TryGetProperty("msg", out var m) ? m.GetString() ?? string.Empty : string.Empty;

        return (code, msg, doc);
    }

    /// <summary>
    /// 通知正文。纯函数，测试直接断言它的形状。
    /// </summary>
    /// <param name="pr">PR 快照。</param>
    /// <param name="result">公布的结论。</param>
    /// <param name="orgUrl">
    /// Azure DevOps 组织地址，只用来兜底拼 PR 链接（老区块里没有 <c>RemoteUrl</c>）。
    /// 两者都没有时正文里就不带链接。
    /// </param>
    internal static string RenderText(PrMeta pr, PromulgationPayload result, string? orgUrl = null)
    {
        var lines = new List<string>(MaxListedFindings + 5)
        {
            $"【Conclave】PR #{pr.PrId.ToString(CultureInfo.InvariantCulture)} 评审结论："
                + DecisionLabels.Decision(result.Decision),
            $"{pr.Project}/{pr.Repo} · {pr.Title}",
            $"{result.Findings.Count.ToString(CultureInfo.InvariantCulture)} 条问题 · "
                + $"{result.ActualQuorum.ToString(CultureInfo.InvariantCulture)}/"
                + $"{result.ExpectedQuorum.ToString(CultureInfo.InvariantCulture)} 票"
                + (result.Degraded ? "（降级：合格节点不足）" : string.Empty),
        };

        foreach (var finding in result.Findings.Take(MaxListedFindings))
        {
            lines.Add(
                $"· [{DecisionLabels.Severity(finding.Best.Severity)}] "
                + $"{finding.Best.File}:{finding.Best.Line.ToString(CultureInfo.InvariantCulture)} "
                + finding.Best.Title);
        }

        if (result.Findings.Count > MaxListedFindings)
        {
            var rest = result.Findings.Count - MaxListedFindings;
            lines.Add($"· 还有 {rest.ToString(CultureInfo.InvariantCulture)} 条，见 PR");
        }

        var url = PrLink.For(pr, orgUrl);
        if (url is not null)
        {
            lines.Add(url);
        }

        return string.Join('\n', lines);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tokenGate.Dispose();
    }
}

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
/// <b>实测邮箱直投就够。</b> <c>receive_id_type=email</c> 的收件人解析是租户侧做的，
/// 不经过应用自己的通讯录可见性 —— 2026-09-17 在一个<b>明确没有</b>
/// <c>contact:user.id:readonly</c> 的应用上连发成功两次（三次调用的全过程见 DESIGN §9.5）。
/// 所以排在最后的那一跳才是常态路径，不是「尽力而为」。
/// </para>
/// <para>
/// 真正会让它静默失效的是<b>可用范围</b>：收件人不在应用的可用范围内时解析不出来，
/// 返回 99992402 —— 跟「这个人不存在」共用一个码，单看没有信息量。换一个应用重来时
/// 要确认的是这个，不是权限列表。
/// </para>
/// <para>
/// 开 <c>contact:user.id:readonly</c> 只省掉每进程一次的 open_id 探测，不是必需品。
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

    /// <summary>
    /// 整份配置，<b>不是</b> <c>options.Lark</c>。
    /// </summary>
    /// <remarks>
    /// 配置是热更新的（见 <c>ConfigHotReload</c>），在构造函数里把 <c>Lark</c> 那一块拆出来
    /// 存下的话，人改完配置这个通知器还在用旧应用发 —— 而那种失效不报错、不打日志，
    /// 表面上一切正常。所以存整份，每次现读 <see cref="Options"/>。
    /// </remarks>
    private readonly ConclaveOptions _conclave;

    private readonly ILogger<LarkNotifier> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    /// <summary>邮箱 → open_id。查一次就够，人不会换 open_id。</summary>
    private readonly ConcurrentDictionary<string, string> _openIds = new(StringComparer.OrdinalIgnoreCase);

    private string _token = string.Empty;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private bool _canResolveOpenId = true;

    /// <summary>换出当前这枚 token 的那对凭据。配置换了应用，旧 token 就不能再用。</summary>
    private (string AppId, string AppSecret) _tokenFor = (string.Empty, string.Empty);

    /// <summary><see cref="_openIds"/> 与 <see cref="_canResolveOpenId"/> 属于哪个应用。</summary>
    private string _cachesFor = string.Empty;

    /// <summary>当前生效的飞书配置。每次现读，不缓存 —— 见 <see cref="_conclave"/>。</summary>
    private LarkOptions Options => _conclave.Lark;

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

        _conclave = options;
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);

        // BaseAddress 和 Timeout 都只在第一次请求前可改，而这两项都是热更新得了的配置。
        // 所以地址每次请求现拼，超时交给 per-request 的 CTS —— HttpClient 自己的超时关掉，
        // 否则 RequestTimeout 配得比它的默认 100 秒大时，先到期的是它而不是配置。
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task NotifyPromulgationAsync(PrMeta pr, PromulgationPayload result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(result);

        if (!Options.IsConfigured)
        {
            return;
        }

        var recipient = Options.RecipientFor(pr.Author);
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
            Content = JsonSerializer.Serialize(
                new { text = RenderText(pr, result, _conclave.AzureDevOpsOrgUrl) }, Json),
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
    /// 按邮箱发失败时要额外指路。99992402 跟「这个人不存在」共用一个码，单看没有信息量，
    /// 而实测最常见的成因是<b>收件人不在应用的可用范围内</b> —— 邮箱直投本身不需要任何
    /// 通讯录权限（见 DESIGN §9.5）。让人从这个码自己反推一遍不合理。
    /// </remarks>
    private static string BuildSendError(string recipient, string idType, int code, string msg)
    {
        var head = $"飞书发送失败（收件人 {recipient}，{idType}）：{code} {msg}";

        return idType == "email" && code == FieldValidationFailed
            ? head + "。这个码同时表示「查无此人」和「解析不出收件人」；"
                + "先去开发者后台确认这个人在应用的可用范围内（实测最常见的成因），"
                + "再看邮箱本身对不对（EmailDomain 拼不准的人用 UserMap 单独兜）"
            : head;
    }

    /// <summary>
    /// 决定这条消息按什么身份发。
    /// </summary>
    /// <returns><c>receive_id_type</c> 与对应的 ID。</returns>
    private async Task<(string IdType, string Id)> ResolveRecipientAsync(string recipient, CancellationToken ct)
    {
        DropCachesIfAppChanged();

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

    /// <summary>
    /// 配置热更新换了应用的话，把跟应用绑死的两样缓存丢掉。
    /// </summary>
    /// <remarks>
    /// <para>
    /// open_id 是<b>应用维度</b>的：同一个人在新应用上是另一个 <c>ou_</c>。拿旧的去发会撞上
    /// 99992402，而那个码跟「查无此人」共用，看日志根本分不出是换应用造成的。
    /// </para>
    /// <para>
    /// 位置很讲究：必须在<b>解析收件人之前</b>。放在 <see cref="TokenAsync"/> 里（换 token 时
    /// 顺手清）看着更自然，但那时收件人早就从旧缓存里取出来了 —— 换应用后的第一条通知
    /// 仍然会用旧的 <c>ou_</c> 发出去，而且只错这一条，最难查的那种。
    /// </para>
    /// <para>
    /// <see cref="_canResolveOpenId"/> 也要一起复位：它记的是「这个应用没申请通讯录权限」，
    /// 换个应用这个结论就不成立了。
    /// </para>
    /// </remarks>
    private void DropCachesIfAppChanged()
    {
        var appId = Options.AppId;
        if (string.Equals(_cachesFor, appId, StringComparison.Ordinal))
        {
            return;
        }

        if (_cachesFor.Length > 0)
        {
            _openIds.Clear();
            _canResolveOpenId = true;
            _logger.LogInformation("飞书应用换成 {AppId}，已清掉 open_id 缓存与 scope 判定", appId);
        }

        _cachesFor = appId;
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
        var credentials = (Options.AppId, Options.AppSecret);

        if (_tokenExpiresAt > DateTimeOffset.UtcNow && _tokenFor == credentials)
        {
            return _token;
        }

        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_tokenExpiresAt > DateTimeOffset.UtcNow && _tokenFor == credentials)
            {
                return _token;
            }

            var (code, msg, doc) = await PostAsync(
                "open-apis/auth/v3/tenant_access_token/internal",
                new { AppId = credentials.AppId, AppSecret = credentials.AppSecret },
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
                _tokenFor = credentials;
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
        // 地址每次现拼：BaseUrl 是热更新得了的，而 HttpClient.BaseAddress 焊死在第一次请求前。
        var url = new Uri(new Uri(Options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute), path);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, body.GetType(), Json), Encoding.UTF8, "application/json"),
        };

        if (authorize)
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", await TokenAsync(ct).ConfigureAwait(false));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Options.RequestTimeout);

        HttpResponseMessage response;
        string raw;
        try
        {
            response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            raw = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 自家的超时不能以取消的形态往上抛。调用方一路都是
            // `catch (Exception ex) when (ex is not OperationCanceledException)`（那是留给停机的），
            // 抛成取消会直接穿过 ReviewOrchestrator.PromulgateAsync 的护栏 ——
            // 一次飞书超时就能掀掉「通知失败绝不影响公布」这条。
            throw new TimeoutException($"飞书 {path} 超时（{Options.RequestTimeout}）");
        }

        using var _ = response;

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
        };

        // 执行失败的正文跟出了结论的完全不一样。
        //
        // 作者拿到它时最想知道的是「我要改什么」，而答案是「你什么都不用改」—— 这句话
        // 必须直接说出来，否则他会去读那些 0 票、降级之类的字眼，然后自己脑补出一个结论。
        // 也不列 finding：失败的那一票本来就没有 finding，列出来只会是一行「0 条问题」，
        // 而那在这个语境下读起来像「评过了，没问题」。
        if (result.Decision == ReviewDecision.Error)
        {
            lines.Add("评审没能跑出结论，是我们这边的问题，不是你代码的问题 —— 不用改什么。");
            lines.Add("已经在排查，修好后会重新评这一版。");

            var errorUrl = PrLink.For(pr, orgUrl);
            if (errorUrl is not null)
            {
                lines.Add(errorUrl);
            }

            return string.Join('\n', lines);
        }

        lines.Add(
            $"{result.Findings.Count.ToString(CultureInfo.InvariantCulture)} 条问题 · "
            + $"{result.ActualQuorum.ToString(CultureInfo.InvariantCulture)}/"
            + $"{result.ExpectedQuorum.ToString(CultureInfo.InvariantCulture)} 票"
            + (result.Degraded ? "（降级：合格节点不足）" : string.Empty));

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

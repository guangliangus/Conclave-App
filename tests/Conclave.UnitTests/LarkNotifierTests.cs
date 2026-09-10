using System.Net;
using System.Text;
using System.Text.Json;
using Conclave.Application;
using Conclave.Domain;
using Conclave.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

/// <summary>
/// 飞书通知：收件人怎么算出来、缺权限怎么降级、token 有没有复用。
/// </summary>
/// <remarks>
/// 这三件事的失败方式都是「静默不发」：算不出收件人、scope 没开、token 过期各自都只会
/// 让消息发不出去，而结论仍然正常公布 —— 也就是说线上表现是「功能好像没接」，
/// 没有任何一处会报错。所以必须在这里钉住。
/// </remarks>
public sealed class LarkNotifierTests
{
    private const string OpenId = "ou_9c1749592527ebd48f3c1ec1269656db";

    private static PrMeta Pr(string author = @"LIONMAIL\tobeyhuang") => new()
    {
        PrId = 2954,
        Project = "liontrip-order",
        Repo = "liontrip-order",
        Title = "feat(voucher): embed 小程序码",
        Author = author,
        SrcCommit = "bdcc84babd7486340b07812586a30f676862f331",
        RemoteUrl = "https://azdevops.liontravel.com/LionTechShanghai/liontrip-order/_git/liontrip-order",
        FilesChanged = 3,
    };

    private static PromulgationPayload Result(int findings = 1) => new(
        "liontrip-order/2954@bdcc84b",
        ReviewDecision.Reject,
        [.. Enumerable.Range(0, findings).Select(i => new MergedFinding(
            new Finding($"src/A{i}.cs", 12 + i, Severity.Major, $"问题 {i}", "细节"), 2, 1.0))],
        Degraded: false,
        ActualQuorum: 2,
        ExpectedQuorum: 2);

    private static ConclaveOptions Options(
        bool enabled = true, string emailDomain = "liontravel.com", string? mapAccount = null, string? mapTo = null)
    {
        var opts = new ConclaveOptions
        {
            Lark = new LarkOptions
            {
                Enabled = enabled,
                AppId = "cli_test",
                AppSecret = "secret",
                BaseUrl = "https://open.larksuite.invalid",
                EmailDomain = emailDomain,
            },
        };

        if (mapAccount is not null && mapTo is not null)
        {
            opts.Lark.UserMap[mapAccount] = mapTo;
        }

        return opts;
    }

    /// <summary>照飞书的信封形状回一个成功响应。</summary>
    private static string Ok(string? data = null)
        => data is null ? """{"code":0,"msg":"success"}""" : $$"""{"code":0,"msg":"success","data":{{data}}}""";

    private sealed class Stub : HttpMessageHandler
    {
        private readonly Func<string, string> _respond;

        internal Stub(Func<string, string> respond) => _respond = respond;

        internal List<(string Path, string Body, string? Auth)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var path = request.RequestUri!.PathAndQuery;
            Calls.Add((path, body, request.Headers.Authorization?.Parameter));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_respond(path), Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>token → open_id → 发送，三步都成功的那条路。</summary>
    private static Func<string, string> HappyPath => path => path switch
    {
        var p when p.Contains("tenant_access_token", StringComparison.Ordinal)
            => """{"code":0,"msg":"ok","tenant_access_token":"t-abc","expire":7200}""",
        var p when p.Contains("batch_get_id", StringComparison.Ordinal)
            => Ok($$"""{"user_list":[{"user_id":"{{OpenId}}","email":"tobeyhuang@liontravel.com"}]}"""),
        _ => Ok(),
    };

    [Fact]
    public async Task Nothing_is_sent_when_the_switch_is_off()
    {
        using var stub = new Stub(HappyPath);
        using var notifier = new LarkNotifier(
            Options(enabled: false), NullLogger<LarkNotifier>.Instance, stub);

        await notifier.NotifyPromulgationAsync(Pr(), Result(), CancellationToken.None);

        Assert.Empty(stub.Calls);
    }

    [Fact]
    public async Task Nothing_is_sent_when_the_author_maps_to_nobody()
    {
        using var stub = new Stub(HappyPath);
        using var notifier = new LarkNotifier(
            Options(emailDomain: string.Empty), NullLogger<LarkNotifier>.Instance, stub);

        await notifier.NotifyPromulgationAsync(Pr(), Result(), CancellationToken.None);

        Assert.Empty(stub.Calls);
    }

    [Fact]
    public async Task The_author_is_resolved_to_an_open_id_and_the_message_goes_out_by_open_id()
    {
        using var stub = new Stub(HappyPath);
        using var notifier = new LarkNotifier(Options(), NullLogger<LarkNotifier>.Instance, stub);

        await notifier.NotifyPromulgationAsync(Pr(), Result(), CancellationToken.None);

        Assert.Equal(3, stub.Calls.Count);
        Assert.Contains("tenant_access_token", stub.Calls[0].Path, StringComparison.Ordinal);

        // 域账号拼成邮箱后才去查通讯录。
        Assert.Contains("batch_get_id", stub.Calls[1].Path, StringComparison.Ordinal);
        Assert.Contains("tobeyhuang@liontravel.com", stub.Calls[1].Body, StringComparison.Ordinal);
        Assert.Equal("t-abc", stub.Calls[1].Auth);

        var send = stub.Calls[2];
        Assert.Contains("receive_id_type=open_id", send.Path, StringComparison.Ordinal);

        using var sent = JsonDocument.Parse(send.Body);
        Assert.Equal(OpenId, sent.RootElement.GetProperty("receive_id").GetString());
        Assert.Equal("text", sent.RootElement.GetProperty("msg_type").GetString());

        // content 是一个 JSON 字符串而不是嵌套对象，发错会被飞书直接拒。
        var content = sent.RootElement.GetProperty("content").GetString();
        Assert.NotNull(content);
        using var inner = JsonDocument.Parse(content);
        Assert.Contains("PR #2954", inner.RootElement.GetProperty("text").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_user_map_entry_holding_an_open_id_skips_the_directory_lookup()
    {
        using var stub = new Stub(HappyPath);
        using var notifier = new LarkNotifier(
            Options(mapAccount: "tobeyhuang", mapTo: OpenId), NullLogger<LarkNotifier>.Instance, stub);

        await notifier.NotifyPromulgationAsync(Pr(), Result(), CancellationToken.None);

        Assert.DoesNotContain(stub.Calls, c => c.Path.Contains("batch_get_id", StringComparison.Ordinal));
        Assert.Contains("receive_id_type=open_id", stub.Calls[^1].Path, StringComparison.Ordinal);
    }

    /// <summary>
    /// 缺 <c>contact:user.id:readonly</c> 时必须退到按邮箱直投，而不是放弃通知。
    /// </summary>
    /// <remarks>
    /// 这正是本机应用现在的状态（实测 99991672），所以这条是「今天就能用」的那条路。
    /// </remarks>
    [Fact]
    public async Task Missing_contact_scope_falls_back_to_sending_by_email()
    {
        using var stub = new Stub(path => path.Contains("batch_get_id", StringComparison.Ordinal)
            ? """{"code":99991672,"msg":"app has not applied for the required scope(s)"}"""
            : HappyPath(path));

        using var notifier = new LarkNotifier(Options(), NullLogger<LarkNotifier>.Instance, stub);

        await notifier.NotifyPromulgationAsync(Pr(), Result(), CancellationToken.None);

        var send = stub.Calls[^1];
        Assert.Contains("receive_id_type=email", send.Path, StringComparison.Ordinal);

        using var sent = JsonDocument.Parse(send.Body);
        Assert.Equal("tobeyhuang@liontravel.com", sent.RootElement.GetProperty("receive_id").GetString());
    }

    [Fact]
    public async Task The_directory_lookup_is_not_retried_after_a_scope_refusal()
    {
        using var stub = new Stub(path => path.Contains("batch_get_id", StringComparison.Ordinal)
            ? """{"code":99991672,"msg":"nope"}"""
            : HappyPath(path));

        using var notifier = new LarkNotifier(Options(), NullLogger<LarkNotifier>.Instance, stub);

        await notifier.NotifyPromulgationAsync(Pr(), Result(), CancellationToken.None);
        await notifier.NotifyPromulgationAsync(Pr(), Result(), CancellationToken.None);

        Assert.Single(stub.Calls, c => c.Path.Contains("batch_get_id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_tenant_token_is_fetched_once_and_reused()
    {
        using var stub = new Stub(HappyPath);
        using var notifier = new LarkNotifier(Options(), NullLogger<LarkNotifier>.Instance, stub);

        await notifier.NotifyPromulgationAsync(Pr(), Result(), CancellationToken.None);
        await notifier.NotifyPromulgationAsync(Pr(), Result(), CancellationToken.None);

        Assert.Single(stub.Calls, c => c.Path.Contains("tenant_access_token", StringComparison.Ordinal));
    }

    /// <summary>飞书用非 0 code 表示失败，HTTP 状态可能仍是 200 —— 不能当成发送成功。</summary>
    [Fact]
    public async Task A_non_zero_code_on_the_send_is_surfaced_as_a_failure()
    {
        using var stub = new Stub(path => path.Contains("/im/", StringComparison.Ordinal)
            ? """{"code":230002,"msg":"bot is not in the chat"}"""
            : HappyPath(path));

        using var notifier = new LarkNotifier(Options(), NullLogger<LarkNotifier>.Instance, stub);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => notifier.NotifyPromulgationAsync(Pr(), Result(), CancellationToken.None));

        Assert.Contains("230002", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 按邮箱发失败时，报错要指出真正的出路。
    /// </summary>
    /// <remarks>
    /// 99992402 跟「这个人不存在」共用一个码，单看没有信息量 —— 实测本租户上按邮箱发
    /// 一律是这个码，因为应用看不到通讯录里的 email 字段。不指路的话下一个人会再反推一遍。
    /// </remarks>
    [Fact]
    public async Task A_failed_email_send_points_at_the_two_real_ways_out()
    {
        using var stub = new Stub(path => path switch
        {
            var p when p.Contains("batch_get_id", StringComparison.Ordinal)
                => """{"code":99991672,"msg":"scope not applied"}""",
            var p when p.Contains("/im/", StringComparison.Ordinal)
                => """{"code":99992402,"msg":"field validation failed"}""",
            _ => HappyPath(path),
        });

        using var notifier = new LarkNotifier(Options(), NullLogger<LarkNotifier>.Instance, stub);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => notifier.NotifyPromulgationAsync(Pr(), Result(), CancellationToken.None));

        Assert.Contains("UserMap", ex.Message, StringComparison.Ordinal);
        Assert.Contains("contact:user.id:readonly", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_body_carries_the_decision_the_counts_and_a_link_to_the_pr()
    {
        var text = LarkNotifier.RenderText(Pr(), Result(findings: 5));

        Assert.Contains("驳回", text, StringComparison.Ordinal);
        Assert.Contains("5 条问题 · 2/2 票", text, StringComparison.Ordinal);
        Assert.Contains("· 还有 2 条，见 PR", text, StringComparison.Ordinal);
        Assert.Contains(
            "https://azdevops.liontravel.com/LionTechShanghai/liontrip-order/_git/liontrip-order/pullrequest/2954",
            text, StringComparison.Ordinal);
    }

    /// <summary>老 Summons 块里没有 RemoteUrl，那种通知不带链接但仍要发得出去。</summary>
    [Fact]
    public void A_snapshot_without_a_remote_url_still_renders()
    {
        var text = LarkNotifier.RenderText(Pr() with { RemoteUrl = string.Empty }, Result());

        Assert.DoesNotContain("pullrequest", text, StringComparison.Ordinal);
        Assert.Contains("PR #2954", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"LIONMAIL\tobeyhuang", "tobeyhuang@liontravel.com")]
    [InlineData("tobeyhuang", "tobeyhuang@liontravel.com")]
    [InlineData("tobeyhuang@liontravel.com", "tobeyhuang@liontravel.com")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Az_identities_of_every_shape_land_on_the_same_mailbox(string? author, string? expected)
        => Assert.Equal(expected, Options().Lark.RecipientFor(author));

    [Fact]
    public void The_user_map_wins_over_the_email_domain()
    {
        var lark = Options(mapAccount: "tobeyhuang", mapTo: "tobey.huang@example.com").Lark;

        Assert.Equal("tobey.huang@example.com", lark.RecipientFor(@"LIONMAIL\tobeyhuang"));
    }

    /// <summary>
    /// UserMap 的键写成 az 里的任何形态都要命中。
    /// </summary>
    /// <remarks>
    /// 「配了但没生效」跟「没配」的外部表现完全一样，而人从面板上复制出来的一定是带域前缀那种。
    /// </remarks>
    [Theory]
    [InlineData(@"LIONMAIL\tobeyhuang")]
    [InlineData("LIONMAIL/tobeyhuang")]
    [InlineData("tobeyhuang@liontravel.com")]
    [InlineData("TobeyHuang")]
    public void A_user_map_key_may_be_written_in_any_az_shape(string key)
    {
        var lark = Options(mapAccount: key, mapTo: OpenId).Lark;

        Assert.Equal(OpenId, lark.RecipientFor(@"LIONMAIL\tobeyhuang"));
    }
}

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

        /// <summary>每次请求打到了哪个主机 —— BaseUrl 是热更新得了的，要看得见它换没换。</summary>
        internal List<string> Hosts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var path = request.RequestUri!.PathAndQuery;
            Hosts.Add(request.RequestUri!.Host);
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
        Assert.Equal("interactive", sent.RootElement.GetProperty("msg_type").GetString());

        // content 是一个 JSON 字符串而不是嵌套对象，发错会被飞书直接拒。
        var content = sent.RootElement.GetProperty("content").GetString();
        Assert.NotNull(content);
        using var inner = JsonDocument.Parse(content);
        Assert.Contains(
            "PR #2954",
            inner.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()!,
            StringComparison.Ordinal);
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

    /// <summary>
    /// 缓存窗口不能超过 token 自己的剩余寿命。
    /// </summary>
    /// <remarks>
    /// <c>expire</c> 是剩余寿命而不是固定 7200（实测见过 2106），所以小读数是可达的。
    /// 用 0 而不是 5 这类小正数来钉：没有可注入的时钟，只有「压根不该进缓存」这个边界
    /// 能不靠 sleep 就观测到 —— 改坏了的话后面几次都复用那枚 token，这里就只剩一次换取。
    /// 期望 3 次：查 open_id 一次、两次发送，三个请求都要授权，各自都得现换。
    /// </remarks>
    [Fact]
    public async Task A_token_with_no_life_left_is_not_served_from_the_cache()
    {
        using var stub = new Stub(path => path.Contains("tenant_access_token", StringComparison.Ordinal)
            ? """{"code":0,"msg":"ok","tenant_access_token":"t-abc","expire":0}"""
            : HappyPath(path));

        using var notifier = new LarkNotifier(Options(), NullLogger<LarkNotifier>.Instance, stub);

        await notifier.NotifyPromulgationAsync(Pr(), Result(), CancellationToken.None);
        await notifier.NotifyPromulgationAsync(Pr(), Result(), CancellationToken.None);

        Assert.Equal(
            3,
            stub.Calls.Count(c => c.Path.Contains("tenant_access_token", StringComparison.Ordinal)));
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
    /// 99992402 跟「这个人不存在」共用一个码，单看没有信息量。实测邮箱直投本身不需要任何
    /// 通讯录权限（DESIGN §9.5），所以真正的成因几乎总是「收件人不在应用的可用范围内」——
    /// 而那是个后台配置问题，报错里不说，下一个人会往权限上反推一遍。
    /// </remarks>
    [Fact]
    public async Task A_failed_email_send_points_at_the_availability_scope()
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

        Assert.Contains("可用范围", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("contact:user.id:readonly", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 执行失败的正文要直说「不是你的问题」。
    /// </summary>
    /// <remarks>
    /// 作者拿到它时想知道的是「我要改什么」，而答案是「什么都不用改」—— 不直说的话，
    /// 他会去读 0 票、降级那些字眼然后自己脑补一个结论。也不能列 finding：失败的票本来
    /// 就没有 finding，「0 条问题」在这个语境下读起来像「评过了，没问题」。
    /// </remarks>
    /// <summary>
    /// 执行失败的卡片要直说「不是你的问题」。
    /// </summary>
    /// <remarks>
    /// 作者拿到它时想知道的是「我要改什么」，而答案是「什么都不用改」—— 不直说的话，
    /// 他会去读 0 票、降级那些字眼然后自己脑补一个结论。也不能列 finding：失败的票本来
    /// 就没有 finding，「0 条问题」在这个语境下读起来像「评过了，没问题」。
    /// 标题栏也不能染红 —— 红色在这张卡上等于「驳回」。
    /// </remarks>
    [Fact]
    public void A_failed_verdict_says_it_is_not_the_authors_fault()
    {
        using var card = JsonDocument.Parse(LarkCard.Render(
            Pr(),
            new PromulgationPayload(
                "liontrip-order/2954@bdcc84b", ReviewDecision.Error, [],
                Degraded: true, ActualQuorum: 0, ExpectedQuorum: 1)));

        Assert.Equal("grey", card.RootElement.GetProperty("header").GetProperty("template").GetString());

        var text = Flatten(card.RootElement);
        Assert.Contains("不是你代码的问题", text, StringComparison.Ordinal);
        Assert.DoesNotContain("🔴", text, StringComparison.Ordinal);
        Assert.DoesNotContain("票", text, StringComparison.Ordinal);
        Assert.DoesNotContain("降级", text, StringComparison.Ordinal);

        // Error 不在 PR 上投票，写个 none 只会让人去找那一票在哪。
        Assert.DoesNotContain("投票", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_card_carries_the_decision_the_vote_the_counts_and_a_button()
    {
        using var card = JsonDocument.Parse(LarkCard.Render(Pr(), Result(findings: 5)));
        var root = card.RootElement;

        Assert.Equal("red", root.GetProperty("header").GetProperty("template").GetString());
        Assert.Equal(
            "📝 代码评审 · PR #2954 feat(voucher): embed 小程序码",
            root.GetProperty("header").GetProperty("title").GetProperty("content").GetString());

        var text = Flatten(root);
        Assert.Contains("**结论**：驳回", text, StringComparison.Ordinal);

        // 投票串必须跟 PR 上真正留下的那一票是同一个词，见 AzVote。
        Assert.Contains("投票 `reject (-10)`", text, StringComparison.Ordinal);
        Assert.Contains("🔴 0 · 🟡 5 · 🔵 0", text, StringComparison.Ordinal);
        Assert.Contains("2/2 票", text, StringComparison.Ordinal);
        Assert.Contains("🟡 `src/A0.cs:12` — 问题 0", text, StringComparison.Ordinal);

        Assert.Equal(
            "https://azdevops.liontravel.com/LionTechShanghai/liontrip-order/_git/liontrip-order/pullrequest/2954",
            Button(root).GetProperty("url").GetString());
    }

    /// <summary>三档圆点跟评审 skill 的 severity 契约对齐；info 并进蓝的那一档。</summary>
    [Fact]
    public void Severities_collapse_onto_three_dots()
    {
        var findings = new[] { Severity.Critical, Severity.Major, Severity.Minor, Severity.Info }
            .Select((sev, i) => new MergedFinding(new Finding($"src/S{i}.cs", i, sev, $"t{i}", "d"), 1, 1.0))
            .ToArray();

        var text = Flatten(JsonDocument.Parse(LarkCard.Render(
            Pr(),
            new PromulgationPayload("r", ReviewDecision.Reject, findings, false, 1, 1))).RootElement);

        Assert.Contains("🔴 1 · 🟡 1 · 🔵 2", text, StringComparison.Ordinal);
    }

    /// <summary>超过 8 条就折起来 —— 到那个量级该看的是 PR 本身。</summary>
    [Fact]
    public void Findings_beyond_the_cap_are_folded_into_one_line()
    {
        var text = Flatten(JsonDocument.Parse(LarkCard.Render(Pr(), Result(findings: 11))).RootElement);

        Assert.Contains("`src/A7.cs:19`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("`src/A8.cs:20`", text, StringComparison.Ordinal);
        Assert.Contains("…… 还有 3 条，见 PR", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 评审人取自公布块的转述，老块上没有这个字段。
    /// </summary>
    /// <remarks>
    /// 没有就整段不出现：「评审人：—」比不写还糟，它看起来像「没人评」。
    /// 有就按 az 账号显示，同一个人写成什么形态都收敛到一份。
    /// </remarks>
    [Fact]
    public void Reviewers_are_normalised_and_dropped_when_the_block_has_none()
    {
        Assert.DoesNotContain(
            "评审人",
            Flatten(JsonDocument.Parse(LarkCard.Render(Pr(), Result())).RootElement),
            StringComparison.Ordinal);

        var named = Result() with { Reviewers = [@"LIONMAIL\tobeyhuang", "tobeyhuang"] };
        Assert.Contains(
            "评审人：tobeyhuang  |  ",
            Flatten(JsonDocument.Parse(LarkCard.Render(Pr(), named)).RootElement),
            StringComparison.Ordinal);
    }

    /// <summary>降级要写在票数后面，不能让它看起来像跑满了。</summary>
    [Fact]
    public void A_degraded_verdict_says_so_next_to_the_vote_count()
    {
        var degraded = Result() with { Degraded = true, ActualQuorum = 1, ExpectedQuorum = 2 };
        var text = Flatten(JsonDocument.Parse(LarkCard.Render(Pr(), degraded)).RootElement);

        Assert.Contains("1/2 票（降级：合格节点不足）", text, StringComparison.Ordinal);
    }

    /// <summary>分支名在老 Summons 块里没有；缺了就只说 repo，不占一行空话。</summary>
    [Fact]
    public void Branches_show_up_when_the_snapshot_has_them()
    {
        var bare = Flatten(JsonDocument.Parse(LarkCard.Render(Pr(), Result())).RootElement);
        Assert.Contains("repo `liontrip-order`", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("→", bare, StringComparison.Ordinal);

        var branched = Pr() with { SourceBranch = "fix/refund", TargetBranch = "develop" };
        Assert.Contains(
            "fix/refund → develop · repo `liontrip-order`",
            Flatten(JsonDocument.Parse(LarkCard.Render(branched, Result())).RootElement),
            StringComparison.Ordinal);
    }

    /// <summary>老 Summons 块里没有 RemoteUrl，那种卡片不带按钮但仍要渲染得出来。</summary>
    [Fact]
    public void A_snapshot_without_a_remote_url_renders_without_a_button()
    {
        using var card = JsonDocument.Parse(
            LarkCard.Render(Pr() with { RemoteUrl = string.Empty }, Result()));

        Assert.Equal(JsonValueKind.Undefined, Button(card.RootElement).ValueKind);
        Assert.Contains("PR #2954", Flatten(card.RootElement), StringComparison.Ordinal);
    }

    /// <summary>
    /// 开评卡片回答的问题只有一个：有人接手了没有。
    /// </summary>
    /// <remarks>
    /// 条数、票数、finding 在这一刻全都还不存在，摆个「0 条问题」上去只会被读成
    /// 「评过了，没问题」—— 跟 Error 卡片要躲的是同一个坑。
    /// </remarks>
    [Fact]
    public void The_started_card_says_who_picked_it_up_and_offers_both_ways_in()
    {
        using var card = JsonDocument.Parse(LarkCard.RenderStarted(
            Pr() with { SourceBranch = "fix/refund", TargetBranch = "develop" },
            new ReviewStarted("liontrip-order/2954@bdcc84b", @"LIONMAIL\tobeyhuang", "http://10.0.0.7:47708/"),
            47708));

        var root = card.RootElement;
        Assert.Equal("blue", root.GetProperty("header").GetProperty("template").GetString());
        Assert.Equal(
            "🔍 开始评审 · PR #2954 feat(voucher): embed 小程序码",
            root.GetProperty("header").GetProperty("title").GetProperty("content").GetString());

        var text = Flatten(root);
        Assert.Contains("**已开始评审** · 评审人：tobeyhuang", text, StringComparison.Ordinal);
        Assert.Contains("fix/refund → develop · repo `liontrip-order`", text, StringComparison.Ordinal);

        // 这一刻还没有任何结论，别摆出结论卡片那一套。
        Assert.DoesNotContain("🔴", text, StringComparison.Ordinal);
        Assert.DoesNotContain("票", text, StringComparison.Ordinal);

        var buttons = Buttons(root);
        Assert.Equal(["在 Conclave 里打开", "在 web 里查看实时日志", "打开 PR"], Labels(buttons));

        // 深链排第一并且是主按钮：面板比浏览器里那页纯日志有用得多。
        Assert.Equal("primary", buttons[0].GetProperty("type").GetString());

        // / 和 @ 在 query 里合法，必须原样留着 —— 飞书会把 %2F / %40 吃掉（实测）。
        Assert.Equal(
            "http://127.0.0.1:47708/open?revision=liontrip-order/2954@bdcc84b&view=review",
            buttons[0].GetProperty("url").GetString());
        Assert.Equal(
            "http://10.0.0.7:47708/log/live?revision=liontrip-order/2954@bdcc84b",
            buttons[1].GetProperty("url").GetString());
    }

    /// <summary>
    /// mesh 没开就没有实时日志那个按钮。
    /// </summary>
    /// <remarks>
    /// 没有 mesh 就没有别的节点，评审者必然是本机 —— 作者自己打开面板就看得到，
    /// 给个点不开的按钮比不给还糟。深链不依赖端点，所以仍然在。
    /// </remarks>
    [Fact]
    public void Without_a_mesh_endpoint_there_is_no_live_log_button()
    {
        using var card = JsonDocument.Parse(LarkCard.RenderStarted(
            Pr(), new ReviewStarted("liontrip-order/2954@bdcc84b", "alan", string.Empty), 47708));

        Assert.Equal(["在 Conclave 里打开", "打开 PR"], Labels(Buttons(card.RootElement)));
    }

    /// <summary>
    /// 有 web 按钮才说网络前提。
    /// </summary>
    /// <remarks>
    /// 那个端点是评审节点的内网地址，人在外面点开只会是一个没有解释的超时。
    /// 但没有按钮的时候还念一句「要在公司网络内」，就是在解释一个不存在的东西。
    /// </remarks>
    [Fact]
    public void The_network_caveat_shows_up_only_with_the_web_button()
    {
        var withButton = Flatten(JsonDocument.Parse(LarkCard.RenderStarted(
            Pr(), new ReviewStarted("r", "alan", "http://10.0.0.7:47708"), 47708)).RootElement);
        Assert.Contains("要在公司网络内", withButton, StringComparison.Ordinal);

        var without = Flatten(JsonDocument.Parse(LarkCard.RenderStarted(
            Pr(), new ReviewStarted("r", "alan", string.Empty), 47708)).RootElement);
        Assert.DoesNotContain("公司网络", without, StringComparison.Ordinal);
    }

    /// <summary>老区块没有 RemoteUrl 时连「打开 PR」也拼不出来，但「在 Conclave 里打开」永远在。</summary>
    [Fact]
    public void The_local_open_button_is_the_one_that_never_needs_anything()
    {
        using var card = JsonDocument.Parse(LarkCard.RenderStarted(
            Pr() with { RemoteUrl = string.Empty },
            new ReviewStarted("liontrip-order/2954@bdcc84b", "alan", string.Empty), 47708));

        Assert.Equal(["在 Conclave 里打开"], Labels(Buttons(card.RootElement)));
    }

    /// <summary>指派的三种卡片各自的标题栏与措辞。</summary>
    [Theory]
    [InlineData(null, "turquoise", "📥 指派给你 · PR #2954", "把这个 PR 指派给你评")]
    [InlineData(true, "green", "✅ 指派已接受 · PR #2954", "接受了你的指派")]
    [InlineData(false, "orange", "↩️ 指派被拒绝 · PR #2954", "拒绝了你的指派")]
    public void An_assignment_card_reads_differently_at_each_step(
        bool? accepted, string tone, string title, string phrase)
    {
        using var card = JsonDocument.Parse(LarkCard.RenderAssignment(
            Pr(),
            new AssignmentNotice("liontrip-order/2954@bdcc84b", "alan", @"LIONMAIL\tobeyhuang", accepted, null), 47708));

        var root = card.RootElement;
        Assert.Equal(tone, root.GetProperty("header").GetProperty("template").GetString());
        Assert.StartsWith(
            title,
            root.GetProperty("header").GetProperty("title").GetProperty("content").GetString(),
            StringComparison.Ordinal);
        Assert.Contains($"**tobeyhuang** {phrase}", Flatten(root), StringComparison.Ordinal);
    }

    /// <summary>
    /// 拒绝用橙不用红。
    /// </summary>
    /// <remarks>
    /// 红在这套卡片里已经有确定含义了 ——「你的代码被驳回」。一个节点不接活跟代码
    /// 好不好毫无关系，染成红的会让收件人白紧张一下。
    /// </remarks>
    [Fact]
    public void A_declined_assignment_is_never_painted_like_a_rejected_pr()
    {
        using var declined = JsonDocument.Parse(LarkCard.RenderAssignment(
            Pr(), new AssignmentNotice("r", "alan", "tobeyhuang", false, null), 47708));
        using var rejected = JsonDocument.Parse(LarkCard.Render(Pr(), Result()));

        Assert.Equal("red", rejected.RootElement.GetProperty("header").GetProperty("template").GetString());
        Assert.NotEqual("red", declined.RootElement.GetProperty("header").GetProperty("template").GetString());
    }

    /// <summary>
    /// 按钮指向 PR 而不是日志。
    /// </summary>
    /// <remarks>
    /// 指派那一刻评审还没开跑，没有日志可看，而收件人要做的正是去面板上处理它。
    /// </remarks>
    [Fact]
    public void The_assignment_card_links_to_the_pr_not_the_log()
    {
        using var card = JsonDocument.Parse(LarkCard.RenderAssignment(
            Pr(), new AssignmentNotice("liontrip-order/2954@bdcc84b", "alan", "tobeyhuang", null, null), 47708));

        var buttons = Buttons(card.RootElement);
        Assert.Equal(["在 Conclave 里打开", "打开 PR"], Labels(buttons));
        Assert.Equal("primary", buttons[0].GetProperty("type").GetString());
        Assert.Equal(
            "http://127.0.0.1:47708/open?revision=liontrip-order/2954@bdcc84b&view=pr",
            buttons[0].GetProperty("url").GetString());
    }

    /// <summary>留言和拒绝理由是同一个位置上的两种东西，都是对面手打的一句话。</summary>
    [Theory]
    [InlineData(null, "留言：这个我自己写的，麻烦你看一下")]
    [InlineData(false, "理由：这台机器额度快满了")]
    public void A_note_is_labelled_by_which_step_it_came_from(bool? accepted, string expected)
    {
        var note = accepted == false ? "这台机器额度快满了" : "这个我自己写的，麻烦你看一下";

        Assert.Contains(
            expected,
            Flatten(JsonDocument.Parse(LarkCard.RenderAssignment(
                Pr(), new AssignmentNotice("r", "alan", "tobeyhuang", accepted, note), 47708)).RootElement),
            StringComparison.Ordinal);
    }

    /// <summary>没有留言就不摆那一行 ——「理由：」后面跟着空白比不写更像出了错。</summary>
    [Fact]
    public void An_empty_note_leaves_no_dangling_label()
    {
        var text = Flatten(JsonDocument.Parse(LarkCard.RenderAssignment(
            Pr(), new AssignmentNotice("r", "alan", "tobeyhuang", false, "   "), 47708)).RootElement);

        Assert.DoesNotContain("理由：", text, StringComparison.Ordinal);
    }

    private static string[] Labels(JsonElement[] buttons)
        => [.. buttons.Select(b => b.GetProperty("text").GetProperty("content").GetString()!)];

    /// <summary>卡片上那一排按钮，按顺序。</summary>
    private static JsonElement[] Buttons(JsonElement card)
    {
        foreach (var element in card.GetProperty("elements").EnumerateArray())
        {
            if (element.TryGetProperty("actions", out var actions))
            {
                return [.. actions.EnumerateArray()];
            }
        }

        return [];
    }

    /// <summary>「打开 PR」那个按钮；没有则返回 <c>default</c>。</summary>
    private static JsonElement Button(JsonElement card)
    {
        foreach (var element in card.GetProperty("elements").EnumerateArray())
        {
            if (element.TryGetProperty("actions", out var actions))
            {
                return actions[0];
            }
        }

        return default;
    }

    /// <summary>把卡片里所有 content 串起来 —— 断言正文时不必关心它落在哪个 element 上。</summary>
    private static string Flatten(JsonElement card)
    {
        var parts = new List<string>();
        Walk(card);
        return string.Join('\n', parts);

        void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Name == "content" && property.Value.ValueKind == JsonValueKind.String)
                        {
                            parts.Add(property.Value.GetString()!);
                        }
                        else
                        {
                            Walk(property.Value);
                        }
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Walk(item);
                    }

                    break;
            }
        }
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

    /// <summary>
    /// 配置热更新换了应用之后，旧 token 和旧 open_id 都不能再用。
    /// </summary>
    /// <remarks>
    /// 两样都是应用维度的：旧应用的 token 在新应用上一律无效，而同一个人在新应用上是
    /// 另一个 <c>ou_</c>。拿旧的去发撞上的是 99992402 —— 跟「查无此人」共用的那个码，
    /// 看日志分不出是换应用造成的。
    /// </remarks>
    [Fact]
    public async Task Swapping_the_app_at_runtime_drops_the_token_and_the_open_id_cache()
    {
        using var stub = new Stub(HappyPath);
        var opts = Options();
        using var notifier = new LarkNotifier(opts, NullLogger<LarkNotifier>.Instance, stub);

        await notifier.NotifyPromulgationAsync(Pr(), Result(), CancellationToken.None);

        // 热更新把整个 Lark 块换掉，跟 ConfigHotReload.Apply 做的事一样。
        opts.Lark = new LarkOptions
        {
            Enabled = true,
            AppId = "cli_new",
            AppSecret = "secret-new",
            BaseUrl = opts.Lark.BaseUrl,
            EmailDomain = opts.Lark.EmailDomain,
        };

        await notifier.NotifyPromulgationAsync(Pr(), Result(), CancellationToken.None);

        var tokens = stub.Calls
            .Where(c => c.Path.Contains("tenant_access_token", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, tokens.Count);
        Assert.Contains("cli_new", tokens[1].Body, StringComparison.Ordinal);

        // 第二次必须重新查一遍 open_id：缓存里那个是旧应用的。
        Assert.Equal(2, stub.Calls.Count(c => c.Path.Contains("batch_get_id", StringComparison.Ordinal)));
    }

    /// <summary>改了 BaseUrl 也得立刻生效 —— 它曾经焊在 HttpClient.BaseAddress 上。</summary>
    [Fact]
    public async Task A_changed_base_url_takes_effect_without_a_restart()
    {
        using var stub = new Stub(HappyPath);
        var opts = Options();
        using var notifier = new LarkNotifier(opts, NullLogger<LarkNotifier>.Instance, stub);

        await notifier.NotifyPromulgationAsync(Pr(), Result(), CancellationToken.None);

        opts.Lark.BaseUrl = "https://open.feishu.invalid";
        await notifier.NotifyPromulgationAsync(Pr(), Result(), CancellationToken.None);

        Assert.Contains(stub.Hosts, h => h == "open.larksuite.invalid");
        Assert.Contains(stub.Hosts, h => h == "open.feishu.invalid");
    }
}

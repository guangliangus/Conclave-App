using System.Globalization;
using System.Text.Json;
using Conclave.Application;
using Conclave.Domain;

namespace Conclave.Infrastructure;

/// <summary>
/// 把公布的结论渲染成一张飞书消息卡片。
/// </summary>
/// <remarks>
/// <para>
/// 纯函数，返回的是 <c>msg_type=interactive</c> 要的那段 content JSON ——
/// 测试直接断言它的形状，不必起 HTTP。
/// </para>
/// <para>
/// 为什么是卡片不是纯文本：文本版把结论、票数、每条 finding 压成没有层次的几行，
/// 在手机上一屏刷过去只看得见「有东西来了」。卡片有一条按结论变色的标题栏 ——
/// 红的不用读就知道要改，绿的不用读就知道没事，而这正是收到通知的人的第一个问题。
/// </para>
/// <para>
/// 刻意<b>不</b>自己造结论的中文名：那套词在 <see cref="DecisionLabels"/>，面板、
/// <c>conclave report</c>、这里三个出口共用一份。同一个结论在面板上叫「驳回」、
/// 在飞书里叫「不通过」的话，收到通知的人会以为是两回事。
/// </para>
/// </remarks>
internal static class LarkCard
{
    /// <summary>
    /// 卡片里最多列几条 finding。
    /// </summary>
    /// <remarks>
    /// 比文本版的 3 条宽松：卡片有分隔线和圆点，十行也不会糊成一片，
    /// 而驳回类 PR 恰恰是条数多的那种 —— 只给 3 条等于逼着人再去开一次 PR。
    /// 再多就没有意义了，到那个量级该看的是 PR 本身。
    /// </remarks>
    private const int MaxListedFindings = 8;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// 卡片 JSON。
    /// </summary>
    /// <param name="pr">PR 快照。</param>
    /// <param name="result">公布的结论。</param>
    /// <param name="orgUrl">
    /// Azure DevOps 组织地址，只用来兜底拼 PR 链接（老区块里没有 <c>RemoteUrl</c>）。
    /// 两者都没有时卡片上就没有「打开 PR」这个按钮。
    /// </param>
    internal static string Render(PrMeta pr, PromulgationPayload result, string? orgUrl = null)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(result);

        var elements = new List<object> { Div(Headline(pr, result)) };

        // 执行失败的卡片跟出了结论的完全不一样。
        //
        // 作者拿到它时最想知道的是「我要改什么」，而答案是「你什么都不用改」—— 这句话
        // 必须直接说出来，否则他会去读那些 0 票、降级之类的字眼，然后自己脑补出一个结论。
        // 也不列 finding：失败的那一票本来就没有 finding，列出来只会是一行「0 条问题」，
        // 而那在这个语境下读起来像「评过了，没问题」。
        if (result.Decision == ReviewDecision.Error)
        {
            elements.Add(Div(
                "评审没能跑出结论，是我们这边的问题，不是你代码的问题 —— 不用改什么。\n"
                + "已经在排查，修好后会重新评这一版。"));
        }
        else
        {
            elements.Add(Div(Meta(result)));

            if (result.Findings.Count > 0)
            {
                elements.Add(new { Tag = "hr" });
                elements.Add(Div(FindingLines(result)));
            }
        }

        var url = PrLink.For(pr, orgUrl);
        if (url is not null)
        {
            elements.Add(new { Tag = "action", Actions = new List<object> { Button("打开 PR", url) } });
        }

        return Card(
            Tone(result.Decision),
            $"📝 代码评审 · PR #{pr.PrId.ToString(CultureInfo.InvariantCulture)} {pr.Title}",
            elements);
    }

    /// <summary>
    /// 「已开始评审」那张卡片。
    /// </summary>
    /// <remarks>
    /// 它要回答的问题只有一个：有人接手了没有。所以正文刻意很短 —— 结论卡片那套
    /// 条数、票数、finding 在这一刻全都还不存在，摆个「0 条问题」上去只会误导。
    /// 剩下的空间给按钮：作者此刻真正想做的是去看它评到哪一步了。
    /// </remarks>
    internal static string RenderStarted(
        PrMeta pr, ReviewStarted started, int localPort, string? orgUrl = null)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(started);

        var reviewer = AzIdentity.Normalize(started.ReviewerAz);
        var who = reviewer.Length > 0 ? $"**已开始评审** · 评审人：{reviewer}" : "**已开始评审**";

        var endpoint = started.LogEndpoint.Trim().TrimEnd('/');

        // 那个端点是评审节点的<b>内网</b>地址（MeshHttpServer.Endpoint 拼的是本机 IP），
        // 手机上、用流量、或者人不在 VPN 上，点开只会是一个没有任何解释的连接超时。
        // 修不了 —— 要从外面访问得有中转 —— 但至少别让人对着转圈的页面自己猜。
        var hint = "评审通常十几分钟。跑完会再发一条结论通知，不用守着。"
            + (endpoint.Length > 0
                ? "\n「在 web 里查看实时日志」连的是评审那台机器，要在公司网络内。"
                : string.Empty);

        var elements = new List<object>
        {
            Div(who + $"\n{BranchLine(pr)}"),
            Div(hint),
        };

        // 深链排第一并且是主按钮：装了 Conclave 的人拿到的是带完整上下文的面板
        // （队列、这个 PR 的历史、别的节点在干什么），比浏览器里那页纯日志有用得多。
        // 它不依赖任何端点，所以无条件给 —— 没装的人点了不会有反应，而那种人正是
        // 第二个按钮服务的对象。
        var buttons = new List<object>
        {
            Button(
                "在 Conclave 里打开",
                LocalOpenLink.For(localPort, DeepLinkTarget.Review, started.RevisionId),
                primary: true),
        };

        // 没装 Conclave 的人走这条。日志在评审那台机器上，不在作者这台 ——
        // 没有 mesh 就没有这个端点，那种情况下评审者必然是本机，作者自己打开面板就看得到，
        // 不必给个点不开的按钮。
        if (endpoint.Length > 0)
        {
            // 不用 Uri.EscapeDataString：它会把 / 和 @ 转成 %2F / %40，而飞书会把百分号编码
            // 吃掉 —— 实测点过去之后 revision= 后面整段消失，服务端收到空值回 400。
            buttons.Add(Button(
                "在 web 里查看实时日志",
                $"{endpoint}/log/live?revision={LocalOpenLink.QueryValue(started.RevisionId)}"));
        }

        var url = PrLink.For(pr, orgUrl);
        if (url is not null)
        {
            buttons.Add(Button("打开 PR", url));
        }

        elements.Add(new { Tag = "action", Actions = buttons });

        return Card("blue", $"🔍 开始评审 · PR #{pr.PrId.ToString(CultureInfo.InvariantCulture)} {pr.Title}", elements);
    }

    /// <summary>
    /// 指派卡片：指派本身、接受、拒绝三种。
    /// </summary>
    /// <remarks>
    /// 拒绝用橙不用红。红在这套卡片里已经有确定含义了 ——「你的代码被驳回」，
    /// 而一个节点不接活跟代码好不好毫无关系，染成红的会让收件人白紧张一下。
    /// </remarks>
    internal static string RenderAssignment(
        PrMeta pr, AssignmentNotice notice, int localPort, string? orgUrl = null)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(notice);

        var other = AzIdentity.Normalize(notice.Counterpart);
        var who = other.Length > 0 ? $"**{other}**" : "某个节点";

        var (tone, icon, headline, hint) = notice.Accepted switch
        {
            null => ("turquoise", "📥", $"{who} 把这个 PR 指派给你评",
                     "到 Conclave 里接受或拒绝 —— 在那之前它不会开跑。"),
            true => ("green", "✅", $"{who} 接受了你的指派，马上开跑",
                     "开评和出结论时还会各来一条通知，不用守着。"),
            _ => ("orange", "↩️", $"{who} 拒绝了你的指派",
                  "换一台机器再指派一次，或者交回自动分配。"),
        };

        var title = notice.Accepted switch
        {
            null => "指派给你",
            true => "指派已接受",
            _ => "指派被拒绝",
        };

        var elements = new List<object> { Div(headline + "\n" + BranchLine(pr)) };

        // 指派的留言和拒绝的理由是同一个位置上的两种东西，都是对面手打的一句话。
        // 没有就不摆空行 —— 「理由：」后面跟着空白比不写更像出了错。
        if (!string.IsNullOrWhiteSpace(notice.Note))
        {
            elements.Add(Div($"{(notice.Accepted == false ? "理由" : "留言")}：{notice.Note.Trim()}"));
        }

        elements.Add(Div(hint));

        // 指向 PR 本身而不是日志：这一刻评审还没开跑，没有日志可看，
        // 而收件人要做的正是去面板上处理它。
        var buttons = new List<object>
        {
            Button(
                "在 Conclave 里打开",
                LocalOpenLink.For(localPort, DeepLinkTarget.Pr, notice.RevisionId),
                primary: true),
        };

        var url = PrLink.For(pr, orgUrl);
        if (url is not null)
        {
            buttons.Add(Button("打开 PR", url));
        }

        elements.Add(new { Tag = "action", Actions = buttons });

        return Card(
            tone,
            $"{icon} {title} · PR #{pr.PrId.ToString(CultureInfo.InvariantCulture)} {pr.Title}",
            elements);
    }

    /// <summary>
    /// 标题栏颜色。
    /// </summary>
    /// <remarks>
    /// 这是卡片相对文本版唯一的实质增量 —— 不读正文就知道要不要动手。所以只有三档语义：
    /// 绿=不用改、橙=等你、红=要改；执行失败用灰，因为它压根不是一个评审意见，
    /// 染成红的会被当成「驳回」。
    /// </remarks>
    private static string Tone(ReviewDecision decision) => decision switch
    {
        ReviewDecision.Approve or ReviewDecision.ApproveWithSuggestions => "green",
        ReviewDecision.WaitForAuthor => "orange",
        ReviewDecision.Reject => "red",
        _ => "grey",
    };

    /// <summary>
    /// <see cref="Severity"/> 的圆点。
    /// </summary>
    /// <remarks>
    /// 这套对应关系是评审 skill 的契约里定的（<c>az-pr-review/SKILL.md</c> 的 severity 表：
    /// critical 🔴 blocking、major 🟡 should-fix、minor 🔵），卡片跟着它走而不是另起一套。
    /// <c>info</c> 在那张表里没有自己的颜色，跟 <c>minor</c> 合并成蓝 —— 三档颜色对人来说
    /// 正好是「必须改 / 该改 / 知道就行」，再分一档没人分得清。
    /// </remarks>
    private static string Dot(Severity severity) => severity switch
    {
        Severity.Critical => "🔴",
        Severity.Major => "🟡",
        _ => "🔵",
    };

    /// <summary>结论那一行，外加「哪个分支进哪个分支」。</summary>
    private static string Headline(PrMeta pr, PromulgationPayload result)
    {
        var head = $"**结论**：{DecisionLabels.Decision(result.Decision)}";

        // 投票串是 PR 上真正留下的那一串，跟投票走同一份映射（见 AzVote）。
        // Error 不投票，这一段整个不出现 —— 写个 none 只会让人去找那一票在哪。
        if (AzVote.IsCast(result.Decision))
        {
            var points = ((int)result.Decision).ToString("+#;-#;0", CultureInfo.InvariantCulture);
            head += $" · 投票 `{AzVote.For(result.Decision)} ({points})`";
        }

        return head + "\n" + BranchLine(pr);
    }

    /// <summary>按严重度分档的条数、评审人、票数。</summary>
    private static string Meta(PromulgationPayload result)
    {
        var red = result.Findings.Count(f => f.Best.Severity == Severity.Critical);
        var yellow = result.Findings.Count(f => f.Best.Severity == Severity.Major);
        var blue = result.Findings.Count - red - yellow;

        var segments = new List<string>(3)
        {
            $"🔴 {red.ToString(CultureInfo.InvariantCulture)}"
            + $" · 🟡 {yellow.ToString(CultureInfo.InvariantCulture)}"
            + $" · 🔵 {blue.ToString(CultureInfo.InvariantCulture)}",
        };

        // Reviewers 是公布者签的转述，老公布块上没有这个字段。没有就整段不出现 ——
        // 「评审人：—」比不写还糟，它看起来像「没人评」。
        var reviewers = (result.Reviewers ?? [])
            .Select(AzIdentity.Normalize)
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (reviewers.Length > 0)
        {
            segments.Add($"评审人：{string.Join('、', reviewers)}");
        }

        segments.Add(
            $"{result.ActualQuorum.ToString(CultureInfo.InvariantCulture)}/"
            + $"{result.ExpectedQuorum.ToString(CultureInfo.InvariantCulture)} 票"
            + (result.Degraded ? "（降级：合格节点不足）" : string.Empty));

        return string.Join("  |  ", segments);
    }

    /// <summary>每条 finding 一行，超出上限的折成一句。</summary>
    private static string FindingLines(PromulgationPayload result)
    {
        var lines = result.Findings
            .Take(MaxListedFindings)
            .Select(f =>
                $"{Dot(f.Best.Severity)} `{f.Best.File}:{f.Best.Line.ToString(CultureInfo.InvariantCulture)}`"
                + $" — {f.Best.Title}")
            .ToList();

        if (result.Findings.Count > MaxListedFindings)
        {
            var rest = result.Findings.Count - MaxListedFindings;
            lines.Add($"…… 还有 {rest.ToString(CultureInfo.InvariantCulture)} 条，见 PR");
        }

        return string.Join('\n', lines);
    }

    private static object Div(string markdown)
        => new { Tag = "div", Text = new { Tag = "lark_md", Content = markdown } };

    private static object Button(string label, string url, bool primary = false) => new
    {
        Tag = "button",
        Text = new { Tag = "plain_text", Content = label },
        Type = primary ? "primary" : "default",
        Url = url,
    };

    /// <summary>「哪个分支进哪个分支」。老区块里没有分支名，缺了就只说 repo，不占一行空话。</summary>
    private static string BranchLine(PrMeta pr)
    {
        var branches = pr.SourceBranch.Length > 0 && pr.TargetBranch.Length > 0
            ? $"{pr.SourceBranch} → {pr.TargetBranch} · "
            : string.Empty;

        return $"{branches}repo `{pr.Repo}`";
    }

    private static string Card(string tone, string title, List<object> elements)
    {
        var card = new
        {
            Config = new { WideScreenMode = true },
            Header = new
            {
                Template = tone,
                Title = new { Tag = "plain_text", Content = title },
            },
            Elements = elements,
        };

        // 必须带上运行时类型：card 是匿名类型，按 object 序列化会得到一个空的 {}。
        return JsonSerializer.Serialize(card, card.GetType(), Json);
    }
}

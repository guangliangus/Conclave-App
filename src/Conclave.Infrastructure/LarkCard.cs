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
            elements.Add(new
            {
                Tag = "action",
                Actions = new object[]
                {
                    new
                    {
                        Tag = "button",
                        Text = new { Tag = "plain_text", Content = "打开 PR" },
                        Type = "default",
                        Url = url,
                    },
                },
            });
        }

        var card = new
        {
            Config = new { WideScreenMode = true },
            Header = new
            {
                Template = Tone(result.Decision),
                Title = new
                {
                    Tag = "plain_text",
                    Content = $"📝 代码评审 · PR #{pr.PrId.ToString(CultureInfo.InvariantCulture)} {pr.Title}",
                },
            },
            Elements = elements,
        };

        // 必须带上运行时类型：card 是匿名类型，按 object 序列化会得到一个空的 {}。
        return JsonSerializer.Serialize(card, card.GetType(), Json);
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

        // 老区块里没有分支名，那时的快照只有 project/repo。缺了就只说 repo，不占一行空话。
        var branches = pr.SourceBranch.Length > 0 && pr.TargetBranch.Length > 0
            ? $"{pr.SourceBranch} → {pr.TargetBranch} · "
            : string.Empty;

        return head + $"\n{branches}repo `{pr.Repo}`";
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
}

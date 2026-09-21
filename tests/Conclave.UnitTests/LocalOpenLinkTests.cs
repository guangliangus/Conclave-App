using Conclave.Application;

namespace Conclave.UnitTests;

/// <summary>
/// 飞书卡片上「在 Conclave 里打开」指向的那个本机地址。
/// </summary>
/// <remarks>
/// 这套东西是被实测逼出来的：<c>conclave://</c> 在飞书里<b>三种写法全部静默失效</b>
/// （纯文本裸链接、卡片按钮的 <c>url</c>、<c>multi_url</c> 含 <c>pc_url</c>），
/// 而同一台机器上从终端 <c>open conclave://…</c> 能把面板唤起来 —— 所以问题不在应用侧。
/// 改走 <c>http://127.0.0.1</c> 之后飞书肯开了，但又踩到第二个坑：百分号编码会被吃掉。
/// </remarks>
public class LocalOpenLinkTests
{
    private const string Rev = "liontrip-order/3262@12ddd11f";

    [Fact]
    public void The_link_points_at_the_recipients_own_machine()
    {
        Assert.Equal(
            "http://127.0.0.1:47708/open?revision=liontrip-order/3262@12ddd11f&view=review",
            LocalOpenLink.For(47708, DeepLinkTarget.Review, Rev));

        Assert.Equal(
            "http://127.0.0.1:47708/open?revision=liontrip-order/3262@12ddd11f&view=pr",
            LocalOpenLink.For(47708, DeepLinkTarget.Pr, Rev));
    }

    /// <summary>端口跟着配置走 —— 拼卡片的节点用的是自己那个。</summary>
    [Fact]
    public void A_custom_port_lands_in_the_link()
        => Assert.StartsWith(
            "http://127.0.0.1:50000/open?", LocalOpenLink.For(50000, DeepLinkTarget.Pr, Rev),
            StringComparison.Ordinal);

    /// <summary>
    /// <c>/</c> 和 <c>@</c> 必须原样留着。
    /// </summary>
    /// <remarks>
    /// 它们在 query 里本来就合法（RFC 3986），而 <see cref="Uri.EscapeDataString(string)"/>
    /// 会转成 <c>%2F</c> / <c>%40</c> —— <b>飞书会把百分号编码吃掉</b>：实测点过去之后
    /// 地址栏里 <c>revision=</c> 后面整段消失，服务端收到空值回 HTTP 400。
    /// 这条就是钉那次事故的。
    /// </remarks>
    [Fact]
    public void Slash_and_at_are_left_alone_because_lark_eats_percent_escapes()
    {
        var link = LocalOpenLink.For(47708, DeepLinkTarget.Review, Rev);

        Assert.DoesNotContain("%2F", link, StringComparison.Ordinal);
        Assert.DoesNotContain("%40", link, StringComparison.Ordinal);
        Assert.Contains("revision=" + Rev, link, StringComparison.Ordinal);
    }

    /// <summary>
    /// 但真正会截断 URL 的字符还是要转义。
    /// </summary>
    /// <remarks>
    /// revision id 是 <c>&lt;project&gt;/&lt;pr&gt;@&lt;commit&gt;</c>，而 project 名来自
    /// Azure DevOps，理论上带得了空格和 <c>&amp;</c>。放行那两个会把 URL 截断、
    /// 或者凭空多出一个查询参数 —— 所以只放行确定安全的 <c>/</c> 和 <c>@</c>。
    /// </remarks>
    [Theory]
    [InlineData("a b/1@c", "%20")]
    [InlineData("a&b/1@c", "%26")]
    [InlineData("a#b/1@c", "%23")]
    public void Characters_that_would_break_the_url_are_still_escaped(string revision, string escaped)
        => Assert.Contains(escaped, LocalOpenLink.QueryValue(revision), StringComparison.Ordinal);

    /// <summary>认不出的 view 退到「在队列里定位」——它对任何一版都成立。</summary>
    [Theory]
    [InlineData("review", DeepLinkTarget.Review)]
    [InlineData("REVIEW", DeepLinkTarget.Review)]
    [InlineData("pr", DeepLinkTarget.Pr)]
    [InlineData("", DeepLinkTarget.Pr)]
    [InlineData(null, DeepLinkTarget.Pr)]
    [InlineData("nonsense", DeepLinkTarget.Pr)]
    public void An_unknown_view_falls_back_to_locating_the_pr(string? raw, DeepLinkTarget expected)
        => Assert.Equal(expected, LocalOpenLink.ViewFrom(raw));

    /// <summary>两头共用同一组键名，免得一边改了另一边不知道。</summary>
    [Fact]
    public void The_query_keys_are_shared_with_the_server()
    {
        var link = LocalOpenLink.For(47708, DeepLinkTarget.Review, Rev);

        Assert.Contains($"?{LocalOpenLink.RevisionKey}=", link, StringComparison.Ordinal);
        Assert.Contains($"&{LocalOpenLink.ViewKey}=", link, StringComparison.Ordinal);
        Assert.Equal("/open", LocalOpenLink.Path);
    }
}

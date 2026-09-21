using System.Text.Json;
using Conclave.Application;
using Conclave.Domain;
using Conclave.Infrastructure.Mesh;

namespace Conclave.UnitTests;

/// <summary>
/// 实时日志页。
/// </summary>
/// <remarks>
/// <c>/log/live</c> 跟它背后的 <c>/log</c> 一样<b>不校验请求方身份</b>，也就是说
/// revision 参数是任何能连上这个端口的人给的。所以转义不是防御性编程，是这个端点的前提。
/// </remarks>
public class LiveLogPageTests
{
    [Fact]
    public void The_revision_reaches_both_the_title_and_the_script()
    {
        var html = LiveLogPage.Html("liontrip-order/2954@bdcc84b");

        Assert.Contains("liontrip-order/2954@bdcc84b", html, StringComparison.Ordinal);
        Assert.Contains("var revision = \"liontrip-order/2954@bdcc84b\";", html, StringComparison.Ordinal);
        Assert.DoesNotContain("__REVISION", html, StringComparison.Ordinal);
    }

    /// <summary>进 HTML 的那一份要转义，否则一个带标签的 revision 就能往页面里塞东西。</summary>
    [Fact]
    public void A_revision_carrying_markup_cannot_escape_into_the_document()
    {
        var html = LiveLogPage.Html("</title><script>alert(1)</script>");

        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    /// <summary>进 JS 的那一份同理：引号和反斜杠不转义就是直接拼出可执行代码。</summary>
    [Fact]
    public void A_revision_carrying_quotes_cannot_break_out_of_the_js_literal()
    {
        var html = LiveLogPage.Html("\";alert(1);//");

        // System.Text.Json 默认把引号转成 \u0022（不是 \"），拼出来仍然是一个合法字面量。
        Assert.DoesNotContain("var revision = \"\";", html, StringComparison.Ordinal);
        Assert.Contains("\\u0022;alert(1);//", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// 页面读的字段名，必须跟 <c>/log</c> 真正发出去的那份 JSON 对得上。
    /// </summary>
    /// <remarks>
    /// 补的是一个真出过的洞：页面原先读 <c>chunk.next / lines / running</c>，而 <c>/log</c>
    /// 走 <see cref="ActaJson"/>，那份 options 刻意没设命名策略（区块哈希覆盖 JSON 的字面文本），
    /// 发出去的是 PascalCase —— 三个全是 <c>undefined</c>，页面一行不显示，还会把第一次响应
    /// 判成「已结束」。当时的用例全在断言 HTML 文本，一条都没红。
    /// <para>
    /// 所以这里不写死名字，而是从 <see cref="LogChunk"/> 的真实序列化结果反过来查 ——
    /// 哪天命名策略变了，红的是这条，而不是线上那一页空白。
    /// </para>
    /// </remarks>
    [Fact]
    public void The_page_reads_the_field_names_that_the_endpoint_actually_sends()
    {
        var html = LiveLogPage.Html("x/1@a");

        using var sent = JsonDocument.Parse(
            ActaJson.Serialize(new LogChunk("x/1@a", 0, 3, ["a"], true)));

        foreach (var field in new[] { "Next", "Lines", "Running" })
        {
            Assert.True(
                sent.RootElement.TryGetProperty(field, out _),
                $"/log 发出去的 JSON 里没有 {field}，这条断言的前提已经变了");

            Assert.Contains($"chunk.{field}", html, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 一个空的 not-running 响应不能让页面就此停摆。
    /// </summary>
    /// <remarks>
    /// 节点重启过、这一版被环形缓冲挤掉、评审换了台机器，读回来的都是这种 chunk，
    /// 而评审其实还在跑。当场判「已结束」的话这一页就再也不会动了。
    /// </remarks>
    [Fact]
    public void One_empty_not_running_chunk_does_not_stop_the_page()
    {
        var html = LiveLogPage.Html("x/1@a");

        Assert.Contains("var SETTLE_POLLS = 3;", html, StringComparison.Ordinal);
        Assert.Contains("settling = lines.length ? 0 : settling + 1;", html, StringComparison.Ordinal);
        Assert.Contains("if (settling < SETTLE_POLLS)", html, StringComparison.Ordinal);
    }

    /// <summary>页面自己去轮询 /log —— 服务端不做推送，见 LiveLogPage 的注释。</summary>
    [Fact]
    public void The_page_polls_the_increment_endpoint()
    {
        var html = LiveLogPage.Html("x/1@a");

        Assert.Contains("'/log?revision=' + encodeURIComponent(revision) + '&from=' + from",
            html, StringComparison.Ordinal);

        // 日志行里有模型原样吐出来的代码片段，只能用 textContent 写入。
        Assert.Contains("row.textContent = line;", html, StringComparison.Ordinal);
        Assert.DoesNotContain(".innerHTML", html, StringComparison.Ordinal);
    }
}

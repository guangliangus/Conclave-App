using System.Globalization;

namespace Conclave.Application;

/// <summary>
/// 「在 Conclave 里打开」那个按钮指向的地址：<c>http://127.0.0.1:&lt;port&gt;/open?…</c>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不用 <see cref="DeepLink"/> 那套 <c>conclave://</c>：<b>飞书客户端把自定义协议
/// 静默丢掉</b>。实测过三种写法 —— 纯文本里的裸链接、卡片按钮的 <c>url</c>、
/// 卡片按钮的 <c>multi_url</c>（含 <c>pc_url</c>）—— 点下去全都毫无反应，
/// 连「无法打开」的提示都没有。而同一台机器上从终端 <c>open conclave://…</c>
/// 是能把面板唤起来的，所以问题不在应用侧。
/// </para>
/// <para>
/// <c>127.0.0.1</c> 这条路反而更稳：飞书肯开 <c>http://</c>，而地址里的回环在谁的机器上
/// 就指谁 —— 卡片<b>不需要知道收件人是谁</b>。它也不依赖 Launch Services 和
/// <c>CFBundleURLTypes</c>，装了就能用，不必为了一个按钮重新打包。
/// </para>
/// <para>
/// <see cref="DeepLink"/> 保留：协议已经注册、也验证过能用，终端和别的工具仍然走它。
/// 只是别再往飞书卡片上放。
/// </para>
/// </remarks>
public static class LocalOpenLink
{
    /// <summary>路由路径。<c>MeshHttpServer</c> 按它匹配。</summary>
    public const string Path = "/open";

    /// <summary>查询串里的键名。两头共用，免得一边改了另一边不知道。</summary>
    public const string RevisionKey = "revision";

    /// <summary>同上。取值是 <see cref="DeepLinkTarget"/> 的小写名。</summary>
    public const string ViewKey = "view";

    /// <summary>
    /// 拼一个指向<b>收件人自己那台</b> Conclave 的地址。
    /// </summary>
    /// <param name="port">
    /// 对方的 <c>Mesh.HttpPort</c>。拼卡片的节点<b>猜不到</b>对方配了什么，只能用自己那个 ——
    /// 全组同配置时没问题，改过端口的人这个按钮会失效。这是已知的将就，
    /// 换成准确值得让收件人先报一次自己的端口，而那条路在通知这个场景里并不存在。
    /// </param>
    /// <param name="target">打开日志还是在队列里定位。</param>
    /// <param name="revisionId">哪一版。</param>
    public static string For(int port, DeepLinkTarget target, string revisionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);

        return $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{Path}"
            + $"?{RevisionKey}={QueryValue(revisionId)}"
            + $"&{ViewKey}={target.ToString().ToLowerInvariant()}";
    }

    /// <summary>
    /// 查询串里的一个值，<b>刚好够用</b>的转义。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 先整体转义，再把 <c>%2F</c> 和 <c>%40</c> 还原成 <c>/</c> 和 <c>@</c>。
    /// 这两个字符在 query 里本来就合法（RFC 3986 的 query 允许 pchar 与 <c>/</c>，
    /// 而 pchar 含 <c>@</c>），<b>而飞书会把百分号编码吃掉</b> —— 实测点过去之后
    /// <c>revision=</c> 后面整段消失，服务端收到空值回 400。
    /// </para>
    /// <para>
    /// 不干脆完全不转义：revision id 是 <c>&lt;project&gt;/&lt;pr&gt;@&lt;commit&gt;</c>，
    /// 而 project 名来自 Azure DevOps，理论上可以带空格或 <c>&amp;</c> —— 那两个不转义会
    /// 直接把 URL 截断或多出一个参数。所以只放行确定安全的那两个。
    /// </para>
    /// <para>
    /// ⚠️ 残留风险：project 名真带了空格时会出现 <c>%20</c>，而飞书对它的处理没验证过。
    /// 本组现有的 project 名都是 <c>liontrip-order</c> 这种，不受影响。
    /// </para>
    /// </remarks>
    public static string QueryValue(string value)
        => Uri.EscapeDataString(value)
            .Replace("%2F", "/", StringComparison.Ordinal)
            .Replace("%40", "@", StringComparison.Ordinal);

    /// <summary>把 <c>view</c> 参数读回来；认不出就当「在队列里定位」。</summary>
    /// <remarks>
    /// 退到 <see cref="DeepLinkTarget.Pr"/> 而不是报错：它对任何一版都成立，
    /// 而日志那条路在评审还没开跑时没有东西可看。
    /// </remarks>
    public static DeepLinkTarget ViewFrom(string? raw)
        => string.Equals(raw, nameof(DeepLinkTarget.Review), StringComparison.OrdinalIgnoreCase)
            ? DeepLinkTarget.Review
            : DeepLinkTarget.Pr;
}

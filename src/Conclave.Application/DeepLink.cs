namespace Conclave.Application;

/// <summary>深链要打开的东西。</summary>
public enum DeepLinkTarget
{
    /// <summary>那一版的实时评审日志。</summary>
    Review,

    /// <summary>队列里的那个 PR 本身 —— 还没开评时没有日志可看。</summary>
    Pr,
}

/// <summary>
/// <c>conclave://</c> 深链：从外面（飞书通知）唤起本机这台 Conclave。
/// </summary>
/// <remarks>
/// <para>
/// 拼和拆放在同一个类里，因为它们分处两个程序集 —— 拼在 <c>LarkCard</c>（Infrastructure），
/// 拆在 <c>App</c>。各写一份的话，改了格式只会改一边，而对不上的表现是
/// <b>「点了按钮没反应」</b>：系统找不到能处理的应用，既不报错也不提示，
/// 跟「压根没装 Conclave」一模一样，没有任何一处会告诉你哪里错了。
/// </para>
/// <para>
/// revision id 形如 <c>liontrip-order/2954@bdcc84b</c>，带 <c>/</c> 和 <c>@</c>，
/// 所以走查询串而不是路径段：<see cref="Uri"/> 对路径里的 <c>%2F</c> 有自己的归一化规则，
/// 而查询串是原样保留的。
/// </para>
/// </remarks>
public static class DeepLink
{
    /// <summary>协议名。跟打包脚本里 <c>CFBundleURLTypes</c> 注册的那个必须一致。</summary>
    public const string Scheme = "conclave";

    private const string RevisionKey = "revision";

    /// <summary>「打开这一版的实时评审日志」。</summary>
    public static string ForReview(string revisionId) => Build(DeepLinkTarget.Review, revisionId);

    /// <summary>
    /// 「在队列里找到这个 PR」。
    /// </summary>
    /// <remarks>
    /// 指派类通知用这个而不是 <see cref="ForReview"/>：那一刻评审还没开跑，没有日志可看，
    /// 而收件人要做的是去面板上接受或拒绝。
    /// </remarks>
    public static string ForPr(string revisionId) => Build(DeepLinkTarget.Pr, revisionId);

    /// <summary>
    /// 拆一个深链；不是我们的深链就返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 来路不明的 URI 一律返回 null 而不是抛：这东西是系统转交过来的，
    /// 别的应用也能构造，为一个畸形链接崩掉面板不合理。
    /// </remarks>
    public static (DeepLinkTarget Target, string RevisionId)? Parse(Uri? uri)
    {
        if (uri is null
            || !uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var target = Host(uri.Host);
        if (target is null)
        {
            return null;
        }

        var revision = Revision(uri.Query);
        return revision is null ? null : (target.Value, revision);
    }

    private static string Build(DeepLinkTarget target, string revisionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);
        return $"{Scheme}://{Host(target)}?{RevisionKey}={Uri.EscapeDataString(revisionId)}";
    }

    private static string Host(DeepLinkTarget target) => target switch
    {
        DeepLinkTarget.Review => "review",
        DeepLinkTarget.Pr => "pr",
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    private static DeepLinkTarget? Host(string host) => host.ToLowerInvariant() switch
    {
        "review" => DeepLinkTarget.Review,
        "pr" => DeepLinkTarget.Pr,
        _ => null,
    };

    private static string? Revision(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0 || !string.Equals(pair[..split], RevisionKey, StringComparison.Ordinal))
            {
                continue;
            }

            var value = Uri.UnescapeDataString(pair[(split + 1)..]);
            return value.Length > 0 ? value : null;
        }

        return null;
    }
}

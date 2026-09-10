using System.Globalization;

namespace Conclave.Domain;

/// <summary>
/// PR 的网页地址。
/// </summary>
/// <remarks>
/// <para>
/// 两个来源，优先前者：
/// </para>
/// <list type="number">
/// <item>
/// <see cref="PrMeta.RemoteUrl"/> —— 就是 <c>…/_git/repo</c>，后面接
/// <c>/pullrequest/{id}</c> 即可。它随 PR 快照落链，所以拿它拼出来的地址跟评审时
/// 真正看的那个仓库必然一致。
/// </item>
/// <item>
/// 组织地址 + project + repo —— <c>az repos pr list</c> 实测不给 <c>remoteUrl</c>
/// （只有 <c>az repos pr show</c> 给），而改造之前落链的老区块里根本没有这个字段。
/// 兜底的拼法跟 <c>AzCliPrSource.GetCloneUrlAsync</c> 一致。
/// </item>
/// </list>
/// <para>
/// 放在 Domain 而不是各自实现一份：飞书通知、界面上的 PR 链接、以后可能的报表用的
/// 必须是同一个地址 —— 两处拼法漂移的话，一边能点开、另一边 404，而那种差异只有人
/// 真去点了才发现。
/// </para>
/// </remarks>
public static class PrLink
{
    /// <summary>PR 页面地址；两个来源都拼不出来时返回 <c>null</c>。</summary>
    public static string? For(PrMeta pr, string? orgUrl = null)
    {
        ArgumentNullException.ThrowIfNull(pr);
        return string.IsNullOrWhiteSpace(pr.RemoteUrl)
            ? For(orgUrl, pr.Project, pr.Repo, pr.PrId)
            : Compose(pr.RemoteUrl, pr.PrId);
    }

    /// <summary>
    /// 只有 project / repo / PR 号时的拼法 —— 账本里的历史记录就只有这些。
    /// </summary>
    /// <remarks>
    /// 组织地址没配就返回 <c>null</c>：拿 <c>https://dev.azure.com</c> 这类默认值去猜，
    /// 会得到一个点开是 404 的链接，比没有链接更糟。
    /// </remarks>
    public static string? For(string? orgUrl, string project, string repo, int prId)
    {
        if (string.IsNullOrWhiteSpace(orgUrl)
            || string.IsNullOrWhiteSpace(project)
            || string.IsNullOrWhiteSpace(repo))
        {
            return null;
        }

        // 实测形态：https://host/Collection/<project>/_git/<repo>
        var repoUrl = string.Concat(
            orgUrl.Trim().TrimEnd('/'),
            "/",
            Uri.EscapeDataString(project),
            "/_git/",
            Uri.EscapeDataString(repo));

        return Compose(repoUrl, prId);
    }

    private static string Compose(string repoUrl, int prId) => string.Concat(
        repoUrl.Trim().TrimEnd('/'),
        "/pullrequest/",
        prId.ToString(CultureInfo.InvariantCulture));
}

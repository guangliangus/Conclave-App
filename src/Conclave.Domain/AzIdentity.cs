namespace Conclave.Domain;

/// <summary>
/// Azure DevOps 身份字符串的比较。
/// </summary>
/// <remarks>
/// 「不评审自己的 PR」是硬规则，而它依赖字符串比对。实测这台 ADO Server 上
/// <c>createdBy.uniqueName</c> 形如 <c>LIONMAIL\youngsun</c>，但 <c>az</c> 登录身份
/// 也可能是 <c>youngsun@liontravel.com</c> 或裸 <c>youngsun</c>。三种形态都要认成同一个人，
/// 否则会出现自己批准自己 PR 的情况。
/// </remarks>
public static class AzIdentity
{
    /// <summary>剥掉 NT 域前缀和邮箱域名，转小写。</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var s = raw.Trim();

        var slash = s.LastIndexOfAny(['\\', '/']);
        if (slash >= 0 && slash < s.Length - 1)
        {
            s = s[(slash + 1)..];
        }

        var at = s.IndexOf('@', StringComparison.Ordinal);
        if (at > 0)
        {
            s = s[..at];
        }

        return s.ToLowerInvariant();
    }

    /// <summary>两个身份字符串是否指向同一个人。任一侧为空时返回 false（宁可多评审，不可漏掉自评审检查）。</summary>
    public static bool SamePerson(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        return na.Length > 0 && na == nb;
    }
}

using System.Reflection;
using System.Text.RegularExpressions;

namespace Conclave.Application;

/// <summary>
/// 本进程跑的是哪一版。
/// </summary>
/// <remarks>
/// <para>
/// 版本号在打包时由 <c>-p:Version</c> 写进程序集（<c>scripts/package-macos.sh</c>），
/// 开发态就是 csproj 默认的 <c>1.0.0</c>。这里读 <c>InformationalVersion</c> 而不是
/// <c>AssemblyVersion</c>：前者保留 <c>1.3.0-beta.1</c> 这种预发布后缀，后者会被截成四段数字。
/// <c>+</c> 之后的构建元数据（SourceLink 会塞进提交号）去掉 —— 它不参与比较。
/// </para>
/// <para>
/// 比较是纯函数，单独测：更新提示「有 v1.3.0」到底该不该弹，全看这一个判断。
/// </para>
/// </remarks>
public static partial class AppInfo
{
    /// <summary>当前版本，形如 <c>1.3.0</c> 或 <c>1.3.0-beta.1</c>。</summary>
    public static string Version { get; } = Read();

    /// <summary>
    /// <paramref name="candidate"/> 是否比 <paramref name="current"/> 新。
    /// </summary>
    /// <remarks>
    /// 数字部分按段比；数字相同时，正式版新于预发布，两个预发布按字面序。
    /// 认不出的字符串一律当作「不新」—— 宁可少弹一次提示，不要因为 tag 打错格式
    /// 而反复催人升级到一个不存在的版本。
    /// </remarks>
    public static bool IsNewer(string candidate, string current)
    {
        var a = Parse(candidate);
        var b = Parse(current);
        if (a is null || b is null)
        {
            return false;
        }

        var byNumber = a.Value.Number.CompareTo(b.Value.Number);
        if (byNumber != 0)
        {
            return byNumber > 0;
        }

        // 数字一样：正式版（没有后缀）新于任何预发布。
        if (a.Value.Pre is null)
        {
            return b.Value.Pre is not null;
        }

        return b.Value.Pre is not null
            && string.CompareOrdinal(a.Value.Pre, b.Value.Pre) > 0;
    }

    private static (Version Number, string? Pre)? Parse(string text)
    {
        var m = VersionPattern().Match(text.Trim().TrimStart('v', 'V'));
        if (!m.Success)
        {
            return null;
        }

        // Version 至少要两段；「3」补成「3.0」。多于四段的截掉（Version 最多四段）。
        var parts = m.Groups["num"].Value.Split('.').Take(4).ToList();
        while (parts.Count < 2)
        {
            parts.Add("0");
        }

        return System.Version.TryParse(string.Join('.', parts), out var v)
            ? (v, m.Groups["pre"].Success ? m.Groups["pre"].Value : null)
            : null;
    }

    private static string Read()
    {
        var asm = typeof(AppInfo).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            return info.Split('+')[0];
        }

        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    [GeneratedRegex(@"^(?<num>\d+(?:\.\d+)*)(?:-(?<pre>[0-9A-Za-z.\-]+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;

namespace Conclave.Domain;

/// <summary>
/// 敏感路径清单。命中即强制拉满 quorum。
/// </summary>
/// <remarks>
/// <see cref="Fingerprint"/> 会写进 Summons 块，这样链上能证明当时用的是哪一版规则 ——
/// 半年后回看某次评审为什么只跑了 1 个节点时，不必猜配置。
/// </remarks>
public sealed record ReservedMatters(IReadOnlyList<string> Globs)
{
    /// <summary>默认清单：钱、身份、密钥、对外契约。</summary>
    public static ReservedMatters Default { get; } = new([
        "**/payment*/**",
        "**/billing*/**",
        "**/auth*/**",
        "**/*Auth*.cs",
        "**/identity/**",
        "**/*credential*",
        "**/*secret*",
        "**/appsettings*.json",
        "**/*.proto",
        "**/openapi*.yaml",
        "**/openapi*.json",
        "**/migrations/**",
        "**/db/**",
    ]);

    public bool Matches(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            // 补前导斜杠：否则 "payment/Foo.cs" 匹配不上 "**/payment*/**"（模式要求 / 之前有内容）
            var normalized = "/" + path.Replace('\\', '/').TrimStart('/');
            foreach (var glob in Globs)
            {
                if (FileSystemName.MatchesSimpleExpression(glob, normalized, ignoreCase: true))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>规则集哈希，前 12 个十六进制字符。</summary>
    public string Fingerprint()
    {
        var canonical = string.Join('\n', Globs.OrderBy(g => g, StringComparer.Ordinal));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }
}

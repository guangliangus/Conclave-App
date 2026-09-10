using Shouldly;

namespace Conclave.ArchitectureTests;

/// <summary>
/// 领域层必须是纯函数。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这条要靠守卫而不是靠 reviewer。</b> 整个 Conclave 能不用共识算法，
/// 靠的是「每个节点各自算，算出同一个席位表」。<see cref="Domain.SeatAssignment.Seats"/>
/// 和 <see cref="Domain.Hrw"/> 只要沾上一点本机状态 —— 读一次文件、看一次
/// <c>DateTimeOffset.UtcNow</c>、问一次环境变量 —— 各节点的结果就会分叉，
/// 表现为「同一个 PR 被两个节点同时评审」或者「所有节点都以为该别人干」。
/// </para>
/// <para>
/// 这种分叉不会报错，只会静默地多烧一倍额度或者让 PR 永远没人评。等到有人发现，
/// 现场早就过去了。所以时间必须由调用方以参数传进来（<c>now</c>），
/// 私钥落盘必须留在 <c>Conclave.Infrastructure.ElectorKeyStore</c>。
/// </para>
/// </remarks>
public class DomainPurityTests
{
    /// <summary>本机状态的入口。命中即视为领域层被污染。</summary>
    private static readonly string[] Forbidden =
    [
        "DateTime.Now", "DateTime.UtcNow", "DateTimeOffset.Now", "DateTimeOffset.UtcNow",
        "File.", "Directory.", "Path.Combine",
        "Environment.", "Process", "HttpClient", "Socket", "Random",
    ];

    [Fact]
    public void Domain_never_reads_ambient_state()
    {
        var offenders = new List<string>();

        foreach (var (path, text) in Layers.SourcesOf("Conclave.Domain"))
        {
            foreach (var line in text.Split('\n').Select((t, i) => (Text: t, Number: i + 1)))
            {
                var code = StripComment(line.Text);
                foreach (var token in Forbidden.Where(t => code.Contains(t, StringComparison.Ordinal)))
                {
                    offenders.Add($"{path}:{line.Number} 用了 {token} —— {line.Text.Trim()}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "领域层必须无本机状态，否则各节点会算出不同的席位表：\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Domain_project_has_no_package_references()
    {
        var csproj = File.ReadAllText(
            Path.Combine(Layers.RepoRoot, "src", "Conclave.Domain", "Conclave.Domain.csproj"));

        csproj.ShouldNotContain("PackageReference",
            customMessage: "领域层引第三方包就不再是零依赖，纯函数的可测性也就守不住了");
        csproj.ShouldNotContain("ProjectReference");
    }

    /// <summary>去掉行内注释，免得注释里提到 UtcNow 也被当成违规。</summary>
    private static string StripComment(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }
}

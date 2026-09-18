using System.Text.Json;
using System.Text.Json.Nodes;
using Conclave.Application;

namespace Conclave.UnitTests;

/// <summary>
/// 设置页写回本机配置。
/// </summary>
/// <remarks>
/// 这个文件是<b>人手写的</b>：里面有注释键、有设置页不认识的项、有整段 Mesh 配置。
/// 所以最要紧的一条是「不该被这次保存动到的东西一个都没动」—— 抹掉了人得下次打开
/// 文件才发现，而那时已经没法追是谁抹的。
/// </remarks>
public sealed class LocalSettingsFileTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "conclave-settings-" + Guid.NewGuid().ToString("N"));

    public LocalSettingsFileTests() => Directory.CreateDirectory(_home);

    private ConclaveOptions Options => new() { HomeDirectory = _home };

    private static Dictionary<string, JsonNode?> Values(params (string Key, JsonNode? Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    [Fact]
    public void An_edited_key_is_written()
    {
        var merged = LocalSettingsFile.Merge(
            """{"Conclave":{"MaxConcurrent":1}}""",
            Values(("MaxConcurrent", JsonValue.Create(3))));

        Assert.Equal(3, JsonNode.Parse(merged)!["Conclave"]!["MaxConcurrent"]!.GetValue<int>());
    }

    /// <summary>注释键、不认识的项、整段 Mesh 配置，一个都不能少。</summary>
    [Fact]
    public void Everything_the_settings_page_does_not_know_about_survives()
    {
        var before = """
        {
          "Conclave": {
            "//MaxConcurrent": "一台机器一次只评一个",
            "MaxConcurrent": 1,
            "SomethingNewerThanThisUi": 42,
            "Mesh": { "HttpPort": 47708, "TrustAllElectors": true }
          }
        }
        """;

        var after = JsonNode.Parse(
            LocalSettingsFile.Merge(before, Values(("MaxConcurrent", JsonValue.Create(2)))))!["Conclave"]!;

        Assert.Equal(2, after["MaxConcurrent"]!.GetValue<int>());
        Assert.Equal("一台机器一次只评一个", after["//MaxConcurrent"]!.GetValue<string>());
        Assert.Equal(42, after["SomethingNewerThanThisUi"]!.GetValue<int>());
        Assert.Equal(47708, after["Mesh"]!["HttpPort"]!.GetValue<int>());
        Assert.True(after["Mesh"]!["TrustAllElectors"]!.GetValue<bool>());
    }

    [Fact]
    public void A_nested_key_lands_in_its_block()
    {
        var after = JsonNode.Parse(
            LocalSettingsFile.Merge(
                """{"Conclave":{"Lark":{"AppId":"cli_old","EmailDomain":"liontravel.com"}}}""",
                Values(("Lark.AppId", JsonValue.Create("cli_new")))))!["Conclave"]!["Lark"]!;

        Assert.Equal("cli_new", after["AppId"]!.GetValue<string>());
        Assert.Equal("liontravel.com", after["EmailDomain"]!.GetValue<string>());
    }

    [Fact]
    public void A_missing_file_is_created_from_scratch()
    {
        var after = JsonNode.Parse(
            LocalSettingsFile.Merge(null, Values(("AutoReview", JsonValue.Create(true)))));

        Assert.True(after!["Conclave"]!["AutoReview"]!.GetValue<bool>());
    }

    /// <summary>
    /// 现有文件是坏 JSON 时什么都不写。
    /// </summary>
    /// <remarks>
    /// 拿一个空对象盖过去等于把人手写的配置全删了 —— 保存失败远好过那个。
    /// </remarks>
    [Fact]
    public void A_corrupt_existing_file_aborts_the_save_instead_of_wiping_it()
    {
        var path = Path.Combine(_home, LocalSettingsFile.Name);
        File.WriteAllText(path, "{ 这不是 JSON");

        // ThrowsAny：实际抛的是 JsonReaderException，它是 JsonException 的子类。
        _ = Assert.ThrowsAny<JsonException>(
            () => LocalSettingsFile.Save(Options, Values(("AutoReview", JsonValue.Create(true)))));

        Assert.Equal("{ 这不是 JSON", File.ReadAllText(path));
    }

    /// <summary>
    /// 被集群盖着的键要认出来。
    /// </summary>
    /// <remarks>
    /// meshsettings.json 在配置链里排在本机这份后面，所以这些键本机改了看着像没生效。
    /// 设置页不标出来的话，人会以为保存坏了或热更新坏了，而两者都是好的。
    /// </remarks>
    [Fact]
    public void Keys_the_cluster_overrides_are_reported()
    {
        File.WriteAllText(
            Path.Combine(_home, MeshSettingsFile.Name),
            """{"Conclave":{"AutoReview":true,"Lark":{"AppId":"cli_cluster"}}}""");

        var overridden = LocalSettingsFile.OverriddenByCluster(
            Options, ["AutoReview", "MaxConcurrent", "Lark.AppId", "Lark.EmailDomain"]);

        Assert.Equal(["AutoReview", "Lark.AppId"], overridden.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Without_a_cluster_file_nothing_is_overridden()
        => Assert.Empty(LocalSettingsFile.OverriddenByCluster(Options, ["AutoReview"]));

    [Fact]
    public void Save_round_trips_through_the_file()
    {
        LocalSettingsFile.Save(Options, Values(("ClaudeModel", JsonValue.Create("claude-opus-5"))));

        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(_home, LocalSettingsFile.Name)));
        Assert.Equal("claude-opus-5", json!["Conclave"]!["ClaudeModel"]!.GetValue<string>());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录删不掉不该让测试变红。
        }
    }
}

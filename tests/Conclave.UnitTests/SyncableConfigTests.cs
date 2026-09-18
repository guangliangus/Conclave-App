using System.Reflection;
using System.Text.Json.Nodes;
using Conclave.Application;

namespace Conclave.UnitTests;

/// <summary>
/// 可同步配置的边界与白名单守卫。
/// </summary>
public class SyncableConfigTests
{
    /// <summary>
    /// 反向守卫：<see cref="ConclaveOptions"/> 上出现的任何可写配置属性，
    /// 必须被显式归类为可同步（<see cref="SyncableConfig.Syncable"/>）
    /// 或明确禁止同步（<see cref="SyncableConfig.Blocked"/>）。
    /// </summary>
    /// <remarks>
    /// 防的是未来给配置加了新字段，却忘了决定它能不能随 mesh 同步。
    /// 白名单设计下漏分类不会泄漏（只会被丢弃），但会让新功能在全组同步时静默失效。
    /// </remarks>
    [Fact]
    public void Every_writable_option_in_ConclaveOptions_must_be_categorized()
    {
        var properties = typeof(ConclaveOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .ToList();

        var known = SyncableConfig.Syncable
            .Concat(SyncableConfig.Blocked.Keys)
            .ToHashSet(StringComparer.Ordinal);

        var uncategorized = properties.Where(p => !known.Contains(p)).ToList();

        Assert.True(
            uncategorized.Count == 0,
            $"ConclaveOptions 上有未分类的配置项（既不在 Syncable 也不在 Blocked）：{string.Join(", ", uncategorized)}");
    }

    [Fact]
    public void Serialize_produces_valid_json_containing_only_syncable_keys()
    {
        var options = new ConclaveOptions();
        var json = SyncableConfig.Serialize(options);

        var node = JsonNode.Parse(json);
        Assert.NotNull(node);
        var conclave = node[SyncableConfig.Section] as JsonObject;
        Assert.NotNull(conclave);

        foreach (var (key, _) in conclave)
        {
            Assert.Contains(key, SyncableConfig.Syncable);
            Assert.DoesNotContain(key, SyncableConfig.Blocked.Keys);
        }
    }

    [Fact]
    public void Filter_drops_blocked_and_unknown_keys()
    {
        var incoming = """
        {
            "Conclave": {
                "AutoReview": true,
                "ClaudeExecutable": "/usr/local/bin/evil",
                "HomeDirectory": "/tmp/evil",
                "UnknownKey": "test",
                "Lark": {
                    "Enabled": true,
                    "AppSecret": "should-not-be-in-json"
                }
            }
        }
        """;

        var filtered = SyncableConfig.Filter(incoming, out var dropped);

        Assert.Contains("ClaudeExecutable", dropped);
        Assert.Contains("HomeDirectory", dropped);
        Assert.Contains("UnknownKey", dropped);
        Assert.Contains("Lark.AppSecret", dropped);

        var node = JsonNode.Parse(filtered);
        Assert.NotNull(node);
        var conclave = node[SyncableConfig.Section] as JsonObject;
        Assert.NotNull(conclave);

        Assert.True(conclave["AutoReview"]?.GetValue<bool>());
        Assert.Null(conclave["ClaudeExecutable"]);
        Assert.Null(conclave["HomeDirectory"]);
        Assert.Null(conclave["UnknownKey"]);

        var lark = conclave["Lark"] as JsonObject;
        Assert.NotNull(lark);
        Assert.True(lark["Enabled"]?.GetValue<bool>());
        Assert.Null(lark["AppSecret"]);
    }
}

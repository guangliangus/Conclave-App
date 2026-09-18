using System.Text.Json;
using System.Text.Json.Nodes;

namespace Conclave.Application;

/// <summary>
/// 本机那份 <c>~/.conclave/appsettings.json</c> 的读写。
/// </summary>
/// <remarks>
/// <para>
/// 界面上的设置页只做一件事：把改动写进这个文件。配置热更新会在几百毫秒内自己生效
/// （见 <see cref="ConfigReloadService"/>），所以不需要重启、也不需要任何别的管线。
/// </para>
/// <para>
/// <b>合并写，不是整份覆盖。</b> 这个文件是人手写的，里面有注释键（<c>"//Xxx"</c>）、
/// 有设置页不认识的项、有整段 <c>Mesh</c> / <c>ConfigSync</c> 配置。整份重写会把那些
/// 全部抹掉，而抹掉的东西人下次打开文件才会发现。
/// </para>
/// </remarks>
public static class LocalSettingsFile
{
    /// <summary>文件名，落在 <see cref="ConclaveOptions.HomeDirectory"/> 下。</summary>
    public const string Name = "appsettings.json";

    /// <summary>这份文件的完整路径。</summary>
    public static string PathIn(ConclaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Path.Combine(options.HomeDirectory, Name);
    }

    /// <summary>
    /// 把 <paramref name="values"/> 合并进 <paramref name="existingJson"/> 的 <c>Conclave</c> 节。
    /// </summary>
    /// <param name="existingJson">现有内容；<c>null</c> 或解析不了都当作空文件从头建。</param>
    /// <param name="values">键 → 值。键是 <c>Conclave</c> 节下的名字，支持 <c>Lark.AppId</c> 这种一层嵌套。</param>
    /// <returns>写回去的完整 JSON。</returns>
    public static string Merge(string? existingJson, IReadOnlyDictionary<string, JsonNode?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(existingJson)
                ? []
                : JsonNode.Parse(existingJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            // 文件被改坏了。这里刻意<b>不</b>拿一个空对象去覆盖 —— 那等于把人手写的配置
            // 全删了。调用方负责在写之前先确认文件解析得了，见 Save。
            throw;
        }

        var conclave = root[SyncableConfig.Section] as JsonObject ?? [];
        root[SyncableConfig.Section] = conclave;

        foreach (var (key, value) in values)
        {
            var dot = key.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0)
            {
                conclave[key] = value?.DeepClone();
                continue;
            }

            var block = key[..dot];
            var inner = conclave[block] as JsonObject ?? [];
            conclave[block] = inner;
            inner[key[(dot + 1)..]] = value?.DeepClone();
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// 合并保存。写坏文件比保存失败糟糕得多，所以先确认现有内容解析得了再动手。
    /// </summary>
    /// <exception cref="JsonException">现有文件不是合法 JSON —— 这时什么都不写。</exception>
    public static void Save(ConclaveOptions options, IReadOnlyDictionary<string, JsonNode?> values)
    {
        ArgumentNullException.ThrowIfNull(options);

        var path = PathIn(options);
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        var merged = Merge(existing, values);

        Directory.CreateDirectory(options.HomeDirectory);

        // 先写临时文件再改名：这份文件正被配置系统盯着，直接写会让它读到半截内容。
        var temp = path + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, merged);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// 这些键里，哪些正被集群配置盖着。
    /// </summary>
    /// <remarks>
    /// <b>设置页必须把这些标出来</b>，否则是个很难受的陷阱：<c>meshsettings.json</c> 在配置链里
    /// 排在本机这份<b>后面</b>，所以集群同步过的键，本机改了看着像没生效 —— 人会以为保存坏了，
    /// 或者以为热更新坏了，而两者都好好的。
    /// </remarks>
    public static IReadOnlySet<string> OverriddenByCluster(
        ConclaveOptions options, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var overridden = new HashSet<string>(StringComparer.Ordinal);

        if (MeshSettingsFile.TryRead(options) is not { } json)
        {
            return overridden;
        }

        JsonObject? conclave;
        try
        {
            conclave = JsonNode.Parse(json) is JsonObject root
                ? root[SyncableConfig.Section] as JsonObject
                : null;
        }
        catch (JsonException)
        {
            return overridden;
        }

        if (conclave is null)
        {
            return overridden;
        }

        foreach (var key in keys)
        {
            var dot = key.IndexOf('.', StringComparison.Ordinal);
            var present = dot < 0
                ? conclave.ContainsKey(key)
                : conclave[key[..dot]] is JsonObject inner && inner.ContainsKey(key[(dot + 1)..]);

            if (present)
            {
                _ = overridden.Add(key);
            }
        }

        return overridden;
    }
}

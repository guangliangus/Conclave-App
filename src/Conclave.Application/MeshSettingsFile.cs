using System.Text.Json.Nodes;

namespace Conclave.Application;

/// <summary>
/// 同步来的那份配置文件，以及它跟配置系统合不来的那一处。
/// </summary>
/// <remarks>
/// <para>
/// <b>.NET 的配置对数组是按「索引」合并的，不是整体替换。</b> 这在多份配置叠加时会
/// 悄悄出错 —— 实测：
/// </para>
/// <code>
/// appsettings.json    ProjectDenyList: ["legacy-a","legacy-b","legacy-c"]
/// meshsettings.json   ProjectDenyList: ["only-x"]
/// 绑定结果            ["only-x","legacy-b","legacy-c"]   ← 后两个是本地残留
/// </code>
/// <para>
/// 最糟的是集群把列表<b>清空</b>的情形：推一个 <c>[]</c> 过去等于一个索引都不提供，
/// 本地条目全部原样留着 —— 「白名单已经清了」在任何有本地条目的机器上完全无效，而且
/// 一声不吭。字典同理：集群删掉的一条 <c>UserMap</c> 映射，本地那条还在。
/// </para>
/// <para>
/// 所以集合不能靠配置叠加来覆盖，只能在绑定<b>之后</b>按这份文件整体替换一遍。
/// 仓库里 <see cref="ConclaveOptions.ExcludedProjectSuffixes"/> 当初被设计成分号分隔的
/// 标量而不是数组，躲的就是这个坑。
/// </para>
/// </remarks>
public static class MeshSettingsFile
{
    /// <summary>文件名，落在 <see cref="ConclaveOptions.HomeDirectory"/> 下。</summary>
    public const string Name = "meshsettings.json";

    /// <summary>这份文件的完整路径。</summary>
    public static string PathIn(ConclaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Path.Combine(options.HomeDirectory, Name);
    }

    /// <summary>
    /// 把这份文件里出现过的集合键<b>整体</b>替换到 <paramref name="live"/> 上。
    /// </summary>
    /// <remarks>
    /// 只动文件里真的出现了的键：没出现就保持本地值不动（那才是「按 key 覆盖」的语义）。
    /// </remarks>
    /// <returns>实际被整体替换掉的键名，给日志用。</returns>
    public static IReadOnlyList<string> ApplyCollections(ConclaveOptions live, string json)
    {
        ArgumentNullException.ThrowIfNull(live);

        var replaced = new List<string>();

        if (JsonNode.Parse(json) is not JsonObject root
            || root[SyncableConfig.Section] is not JsonObject conclave)
        {
            return replaced;
        }

        if (conclave["ProjectAllowList"] is JsonArray allow)
        {
            live.ProjectAllowList = [.. Strings(allow)];
            replaced.Add("ProjectAllowList");
        }

        if (conclave["ProjectDenyList"] is JsonArray deny)
        {
            live.ProjectDenyList = [.. Strings(deny)];
            replaced.Add("ProjectDenyList");
        }

        if (conclave["Lark"] is JsonObject lark && lark["UserMap"] is JsonObject map)
        {
            live.Lark.UserMap.Clear();
            foreach (var (key, value) in map)
            {
                if (value?.GetValue<string>() is { Length: > 0 } mapped)
                {
                    live.Lark.UserMap[key] = mapped;
                }
            }

            replaced.Add("Lark.UserMap");
        }

        return replaced;
    }

    /// <summary>读这份文件；不存在或者读坏了返回 null。</summary>
    /// <remarks>
    /// 写方是「先写临时文件再改名」，所以读到半截内容是不可能的；这里防的是文件被手工
    /// 改坏、或者盘上根本没有。任何一种都只是「没有集群配置」，不该抛。
    /// </remarks>
    public static string? TryRead(ConclaveOptions options)
    {
        var path = PathIn(options);

        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Strings(JsonArray array)
        => array.Select(n => n?.GetValue<string>()).OfType<string>();
}

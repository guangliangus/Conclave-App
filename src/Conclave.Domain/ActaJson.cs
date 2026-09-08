using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conclave.Domain;

/// <summary>
/// Acta payload 的序列化设置。
/// </summary>
/// <remarks>
/// 区块哈希覆盖 PayloadJson 的字面文本，所以序列化必须稳定：不缩进、属性顺序固定、
/// 枚举写成字符串（枚举值将来重排也不会让老区块的哈希失效）。
/// 各节点必须用同一套设置，否则算出来的哈希对不上。
/// </remarks>
public static class ActaJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlHub.Protocol;

/// <summary>
/// 两端统一的 JSON 序列化配置。
/// 使用 camelCase、宽松转义（保留中文原样），并容忍注释与尾随逗号以便人工维护配置文件。
/// </summary>
public static class HubJson
{
    /// <summary>共享的序列化选项。只读，线程安全。</summary>
    public static readonly JsonSerializerOptions Options = Create();

    /// <summary>创建一份序列化选项（需要局部定制时可基于此副本修改）。</summary>
    public static JsonSerializerOptions Create(bool indented = false) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = indented,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>序列化为字符串。</summary>
    public static string Serialize<T>(T value, bool indented = false) =>
        JsonSerializer.Serialize(value, indented ? Create(true) : Options);

    /// <summary>从字符串反序列化。</summary>
    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>从字符串反序列化，失败时返回 <paramref name="fallback"/>。</summary>
    public static T DeserializeOrDefault<T>(string? json, T fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}

using System.Text.Json;

namespace NE.ChatAI.Import;

internal static class JsonValues
{
    public static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Whitespace-only strings stay non-null because turn markers can be significant whitespace.
    public static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}

using System.Text.Json;

namespace NE.ChatAI.Import;

/// <summary>A SillyTavern file's values as the importers read them.</summary>
internal static class JsonValues
{
    /// <summary>The property's text; none when it is missing or not a string.</summary>
    public static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>None for an empty text, the one rule for an optional text: whitespace is kept, as a turn's marker means it.</summary>
    public static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}

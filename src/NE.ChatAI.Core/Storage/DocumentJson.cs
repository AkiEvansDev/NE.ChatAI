using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NE.ChatAI.Storage;

/// <summary>How a kept document is written: indented camelCase a person can read and edit, enums as their names, nulls left out.</summary>
public static class DocumentJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // A prompt is full of <, > and quotes; escaped they would be unreadable by hand.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReadCommentHandling = JsonCommentHandling.Skip,
            // A chat nests a variant's messages in the variant, about six levels for every branch inside a branch; 64 holds ten.
            MaxDepth = 512,
            AllowTrailingCommas = true
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);

        return options;
    }
}

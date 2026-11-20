using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NE.ChatAI.Storage;

/// <summary>JSON serializer settings and BOM-safe readers for persisted documents.</summary>
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
            // Branch tails nest variants inside variants across deep multi-branch histories.
            MaxDepth = 512,
            AllowTrailingCommas = true
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);

        return options;
    }

    /// <summary>Strips a leading UTF-8 byte-order mark when a file was saved by hand.</summary>
    public static ReadOnlySpan<byte> StripBom(ReadOnlySpan<byte> utf8)
        => utf8.StartsWith("\uFEFF"u8) ? utf8[3..] : utf8;

    /// <summary>Deserializes a document from UTF-8 JSON and rejects null payloads or newer format versions.</summary>
    public static T Read<T>(ReadOnlySpan<byte> utf8, string path) where T : Document
    {
        T? document;

        try
        {
            document = JsonSerializer.Deserialize<T>(StripBom(utf8), Options);
        }
        catch (JsonException exception)
        {
            throw new DocumentFormatException(path, exception.Message, exception);
        }

        if (document is null)
            throw new DocumentFormatException(path, "the file holds no document.");

        if (document.Format > Document.CurrentFormat)
            throw new DocumentFormatException(path, $"format {document.Format} is newer than this version writes ({Document.CurrentFormat}).");

        return document;
    }

    /// <summary>Deserializes a document from UTF-8 JSON when valid and supported, or returns <see langword="null"/> when malformed.</summary>
    public static T? TryRead<T>(ReadOnlySpan<byte> utf8) where T : Document
    {
        try
        {
            T? document = JsonSerializer.Deserialize<T>(StripBom(utf8), Options);

            return document is not null && document.Format <= Document.CurrentFormat ? document : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

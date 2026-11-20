using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using NE.ChatAI.Characters;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Import;

/// <summary>A character card's version: the bare V1 fields, V2's <c>chara_card_v2</c>, V3's <c>chara_card_v3</c>.</summary>
public enum CardVersion
{
    V1,
    V2,
    V3
}

/// <summary>A card read: the character it describes, and the version it was written in.</summary>
public sealed record CharacterCard(Character Character, CardVersion Version);

/// <summary>A file that is no character card: neither a PNG carrying one, nor its JSON.</summary>
public sealed class CardFormatException : Exception
{
    public CardFormatException()
    {
    }

    public CardFormatException(string message) : base(message)
    {
    }

    public CardFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Character cards V1, V2 and V3 — a PNG carrying the card in a text chunk (<c>ccv3</c> before <c>chara</c>), or its JSON.</summary>
public static class CharacterCards
{
    /// <summary>Reads the V1–V3 character card embedded in <paramref name="file"/> (PNG or UTF-8 JSON).</summary>
    public static CharacterCard Read(ReadOnlySpan<byte> file)
    {
        if (!PngText.IsPng(file))
            return ReadJson(file);

        IReadOnlyDictionary<string, string> texts = PngText.Read(file);

        // A V3 card keeps a V2 copy beside it for older readers; prefer ccv3 when non-empty.
        var encoded = NonEmpty(texts.GetValueOrDefault("ccv3"))
            ?? NonEmpty(texts.GetValueOrDefault("chara"))
            ?? throw new CardFormatException("The picture carries no character card.");
        byte[] json;

        try
        {
            json = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new CardFormatException("The picture's card is not base64.", exception);
        }

        return ReadJson(json);
    }

    private static string? NonEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static CharacterCard ReadJson(ReadOnlySpan<byte> json)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(Encoding.UTF8.GetString(DocumentJson.StripBom(json)));
        }
        catch (JsonException exception)
        {
            throw new CardFormatException("The file is neither a PNG nor a card's JSON.", exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                throw new CardFormatException("The card's JSON is not an object.");

            var spec = JsonValues.String(root, "spec");
            CardVersion version = spec switch
            {
                "chara_card_v3" => CardVersion.V3,
                "chara_card_v2" => CardVersion.V2,
                _ => CardVersion.V1
            };

            JsonElement data = version != CardVersion.V1 && root.TryGetProperty("data", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;

            if (JsonValues.String(data, "name") is not { Length: > 0 } name)
                throw new CardFormatException("The card has no name.");

            return new CharacterCard(Map(data, name), version);
        }
    }

    private static Character Map(JsonElement data, string name)
    {
        List<string> firstMessages = [];

        if (JsonValues.String(data, "first_mes")?.TrimEnd('\r', '\n') is { Length: > 0 } first)
            firstMessages.Add(first);

        firstMessages.AddRange(Strings(data, "alternate_greetings").Where(static greeting => greeting.Length > 0));

        return new Character
        {
            Name = name,
            Tags = [.. Strings(data, "tags").Where(static tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
            Description = (JsonValues.String(data, "description") ?? string.Empty).TrimEnd('\r', '\n'),
            Personality = (JsonValues.String(data, "personality") ?? string.Empty).TrimEnd('\r', '\n'),
            Scenario = (JsonValues.String(data, "scenario") ?? string.Empty).TrimEnd('\r', '\n'),
            ExampleMessages = (JsonValues.String(data, "mes_example") ?? string.Empty).TrimEnd('\r', '\n'),
            FirstMessages = firstMessages,
            // A card's own prompts are trimmed as SillyTavern trims them, so one of spaces alone is none.
            SystemPrompt = JsonValues.NullIfEmpty(JsonValues.String(data, "system_prompt")?.Trim()),
            PostHistoryInstructions = JsonValues.NullIfEmpty(JsonValues.String(data, "post_history_instructions")?.Trim())
        };
    }

    private static IEnumerable<string> Strings(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String).Select(static item => item.GetString()!.Trim())
            : [];
}

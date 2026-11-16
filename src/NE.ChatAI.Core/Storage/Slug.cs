using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NE.ChatAI.Storage;

/// <summary>A document's id: a slug of its name (Cyrillic transliterated to Latin) and eight hex digits.</summary>
public static class Slug
{
    private const int MaxSlugLength = 40;
    private const int SuffixLength = 8;

    private const string CyrillicLetters = "абвгдеёжзийклмнопрстуфхцчшщъыьэюя";

    private static readonly string[] CyrillicLatin =
    [
        "a", "b", "v", "g", "d", "e", "e", "zh", "z", "i", "y", "k", "l", "m", "n", "o", "p", "r", "s", "t", "u", "f", "h", "ts", "ch",
        "sh", "sch", "", "y", "", "e", "yu", "ya"
    ];

    /// <summary>A new id for a document named <paramref name="name"/>: <c>seraphina-3f2a91c0</c>, or the hex digits alone.</summary>
    public static string NewId(string name)
    {
        var slug = Of(name);
        var suffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

        return slug.Length == 0 ? suffix : $"{slug}-{suffix}";
    }

    /// <summary>Recomputes the slug prefix of <paramref name="currentId"/> from <paramref name="name"/> while preserving its 8-digit hex suffix.</summary>
    public static string RenamedId(string currentId, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(currentId);
        ArgumentNullException.ThrowIfNull(name);

        if (!TryExtractHexSuffix(currentId, out var suffix))
            return NewId(name);

        var slug = Of(name);

        return slug.Length == 0 ? suffix : $"{slug}-{suffix}";
    }

    private static bool TryExtractHexSuffix(string id, out string suffix)
    {
        if (id.Length == SuffixLength && IsLowerHex(id.AsSpan()))
        {
            suffix = id;
            return true;
        }

        if (id.Length > SuffixLength + 1 && id[^(SuffixLength + 1)] == '-' && IsLowerHex(id.AsSpan(id.Length - SuffixLength)))
        {
            suffix = id[^SuffixLength..];
            return true;
        }

        suffix = string.Empty;
        return false;
    }

    private static bool IsLowerHex(ReadOnlySpan<char> span)
    {
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        }

        return true;
    }

    /// <summary>Lower-case Latin letters, digits and single hyphens; Cyrillic is transliterated, anything else is a word break.</summary>
    public static string Of(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        StringBuilder slug = new(name.Length);
        var pendingHyphen = false;

        foreach (var character in name.Normalize(NormalizationForm.FormC).ToLower(CultureInfo.InvariantCulture))
        {
            var piece = Transliterated(character);

            if (piece is null)
            {
                pendingHyphen = slug.Length > 0;
                continue;
            }

            if (piece.Length == 0)
                continue;

            if (slug.Length + (pendingHyphen ? 1 : 0) + piece.Length > MaxSlugLength)
                break;

            if (pendingHyphen)
                _ = slug.Append('-');

            _ = slug.Append(piece);
            pendingHyphen = false;
        }

        return slug.ToString().TrimEnd('-');
    }

    /// <summary>
    /// A letter or digit as Latin, or <c>null</c> for a word break. Cyrillic is looked up whole — decomposed, й would lose its breve
    /// and read as и — and a Latin letter with a mark keeps its base: é is e.
    /// </summary>
    private static string? Transliterated(char character)
    {
        if (character is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            return character.ToString();

        var cyrillic = CyrillicLetters.IndexOf(character, StringComparison.Ordinal);

        if (cyrillic >= 0)
            return CyrillicLatin[cyrillic];

        var decomposed = character.ToString().Normalize(NormalizationForm.FormD);

        return decomposed.Length > 1 && decomposed[0] is >= 'a' and <= 'z' ? decomposed[0].ToString() : null;
    }

    /// <summary>Whether <paramref name="id"/> can be a document's id — a file name with nothing that walks out of its folder.</summary>
    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxSlugLength + 9)
            return false;

        foreach (var character in id)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                return false;
        }

        return id[0] != '-' && id[^1] != '-';
    }
}

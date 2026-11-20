using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace NE.ChatAI.Import;

/// <summary>The text a PNG carries in its <c>tEXt</c> and uncompressed <c>iTXt</c> chunks, by keyword — where character cards live.</summary>
public static class PngText
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsPng(ReadOnlySpan<byte> file)
        => file.StartsWith(Signature);

    /// <summary>Every text chunk's keyword and text; a later chunk of the same keyword wins. Not a PNG, and it is empty.</summary>
    public static IReadOnlyDictionary<string, string> Read(ReadOnlySpan<byte> file)
    {
        Dictionary<string, string> texts = [with(StringComparer.Ordinal)];

        if (!IsPng(file))
            return texts;

        var offset = Signature.Length;

        while (offset + 12 <= file.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(file[offset..]);

            if (length > (uint)(file.Length - offset - 12))
                break;

            ReadOnlySpan<byte> type = file.Slice(offset + 4, 4);
            ReadOnlySpan<byte> data = file.Slice(offset + 8, (int)length);

            if (type.SequenceEqual("tEXt"u8))
                ReadText(data, texts);
            else if (type.SequenceEqual("iTXt"u8))
                ReadInternationalText(data, texts);
            else if (type.SequenceEqual("IEND"u8))
                break;

            offset += 12 + (int)length;
        }

        return texts;
    }

    private static void ReadText(ReadOnlySpan<byte> data, Dictionary<string, string> texts)
    {
        var separator = data.IndexOf((byte)0);

        if (separator > 0)
            texts[Encoding.Latin1.GetString(data[..separator])] = Encoding.Latin1.GetString(data[(separator + 1)..]);
    }

    private static void ReadInternationalText(ReadOnlySpan<byte> data, Dictionary<string, string> texts)
    {
        var separator = data.IndexOf((byte)0);

        // Byte after the keyword null terminator is the compression flag; compressed iTXt chunks are skipped.
        if (separator <= 0 || separator + 3 > data.Length || data[separator + 1] != 0)
            return;

        ReadOnlySpan<byte> rest = data[(separator + 3)..];
        var language = rest.IndexOf((byte)0);

        if (language < 0)
            return;

        rest = rest[(language + 1)..];
        var translated = rest.IndexOf((byte)0);

        if (translated >= 0)
            texts[Encoding.Latin1.GetString(data[..separator])] = Encoding.UTF8.GetString(rest[(translated + 1)..]);
    }
}

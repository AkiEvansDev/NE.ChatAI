using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Import;

/// <summary>A card taken into the data folder: the character kept, its card beside it as it came, a PNG card its own avatar.</summary>
public static class CardImport
{
    public const string SourcePng = "source.png";
    public const string SourceJson = "source.json";
    public const string AvatarPng = "avatar.png";

    /// <summary>Keeps the card in <paramref name="file"/> as a new character and answers its id.</summary>
    /// <exception cref="CardFormatException">The file holds no card.</exception>
    public static async Task<string> ImportAsync(DataFolder data, ReadOnlyMemory<byte> file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        CharacterCard card = CharacterCards.Read(file.Span);
        var png = PngText.IsPng(file.Span);
        var id = await data.Characters.CreateAsync(card.Character, cancellationToken).ConfigureAwait(false);
        var folder = data.Characters.FolderOf(id);

        await AtomicFile.WriteAsync(Path.Combine(folder, png ? SourcePng : SourceJson), file, cancellationToken).ConfigureAwait(false);

        // The card's picture is the character's: the same bytes, until pictures are re-encoded on the way in.
        if (png)
            await AtomicFile.WriteAsync(Path.Combine(folder, AvatarPng), file, cancellationToken).ConfigureAwait(false);

        return id;
    }
}

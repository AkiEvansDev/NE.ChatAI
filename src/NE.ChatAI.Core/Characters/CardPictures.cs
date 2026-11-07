using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Characters;

/// <summary>
/// A picture kept in a folder under a name of its own — <c>avatar</c>, <c>background</c> — as <c>.webp</c>, <c>.png</c> or
/// <c>.jpg</c>; the one written last replaces whichever type was there.
/// </summary>
public static class PictureFiles
{
    /// <summary>The types a picture may be kept as, in the order they are looked for.</summary>
    private static readonly string[] Extensions = [".webp", ".png", ".jpg", ".jpeg"];

    /// <summary>The picture's file, or <c>null</c> when the folder holds none under <paramref name="name"/>.</summary>
    public static string? Find(string folder, string name)
    {
        foreach (var extension in Extensions)
        {
            var path = Path.Combine(folder, name + extension);

            if (File.Exists(path))
                return path;
        }

        return null;
    }

    /// <summary>Keeps <paramref name="picture"/> under <paramref name="name"/>, in place of the one before whatever its type.</summary>
    public static async Task WriteAsync(string folder, string name, ReadOnlyMemory<byte> picture, string? contentType, CancellationToken cancellationToken)
    {
        var kept = name + ExtensionOf(contentType);

        await AtomicFile.WriteAsync(Path.Combine(folder, kept), picture, cancellationToken).ConfigureAwait(false);

        foreach (var extension in Extensions)
        {
            if (name + extension != kept)
                File.Delete(Path.Combine(folder, name + extension));
        }
    }

    private static string ExtensionOf(string? contentType)
        => contentType?.ToUpperInvariant() switch
        {
            "IMAGE/WEBP" => ".webp",
            "IMAGE/JPEG" => ".jpg",
            _ => ".png"
        };

    /// <summary>Takes the picture of that name away, whatever its type.</summary>
    public static void Delete(string folder, string name)
    {
        if (!Directory.Exists(folder))
            return;

        foreach (var extension in Extensions)
            File.Delete(Path.Combine(folder, name + extension));
    }
}

/// <summary>A card's own pictures in its folder: its avatar, and the picture behind its chats — a character's, a persona's, a world's alike.</summary>
public static class CardPictures
{
    public const string Avatar = "avatar";
    public const string Background = "background";

    /// <summary>The card's picture of that name, or <c>null</c> for a card that has none or is not there.</summary>
    public static string? PathOf<T>(DocumentStore<T> store, string id, string name) where T : NamedDocument
    {
        ArgumentNullException.ThrowIfNull(store);

        return Slug.IsValidId(id) && store.Exists(id) ? PictureFiles.Find(store.FolderOf(id), name) : null;
    }

    /// <summary>Keeps <paramref name="picture"/> as the card's picture of that name.</summary>
    public static Task WriteAsync<T>(DocumentStore<T> store, string id, string name, ReadOnlyMemory<byte> picture, string? contentType, CancellationToken cancellationToken) where T : NamedDocument
    {
        ArgumentNullException.ThrowIfNull(store);

        return PictureFiles.WriteAsync(store.FolderOf(id), name, picture, contentType, cancellationToken);
    }

    /// <summary>Takes the card's picture of that name away; without an avatar it is drawn with the person's glyph.</summary>
    public static void Delete<T>(DocumentStore<T> store, string id, string name) where T : NamedDocument
    {
        ArgumentNullException.ThrowIfNull(store);

        PictureFiles.Delete(store.FolderOf(id), name);
    }
}

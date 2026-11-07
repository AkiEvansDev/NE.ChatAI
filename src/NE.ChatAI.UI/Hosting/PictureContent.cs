using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Characters;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Hosting;

/// <summary>
/// The pictures the pages show from the data folder: a character's avatar at <c>c{id}/{version}</c>, a persona's at
/// <c>p{id}/{version}</c>, a world's at <c>w{id}/{version}</c>; the picture behind a character's chats at <c>b{id}/{version}</c>,
/// the application's own at <c>gapp/{version}</c>. Anything else is no content, and the framework answers it with a 404.
/// </summary>
/// <remarks>
/// The version is the file's last write, so a picture replaced under the same id is a new address and the old one may be kept for
/// good; the resolver reads only the id.
/// </remarks>
internal sealed class PictureContent(DataFolder data, IUIContentAddressResolver addresses) : IUIContentProvider
{
    private const char CharacterPrefix = 'c';
    private const char PersonaPrefix = 'p';
    private const char WorldPrefix = 'w';
    private const char BackgroundPrefix = 'b';
    private const char AppBackgroundPrefix = 'g';
    private const string AppId = "app";

    /// <summary>The character's avatar, or <c>null</c> for a character with none, so the page draws its own mark.</summary>
    public string? CharacterAvatarOf(string? characterId)
        => characterId is null ? null : AddressOf(CharacterPrefix, characterId, CardPictures.PathOf(data.Characters, characterId, CardPictures.Avatar));

    /// <summary>The persona's avatar, or <c>null</c> for a persona with none.</summary>
    public string? PersonaAvatarOf(string? personaId)
        => personaId is null ? null : AddressOf(PersonaPrefix, personaId, CardPictures.PathOf(data.Personas, personaId, CardPictures.Avatar));

    /// <summary>The world's picture, or <c>null</c> for a world with none.</summary>
    public string? WorldAvatarOf(string? worldId)
        => worldId is null ? null : AddressOf(WorldPrefix, worldId, CardPictures.PathOf(data.Worlds, worldId, CardPictures.Avatar));

    /// <summary>The picture behind a chat with the character: its own, else the application's; none, and the chat stands on the theme.</summary>
    public string? BackgroundOf(string? characterId)
        => CharacterBackgroundOf(characterId) ?? AppBackground();

    /// <summary>The character's own picture behind its chats, or <c>null</c> for none.</summary>
    public string? CharacterBackgroundOf(string? characterId)
        => characterId is null ? null : AddressOf(BackgroundPrefix, characterId, CardPictures.PathOf(data.Characters, characterId, CardPictures.Background));

    /// <summary>The application's picture behind a chat whose character has none, or <c>null</c>.</summary>
    public string? AppBackground()
        => AddressOf(AppBackgroundPrefix, AppId, PictureFiles.Find(data.Root, CardPictures.Background));

    private string? AddressOf(char prefix, string id, string? path)
        => path is null ? null : addresses.AddressOf($"{prefix}{id}/{File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture)}");

    public Task<UIContent?> ResolveAsync(UIContentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = request.Key;
        var end = key.IndexOf('/', StringComparison.Ordinal);
        var id = key.Length < 2 ? string.Empty : end < 1 ? key[1..] : key[1..end];

        var path = id.Length == 0 ? null : key[0] switch
        {
            CharacterPrefix => CardPictures.PathOf(data.Characters, id, CardPictures.Avatar),
            PersonaPrefix => CardPictures.PathOf(data.Personas, id, CardPictures.Avatar),
            WorldPrefix => CardPictures.PathOf(data.Worlds, id, CardPictures.Avatar),
            BackgroundPrefix => CardPictures.PathOf(data.Characters, id, CardPictures.Background),
            AppBackgroundPrefix when id == AppId => PictureFiles.Find(data.Root, CardPictures.Background),
            _ => null
        };

        if (path is null)
            return Task.FromResult<UIContent?>(null);

        UIContent content = new() { Content = File.OpenRead(path), ContentType = ContentTypeOf(path), Immutable = true };

        return Task.FromResult<UIContent?>(content);
    }

    private static string ContentTypeOf(string path)
        => Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".WEBP" => "image/webp",
            ".JPG" or ".JPEG" => "image/jpeg",
            _ => "image/png"
        };
}

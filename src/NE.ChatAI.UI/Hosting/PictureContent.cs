using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Characters;
using NE.ChatAI.Chats;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Hosting;

/// <summary>
/// The pictures the pages show from the data folder: avatars (<c>c/p/w</c>), backgrounds (<c>h/b/gapp</c>), global gallery images
/// (<c>ig/{id}/{version}</c>), character gallery images (<c>ic/{char}/{id}/{version}</c>), chat media (<c>ih/{chat}/{id}/{version}</c>),
/// and ephemeral canvas-preview images (<c>ip/{id}</c>). Anything else is no content, and the framework answers it with a 404.
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
    private const char ChatBackgroundPrefix = 'h';
    private const char AppBackgroundPrefix = 'g';
    private const char ImagePrefix = 'i';
    private const string AppId = "app";
    private const string ProbeKey = "__probe__";
    private const int MaxPreviewEntries = 32;

    private readonly Lock _previewLock = new();
    private readonly Queue<string> _previewOrder = new();
    private readonly Dictionary<string, (byte[] Bytes, string ContentType)> _previews = [with(StringComparer.Ordinal)];

    /// <summary>The character's avatar, or <c>null</c> for a character with none, so the page draws its own mark.</summary>
    public string? CharacterAvatarOf(string? characterId)
        => characterId is null ? null : AddressOf(CharacterPrefix, characterId, CardPictures.PathOf(data.Characters, characterId, CardPictures.Avatar));

    private string? AddressOf(char prefix, string id, string? path)
        => path is null ? null : addresses.AddressOf($"{prefix}{id}/{File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>The persona's avatar, or <c>null</c> for a persona with none.</summary>
    public string? PersonaAvatarOf(string? personaId)
        => personaId is null ? null : AddressOf(PersonaPrefix, personaId, CardPictures.PathOf(data.Personas, personaId, CardPictures.Avatar));

    /// <summary>The world's picture, or <c>null</c> for a world with none.</summary>
    public string? WorldAvatarOf(string? worldId)
        => worldId is null ? null : AddressOf(WorldPrefix, worldId, CardPictures.PathOf(data.Worlds, worldId, CardPictures.Avatar));

    /// <summary>The picture behind a chat: the chat's own, else its character's, else the application's; none, and the chat stands on the theme.</summary>
    public string? BackgroundOf(string? characterId, string? chatId = null)
        => ChatBackgroundOf(chatId) ?? CharacterBackgroundOf(characterId) ?? AppBackground();

    /// <summary>The chat's own picture behind it, or <c>null</c> for none.</summary>
    public string? ChatBackgroundOf(string? chatId)
        => chatId is null ? null : AddressOf(ChatBackgroundPrefix, chatId, ChatPictures.PathOf(data, chatId));

    /// <summary>The character's own picture behind its chats, or <c>null</c> for none.</summary>
    public string? CharacterBackgroundOf(string? characterId)
        => characterId is null ? null : AddressOf(BackgroundPrefix, characterId, CardPictures.PathOf(data.Characters, characterId, CardPictures.Background));

    /// <summary>The application's picture behind a chat whose character has none, or <c>null</c>.</summary>
    public string? AppBackground()
        => AddressOf(AppBackgroundPrefix, AppId, PictureFiles.Find(data.Root, CardPictures.Background));

    /// <summary>The served address of <paramref name="imageId"/> in the global gallery, or <c>null</c> when absent.</summary>
    public string? GlobalGalleryImageOf(string? imageId)
        => imageId is null ? null : KeyedAddressOf($"ig/{imageId}", GalleryPictures.GlobalPathOf(data, imageId));

    private string? KeyedAddressOf(string contentKey, string? path)
        => path is null ? null : addresses.AddressOf($"{contentKey}/{File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>The served address of <paramref name="imageId"/> in <paramref name="characterId"/>'s gallery, or <c>null</c> when absent.</summary>
    public string? CharacterGalleryImageOf(string? characterId, string? imageId)
        => characterId is null || imageId is null ? null : KeyedAddressOf($"ic/{characterId}/{imageId}", GalleryPictures.CharacterPathOf(data, characterId, imageId));

    /// <summary>The served address of scenario-generated <paramref name="imageId"/> in <paramref name="chatId"/>'s media folder, or <c>null</c> when absent.</summary>
    public string? ChatMediaOf(string? chatId, string? imageId)
        => chatId is null || imageId is null ? null : KeyedAddressOf($"ih/{chatId}/{imageId}", GalleryPictures.ChatMediaPathOf(data, chatId, imageId));

    /// <summary>Stores an ephemeral preview image in memory (for canvas preview runs) and returns its served address.</summary>
    public string SavePreviewImage(ReadOnlyMemory<byte> picture, string? contentType)
    {
        var id = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
        var normalizedType = NormalizePreviewContentType(picture.Span, contentType);

        lock (_previewLock)
        {
            while (_previewOrder.Count >= MaxPreviewEntries)
            {
                var evicted = _previewOrder.Dequeue();
                _ = _previews.Remove(evicted);
            }

            _previewOrder.Enqueue(id);
            _previews[id] = (picture.ToArray(), normalizedType);
        }

        return addresses.AddressOf($"ip/{id}");
    }

    private static string NormalizePreviewContentType(ReadOnlySpan<byte> bytes, string? contentType)
    {
        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return "image/webp";

        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var trimmed = contentType.Trim();

            if (trimmed.Equals("image/webp", StringComparison.OrdinalIgnoreCase))
                return "image/webp";

            if (trimmed.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("image/jpg", StringComparison.OrdinalIgnoreCase))
                return "image/jpeg";
        }

        return "image/png";
    }

    /// <summary>Resolves a stored visual key (<c>global.&lt;id&gt;</c>, <c>&lt;char&gt;.&lt;id&gt;</c>, or <c>chat/&lt;chat&gt;/&lt;id&gt;</c>) to its live served address, or <c>null</c> when the file was removed.</summary>
    public string? VisualAddressOf(string? canonicalKey, string? characterId, IReadOnlyDictionary<string, string>? characterTags = null, IReadOnlyDictionary<string, string>? globalTags = null)
    {
        if (GalleryPictures.Resolve(data, canonicalKey, characterId, characterTags, globalTags) is not (var resolvedKey, var path))
            return null;

        if (resolvedKey.StartsWith(GalleryPictures.ChatPrefix, StringComparison.Ordinal))
            return KeyedAddressOf("ih/" + resolvedKey[GalleryPictures.ChatPrefix.Length..], path);

        var dot = resolvedKey.IndexOf('.', StringComparison.Ordinal);

        if (dot <= 0 || dot >= resolvedKey.Length - 1)
            return null;

        var scope = resolvedKey[..dot];
        var imageId = resolvedKey[(dot + 1)..];

        return string.Equals(scope, GalleryPictures.GlobalScope, StringComparison.Ordinal)
            ? KeyedAddressOf("ig/" + imageId, path)
            : KeyedAddressOf($"ic/{scope}/{imageId}", path);
    }

    /// <summary>
    /// Resolves either a served <see cref="PictureContent"/> address or a gallery/chat key into a canonical persistent key
    /// (or an ephemeral preview marker when <c>IsPreview</c> is <c>true</c>).
    /// </summary>
    public (string Key, bool IsPreview)? TryResolveVisualKey(string? input, string? characterId, IReadOnlyDictionary<string, string>? characterTags = null, IReadOnlyDictionary<string, string>? globalTags = null)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var trimmed = input.Trim();

        if (TryExtractContentKey(trimmed) is string contentKey)
            return ResolveFromContentKey(contentKey);

        return GalleryPictures.Resolve(data, trimmed, characterId, characterTags, globalTags) is (var key, _)
            ? (key, false)
            : null;
    }

    private string? TryExtractContentKey(string address)
    {
        var probe = addresses.AddressOf(ProbeKey);

        if (!probe.EndsWith(ProbeKey, StringComparison.Ordinal))
            return null;

        var prefix = probe[..^ProbeKey.Length];

        return address.StartsWith(prefix, StringComparison.Ordinal) && address.Length > prefix.Length
            ? Uri.UnescapeDataString(address[prefix.Length..])
            : null;
    }

    private (string Key, bool IsPreview)? ResolveFromContentKey(string contentKey)
    {
        var parts = contentKey.Split('/');

        if (parts.Length >= 2 && string.Equals(parts[0], "ig", StringComparison.Ordinal))
            return GalleryPictures.GlobalPathOf(data, parts[1]) is not null ? ($"{GalleryPictures.GlobalScope}.{parts[1]}", false) : null;

        if (parts.Length >= 3 && string.Equals(parts[0], "ic", StringComparison.Ordinal))
            return GalleryPictures.CharacterPathOf(data, parts[1], parts[2]) is not null ? ($"{parts[1]}.{parts[2]}", false) : null;

        if (parts.Length >= 3 && string.Equals(parts[0], "ih", StringComparison.Ordinal))
            return GalleryPictures.ChatMediaPathOf(data, parts[1], parts[2]) is not null ? ($"{GalleryPictures.ChatPrefix}{parts[1]}/{parts[2]}", false) : null;

        if (parts.Length >= 2 && string.Equals(parts[0], "ip", StringComparison.Ordinal))
        {
            lock (_previewLock)
                return _previews.ContainsKey(parts[1]) ? ($"preview.{parts[1]}", true) : null;
        }

        return null;
    }

    public Task<UIContent?> ResolveAsync(UIContentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = request.Key;

        if (key.Length >= 3 && key[0] == ImagePrefix)
            return Task.FromResult(ResolveImageContent(key));

        var end = key.IndexOf('/', StringComparison.Ordinal);
        var id = key.Length < 2 ? string.Empty : end < 1 ? key[1..] : key[1..end];

        var path = id.Length == 0 ? null : key[0] switch
        {
            CharacterPrefix => CardPictures.PathOf(data.Characters, id, CardPictures.Avatar),
            PersonaPrefix => CardPictures.PathOf(data.Personas, id, CardPictures.Avatar),
            WorldPrefix => CardPictures.PathOf(data.Worlds, id, CardPictures.Avatar),
            BackgroundPrefix => CardPictures.PathOf(data.Characters, id, CardPictures.Background),
            ChatBackgroundPrefix => ChatPictures.PathOf(data, id),
            AppBackgroundPrefix when id == AppId => PictureFiles.Find(data.Root, CardPictures.Background),
            _ => null
        };

        return Task.FromResult(OpenFileContent(path));
    }

    private UIContent? ResolveImageContent(string key)
    {
        var parts = key.Split('/');

        if (parts.Length >= 2 && string.Equals(parts[0], "ig", StringComparison.Ordinal))
            return OpenFileContent(GalleryPictures.GlobalPathOf(data, parts[1]));

        if (parts.Length >= 3 && string.Equals(parts[0], "ic", StringComparison.Ordinal))
            return OpenFileContent(GalleryPictures.CharacterPathOf(data, parts[1], parts[2]));

        if (parts.Length >= 3 && string.Equals(parts[0], "ih", StringComparison.Ordinal))
            return OpenFileContent(GalleryPictures.ChatMediaPathOf(data, parts[1], parts[2]));

        if (parts.Length >= 2 && string.Equals(parts[0], "ip", StringComparison.Ordinal))
        {
            lock (_previewLock)
            {
                if (_previews.TryGetValue(parts[1], out (byte[] Bytes, string ContentType) preview))
                    return new UIContent { Content = new MemoryStream(preview.Bytes, writable: false), ContentType = preview.ContentType, Immutable = true };
            }
        }

        return null;
    }

    private static UIContent? OpenFileContent(string? path)
        => path is null ? null : new UIContent { Content = File.OpenRead(path), ContentType = ContentTypeOf(path), Immutable = true };

    private static string ContentTypeOf(string path)
        => Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".WEBP" => "image/webp",
            ".JPG" or ".JPEG" => "image/jpeg",
            _ => "image/png"
        };
}

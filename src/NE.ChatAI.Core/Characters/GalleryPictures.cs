using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Chats;
using NE.ChatAI.Settings;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Characters;

/// <summary>An image stored in the global gallery, a character's gallery, or a chat's media folder.</summary>
public sealed record GalleryImage(string Id, string Key, string FileName, string? Tag = null)
{
    /// <summary>The assigned tag when present, else the canonical <see cref="Key"/> (<c>&lt;scope&gt;.&lt;id&gt;</c>).</summary>
    public string CopyKey => !string.IsNullOrEmpty(Tag) ? Tag : Key;
}

/// <summary>Outcome of assigning or clearing a gallery image's alias tag.</summary>
public sealed record GalleryTagResult(string? Tag, bool Conflict, bool Invalid = false);

/// <summary>
/// Manages named gallery images under <c>images/&lt;id&gt;.*</c> (<c>global.&lt;id&gt;</c>),
/// <c>characters/&lt;char-id&gt;/images/&lt;id&gt;.*</c> (<c>&lt;char-id&gt;.&lt;id&gt;</c>), and scenario-generated chat media under
/// <c>chats/&lt;chat-id&gt;/images/&lt;id&gt;.*</c> (<c>chat/&lt;chat-id&gt;/&lt;id&gt;</c>).
/// </summary>
public static class GalleryPictures
{
    public const string GlobalScope = "global";
    public const string ImagesFolderName = "images";
    public const string ChatPrefix = "chat/";

    private const string ChatsFolderName = "chats";

    /// <summary>Lists every image in the global gallery (<c>global.&lt;id&gt;</c>), ordered by slug.</summary>
    public static IReadOnlyList<GalleryImage> ListGlobal(DataFolder data, IReadOnlyDictionary<string, string>? imageTags = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        return ListFolder(GlobalFolderOf(data), GlobalScope, imageTags);
    }

    /// <summary>Lists every image in the global gallery together with its persisted alias tags.</summary>
    public static async Task<IReadOnlyList<GalleryImage>> ListGlobalAsync(DataFolder data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

        return ListGlobal(data, settings.ImageTags);
    }

    private static List<GalleryImage> ListFolder(string folder, string scope, IReadOnlyDictionary<string, string>? imageTags)
    {
        if (!Directory.Exists(folder))
            return [];

        HashSet<string> ids = [with(StringComparer.Ordinal)];

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var ext = Path.GetExtension(file);
            var id = Path.GetFileNameWithoutExtension(file);

            if (IsImageExtension(ext) && Slug.IsValidId(id))
                _ = ids.Add(id);
        }

        if (ids.Count == 0)
            return [];

        List<string> ordered = [.. ids];
        ordered.Sort(StringComparer.Ordinal);
        List<GalleryImage> items = [with(ordered.Count)];

        for (var i = 0; i < ordered.Count; i++)
        {
            var id = ordered[i];

            if (PictureFiles.Find(folder, id) is string chosen)
            {
                var tag = imageTags is not null
                    && imageTags.TryGetValue(id, out var assigned)
                    && Slug.IsValidId(assigned)
                    && !string.Equals(assigned, GlobalScope, StringComparison.OrdinalIgnoreCase)
                    ? assigned
                    : null;
                items.Add(new GalleryImage(id, $"{scope}.{id}", Path.GetFileName(chosen), tag));
            }
        }

        return items;
    }

    /// <summary>Whether <paramref name="extension"/> is one of the supported picture file extensions.</summary>
    public static bool IsImageExtension(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);

        return extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The global gallery directory (<c>data/images</c>).</summary>
    public static string GlobalFolderOf(DataFolder data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return Path.Combine(data.Root, ImagesFolderName);
    }

    /// <summary>Lists every image in <paramref name="characterId"/>'s gallery (<c>&lt;char-id&gt;.&lt;id&gt;</c>), ordered by slug.</summary>
    public static IReadOnlyList<GalleryImage> ListCharacter(DataFolder data, string? characterId, IReadOnlyDictionary<string, string>? imageTags = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        return Slug.IsValidId(characterId) && data.Characters.Exists(characterId!)
            ? ListFolder(CharacterFolderOf(data, characterId!), characterId!, imageTags)
            : [];
    }

    /// <summary>Lists every image in <paramref name="characterId"/>'s gallery together with its persisted alias tags.</summary>
    public static async Task<IReadOnlyList<GalleryImage>> ListCharacterAsync(DataFolder data, string? characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!Slug.IsValidId(characterId))
            return [];

        Character? character = await data.Characters.ReadAsync(characterId!, cancellationToken).ConfigureAwait(false);

        return character is null
            ? []
            : ListFolder(CharacterFolderOf(data, characterId!), characterId!, character.ImageTags);
    }

    /// <summary>The character gallery directory (<c>data/characters/&lt;id&gt;/images</c>).</summary>
    public static string CharacterFolderOf(DataFolder data, string characterId)
    {
        ArgumentNullException.ThrowIfNull(data);

        return Path.Combine(data.Characters.FolderOf(characterId), ImagesFolderName);
    }

    /// <summary>The file path of <paramref name="imageId"/> in the global gallery, or <c>null</c> when absent.</summary>
    public static string? GlobalPathOf(DataFolder data, string? imageId)
    {
        ArgumentNullException.ThrowIfNull(data);

        return Slug.IsValidId(imageId) ? PictureFiles.Find(GlobalFolderOf(data), imageId!) : null;
    }

    /// <summary>The file path of <paramref name="imageId"/> in <paramref name="characterId"/>'s gallery, or <c>null</c> when absent.</summary>
    public static string? CharacterPathOf(DataFolder data, string? characterId, string? imageId)
    {
        ArgumentNullException.ThrowIfNull(data);

        return Slug.IsValidId(characterId) && Slug.IsValidId(imageId) && data.Characters.Exists(characterId!)
            ? PictureFiles.Find(CharacterFolderOf(data, characterId!), imageId!)
            : null;
    }

    /// <summary>The file path of scenario-generated <paramref name="imageId"/> in <paramref name="chatId"/>'s media folder, or <c>null</c> when absent.</summary>
    public static string? ChatMediaPathOf(DataFolder data, string? chatId, string? imageId)
    {
        ArgumentNullException.ThrowIfNull(data);

        return Slug.IsValidId(chatId) && Slug.IsValidId(imageId) && data.Chats.Exists(chatId!)
            ? PictureFiles.Find(ChatFolderOf(data, chatId!), imageId!)
            : null;
    }

    /// <summary>The chat media directory (<c>data/chats/&lt;id&gt;/images</c>).</summary>
    public static string ChatFolderOf(DataFolder data, string chatId)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!Slug.IsValidId(chatId))
            throw new ArgumentException($"'{chatId}' is not a chat id.", nameof(chatId));

        return Path.Combine(data.Root, ChatsFolderName, chatId, ImagesFolderName);
    }

    /// <summary>Stores a new picture in the global gallery under a collision-free slug derived from <paramref name="preferredName"/>.</summary>
    public static async Task<GalleryImage> WriteGlobalAsync(DataFolder data, string? preferredName, ReadOnlyMemory<byte> picture, string? contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        HashSet<string> reserved = await CollectReservedGlobalSlugsAsync(data, cancellationToken).ConfigureAwait(false);

        return await WriteGalleryAsync(GlobalFolderOf(data), GlobalScope, preferredName, picture, contentType, reserved, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HashSet<string>> CollectReservedGlobalSlugsAsync(DataFolder data, CancellationToken cancellationToken)
    {
        HashSet<string> reserved = [with(StringComparer.Ordinal), GlobalScope];
        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        AddTagValues(reserved, settings.ImageTags);
        DocumentListing<Character> characters = await data.Characters.ListAsync(cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < characters.Items.Count; i++)
            AddTagValues(reserved, characters.Items[i].Document.ImageTags);

        return reserved;
    }

    private static void AddTagValues(HashSet<string> target, IReadOnlyDictionary<string, string> tags)
    {
        foreach ((_, var tag) in tags)
        {
            if (Slug.IsValidId(tag))
                _ = target.Add(tag);
        }
    }

    private static async Task<GalleryImage> WriteGalleryAsync(string folder, string scope, string? preferredName, ReadOnlyMemory<byte> picture, string? contentType, HashSet<string> reserved, CancellationToken cancellationToken)
    {
        var baseId = NormalizeId(preferredName, scope);
        var id = NextAvailableId(folder, baseId, reserved);
        _ = Directory.CreateDirectory(folder);
        await PictureFiles.WriteAsync(folder, id, picture, contentType, cancellationToken).ConfigureAwait(false);

        return new GalleryImage(id, $"{scope}.{id}", Path.GetFileName(PictureFiles.Find(folder, id)!));
    }

    private static string NormalizeId(string? rawName, string scope)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return "image";

        var candidate = rawName.Trim();
        var ext = Path.GetExtension(candidate);

        if (ext.Length > 0 && IsImageExtension(ext))
            candidate = Path.GetFileNameWithoutExtension(candidate);

        var prefix = scope + ".";

        if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            candidate = candidate[prefix.Length..];

        var slug = Slug.Of(candidate);

        return slug.Length == 0 || string.Equals(slug, GlobalScope, StringComparison.OrdinalIgnoreCase) ? "image" : slug;
    }

    private static string NextAvailableId(string folder, string baseId, HashSet<string> reserved)
    {
        if (!reserved.Contains(baseId) && (!Directory.Exists(folder) || PictureFiles.Find(folder, baseId) is null))
            return baseId;

        for (var suffix = 2; ; suffix++)
        {
            var tag = "-" + suffix.ToString(CultureInfo.InvariantCulture);
            var trimmed = baseId.Length > 40 ? baseId[..40].TrimEnd('-') : baseId;
            var candidate = trimmed + tag;

            if (!reserved.Contains(candidate) && (!Directory.Exists(folder) || PictureFiles.Find(folder, candidate) is null))
                return candidate;
        }
    }

    /// <summary>Stores a new picture in <paramref name="characterId"/>'s gallery under a collision-free slug derived from <paramref name="preferredName"/>.</summary>
    public static async Task<GalleryImage> WriteCharacterAsync(DataFolder data, string characterId, string? preferredName, ReadOnlyMemory<byte> picture, string? contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!Slug.IsValidId(characterId) || !data.Characters.Exists(characterId))
            throw new ArgumentException($"'{characterId}' is not an existing character id.", nameof(characterId));

        HashSet<string> reserved = await CollectReservedCharacterSlugsAsync(data, characterId, cancellationToken).ConfigureAwait(false);

        return await WriteGalleryAsync(CharacterFolderOf(data, characterId), characterId, preferredName, picture, contentType, reserved, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HashSet<string>> CollectReservedCharacterSlugsAsync(DataFolder data, string characterId, CancellationToken cancellationToken)
    {
        HashSet<string> reserved = [with(StringComparer.Ordinal), GlobalScope];
        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        AddTagValues(reserved, settings.ImageTags);

        if (await data.Characters.ReadAsync(characterId, cancellationToken).ConfigureAwait(false) is Character character)
            AddTagValues(reserved, character.ImageTags);

        return reserved;
    }

    /// <summary>Replaces the file of <paramref name="imageId"/> in the global gallery while keeping its slug id and assigned tag.</summary>
    public static async Task<GalleryImage?> ReplaceGlobalAsync(DataFolder data, string imageId, ReadOnlyMemory<byte> picture, string? contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

        return await ReplaceInFolderAsync(GlobalFolderOf(data), GlobalScope, imageId, picture, contentType, settings.ImageTags, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<GalleryImage?> ReplaceInFolderAsync(string folder, string scope, string imageId, ReadOnlyMemory<byte> picture, string? contentType, IReadOnlyDictionary<string, string>? imageTags, CancellationToken cancellationToken)
    {
        if (!Slug.IsValidId(imageId) || PictureFiles.Find(folder, imageId) is not string existingPath)
            return null;

        var previousTicks = File.GetLastWriteTimeUtc(existingPath).Ticks;
        await PictureFiles.WriteAsync(folder, imageId, picture, contentType, cancellationToken).ConfigureAwait(false);

        if (PictureFiles.Find(folder, imageId) is not string replacedPath)
            return null;

        // Guarantee a strictly newer write timestamp so versioned content URLs always invalidate client caches.
        var updatedTicks = File.GetLastWriteTimeUtc(replacedPath).Ticks;

        if (updatedTicks <= previousTicks)
            File.SetLastWriteTimeUtc(replacedPath, new DateTime(previousTicks + 1, DateTimeKind.Utc));

        var tag = imageTags is not null
            && imageTags.TryGetValue(imageId, out var assigned)
            && Slug.IsValidId(assigned)
            && !string.Equals(assigned, GlobalScope, StringComparison.OrdinalIgnoreCase)
            ? assigned
            : null;

        return new GalleryImage(imageId, $"{scope}.{imageId}", Path.GetFileName(replacedPath), tag);
    }

    /// <summary>Replaces the file of <paramref name="imageId"/> in <paramref name="characterId"/>'s gallery while keeping its slug id and assigned tag.</summary>
    public static async Task<GalleryImage?> ReplaceCharacterAsync(DataFolder data, string characterId, string imageId, ReadOnlyMemory<byte> picture, string? contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!Slug.IsValidId(characterId) || await data.Characters.ReadAsync(characterId, cancellationToken).ConfigureAwait(false) is not Character character)
            return null;

        return await ReplaceInFolderAsync(CharacterFolderOf(data, characterId), characterId, imageId, picture, contentType, character.ImageTags, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Assigns or clears an alias tag on <paramref name="imageId"/> in the global gallery, enforcing uniqueness across the global gallery and every character.</summary>
    public static async Task<GalleryTagResult> SetGlobalTagAsync(DataFolder data, string imageId, string? rawTag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!Slug.IsValidId(imageId) || GlobalPathOf(data, imageId) is null)
            return new GalleryTagResult(null, Conflict: false, Invalid: true);

        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        var folder = GlobalFolderOf(data);

        if (string.IsNullOrWhiteSpace(rawTag))
        {
            await data.WriteSettingsAsync(settings with { ImageTags = BuildUpdatedTags(folder, settings.ImageTags, imageId, null) }, cancellationToken).ConfigureAwait(false);
            return new GalleryTagResult(null, Conflict: false);
        }

        if (NormalizeTag(rawTag, GlobalScope) is not string normalized)
            return new GalleryTagResult(null, Conflict: false, Invalid: true);

        IReadOnlyList<GalleryImage> globalImages = ListGlobal(data, settings.ImageTags);

        for (var i = 0; i < globalImages.Count; i++)
        {
            GalleryImage existing = globalImages[i];

            if (!string.Equals(existing.Id, imageId, StringComparison.Ordinal)
                && (string.Equals(existing.Id, normalized, StringComparison.Ordinal) || string.Equals(existing.Tag, normalized, StringComparison.Ordinal)))
            {
                return new GalleryTagResult(null, Conflict: true);
            }
        }

        DocumentListing<Character> characters = await data.Characters.ListAsync(cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < characters.Items.Count; i++)
        {
            StoredDocument<Character> stored = characters.Items[i];
            IReadOnlyList<GalleryImage> charImages = ListCharacter(data, stored.Id, stored.Document.ImageTags);

            for (var j = 0; j < charImages.Count; j++)
            {
                GalleryImage charImage = charImages[j];

                if (string.Equals(charImage.Id, normalized, StringComparison.Ordinal)
                    || string.Equals(charImage.Tag, normalized, StringComparison.Ordinal))
                {
                    return new GalleryTagResult(null, Conflict: true);
                }
            }
        }

        await data.WriteSettingsAsync(settings with { ImageTags = BuildUpdatedTags(folder, settings.ImageTags, imageId, normalized) }, cancellationToken).ConfigureAwait(false);

        return new GalleryTagResult(normalized, Conflict: false);
    }

    private static IReadOnlyDictionary<string, string> BuildUpdatedTags(string folder, IReadOnlyDictionary<string, string> current, string targetImageId, string? newTag)
    {
        SortedDictionary<string, string> updated = [with(StringComparer.Ordinal)];

        foreach ((var id, var tag) in current)
        {
            if (!string.Equals(id, targetImageId, StringComparison.Ordinal)
                && Slug.IsValidId(id)
                && Slug.IsValidId(tag)
                && !string.Equals(tag, GlobalScope, StringComparison.OrdinalIgnoreCase)
                && PictureFiles.Find(folder, id) is not null)
            {
                updated[id] = tag;
            }
        }

        if (newTag is not null)
            updated[targetImageId] = newTag;

        return updated.Count == 0 ? FrozenDictionary<string, string>.Empty : updated;
    }

    private static string? NormalizeTag(string rawTag, string scope)
    {
        var candidate = rawTag.Trim();
        var prefix = scope + ".";

        if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            candidate = candidate[prefix.Length..];

        var slug = Slug.Of(candidate);

        return slug.Length == 0 || string.Equals(slug, GlobalScope, StringComparison.OrdinalIgnoreCase) ? null : slug;
    }

    /// <summary>Assigns or clears an alias tag on <paramref name="imageId"/> in <paramref name="characterId"/>'s gallery, enforcing uniqueness across that character and the global gallery.</summary>
    public static async Task<GalleryTagResult> SetCharacterTagAsync(DataFolder data, string characterId, string imageId, string? rawTag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!Slug.IsValidId(characterId)
            || !Slug.IsValidId(imageId)
            || CharacterPathOf(data, characterId, imageId) is null
            || await data.Characters.ReadAsync(characterId, cancellationToken).ConfigureAwait(false) is not Character character)
        {
            return new GalleryTagResult(null, Conflict: false, Invalid: true);
        }

        var folder = CharacterFolderOf(data, characterId);

        if (string.IsNullOrWhiteSpace(rawTag))
        {
            await data.Characters.WriteAsync(characterId, character with { ImageTags = BuildUpdatedTags(folder, character.ImageTags, imageId, null) }, cancellationToken).ConfigureAwait(false);
            return new GalleryTagResult(null, Conflict: false);
        }

        if (NormalizeTag(rawTag, characterId) is not string normalized)
            return new GalleryTagResult(null, Conflict: false, Invalid: true);

        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<GalleryImage> globalImages = ListGlobal(data, settings.ImageTags);

        for (var i = 0; i < globalImages.Count; i++)
        {
            GalleryImage globalImage = globalImages[i];

            if (string.Equals(globalImage.Id, normalized, StringComparison.Ordinal)
                || string.Equals(globalImage.Tag, normalized, StringComparison.Ordinal))
            {
                return new GalleryTagResult(null, Conflict: true);
            }
        }

        IReadOnlyList<GalleryImage> charImages = ListCharacter(data, characterId, character.ImageTags);

        for (var i = 0; i < charImages.Count; i++)
        {
            GalleryImage existing = charImages[i];

            if (!string.Equals(existing.Id, imageId, StringComparison.Ordinal)
                && (string.Equals(existing.Id, normalized, StringComparison.Ordinal) || string.Equals(existing.Tag, normalized, StringComparison.Ordinal)))
            {
                return new GalleryTagResult(null, Conflict: true);
            }
        }

        await data.Characters.WriteAsync(characterId, character with { ImageTags = BuildUpdatedTags(folder, character.ImageTags, imageId, normalized) }, cancellationToken).ConfigureAwait(false);

        return new GalleryTagResult(normalized, Conflict: false);
    }

    /// <summary>Deletes <paramref name="imageId"/> from the global gallery and removes any tag assigned to it.</summary>
    public static async Task DeleteGlobalAsync(DataFolder data, string imageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        DeleteGlobal(data, imageId);
        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

        if (settings.ImageTags.Count > 0)
            await data.WriteSettingsAsync(settings with { ImageTags = BuildUpdatedTags(GlobalFolderOf(data), settings.ImageTags, imageId, null) }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes <paramref name="imageId"/> from the global gallery.</summary>
    public static void DeleteGlobal(DataFolder data, string imageId)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (Slug.IsValidId(imageId))
            PictureFiles.Delete(GlobalFolderOf(data), imageId);
    }

    /// <summary>Deletes <paramref name="imageId"/> from <paramref name="characterId"/>'s gallery and removes any tag assigned to it.</summary>
    public static async Task DeleteCharacterAsync(DataFolder data, string characterId, string imageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        DeleteCharacter(data, characterId, imageId);

        if (Slug.IsValidId(characterId)
            && await data.Characters.ReadAsync(characterId, cancellationToken).ConfigureAwait(false) is Character character
            && character.ImageTags.Count > 0)
        {
            await data.Characters.WriteAsync(characterId, character with { ImageTags = BuildUpdatedTags(CharacterFolderOf(data, characterId), character.ImageTags, imageId, null) }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Deletes <paramref name="imageId"/> from <paramref name="characterId"/>'s gallery.</summary>
    public static void DeleteCharacter(DataFolder data, string characterId, string imageId)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (Slug.IsValidId(characterId) && Slug.IsValidId(imageId) && data.Characters.Exists(characterId))
            PictureFiles.Delete(CharacterFolderOf(data, characterId), imageId);
    }

    /// <summary>Stores a scenario-generated image in <paramref name="chatId"/>'s media folder and returns its descriptor.</summary>
    public static async Task<GalleryImage> WriteChatMediaAsync(DataFolder data, string chatId, string? preferredName, ReadOnlyMemory<byte> picture, string? contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!Slug.IsValidId(chatId))
            throw new ArgumentException($"'{chatId}' is not a chat id.", nameof(chatId));

        var folder = ChatFolderOf(data, chatId);
        var stem = string.IsNullOrWhiteSpace(preferredName) ? "image" : Path.GetFileNameWithoutExtension(preferredName);
        string id;

        do
            id = Slug.NewId(stem);
        while (PictureFiles.Find(folder, id) is not null);

        _ = Directory.CreateDirectory(folder);
        await PictureFiles.WriteAsync(folder, id, picture, contentType, cancellationToken).ConfigureAwait(false);

        return new GalleryImage(id, $"{ChatPrefix}{chatId}/{id}", Path.GetFileName(PictureFiles.Find(folder, id)!));
    }

    /// <summary>Copies any chat-scoped media referenced by <paramref name="visuals"/> into <paramref name="targetChatId"/>'s media folder and rewrites their keys.</summary>
    public static IReadOnlyList<ChatVisual> CopyReferencedChatMedia(DataFolder data, IReadOnlyList<ChatVisual> visuals, string targetChatId)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(visuals);

        if (visuals.Count == 0 || !Slug.IsValidId(targetChatId))
            return visuals;

        List<ChatVisual>? updated = null;

        for (var i = 0; i < visuals.Count; i++)
        {
            ChatVisual visual = visuals[i];

            if (!TryParseChatMediaKey(visual.Image, out var sourceChatId, out var imageId))
            {
                updated?.Add(visual);
                continue;
            }

            updated ??= CopyVisualPrefix(visuals, i);

            if (ChatMediaPathOf(data, sourceChatId, imageId) is string sourcePath)
            {
                var targetFolder = ChatFolderOf(data, targetChatId);
                _ = Directory.CreateDirectory(targetFolder);
                PictureFiles.Delete(targetFolder, imageId);
                File.Copy(sourcePath, Path.Combine(targetFolder, imageId + Path.GetExtension(sourcePath).ToLowerInvariant()), overwrite: true);
                updated.Add(visual with { Image = $"{ChatPrefix}{targetChatId}/{imageId}" });
            }
            else
            {
                updated.Add(visual);
            }
        }

        return updated ?? visuals;
    }

    private static bool TryParseChatMediaKey(string? key, out string chatId, out string imageId)
    {
        chatId = string.Empty;
        imageId = string.Empty;

        if (key is null || !key.StartsWith(ChatPrefix, StringComparison.Ordinal))
            return false;

        var rest = key[ChatPrefix.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);

        if (slash <= 0 || slash >= rest.Length - 1)
            return false;

        var candidateChat = rest[..slash];
        var candidateImage = rest[(slash + 1)..];

        if (!Slug.IsValidId(candidateChat) || !Slug.IsValidId(candidateImage))
            return false;

        chatId = candidateChat;
        imageId = candidateImage;
        return true;
    }

    private static List<ChatVisual> CopyVisualPrefix(IReadOnlyList<ChatVisual> visuals, int count)
    {
        List<ChatVisual> copy = [with(visuals.Count)];

        for (var i = 0; i < count; i++)
            copy.Add(visuals[i]);

        return copy;
    }

    /// <summary>Deletes <paramref name="chatId"/>'s media folder when the chat is removed.</summary>
    public static void DeleteChatMedia(DataFolder data, string chatId)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!Slug.IsValidId(chatId))
            return;

        var chatFolder = Path.Combine(data.Root, ChatsFolderName, chatId);

        if (Directory.Exists(chatFolder))
            Directory.Delete(chatFolder, recursive: true);
    }

    /// <summary>Resolves <paramref name="rawKey"/> against persisted character and global gallery tags and ids.</summary>
    public static async Task<(string Key, string Path)?> ResolveAsync(DataFolder data, string? rawKey, string? characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        Character? character = Slug.IsValidId(characterId)
            ? await data.Characters.ReadAsync(characterId!, cancellationToken).ConfigureAwait(false)
            : null;

        return Resolve(data, rawKey, characterId, character?.ImageTags, settings.ImageTags);
    }

    /// <summary>
    /// Resolves <paramref name="rawKey"/> (<c>global.&lt;id|tag&gt;</c>, <c>&lt;char-id&gt;.&lt;id|tag&gt;</c>, <c>chat/&lt;chat-id&gt;/&lt;id&gt;</c>,
    /// or bare <c>&lt;id|tag&gt;</c> checking <paramref name="characterId"/> first then <c>global</c>) to its canonical key and file path.
    /// </summary>
    public static (string Key, string Path)? Resolve(DataFolder data, string? rawKey, string? characterId, IReadOnlyDictionary<string, string>? characterTags = null, IReadOnlyDictionary<string, string>? globalTags = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (string.IsNullOrWhiteSpace(rawKey))
            return null;

        var cleaned = rawKey.Trim().Trim('"', '\'', '`', '.').Trim();

        if (cleaned.Length == 0)
            return null;

        if (TryParseChatMediaKey(cleaned, out var chatId, out var mediaId))
            return ChatMediaPathOf(data, chatId, mediaId) is string mediaPath ? ($"{ChatPrefix}{chatId}/{mediaId}", mediaPath) : null;

        var dot = cleaned.IndexOf('.', StringComparison.Ordinal);

        if (dot > 0 && dot < cleaned.Length - 1)
        {
            var rawScope = cleaned[..dot].Trim();
            var rawName = cleaned[(dot + 1)..].Trim();
            var candidate = Slug.IsValidId(rawName) ? rawName : Slug.Of(rawName);

            if (!Slug.IsValidId(candidate))
                return null;

            if (string.Equals(rawScope, GlobalScope, StringComparison.OrdinalIgnoreCase))
            {
                if (GlobalPathOf(data, candidate) is string globalPath)
                    return ($"{GlobalScope}.{candidate}", globalPath);

                return FindImageIdByTag(globalTags, candidate) is string taggedGlobalId && GlobalPathOf(data, taggedGlobalId) is string taggedGlobalPath
                    ? ($"{GlobalScope}.{taggedGlobalId}", taggedGlobalPath)
                    : null;
            }

            var resolvedCharId = Slug.IsValidId(rawScope) ? rawScope : Slug.Of(rawScope);

            if (CharacterPathOf(data, resolvedCharId, candidate) is string charPath)
                return ($"{resolvedCharId}.{candidate}", charPath);

            return string.Equals(resolvedCharId, characterId, StringComparison.Ordinal)
                && FindImageIdByTag(characterTags, candidate) is string taggedCharId
                && CharacterPathOf(data, resolvedCharId, taggedCharId) is string taggedCharPath
                ? ($"{resolvedCharId}.{taggedCharId}", taggedCharPath)
                : null;
        }

        var bareId = Slug.IsValidId(cleaned) ? cleaned : Slug.Of(cleaned);

        if (!Slug.IsValidId(bareId))
            return null;

        if (CharacterPathOf(data, characterId, bareId) is string openCharPath)
            return ($"{characterId}.{bareId}", openCharPath);

        if (characterId is not null
            && FindImageIdByTag(characterTags, bareId) is string charTaggedId
            && CharacterPathOf(data, characterId, charTaggedId) is string charTaggedPath)
        {
            return ($"{characterId}.{charTaggedId}", charTaggedPath);
        }

        if (GlobalPathOf(data, bareId) is string fallbackGlobalPath)
            return ($"{GlobalScope}.{bareId}", fallbackGlobalPath);

        return FindImageIdByTag(globalTags, bareId) is string globalTaggedId && GlobalPathOf(data, globalTaggedId) is string globalTaggedPath
            ? ($"{GlobalScope}.{globalTaggedId}", globalTaggedPath)
            : null;
    }

    private static string? FindImageIdByTag(IReadOnlyDictionary<string, string>? tags, string candidate)
    {
        if (tags is null || tags.Count == 0)
            return null;

        foreach ((var imageId, var tag) in tags)
        {
            if (string.Equals(tag, candidate, StringComparison.Ordinal) && Slug.IsValidId(imageId))
                return imageId;
        }

        return null;
    }
}

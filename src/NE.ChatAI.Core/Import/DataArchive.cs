using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Characters;
using NE.ChatAI.Chats;
using NE.ChatAI.Settings;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Import;

/// <summary>Which parts of the data folder an export archive carries.</summary>
[Flags]
public enum DataArchiveScopes
{
    None = 0,
    Settings = 1 << 0,
    Content = 1 << 1,
    Chats = 1 << 2,
    All = Settings | Content | Chats
}

/// <summary>What an imported data archive wrote into the data folder.</summary>
public sealed record DataArchiveImportSummary(bool Settings, int Presets, int Characters, int Personas, int Worlds, int Chats)
{
    public bool IsEmpty => !Settings && Presets == 0 && Characters == 0 && Personas == 0 && Worlds == 0 && Chats == 0;
}

/// <summary>Packs selected parts of a <see cref="DataFolder"/> into a <c>.zip</c> archive and restores them back in place.</summary>
public static class DataArchive
{
    public const string ManifestFile = "chatai-export.json";
    public const long MaxExpandedBytes = 256L * 1024 * 1024;

    private const string SettingsFile = "settings.json";
    private const string ConnectionsFolder = "connections";
    private const string FormatsFolder = "instruct";
    private const string SystemPromptsFolder = "sysprompts";
    private const string PromptsFolder = "prompts";
    private const string SamplersFolder = "samplers";
    private const string ScenariosFolder = "graphs";
    private const string CharactersFolder = "characters";
    private const string PersonasFolder = "personas";
    private const string WorldsFolder = "worlds";
    private const string ChatsFolder = "chats";

    /// <summary>Whether <paramref name="content"/> starts with a ZIP local file or empty archive signature.</summary>
    public static bool IsZip(ReadOnlySpan<byte> content)
        => content.Length >= 4
            && content[0] == (byte)'P'
            && content[1] == (byte)'K'
            && ((content[2] == 3 && content[3] == 4) || (content[2] == 5 && content[3] == 6));

    /// <summary>Packs the selected <paramref name="scopes"/> of <paramref name="data"/> into a <c>.zip</c> archive.</summary>
    public static async Task<byte[]> ExportAsync(DataFolder data, DataArchiveScopes scopes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        using MemoryStream buffer = new();

        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ArchiveManifest manifest = new()
            {
                Format = Document.CurrentFormat,
                Settings = scopes.HasFlag(DataArchiveScopes.Settings),
                Content = scopes.HasFlag(DataArchiveScopes.Content),
                Chats = scopes.HasFlag(DataArchiveScopes.Chats)
            };

            await WriteJsonEntryAsync(archive, ManifestFile, manifest, cancellationToken).ConfigureAwait(false);

            if (scopes != DataArchiveScopes.None)
            {
                AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
                await WriteJsonEntryAsync(archive, SettingsFile, settings with { Format = Document.CurrentFormat, PinHash = null }, cancellationToken).ConfigureAwait(false);
            }

            if (scopes.HasFlag(DataArchiveScopes.Settings))
            {
                await AddRootBackgroundAsync(archive, data.Root, cancellationToken).ConfigureAwait(false);
                await AddStoreFilesAsync(archive, data.Root, ConnectionsFolder, cancellationToken).ConfigureAwait(false);
                await AddStoreFilesAsync(archive, data.Root, FormatsFolder, cancellationToken).ConfigureAwait(false);
                await AddStoreFilesAsync(archive, data.Root, SystemPromptsFolder, cancellationToken).ConfigureAwait(false);
                await AddStoreFilesAsync(archive, data.Root, PromptsFolder, cancellationToken).ConfigureAwait(false);
                await AddStoreFilesAsync(archive, data.Root, SamplersFolder, cancellationToken).ConfigureAwait(false);
                await AddStoreFilesAsync(archive, data.Root, ScenariosFolder, cancellationToken).ConfigureAwait(false);
            }

            if (scopes.HasFlag(DataArchiveScopes.Content))
            {
                await AddCardFoldersAsync(archive, data.Root, CharactersFolder, "character", cancellationToken).ConfigureAwait(false);
                await AddCardFoldersAsync(archive, data.Root, PersonasFolder, "persona", cancellationToken).ConfigureAwait(false);
                await AddCardFoldersAsync(archive, data.Root, WorldsFolder, "world", cancellationToken).ConfigureAwait(false);
            }

            if (scopes.HasFlag(DataArchiveScopes.Chats))
                await AddChatFilesAsync(archive, data, cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private static async Task WriteJsonEntryAsync<T>(ZipArchive archive, string entryPath, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, DocumentJson.Options);
        ZipArchiveEntry entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        Stream output = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (output.ConfigureAwait(false))
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddRootBackgroundAsync(ZipArchive archive, string root, CancellationToken cancellationToken)
    {
        if (PictureFiles.Find(root, CardPictures.Background) is string path)
            await AddFileEntryAsync(archive, Path.GetFileName(path), path, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddFileEntryAsync(ZipArchive archive, string entryPath, string filePath, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        ZipArchiveEntry entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        Stream output = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (output.ConfigureAwait(false))
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddStoreFilesAsync(ZipArchive archive, string root, string folderName, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(root, folderName);

        if (!Directory.Exists(directory))
            return;

        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(static path => path, StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(file);

            if (Slug.IsValidId(id))
                await AddFileEntryAsync(archive, $"{folderName}/{id}.json", file, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AddCardFoldersAsync(ZipArchive archive, string root, string folderName, string documentName, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(root, folderName);

        if (!Directory.Exists(directory))
            return;

        var documentFileName = documentName + ".json";

        foreach (var folder in Directory.EnumerateDirectories(directory).OrderBy(static path => path, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(folder);

            if (!Slug.IsValidId(id) || !File.Exists(Path.Combine(folder, documentFileName)))
                continue;

            foreach (var file in Directory.EnumerateFiles(folder).OrderBy(static path => path, StringComparer.Ordinal))
            {
                var fileName = Path.GetFileName(file);

                if (IsAllowedCardFile(fileName, documentFileName))
                    await AddFileEntryAsync(archive, $"{folderName}/{id}/{fileName}", file, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsAllowedCardFile(string fileName, string documentFileName)
    {
        if (string.Equals(fileName, documentFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, CardImport.SourcePng, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, CardImport.SourceJson, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);

        return (string.Equals(stem, CardPictures.Avatar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(stem, CardPictures.Background, StringComparison.OrdinalIgnoreCase))
            && IsPictureExtension(ext);
    }

    private static bool IsPictureExtension(string extension)
        => extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

    private static async Task AddChatFilesAsync(ZipArchive archive, DataFolder data, CancellationToken cancellationToken)
    {
        await AddStoreFilesAsync(archive, data.Root, ChatsFolder, cancellationToken).ConfigureAwait(false);

        var directory = Path.Combine(data.Root, ChatsFolder);

        if (!Directory.Exists(directory))
            return;

        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(static path => path, StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(file);

            if (Slug.IsValidId(id) && ChatPictures.PathOf(data, id) is string picturePath)
                await AddFileEntryAsync(archive, $"{ChatsFolder}/{Path.GetFileName(picturePath)}", picturePath, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Restores the documents, pictures and settings inside <paramref name="archiveBytes"/> into <paramref name="data"/>, overwriting matching ids.</summary>
    public static async Task<DataArchiveImportSummary> ImportAsync(DataFolder data, ReadOnlyMemory<byte> archiveBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        Dictionary<string, byte[]> entries = StripCommonRootFolder(await ReadEntriesAsync(archiveBytes, cancellationToken).ConfigureAwait(false));
        ArchiveManifest? explicitManifest = entries.TryGetValue(ManifestFile, out var manifestBytes) ? TryDeserialize<ArchiveManifest>(manifestBytes) : null;

        if (entries.ContainsKey(ManifestFile) && explicitManifest is null)
            return new DataArchiveImportSummary(false, 0, 0, 0, 0, 0);

        AppSettings? incomingSettings = entries.TryGetValue(SettingsFile, out var settingsBytes) ? TryDeserialize<AppSettings>(settingsBytes) : null;
        ArchiveManifest manifest = explicitManifest ?? new ArchiveManifest
        {
            Settings = incomingSettings is not null,
            Content = true,
            Chats = true
        };

        (string FileName, byte[] Content)? rootBackground = null;
        var presets = 0;
        var characters = 0;
        var personas = 0;
        var worlds = 0;
        var chats = 0;
        HashSet<string> validCards = [with(StringComparer.Ordinal)];
        List<(string Folder, string Id, string FileName, byte[] Content)> companionFiles = [];
        List<(string Id, string Ext, byte[] Content)> chatPictures = [];

        foreach ((var path, var content) in entries.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (path is ManifestFile or SettingsFile)
                continue;

            var parts = path.Split('/');

            if (parts.Length == 1)
            {
                var stem = Path.GetFileNameWithoutExtension(parts[0]);
                var ext = Path.GetExtension(parts[0]);

                if (manifest.Settings && string.Equals(stem, CardPictures.Background, StringComparison.OrdinalIgnoreCase) && IsPictureExtension(ext))
                    rootBackground = (CardPictures.Background + ext.ToLowerInvariant(), content);

                continue;
            }

            if (parts.Length == 2 && parts[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var id = Path.GetFileNameWithoutExtension(parts[1]);

                if (!Slug.IsValidId(id))
                    continue;

                if (parts[0] == ChatsFolder)
                {
                    if (manifest.Chats && await ImportChatEntryAsync(data, id, content, cancellationToken).ConfigureAwait(false))
                        chats++;
                }
                else if (manifest.Settings && await ImportPresetEntryAsync(data, parts[0], id, content, cancellationToken).ConfigureAwait(false))
                {
                    presets++;
                }

                continue;
            }

            if (parts.Length == 2 && parts[0] == ChatsFolder && manifest.Chats)
            {
                var id = Path.GetFileNameWithoutExtension(parts[1]);
                var ext = Path.GetExtension(parts[1]);

                if (Slug.IsValidId(id) && IsPictureExtension(ext))
                    chatPictures.Add((id, ext.ToLowerInvariant(), content));

                continue;
            }

            if (parts.Length == 3 && manifest.Content && Slug.IsValidId(parts[1]))
            {
                (var imported, var kind) = await ImportCardDocumentAsync(data, parts[0], parts[1], parts[2], content, cancellationToken).ConfigureAwait(false);

                if (imported)
                {
                    _ = validCards.Add($"{parts[0]}/{parts[1]}");

                    if (kind == CharactersFolder)
                        characters++;
                    else if (kind == PersonasFolder)
                        personas++;
                    else if (kind == WorldsFolder)
                        worlds++;
                }
                else
                {
                    companionFiles.Add((parts[0], parts[1], parts[2], content));
                }
            }
        }

        foreach ((var folder, var id, var fileName, var content) in companionFiles)
        {
            if (validCards.Contains($"{folder}/{id}") || CardExists(data, folder, id))
                await ImportCardFileAsync(data, folder, id, fileName, content, cancellationToken).ConfigureAwait(false);
        }

        foreach ((var id, var ext, var content) in chatPictures)
        {
            if (data.Chats.Exists(id))
            {
                ChatPictures.Delete(data, id);
                await AtomicFile.WriteAsync(Path.Combine(data.Root, ChatsFolder, id + ext), content, cancellationToken).ConfigureAwait(false);
            }
        }

        var settingsApplied = await ApplySettingsAsync(data, manifest, incomingSettings, rootBackground, cancellationToken).ConfigureAwait(false);

        return new DataArchiveImportSummary(settingsApplied, presets, characters, personas, worlds, chats);
    }

    private static async Task<Dictionary<string, byte[]>> ReadEntriesAsync(ReadOnlyMemory<byte> archiveBytes, CancellationToken cancellationToken)
    {
        Dictionary<string, byte[]> entries = [with(StringComparer.Ordinal)];

        try
        {
            using MemoryStream input = new(archiveBytes.ToArray(), writable: false);
            using ZipArchive archive = new(input, ZipArchiveMode.Read, leaveOpen: false);
            long totalBytes = 0;

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (NormalizeEntryPath(entry.FullName) is not string path)
                    continue;

                Stream stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);

                await using (stream.ConfigureAwait(false))
                {
                    using MemoryStream buffer = new();
                    var chunk = new byte[8192];
                    int read;

                    while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        totalBytes += read;

                        if (totalBytes > MaxExpandedBytes)
                            return [];

                        await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }

                    entries[path] = buffer.ToArray();
                }
            }
        }
        catch (InvalidDataException)
        {
            return [];
        }

        return entries;
    }

    private static string? NormalizeEntryPath(string fullName)
    {
        var normalized = fullName.Replace('\\', '/').Trim('/');

        if (normalized.Length == 0 || fullName.EndsWith('/', StringComparison.Ordinal) || fullName.EndsWith('\\', StringComparison.Ordinal) || normalized.Contains(':', StringComparison.Ordinal))
            return null;

        var parts = normalized.Split('/');

        return parts.Any(static part => part.Length == 0 || part is "." or "..") ? null : normalized;
    }

    private static Dictionary<string, byte[]> StripCommonRootFolder(Dictionary<string, byte[]> entries)
    {
        if (entries.Count == 0 || entries.Keys.Any(static path => IsKnownRootSegment(path.Split('/')[0])))
            return entries;

        var firstSegments = entries.Keys.Select(static path => path.Split('/')[0]).Distinct(StringComparer.Ordinal).ToArray();

        if (firstSegments.Length != 1)
            return entries;

        var prefix = firstSegments[0] + "/";
        Dictionary<string, byte[]> stripped = [with(StringComparer.Ordinal)];

        foreach ((var path, var content) in entries)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal) && path.Length > prefix.Length)
                stripped[path[prefix.Length..]] = content;
        }

        return stripped;
    }

    private static bool IsKnownRootSegment(string segment)
        => segment is ManifestFile or SettingsFile or ConnectionsFolder or FormatsFolder or SystemPromptsFolder or PromptsFolder or SamplersFolder or ScenariosFolder or CharactersFolder or PersonasFolder or WorldsFolder or ChatsFolder
            || (string.Equals(Path.GetFileNameWithoutExtension(segment), CardPictures.Background, StringComparison.OrdinalIgnoreCase) && IsPictureExtension(Path.GetExtension(segment)));

    private static T? TryDeserialize<T>(byte[] content) where T : Document
    {
        try
        {
            T? doc = JsonSerializer.Deserialize<T>(content, DocumentJson.Options);

            return doc is not null && doc.Format <= Document.CurrentFormat ? doc : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task<bool> ImportPresetEntryAsync(DataFolder data, string folder, string id, byte[] content, CancellationToken cancellationToken)
        => folder switch
        {
            ConnectionsFolder => WriteVerifiedAsync(data.Connections, id, content, cancellationToken),
            FormatsFolder => WriteVerifiedAsync(data.ModelFormats, id, content, cancellationToken),
            SystemPromptsFolder => WriteVerifiedAsync(data.SystemPrompts, id, content, cancellationToken),
            PromptsFolder => WriteVerifiedAsync(data.Prompts, id, content, cancellationToken),
            SamplersFolder => WriteVerifiedAsync(data.Samplers, id, content, cancellationToken),
            ScenariosFolder => WriteVerifiedAsync(data.Scenarios, id, content, cancellationToken),
            _ => Task.FromResult(false)
        };

    private static async Task<bool> WriteVerifiedAsync<T>(DocumentStore<T> store, string id, byte[] content, CancellationToken cancellationToken) where T : NamedDocument
    {
        if (TryDeserialize<T>(content) is not T document)
            return false;

        await store.WriteAsync(id, document, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<(bool Imported, string? Kind)> ImportCardDocumentAsync(DataFolder data, string folder, string id, string fileName, byte[] content, CancellationToken cancellationToken)
    {
        if (folder == CharactersFolder && string.Equals(fileName, "character.json", StringComparison.OrdinalIgnoreCase))
            return (await WriteVerifiedAsync(data.Characters, id, content, cancellationToken).ConfigureAwait(false), CharactersFolder);

        if (folder == PersonasFolder && string.Equals(fileName, "persona.json", StringComparison.OrdinalIgnoreCase))
            return (await WriteVerifiedAsync(data.Personas, id, content, cancellationToken).ConfigureAwait(false), PersonasFolder);

        if (folder == WorldsFolder && string.Equals(fileName, "world.json", StringComparison.OrdinalIgnoreCase))
            return (await WriteVerifiedAsync(data.Worlds, id, content, cancellationToken).ConfigureAwait(false), WorldsFolder);

        return (false, null);
    }

    private static bool CardExists(DataFolder data, string folder, string id)
        => folder switch
        {
            CharactersFolder => data.Characters.Exists(id),
            PersonasFolder => data.Personas.Exists(id),
            WorldsFolder => data.Worlds.Exists(id),
            _ => false
        };

    private static async Task ImportCardFileAsync(DataFolder data, string folder, string id, string fileName, byte[] content, CancellationToken cancellationToken)
    {
        var cardFolder = folder switch
        {
            CharactersFolder when IsAllowedCardFile(fileName, "character.json") && !string.Equals(fileName, "character.json", StringComparison.OrdinalIgnoreCase) => data.Characters.FolderOf(id),
            PersonasFolder when IsAllowedCardFile(fileName, "persona.json") && !string.Equals(fileName, "persona.json", StringComparison.OrdinalIgnoreCase) => data.Personas.FolderOf(id),
            WorldsFolder when IsAllowedCardFile(fileName, "world.json") && !string.Equals(fileName, "world.json", StringComparison.OrdinalIgnoreCase) => data.Worlds.FolderOf(id),
            _ => null
        };

        if (cardFolder is null)
            return;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        if (string.Equals(stem, CardPictures.Avatar, StringComparison.OrdinalIgnoreCase))
        {
            PictureFiles.Delete(cardFolder, CardPictures.Avatar);
            await AtomicFile.WriteAsync(Path.Combine(cardFolder, CardPictures.Avatar + ext), content, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(stem, CardPictures.Background, StringComparison.OrdinalIgnoreCase))
        {
            PictureFiles.Delete(cardFolder, CardPictures.Background);
            await AtomicFile.WriteAsync(Path.Combine(cardFolder, CardPictures.Background + ext), content, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(fileName, CardImport.SourcePng, StringComparison.OrdinalIgnoreCase) || string.Equals(fileName, CardImport.SourceJson, StringComparison.OrdinalIgnoreCase))
        {
            await AtomicFile.WriteAsync(Path.Combine(cardFolder, fileName.ToLowerInvariant()), content, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<bool> ImportChatEntryAsync(DataFolder data, string id, byte[] content, CancellationToken cancellationToken)
        => WriteVerifiedAsync(data.Chats, id, content, cancellationToken);

    private static async Task<bool> ApplySettingsAsync(DataFolder data, ArchiveManifest manifest, AppSettings? incoming, (string FileName, byte[] Content)? background, CancellationToken cancellationToken)
    {
        if (background is (var fileName, var bytes))
        {
            PictureFiles.Delete(data.Root, CardPictures.Background);
            await AtomicFile.WriteAsync(Path.Combine(data.Root, fileName), bytes, cancellationToken).ConfigureAwait(false);
        }

        if (incoming is null)
            return background is not null;

        AppSettings current = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        var mergeTagStyles = manifest.Settings || manifest.Content;
        IReadOnlyList<TagStyle> tags = mergeTagStyles ? MergeTags(current.Tags, incoming.Tags) : current.Tags;
        IReadOnlyList<TagStyle> personaTags = mergeTagStyles ? MergeTags(current.PersonaTags, incoming.PersonaTags) : current.PersonaTags;
        IReadOnlyList<TagStyle> worldTags = mergeTagStyles ? MergeTags(current.WorldTags, incoming.WorldTags) : current.WorldTags;

        if (manifest.Settings)
        {
            AppSettings updated = incoming with
            {
                Format = Document.CurrentFormat,
                Connection = incoming.Connection ?? current.Connection,
                Persona = incoming.Persona ?? current.Persona,
                LastChat = incoming.LastChat ?? current.LastChat,
                PinHash = current.PinHash,
                Tags = tags,
                PersonaTags = personaTags,
                WorldTags = worldTags
            };

            await data.WriteSettingsAsync(updated, cancellationToken).ConfigureAwait(false);
            return true;
        }

        AppSettings merged = current with
        {
            Persona = manifest.Content ? current.Persona ?? incoming.Persona : current.Persona,
            LastChat = manifest.Chats ? incoming.LastChat ?? current.LastChat : current.LastChat,
            Tags = tags,
            PersonaTags = personaTags,
            WorldTags = worldTags
        };

        await data.WriteSettingsAsync(merged, cancellationToken).ConfigureAwait(false);
        return false;
    }

    private static IReadOnlyList<TagStyle> MergeTags(IReadOnlyList<TagStyle> current, IReadOnlyList<TagStyle> incoming)
    {
        Dictionary<string, TagStyle> merged = [with(StringComparer.OrdinalIgnoreCase)];

        foreach (TagStyle tag in current)
        {
            if (!string.IsNullOrWhiteSpace(tag.Name))
                merged[tag.Name] = tag;
        }

        foreach (TagStyle tag in incoming)
        {
            if (string.IsNullOrWhiteSpace(tag.Name))
                continue;

            if (merged.TryGetValue(tag.Name, out TagStyle? existing))
                merged[tag.Name] = new TagStyle(existing.Name, tag.Color ?? existing.Color);
            else
                merged[tag.Name] = tag;
        }

        return [.. merged.Values];
    }

    private sealed record ArchiveManifest : Document
    {
        public bool Settings { get; init; }

        public bool Content { get; init; }

        public bool Chats { get; init; }
    }
}

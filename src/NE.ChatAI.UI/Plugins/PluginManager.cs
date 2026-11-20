using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Scenarios;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Plugins;

internal enum PluginErrorKind
{
    None,
    InvalidRepository,
    ReleaseNotFound,
    AssetMissing,
    ManifestInvalid,
    VersionIncompatible,
    LoadFailed,
    DuplicateNodeKey,
    NetworkFailed
}

internal sealed record PluginOperationResult(bool Succeeded, PluginStatus? Plugin = null, PluginErrorKind Error = PluginErrorKind.None, string? Detail = null);

internal sealed record PluginUpdateCheckResult(int UpdatesAvailable, int FailedCount);

/// <summary>Loads, installs, hot-updates, toggles, and uninstalls scenario plugins under <c>data/plugins/</c> and rebuilds <see cref="Catalog"/> live.</summary>
internal sealed class PluginManager : IDisposable
{
    public const string HttpClientName = "NE.ChatAI.Plugins";
    public const long MaxArchiveBytes = 64 * 1024 * 1024;
    private const long MaxExtractedBytes = MaxArchiveBytes * 2;
    private const long MaxManifestBytes = 256 * 1024;

    private const string PluginsFolderName = "plugins";
    private const string ManifestFileName = "plugin.json";
    private const string StateFileName = "plugins.json";

    private readonly DataFolder _data;
    private readonly IHttpClientFactory _httpClients;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly Dictionary<string, LoadedPluginHandle> _loaded = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, string> _loadErrors = [with(StringComparer.Ordinal)];
    private List<InstalledPluginRecord> _records = [];
    private UINodeCatalog _catalog = ScenarioCatalog.BuiltIn;
    private bool _disposed;

    public event Action<UINodeCatalog>? CatalogChanged;

    public UINodeCatalog Catalog
    {
        get
        {
            lock (_stateLock)
                return _catalog;
        }
    }

    public PluginManager(DataFolder data, IHttpClientFactory httpClients, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(httpClients);
        ArgumentNullException.ThrowIfNull(time);

        _data = data;
        _httpClients = httpClients;
        _time = time;

        InitializeFromDisk();
    }

    private void InitializeFromDisk()
    {
        List<InstalledPluginRecord> records = ReadStateFile();

        lock (_stateLock)
        {
            _records = records;
            RebuildCatalogLocked();
        }
    }

    private List<InstalledPluginRecord> ReadStateFile()
    {
        var statePath = StateFilePath();

        if (!File.Exists(statePath))
            return [];

        try
        {
            using FileStream stream = new(statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);

            if (DocumentJson.TryRead<InstalledPluginsDocument>(bytes)?.Plugins is not { Count: > 0 } items)
                return [];

            List<InstalledPluginRecord> valid = [with(items.Count)];
            HashSet<string> seen = [with(StringComparer.Ordinal)];

            for (var i = 0; i < items.Count; i++)
            {
                InstalledPluginRecord item = items[i];

                if (!Slug.IsValidId(item.Id) || string.IsNullOrWhiteSpace(item.InstalledVersion) || string.IsNullOrWhiteSpace(item.EntryAssembly) || !seen.Add(item.Id))
                    continue;

                IReadOnlyList<string> tags = PluginCoordinates.NormalizeTags(item.Tags);

                if (tags.Count == 0 && ReadPackageManifest(VersionDirectoryOf(item.Id, item.InstalledVersion)) is { Tags.Count: > 0 } packageManifest)
                    tags = PluginCoordinates.NormalizeTags(packageManifest.Tags);

                valid.Add(item with { Tags = tags });
            }

            return valid;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private string StateFilePath()
        => Path.Combine(PluginsRoot(), StateFileName);

    private string PluginsRoot()
        => Path.Combine(_data.Root, PluginsFolderName);

    private static PluginManifest? ReadPackageManifest(string packageRoot)
    {
        var manifestPath = Path.Combine(packageRoot, ManifestFileName);

        return File.Exists(manifestPath) ? DeserializeManifest(File.ReadAllBytes(manifestPath)) : null;
    }

    private static PluginManifest? DeserializeManifest(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(DocumentJson.StripBom(utf8Json), DocumentJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string VersionDirectoryOf(string pluginId, string version)
        => Path.Combine(PluginDirectoryOf(pluginId), PluginCoordinates.VersionFolderName(version));

    private string PluginDirectoryOf(string pluginId)
        => Path.Combine(PluginsRoot(), pluginId);

    private void RebuildCatalogLocked()
    {
        List<Type> combinedTypes = [.. ScenarioCatalog.BuiltInTypes];
        HashSet<string> activeKeys = new(ScenarioCatalog.BuiltInKeys, StringComparer.Ordinal);
        _loadErrors.Clear();

        List<InstalledPluginRecord> ordered = OrderRecords(_records);

        for (var i = 0; i < ordered.Count; i++)
        {
            InstalledPluginRecord record = ordered[i];

            if (!record.Enabled)
            {
                UnloadHandleLocked(record.Id);
                continue;
            }

            if (!_loaded.TryGetValue(record.Id, out LoadedPluginHandle? handle) || !string.Equals(handle.Version, record.InstalledVersion, StringComparison.Ordinal))
            {
                UnloadHandleLocked(record.Id);
                var versionDir = VersionDirectoryOf(record.Id, record.InstalledVersion);
                (LoadedPluginHandle? loadedHandle, _, var loadError) = TryLoadPlugin(record.Id, record.InstalledVersion, versionDir, record.EntryAssembly, activeKeys);

                if (loadedHandle is null)
                {
                    if (loadError is not null)
                        _loadErrors[record.Id] = loadError;

                    continue;
                }

                handle = loadedHandle;
                _loaded[record.Id] = handle;
            }
            else if (HasKeyConflict(handle.Nodes, activeKeys, out var conflictKey))
            {
                _loadErrors[record.Id] = $"Duplicate node key '{conflictKey}'.";
                continue;
            }

            for (var n = 0; n < handle.Nodes.Count; n++)
            {
                _ = activeKeys.Add(handle.Nodes[n].Summary.Key);
                combinedTypes.Add(handle.Nodes[n].NodeType);
            }
        }

        _catalog = combinedTypes.Count == ScenarioCatalog.BuiltInTypes.Count
            ? ScenarioCatalog.BuiltIn
            : UINodeCatalog.FromTypes([.. combinedTypes]);
    }

    private static List<InstalledPluginRecord> OrderRecords(List<InstalledPluginRecord> source)
    {
        List<InstalledPluginRecord> copy = [.. source];
        copy.Sort(static (left, right) =>
        {
            var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

            return byName != 0 ? byName : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
        });

        return copy;
    }

    private void UnloadHandleLocked(string pluginId)
    {
        if (_loaded.Remove(pluginId, out LoadedPluginHandle? existing))
            existing.Context.Unload();
    }

    private static (LoadedPluginHandle? Handle, PluginErrorKind Error, string? Detail) TryLoadPlugin(string expectedId, string version, string versionDir, string entryAssemblyName, HashSet<string> reservedKeys)
    {
        if (!Directory.Exists(versionDir))
            return (null, PluginErrorKind.LoadFailed, $"Plugin directory for {expectedId} ({version}) is missing.");

        PluginLoadContext loadContext = new($"plugin:{expectedId}:{version}:{Guid.CreateVersion7():N}", versionDir);

        try
        {
            Assembly assembly = loadContext.LoadEntryAssembly(entryAssemblyName);
            List<Type> rawNodeTypes = DiscoverNodeTypes(assembly, expectedId);

            if (rawNodeTypes.Count == 0)
            {
                loadContext.Unload();

                return (null, PluginErrorKind.LoadFailed, "Plugin assembly defines no public [GraphNode] classes.");
            }

            List<DiscoveredPluginNode> discovered = [with(rawNodeTypes.Count)];
            HashSet<string> localKeys = [with(StringComparer.Ordinal)];

            for (var i = 0; i < rawNodeTypes.Count; i++)
            {
                Type candidate = rawNodeTypes[i];

                if (candidate.IsAbstract || candidate.IsGenericTypeDefinition || candidate.GetConstructor(Type.EmptyTypes) is null)
                {
                    loadContext.Unload();

                    return (null, PluginErrorKind.LoadFailed, $"Node type '{candidate.FullName}' must be a concrete class with a public parameterless constructor.");
                }

                GraphNodeAttribute? attr = candidate.GetCustomAttribute<GraphNodeAttribute>(inherit: false);

                if (attr is null || string.IsNullOrWhiteSpace(attr.Key))
                {
                    loadContext.Unload();

                    return (null, PluginErrorKind.LoadFailed, $"Node type '{candidate.FullName}' is missing [GraphNode(Key = ...)].");
                }

                if (!localKeys.Add(attr.Key) || reservedKeys.Contains(attr.Key))
                {
                    loadContext.Unload();

                    return (null, PluginErrorKind.DuplicateNodeKey, attr.Key);
                }

                var title = string.IsNullOrWhiteSpace(attr.Title) ? candidate.Name : attr.Title;
                var category = string.IsNullOrWhiteSpace(attr.Category) ? "Plugins" : attr.Category;
                discovered.Add(new DiscoveredPluginNode(candidate, new PluginNodeSummary(attr.Key, title, category)));
            }

            // Validates every pin/attribute signature before committing the plugin to the live catalogue.
            _ = UINodeCatalog.FromTypes([.. rawNodeTypes]);

            return (new LoadedPluginHandle(version, loadContext, discovered), PluginErrorKind.None, null);
        }
        catch (Exception exception)
        {
            loadContext.Unload();

            return (null, PluginErrorKind.LoadFailed, exception.Message);
        }
    }

    private static List<Type> DiscoverNodeTypes(Assembly assembly, string expectedId)
    {
        Type[] exported = assembly.GetExportedTypes();
        List<Type> pluginEntryTypes = [];

        for (var i = 0; i < exported.Length; i++)
        {
            Type type = exported[i];

            if (!type.IsAbstract && !type.IsInterface && typeof(IChatPlugin).IsAssignableFrom(type))
                pluginEntryTypes.Add(type);
        }

        if (pluginEntryTypes.Count > 0)
        {
            List<Type> declared = [];

            for (var i = 0; i < pluginEntryTypes.Count; i++)
            {
                Type entryType = pluginEntryTypes[i];

                if (entryType.GetConstructor(Type.EmptyTypes) is null)
                    throw new InvalidOperationException($"Plugin entry '{entryType.FullName}' must have a public parameterless constructor.");

                IChatPlugin instance = (IChatPlugin)Activator.CreateInstance(entryType)!;

                if (!string.IsNullOrWhiteSpace(instance.Id) && !string.Equals(instance.Id, expectedId, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Plugin entry id '{instance.Id}' does not match manifest id '{expectedId}'.");

                if (instance.NodeTypes is not null)
                {
                    for (var n = 0; n < instance.NodeTypes.Count; n++)
                    {
                        if (instance.NodeTypes[n] is Type nodeType)
                            declared.Add(nodeType);
                    }
                }
            }

            return declared;
        }

        List<Type> attributed = [];

        for (var i = 0; i < exported.Length; i++)
        {
            Type type = exported[i];

            if (!type.IsAbstract && !type.IsInterface && type.GetCustomAttribute<GraphNodeAttribute>(inherit: false) is not null)
                attributed.Add(type);
        }

        return attributed;
    }

    private static bool HasKeyConflict(IReadOnlyList<DiscoveredPluginNode> nodes, HashSet<string> activeKeys, out string conflictKey)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            var key = nodes[i].Summary.Key;

            if (activeKeys.Contains(key))
            {
                conflictKey = key;
                return true;
            }
        }

        conflictKey = string.Empty;
        return false;
    }

    public IReadOnlyList<PluginStatus> List()
    {
        lock (_stateLock)
        {
            List<InstalledPluginRecord> ordered = OrderRecords(_records);
            List<PluginStatus> result = [with(ordered.Count)];

            for (var i = 0; i < ordered.Count; i++)
                result.Add(BuildStatusLocked(ordered[i]));

            return result;
        }
    }

    private PluginStatus BuildStatusLocked(InstalledPluginRecord record)
    {
        IReadOnlyList<PluginNodeSummary> nodes = _loaded.TryGetValue(record.Id, out LoadedPluginHandle? handle)
            ? handle.Summaries
            : [];
        _ = _loadErrors.TryGetValue(record.Id, out var error);

        return new PluginStatus(record, nodes, error);
    }

    public async Task<PluginOperationResult> InstallAsync(string repositoryInput, CancellationToken cancellationToken)
    {
        if (PluginCoordinates.NormalizeRepository(repositoryInput) is not string repository)
            return new PluginOperationResult(false, Error: PluginErrorKind.InvalidRepository);

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await InstallOrUpdateFromGitHubCoreAsync(repository, expectedPluginId: null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _mutationGate.Release();
        }
    }

    private async Task<PluginOperationResult> InstallOrUpdateFromGitHubCoreAsync(string repository, string? expectedPluginId, CancellationToken cancellationToken)
    {
        (GitHubReleaseInfo? release, PluginErrorKind releaseError, var errorDetail) = await FetchLatestReleaseAsync(repository, cancellationToken).ConfigureAwait(false);

        if (release is null)
            return new PluginOperationResult(false, Error: releaseError, Detail: errorDetail);

        if (release.ZipAssetUrl is null)
            return new PluginOperationResult(false, Error: PluginErrorKind.AssetMissing, Detail: ".zip");

        PluginManifest? externalManifest = null;

        if (release.ManifestAssetUrl is not null)
        {
            (var manifestBytes, PluginErrorKind dlErr, var dlDetail) = await DownloadBytesAsync(release.ManifestAssetUrl, MaxManifestBytes, cancellationToken).ConfigureAwait(false);

            if (manifestBytes is null)
                return new PluginOperationResult(false, Error: dlErr, Detail: dlDetail);

            externalManifest = DeserializeManifest(manifestBytes);

            if (externalManifest is null)
                return new PluginOperationResult(false, Error: PluginErrorKind.ManifestInvalid, Detail: ManifestFileName);
        }

        (var archiveBytes, PluginErrorKind zipErr, var zipDetail) = await DownloadBytesAsync(release.ZipAssetUrl, MaxArchiveBytes, cancellationToken).ConfigureAwait(false);

        if (archiveBytes is null)
            return new PluginOperationResult(false, Error: zipErr, Detail: zipDetail);

        using MemoryStream archiveStream = new(archiveBytes, writable: false);

        return await InstallArchiveCoreAsync(repository, archiveStream, release.TagName, externalManifest, expectedPluginId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(GitHubReleaseInfo? Release, PluginErrorKind Error, string? Detail)> FetchLatestReleaseAsync(string repository, CancellationToken cancellationToken)
    {
        HttpClient client = CreateGitHubClient();

        try
        {
            using HttpResponseMessage response = await client.GetAsync(new Uri($"repos/{repository}/releases/latest", UriKind.Relative), cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return await FetchFirstNonDraftReleaseAsync(client, repository, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return (null, PluginErrorKind.NetworkFailed, $"HTTP {(int)response.StatusCode}");

            Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            await using (body.ConfigureAwait(false))
            {
                using JsonDocument doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);

                return ParseReleaseElement(doc.RootElement);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (null, PluginErrorKind.NetworkFailed, exception.Message);
        }
    }

    private HttpClient CreateGitHubClient()
    {
        HttpClient client = _httpClients.CreateClient(HttpClientName);
        client.BaseAddress ??= new Uri("https://api.github.com/");

        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NE.ChatAI/1.0");

        if (client.DefaultRequestHeaders.Accept.Count == 0)
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        return client;
    }

    private static async Task<(GitHubReleaseInfo? Release, PluginErrorKind Error, string? Detail)> FetchFirstNonDraftReleaseAsync(HttpClient client, string repository, CancellationToken cancellationToken)
    {
        using HttpResponseMessage listResponse = await client.GetAsync(new Uri($"repos/{repository}/releases?per_page=6", UriKind.Relative), cancellationToken).ConfigureAwait(false);

        if (listResponse.StatusCode == HttpStatusCode.NotFound)
            return (null, PluginErrorKind.ReleaseNotFound, repository);

        if (!listResponse.IsSuccessStatusCode)
            return (null, PluginErrorKind.NetworkFailed, $"HTTP {(int)listResponse.StatusCode}");

        Stream body = await listResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await using (body.ConfigureAwait(false))
        {
            using JsonDocument doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("draft", out JsonElement draftProp) && draftProp.ValueKind == JsonValueKind.True)
                        continue;

                    return ParseReleaseElement(item);
                }
            }
        }

        return (null, PluginErrorKind.ReleaseNotFound, repository);
    }

    private static (GitHubReleaseInfo? Release, PluginErrorKind Error, string? Detail) ParseReleaseElement(JsonElement release)
    {
        var tagName = release.TryGetProperty("tag_name", out JsonElement tagProp) && tagProp.ValueKind == JsonValueKind.String
            ? tagProp.GetString() ?? string.Empty
            : string.Empty;

        Uri? zipUrl = null;
        Uri? manifestUrl = null;

        if (release.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out JsonElement nameProp) && nameProp.ValueKind == JsonValueKind.String
                    ? nameProp.GetString()
                    : null;
                var urlText = asset.TryGetProperty("browser_download_url", out JsonElement urlProp) && urlProp.ValueKind == JsonValueKind.String
                    ? urlProp.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(name) || !Uri.TryCreate(urlText, UriKind.Absolute, out Uri? assetUri))
                    continue;

                if (name.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    manifestUrl = assetUri;
                else if (zipUrl is null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    zipUrl = assetUri;
            }
        }

        return (new GitHubReleaseInfo(tagName, zipUrl, manifestUrl), PluginErrorKind.None, null);
    }

    private async Task<(byte[]? Bytes, PluginErrorKind Error, string? Detail)> DownloadBytesAsync(Uri url, long maxBytes, CancellationToken cancellationToken)
    {
        HttpClient client = CreateGitHubClient();

        try
        {
            using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return (null, PluginErrorKind.NetworkFailed, $"HTTP {(int)response.StatusCode}");

            if (response.Content.Headers.ContentLength is long length && length > maxBytes)
                return (null, PluginErrorKind.ManifestInvalid, $"Plugin download exceeds the {FormatLimit(maxBytes)} limit.");

            Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            await using (stream.ConfigureAwait(false))
            {
                using MemoryStream buffer = new();
                var chunk = new byte[16384];
                long total = 0;

                while (true)
                {
                    var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);

                    if (read == 0)
                        break;

                    total += read;

                    if (total > maxBytes)
                        return (null, PluginErrorKind.ManifestInvalid, $"Plugin download exceeds the {FormatLimit(maxBytes)} limit.");

                    await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                return (buffer.ToArray(), PluginErrorKind.None, null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (null, PluginErrorKind.NetworkFailed, exception.Message);
        }
    }

    private static string FormatLimit(long maxBytes)
        => maxBytes >= 1024 * 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{maxBytes / (1024 * 1024)} MB")
            : string.Create(CultureInfo.InvariantCulture, $"{maxBytes / 1024} KB");

    private async Task<PluginOperationResult> InstallArchiveCoreAsync(string repository, Stream archiveStream, string? fallbackTagVersion, PluginManifest? externalManifest, string? expectedPluginId, CancellationToken cancellationToken)
    {
        var stagingRoot = Path.Combine(PluginsRoot(), ".staging-" + Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture));
        _ = Directory.CreateDirectory(stagingRoot);

        try
        {
            ExtractArchiveSafely(archiveStream, stagingRoot);
            var packageRoot = UnwrapSingleRootFolder(stagingRoot);
            PluginManifest? manifest = ReadPackageManifest(packageRoot) ?? externalManifest;

            if (manifest is null)
                return new PluginOperationResult(false, Error: PluginErrorKind.ManifestInvalid, Detail: ManifestFileName);

            var pluginId = manifest.Id.Trim();

            if (!Slug.IsValidId(pluginId))
                return new PluginOperationResult(false, Error: PluginErrorKind.ManifestInvalid, Detail: "id");

            if (expectedPluginId is not null && !string.Equals(pluginId, expectedPluginId, StringComparison.Ordinal))
                return new PluginOperationResult(false, Error: PluginErrorKind.ManifestInvalid, Detail: $"Expected '{expectedPluginId}', got '{pluginId}'.");

            if (string.IsNullOrWhiteSpace(manifest.Name))
                return new PluginOperationResult(false, Error: PluginErrorKind.ManifestInvalid, Detail: "name");

            var version = PluginCoordinates.NormalizeVersion(!string.IsNullOrWhiteSpace(manifest.Version) ? manifest.Version : fallbackTagVersion);

            if (string.IsNullOrWhiteSpace(version))
                return new PluginOperationResult(false, Error: PluginErrorKind.ManifestInvalid, Detail: "version");

            var entryAssembly = manifest.EntryAssembly.Trim();

            if (!IsSafeDllFileName(entryAssembly))
                return new PluginOperationResult(false, Error: PluginErrorKind.ManifestInvalid, Detail: "entryAssembly");

            if (!IsCompatibleWithHost(manifest.MinAppVersion))
                return new PluginOperationResult(false, Error: PluginErrorKind.VersionIncompatible, Detail: manifest.MinAppVersion);

            HashSet<string> keysExcludingSelf;

            lock (_stateLock)
                keysExcludingSelf = CollectActiveKeysExceptLocked(pluginId);

            (LoadedPluginHandle? verifiedHandle, PluginErrorKind verifyError, var verifyDetail) = TryLoadPlugin(pluginId, version, packageRoot, entryAssembly, keysExcludingSelf);

            if (verifiedHandle is null)
                return new PluginOperationResult(false, Error: verifyError, Detail: verifyDetail);

            verifiedHandle.Context.Unload();

            var targetDir = VersionDirectoryOf(pluginId, version);
            _ = Directory.CreateDirectory(PluginDirectoryOf(pluginId));

            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, recursive: true);

            Directory.Move(packageRoot, targetDir);

            PluginStatus status;
            List<InstalledPluginRecord> snapshotToPersist;
            UINodeCatalog updatedCatalog;
            string? oldVersionToDelete = null;

            lock (_stateLock)
            {
                var existingIndex = FindRecordIndex(_records, pluginId);
                InstalledPluginRecord? previous = existingIndex >= 0 ? _records[existingIndex] : null;

                if (previous is not null && !string.Equals(PluginCoordinates.VersionFolderName(previous.InstalledVersion), PluginCoordinates.VersionFolderName(version), StringComparison.Ordinal))
                    oldVersionToDelete = VersionDirectoryOf(pluginId, previous.InstalledVersion);

                var latestVersion = previous?.LatestVersion is string knownLatest && PluginCoordinates.CompareVersions(knownLatest, version) > 0
                    ? knownLatest
                    : version;

                InstalledPluginRecord updated = new()
                {
                    Id = pluginId,
                    Name = manifest.Name.Trim(),
                    Description = string.IsNullOrWhiteSpace(manifest.Description) ? null : manifest.Description.Trim(),
                    Author = string.IsNullOrWhiteSpace(manifest.Author) ? null : manifest.Author.Trim(),
                    Repository = PluginCoordinates.NormalizeRepository(manifest.Repository) ?? repository,
                    InstalledVersion = version,
                    LatestVersion = latestVersion,
                    EntryAssembly = entryAssembly,
                    Tags = PluginCoordinates.NormalizeTags(manifest.Tags),
                    Enabled = previous?.Enabled ?? true,
                    InstalledAt = previous?.InstalledAt ?? _time.GetUtcNow()
                };

                if (existingIndex >= 0)
                    _records[existingIndex] = updated;
                else
                    _records.Add(updated);

                UnloadHandleLocked(pluginId);
                RebuildCatalogLocked();
                updatedCatalog = _catalog;
                status = BuildStatusLocked(updated);
                snapshotToPersist = [.. _records];
            }

            if (oldVersionToDelete is not null && Directory.Exists(oldVersionToDelete))
                TryDeleteDirectory(oldVersionToDelete);

            await WriteStateFileAsync(snapshotToPersist, cancellationToken).ConfigureAwait(false);
            CatalogChanged?.Invoke(updatedCatalog);

            return new PluginOperationResult(true, status);
        }
        catch (InvalidDataException exception)
        {
            return new PluginOperationResult(false, Error: PluginErrorKind.ManifestInvalid, Detail: exception.Message);
        }
        catch (IOException exception)
        {
            return new PluginOperationResult(false, Error: PluginErrorKind.ManifestInvalid, Detail: exception.Message);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
                TryDeleteDirectory(stagingRoot);
        }
    }

    private static void ExtractArchiveSafely(Stream archiveStream, string destinationDirectory)
    {
        using ZipArchive archive = new(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        var rootFull = Path.GetFullPath(destinationDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var buffer = new byte[16384];
        long extractedBytes = 0;

        for (var i = 0; i < archive.Entries.Count; i++)
        {
            ZipArchiveEntry entry = archive.Entries[i];

            if (string.IsNullOrEmpty(entry.FullName))
                continue;

            var targetPath = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));

            if (!targetPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Archive contains an entry outside the target directory.");

            if (entry.FullName[^1] is '/' or '\\')
            {
                _ = Directory.CreateDirectory(targetPath);
                continue;
            }

            if (entry.Length > MaxExtractedBytes - extractedBytes)
                throw new InvalidDataException("Extracted plugin files exceed the size limit.");

            _ = Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using Stream source = entry.Open();
            using FileStream target = File.Create(targetPath);

            while (true)
            {
                var read = source.Read(buffer, 0, buffer.Length);

                if (read == 0)
                    break;

                extractedBytes += read;

                if (extractedBytes > MaxExtractedBytes)
                    throw new InvalidDataException("Extracted plugin files exceed the size limit.");

                target.Write(buffer, 0, read);
            }
        }
    }

    private static string UnwrapSingleRootFolder(string stagingRoot)
    {
        if (File.Exists(Path.Combine(stagingRoot, ManifestFileName)))
            return stagingRoot;

        var files = Directory.GetFiles(stagingRoot);
        var dirs = Directory.GetDirectories(stagingRoot);

        return files.Length == 0 && dirs.Length == 1 ? dirs[0] : stagingRoot;
    }

    private static bool IsSafeDllFileName(string name)
        => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            && name.Length > 4
            && name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) < 0
            && !name.Contains("..", StringComparison.Ordinal);

    private static bool IsCompatibleWithHost(string? minAppVersion)
    {
        if (string.IsNullOrWhiteSpace(minAppVersion))
            return true;

        Version? hostVersion = typeof(PluginManager).Assembly.GetName().Version;

        if (hostVersion is null or { Major: 1, Minor: 0, Build: 0, Revision: 0 })
            return true;

        var hostString = $"{hostVersion.Major}.{Math.Max(0, hostVersion.Minor)}.{Math.Max(0, hostVersion.Build)}";

        return PluginCoordinates.CompareVersions(hostString, minAppVersion) >= 0;
    }

    private HashSet<string> CollectActiveKeysExceptLocked(string excludedPluginId)
    {
        HashSet<string> keys = new(ScenarioCatalog.BuiltInKeys, StringComparer.Ordinal);

        foreach ((var id, LoadedPluginHandle handle) in _loaded)
        {
            if (string.Equals(id, excludedPluginId, StringComparison.Ordinal))
                continue;

            for (var i = 0; i < handle.Nodes.Count; i++)
                _ = keys.Add(handle.Nodes[i].Summary.Key);
        }

        return keys;
    }

    private static int FindRecordIndex(List<InstalledPluginRecord> records, string pluginId)
    {
        for (var i = 0; i < records.Count; i++)
        {
            if (string.Equals(records[i].Id, pluginId, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private Task WriteStateFileAsync(IReadOnlyList<InstalledPluginRecord> records, CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(PluginsRoot());
        InstalledPluginsDocument doc = new()
        {
            Format = Document.CurrentFormat,
            Plugins = records
        };

        return AtomicFile.WriteAsync(StateFilePath(), JsonSerializer.SerializeToUtf8Bytes(doc, DocumentJson.Options), cancellationToken);
    }

    internal async Task<PluginOperationResult> InstallArchiveAsync(string repository, Stream archiveStream, string? fallbackTagVersion, PluginManifest? externalManifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);

        if (PluginCoordinates.NormalizeRepository(repository) is not string normalizedRepo)
            return new PluginOperationResult(false, Error: PluginErrorKind.InvalidRepository);

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await InstallArchiveCoreAsync(normalizedRepo, archiveStream, fallbackTagVersion, externalManifest, expectedPluginId: null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _mutationGate.Release();
        }
    }

    public async Task<PluginUpdateCheckResult> CheckUpdatesAsync(CancellationToken cancellationToken)
    {
        List<InstalledPluginRecord> snapshot;

        lock (_stateLock)
            snapshot = [.. _records];

        if (snapshot.Count == 0)
            return new PluginUpdateCheckResult(0, 0);

        Dictionary<string, string> discoveredVersions = [with(StringComparer.Ordinal)];
        var failed = 0;

        for (var i = 0; i < snapshot.Count; i++)
        {
            InstalledPluginRecord item = snapshot[i];
            (GitHubReleaseInfo? release, _, _) = await FetchLatestReleaseAsync(item.Repository, cancellationToken).ConfigureAwait(false);

            if (release is null)
            {
                failed++;
                continue;
            }

            var candidateVersion = PluginCoordinates.NormalizeVersion(release.TagName);

            if (release.ManifestAssetUrl is not null)
            {
                (var manifestBytes, _, _) = await DownloadBytesAsync(release.ManifestAssetUrl, MaxManifestBytes, cancellationToken).ConfigureAwait(false);

                if (manifestBytes is not null && DeserializeManifest(manifestBytes) is { Version: { Length: > 0 } manifestVer })
                    candidateVersion = PluginCoordinates.NormalizeVersion(manifestVer);
            }

            if (!string.IsNullOrWhiteSpace(candidateVersion))
                discoveredVersions[item.Id] = candidateVersion;
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            List<InstalledPluginRecord>? updatedToPersist = null;
            var updatesAvailable = 0;

            lock (_stateLock)
            {
                var changed = false;

                for (var i = 0; i < _records.Count; i++)
                {
                    InstalledPluginRecord record = _records[i];

                    if (discoveredVersions.TryGetValue(record.Id, out var latest) && !string.Equals(record.LatestVersion, latest, StringComparison.Ordinal))
                    {
                        record = record with { LatestVersion = latest };
                        _records[i] = record;
                        changed = true;
                    }

                    if (!string.IsNullOrWhiteSpace(record.LatestVersion) && PluginCoordinates.CompareVersions(record.LatestVersion, record.InstalledVersion) > 0)
                        updatesAvailable++;
                }

                if (changed)
                    updatedToPersist = [.. _records];
            }

            if (updatedToPersist is not null)
                await WriteStateFileAsync(updatedToPersist, cancellationToken).ConfigureAwait(false);

            return new PluginUpdateCheckResult(updatesAvailable, failed);
        }
        finally
        {
            _ = _mutationGate.Release();
        }
    }

    public async Task<PluginOperationResult> UpdateAsync(string pluginId, CancellationToken cancellationToken)
    {
        if (!Slug.IsValidId(pluginId))
            return new PluginOperationResult(false, Error: PluginErrorKind.ManifestInvalid);

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string? repository;

            lock (_stateLock)
            {
                var index = FindRecordIndex(_records, pluginId);
                repository = index >= 0 ? _records[index].Repository : null;
            }

            if (repository is null)
                return new PluginOperationResult(false, Error: PluginErrorKind.ReleaseNotFound);

            return await InstallOrUpdateFromGitHubCoreAsync(repository, expectedPluginId: pluginId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _mutationGate.Release();
        }
    }

    public async Task<PluginOperationResult> SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken)
    {
        if (!Slug.IsValidId(pluginId))
            return new PluginOperationResult(false, Error: PluginErrorKind.ManifestInvalid);

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            PluginStatus? status = null;
            List<InstalledPluginRecord>? snapshotToPersist = null;
            UINodeCatalog updatedCatalog;

            lock (_stateLock)
            {
                var index = FindRecordIndex(_records, pluginId);

                if (index < 0)
                    return new PluginOperationResult(false, Error: PluginErrorKind.ReleaseNotFound);

                InstalledPluginRecord updated = _records[index] with { Enabled = enabled };
                _records[index] = updated;
                RebuildCatalogLocked();
                updatedCatalog = _catalog;
                status = BuildStatusLocked(updated);
                snapshotToPersist = [.. _records];
            }

            await WriteStateFileAsync(snapshotToPersist, cancellationToken).ConfigureAwait(false);
            CatalogChanged?.Invoke(updatedCatalog);

            return new PluginOperationResult(true, status);
        }
        finally
        {
            _ = _mutationGate.Release();
        }
    }

    public async Task<bool> UninstallAsync(string pluginId, CancellationToken cancellationToken)
    {
        if (!Slug.IsValidId(pluginId))
            return false;

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            List<InstalledPluginRecord>? snapshotToPersist = null;
            UINodeCatalog updatedCatalog;

            lock (_stateLock)
            {
                var index = FindRecordIndex(_records, pluginId);

                if (index < 0)
                    return false;

                _records.RemoveAt(index);
                UnloadHandleLocked(pluginId);
                _ = _loadErrors.Remove(pluginId);
                RebuildCatalogLocked();
                updatedCatalog = _catalog;
                snapshotToPersist = [.. _records];
            }

            var pluginDir = PluginDirectoryOf(pluginId);

            if (Directory.Exists(pluginDir))
                TryDeleteDirectory(pluginDir);

            await WriteStateFileAsync(snapshotToPersist, cancellationToken).ConfigureAwait(false);
            CatalogChanged?.Invoke(updatedCatalog);

            return true;
        }
        finally
        {
            _ = _mutationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (_stateLock)
        {
            foreach (LoadedPluginHandle handle in _loaded.Values)
                handle.Context.Unload();

            _loaded.Clear();
        }

        _mutationGate.Dispose();
    }

    private sealed record DiscoveredPluginNode(Type NodeType, PluginNodeSummary Summary);

    private sealed class LoadedPluginHandle(string version, PluginLoadContext context, IReadOnlyList<DiscoveredPluginNode> nodes)
    {
        public string Version { get; } = version;

        public PluginLoadContext Context { get; } = context;

        public IReadOnlyList<DiscoveredPluginNode> Nodes { get; } = nodes;

        public IReadOnlyList<PluginNodeSummary> Summaries { get; } = BuildSummaries(nodes);

        private static PluginNodeSummary[] BuildSummaries(IReadOnlyList<DiscoveredPluginNode> items)
        {
            PluginNodeSummary[] result = new PluginNodeSummary[items.Count];

            for (var i = 0; i < items.Count; i++)
                result[i] = items[i].Summary;

            return result;
        }
    }

    private sealed record GitHubReleaseInfo(string TagName, Uri? ZipAssetUrl, Uri? ManifestAssetUrl);
}

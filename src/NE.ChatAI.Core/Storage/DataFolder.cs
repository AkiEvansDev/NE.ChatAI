using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Characters;
using NE.ChatAI.Chats;
using NE.ChatAI.Connections;
using NE.ChatAI.Prompts;
using NE.ChatAI.Samplers;
using NE.ChatAI.Scenarios;
using NE.ChatAI.Settings;

namespace NE.ChatAI.Storage;

/// <summary>The data folder and a store for each kind of document in it (<c>docs/STORAGE.md</c>).</summary>
public sealed class DataFolder : IDisposable
{
    private sealed record CachedSettings(long LastWriteTicks, long Length, AppSettings Settings);

    private const string SettingsFile = "settings.json";

    private readonly SemaphoreSlim _settingsWrite = new(1, 1);
    private readonly Lock _settingsCacheLock = new();
    private CachedSettings? _cachedSettings;

    public DataFolder(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);

        Root = Path.GetFullPath(root);
        _ = Directory.CreateDirectory(Root);
        AtomicFile.SweepTemporaries(Root);

        Connections = new DocumentStore<ConnectionProfile>(Path.Combine(Root, "connections"));
        ModelFormats = new DocumentStore<ModelFormat>(Path.Combine(Root, "instruct"));
        SystemPrompts = new DocumentStore<SystemPromptPreset>(Path.Combine(Root, "sysprompts"));
        Prompts = new DocumentStore<PromptPreset>(Path.Combine(Root, "prompts"));
        Samplers = new DocumentStore<SamplerPreset>(Path.Combine(Root, "samplers"));
        Characters = new DocumentStore<Character>(Path.Combine(Root, "characters"), "character");
        Personas = new DocumentStore<Persona>(Path.Combine(Root, "personas"), "persona");
        Worlds = new DocumentStore<World>(Path.Combine(Root, "worlds"), "world");
        Scenarios = new DocumentStore<Scenario>(Path.Combine(Root, "graphs"));
        Chats = new DocumentStore<Chat>(Path.Combine(Root, "chats"));
    }

    public string Root { get; }

    public DocumentStore<ConnectionProfile> Connections { get; }

    public DocumentStore<ModelFormat> ModelFormats { get; }

    public DocumentStore<SystemPromptPreset> SystemPrompts { get; }

    public DocumentStore<PromptPreset> Prompts { get; }

    public DocumentStore<SamplerPreset> Samplers { get; }

    public DocumentStore<Character> Characters { get; }

    public DocumentStore<Persona> Personas { get; }

    public DocumentStore<World> Worlds { get; }

    public DocumentStore<Scenario> Scenarios { get; }

    public DocumentStore<Chat> Chats { get; }

    /// <summary>The application's settings; the defaults while the file does not exist yet.</summary>
    public async Task<AppSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(Root, SettingsFile);
        FileInfo info = new(path);

        if (!info.Exists)
        {
            lock (_settingsCacheLock)
                _cachedSettings = null;

            return new AppSettings();
        }

        var lastWriteTicks = info.LastWriteTimeUtc.Ticks;
        var length = info.Length;

        lock (_settingsCacheLock)
        {
            if (_cachedSettings is { } cached && cached.LastWriteTicks == lastWriteTicks && cached.Length == length)
                return cached.Settings;
        }

        var content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<byte> utf8 = content.AsSpan();

        if (utf8.StartsWith("\uFEFF"u8))
            utf8 = utf8[3..];

        try
        {
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(utf8, DocumentJson.Options) ?? new AppSettings();

            if (settings.Format > Document.CurrentFormat)
            {
                lock (_settingsCacheLock)
                    _cachedSettings = null;

                throw new DocumentFormatException(path, $"format {settings.Format} is newer than this version writes.");
            }

            var writtenTicks = File.GetLastWriteTimeUtc(path).Ticks;

            lock (_settingsCacheLock)
                _cachedSettings = new CachedSettings(writtenTicks, content.LongLength, settings);

            return settings;
        }
        catch (JsonException exception)
        {
            lock (_settingsCacheLock)
                _cachedSettings = null;

            throw new DocumentFormatException(path, exception.Message, exception);
        }
    }

    public async Task WriteSettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _settingsWrite.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await AtomicFile.WriteAsync(Path.Combine(Root, SettingsFile), JsonSerializer.SerializeToUtf8Bytes(settings with { Format = Document.CurrentFormat }, DocumentJson.Options), cancellationToken).ConfigureAwait(false);

            lock (_settingsCacheLock)
                _cachedSettings = null;
        }
        finally
        {
            _ = _settingsWrite.Release();
        }
    }

    internal void InvalidateCaches()
    {
        lock (_settingsCacheLock)
            _cachedSettings = null;

        Connections.ClearCache();
        ModelFormats.ClearCache();
        SystemPrompts.ClearCache();
        Prompts.ClearCache();
        Samplers.ClearCache();
        Characters.ClearCache();
        Personas.ClearCache();
        Worlds.ClearCache();
        Scenarios.ClearCache();
        Chats.ClearCache();
    }

    public void Dispose()
        => _settingsWrite.Dispose();
}

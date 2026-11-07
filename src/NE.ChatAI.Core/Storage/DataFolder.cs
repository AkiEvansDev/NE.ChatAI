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
    private const string SettingsFile = "settings.json";

    private readonly SemaphoreSlim _settingsWrite = new(1, 1);

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

        if (!File.Exists(path))
            return new AppSettings();

        var content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        try
        {
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(content, DocumentJson.Options) ?? new AppSettings();

            return settings.Format > Document.CurrentFormat ? throw new DocumentFormatException(path, $"format {settings.Format} is newer than this version writes.") : settings;
        }
        catch (JsonException exception)
        {
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
        }
        finally
        {
            _ = _settingsWrite.Release();
        }
    }

    public void Dispose()
        => _settingsWrite.Dispose();
}

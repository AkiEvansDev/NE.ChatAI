using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Prompts;
using NE.ChatAI.Samplers;
using NE.ChatAI.Settings;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Connections;

/// <summary>A connection with the documents it names read: what a generation is made with.</summary>
public sealed record ActiveProfile(string ConnectionId, ConnectionProfile Connection, ModelFormat Format, SystemPromptPreset SystemPrompt, PromptPreset Prompt, SamplerPreset Samplers);

/// <summary>The active connection and its parts, and the documents a first start lays down so a chat works at once.</summary>
public static class Profiles
{
    /// <summary>The field a prompt preset kept its system prompt in before the system prompt became a document of its own.</summary>
    private const string LegacySystemPrompt = "systemPrompt";

    /// <summary>The field a prompt preset kept where author's notes go in before it became the global note's setting.</summary>
    private const string LegacyNotePlacement = "authorsNote";

    /// <summary>
    /// The connection the settings name, with its model format, system prompt, prompt and samplers; a reference to a document that is
    /// gone falls back to the defaults rather than refusing to generate. <c>null</c> when no connection is chosen or it is gone.
    /// </summary>
    public static async Task<ActiveProfile?> LoadActiveAsync(DataFolder data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

        if (settings.Connection is not string id || await data.Connections.ReadAsync(id, cancellationToken).ConfigureAwait(false) is not ConnectionProfile connection)
            return null;

        return await ResolveAsync(data, id, connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves a connection's model format, system prompt, prompt and samplers; missing documents fall back to defaults.</summary>
    public static async Task<ActiveProfile> ResolveAsync(DataFolder data, string id, ConnectionProfile connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(connection);

        ModelFormat format = connection.Instruct is string instruct ? await data.ModelFormats.ReadAsync(instruct, cancellationToken).ConfigureAwait(false) ?? ModelFormat.Gemma4 : ModelFormat.Gemma4;
        SystemPromptPreset system = connection.SystemPrompt is string systemId ? await data.SystemPrompts.ReadAsync(systemId, cancellationToken).ConfigureAwait(false) ?? new SystemPromptPreset() : new SystemPromptPreset();
        PromptPreset prompt = connection.Prompt is string promptId ? await data.Prompts.ReadAsync(promptId, cancellationToken).ConfigureAwait(false) ?? new PromptPreset() : new PromptPreset();
        SamplerPreset samplers = connection.Samplers is string samplersId ? await data.Samplers.ReadAsync(samplersId, cancellationToken).ConfigureAwait(false) ?? new SamplerPreset() : new SamplerPreset();

        return new ActiveProfile(id, connection, format, system, prompt, samplers);
    }

    /// <summary>
    /// On a data folder with no connection yet: the base set of presets (<see cref="DefaultDocuments"/>) and a koboldcpp connection at
    /// <paramref name="koboldCppAddress"/> naming its Gemma 4 format, roleplay prompts and balanced samplers, made the active one. A
    /// folder that has a connection is left as it is, save that what older versions kept inside a prompt preset is moved out.
    /// </summary>
    public static async Task EnsureDefaultsAsync(DataFolder data, Uri koboldCppAddress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        if ((await data.Connections.ListAsync(cancellationToken).ConfigureAwait(false)).Items.Count > 0)
        {
            await MoveSystemPromptsOutAsync(data, cancellationToken).ConfigureAwait(false);
            await MoveNotePlacementOutAsync(data, cancellationToken).ConfigureAwait(false);

            if (!Directory.Exists(Path.Combine(data.Root, "graphs")))
                await DefaultDocuments.LayDownScenariosAsync(data, cancellationToken).ConfigureAwait(false);
            else if (!data.Scenarios.Exists(DefaultDocuments.ImpersonateScenarioId))
                _ = await DefaultDocuments.EnsureScenarioAsync(data, DefaultDocuments.ImpersonateScenarioId, cancellationToken).ConfigureAwait(false);
            else if (await data.Scenarios.ReadAsync(DefaultDocuments.ImpersonateScenarioId, cancellationToken).ConfigureAwait(false) is { System: false } impersonate)
                await data.Scenarios.WriteAsync(DefaultDocuments.ImpersonateScenarioId, impersonate with { System = true }, cancellationToken).ConfigureAwait(false);

            return;
        }

        _ = await DefaultDocuments.LayDownAsync(data, cancellationToken).ConfigureAwait(false);

        var connection = await data.Connections.CreateAsync(new ConnectionProfile
        {
            Name = "koboldcpp",
            Backend = BackendKind.KoboldCpp,
            Address = koboldCppAddress,
            Instruct = DefaultDocuments.FormatId,
            SystemPrompt = DefaultDocuments.SystemPromptId,
            Prompt = DefaultDocuments.PromptId,
            Samplers = DefaultDocuments.SamplersId
        }, cancellationToken).ConfigureAwait(false);

        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

        await data.WriteSettingsAsync(settings with { Connection = connection }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Each prompt preset's own system prompt, from before it was a document of its own, becomes one under the preset's name; every
    /// connection naming the preset and no system prompt is pointed at it, and the preset forgets the field.
    /// </summary>
    public static async Task MoveSystemPromptsOutAsync(DataFolder data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        foreach (StoredDocument<PromptPreset> stored in (await data.Prompts.ListAsync(cancellationToken).ConfigureAwait(false)).Items)
        {
            if (stored.Document.Extra is not { } extra || !extra.TryGetValue(LegacySystemPrompt, out JsonElement legacy))
                continue;

            if (legacy.ValueKind == JsonValueKind.String && legacy.GetString() is { Length: > 0 } content)
            {
                var system = await data.SystemPrompts.CreateAsync(new SystemPromptPreset { Name = stored.Document.Name, Content = content }, cancellationToken).ConfigureAwait(false);

                // Read for each preset: a connection pointed at an earlier one has a system prompt by now.
                foreach (StoredDocument<ConnectionProfile> connection in (await data.Connections.ListAsync(cancellationToken).ConfigureAwait(false)).Items)
                {
                    if (connection.Document.Prompt == stored.Id && connection.Document.SystemPrompt is null)
                        await data.Connections.WriteAsync(connection.Id, connection.Document with { SystemPrompt = system }, cancellationToken).ConfigureAwait(false);
                }
            }

            Dictionary<string, JsonElement> kept = new(extra);
            _ = kept.Remove(LegacySystemPrompt);

            await data.Prompts.WriteAsync(stored.Id, stored.Document with { Extra = kept.Count > 0 ? kept : null }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Where author's notes go, from before it was the global note's setting: the active connection's prompt preset's placement
    /// becomes the settings' — its depth and role, "before the last message" a depth of 1 — and every preset forgets the field.
    /// </summary>
    public static async Task MoveNotePlacementOutAsync(DataFolder data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        ConnectionProfile? active = settings.Connection is string id ? await data.Connections.ReadAsync(id, cancellationToken).ConfigureAwait(false) : null;
        var moved = false;

        foreach (StoredDocument<PromptPreset> stored in (await data.Prompts.ListAsync(cancellationToken).ConfigureAwait(false)).Items)
        {
            if (stored.Document.Extra is not { } extra || !extra.TryGetValue(LegacyNotePlacement, out JsonElement legacy))
                continue;

            if (!moved && (active?.Prompt == stored.Id || active is null) && legacy.ValueKind == JsonValueKind.Object)
            {
                settings = settings with { AuthorsNotePlacement = LegacyPlacement(legacy) };
                moved = true;
            }

            Dictionary<string, JsonElement> kept = new(extra);
            _ = kept.Remove(LegacyNotePlacement);

            await data.Prompts.WriteAsync(stored.Id, stored.Document with { Extra = kept.Count > 0 ? kept : null }, cancellationToken).ConfigureAwait(false);
        }

        if (moved)
            await data.WriteSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    private static AuthorsNotePlacement LegacyPlacement(JsonElement legacy)
    {
        AuthorsNotePlacement placement = legacy.Deserialize<AuthorsNotePlacement>(DocumentJson.Options) ?? new AuthorsNotePlacement();

        return legacy.TryGetProperty("position", out JsonElement position) && position.ValueKind == JsonValueKind.String && position.GetString() == "beforeLast"
            ? placement with { Depth = 1 }
            : placement;
    }
}

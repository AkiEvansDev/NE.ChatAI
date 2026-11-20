using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Prompts;
using NE.ChatAI.Samplers;
using NE.ChatAI.Scenarios;
using NE.ChatAI.Settings;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Connections;

/// <summary>A connection with the documents it names read: what a generation is made with.</summary>
public sealed record ActiveProfile(string ConnectionId, ConnectionProfile Connection, ModelFormat Format, SystemPromptPreset SystemPrompt, PromptPreset Prompt, SamplerPreset Samplers);

/// <summary>Loads active connection profiles, seeds first-start defaults and upgrades legacy preset fields.</summary>
public static class Profiles
{
    private const string LegacySystemPrompt = "systemPrompt";
    private const string LegacyNotePlacement = "authorsNote";

    /// <summary>Loads the active connection profile and its referenced documents, or <see langword="null"/> when none is configured.</summary>
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

    /// <summary>Seeds shipped presets and a default koboldcpp connection on an empty data folder, or upgrades legacy documents in place.</summary>
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
            else if (await data.Scenarios.ReadAsync(DefaultDocuments.ImpersonateScenarioId, cancellationToken).ConfigureAwait(false) is Scenario impersonate)
                await UpgradeImpersonateAsync(data, impersonate, cancellationToken).ConfigureAwait(false);

            await UpgradeLegacyScenariosAsync(data, cancellationToken).ConfigureAwait(false);

            return;
        }

        _ = await DefaultDocuments.LayDownAsync(data, cancellationToken).ConfigureAwait(false);

        await data.Connections.WriteAsync(
            DefaultDocuments.ConnectionId,
            new ConnectionProfile
            {
                Name = "koboldcpp",
                Backend = BackendKind.KoboldCpp,
                Address = koboldCppAddress,
                Instruct = DefaultDocuments.FormatId,
                SystemPrompt = DefaultDocuments.SystemPromptId,
                Prompt = DefaultDocuments.PromptId,
                Samplers = DefaultDocuments.SamplersId
            },
            cancellationToken
        ).ConfigureAwait(false);

        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

        await data.WriteSettingsAsync(settings with { Connection = DefaultDocuments.ConnectionId }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpgradeImpersonateAsync(DataFolder data, Scenario impersonate, CancellationToken cancellationToken)
    {
        Scenario updated = impersonate.System ? impersonate : impersonate with { System = true };

        if (updated.Sheet is JsonElement sheetElement && JsonNode.Parse(sheetElement.GetRawText()) is JsonObject sheet && sheet["nodes"] is JsonArray nodes)
        {
            var patched = false;

            foreach (JsonNode? item in nodes)
            {
                if (item is not JsonObject node || (string?)node["type"] != "chat.draft")
                    continue;

                JsonObject values = node["values"] as JsonObject ?? [];

                if (values.ContainsKey("Mode"))
                    continue;

                values["Mode"] = "Continue";
                node["values"] = values;
                patched = true;
            }

            if (patched)
                updated = updated with { Sheet = JsonSerializer.SerializeToElement(sheet, DocumentJson.Options) };
        }

        if (!ReferenceEquals(updated, impersonate))
            await data.Scenarios.WriteAsync(DefaultDocuments.ImpersonateScenarioId, updated, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Upgrades any on-disk scenarios carrying a legacy <c>contextShare</c> trigger or retired <c>chat.gate</c> node.</summary>
    public static async Task UpgradeLegacyScenariosAsync(DataFolder data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!Directory.Exists(Path.Combine(data.Root, "graphs")))
            return;

        foreach (StoredDocument<Scenario> stored in (await data.Scenarios.ListAsync(cancellationToken).ConfigureAwait(false)).Items)
        {
            Scenario upgraded = stored.Document.UpgradeLegacyTrigger();

            if (!ReferenceEquals(upgraded, stored.Document))
                await data.Scenarios.WriteAsync(stored.Id, upgraded, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Extracts embedded <c>systemPrompt</c> fields from legacy prompt presets into standalone system prompt documents.</summary>
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

            Dictionary<string, JsonElement> kept = [with(extra)];
            _ = kept.Remove(LegacySystemPrompt);

            await data.Prompts.WriteAsync(stored.Id, stored.Document with { Extra = kept.Count > 0 ? kept : null }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Extracts embedded <c>authorsNote</c> placement objects from legacy prompt presets into <see cref="AppSettings.AuthorsNotePlacement"/>.</summary>
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

            Dictionary<string, JsonElement> kept = [with(extra)];
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

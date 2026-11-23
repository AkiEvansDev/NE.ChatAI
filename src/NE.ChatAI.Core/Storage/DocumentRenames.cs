using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
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

/// <summary>Writes user-created documents under updated name slugs and keeps cross-document references in <see cref="DataFolder"/> synchronized.</summary>
public static class DocumentRenames
{
    /// <summary>Renames any user-created document on disk whose file or folder slug no longer matches its <see cref="NamedDocument.Name"/>.</summary>
    public static async Task SyncAllIdsAsync(DataFolder data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        await SyncStoreAsync(data.Worlds, (id, doc, ct) => SaveWorldAsync(data, id, doc, ct), cancellationToken).ConfigureAwait(false);
        await SyncStoreAsync(data.Personas, (id, doc, ct) => SavePersonaAsync(data, id, doc, ct), cancellationToken).ConfigureAwait(false);
        await SyncStoreAsync(data.Characters, (id, doc, ct) => SaveCharacterAsync(data, id, doc, ct), cancellationToken).ConfigureAwait(false);
        await SyncStoreAsync(data.ModelFormats, (id, doc, ct) => SaveFormatAsync(data, id, doc, ct), cancellationToken).ConfigureAwait(false);
        await SyncStoreAsync(data.SystemPrompts, (id, doc, ct) => SaveSystemPromptAsync(data, id, doc, ct), cancellationToken).ConfigureAwait(false);
        await SyncStoreAsync(data.Prompts, (id, doc, ct) => SavePromptAsync(data, id, doc, ct), cancellationToken).ConfigureAwait(false);
        await SyncStoreAsync(data.Samplers, (id, doc, ct) => SaveSamplersAsync(data, id, doc, ct), cancellationToken).ConfigureAwait(false);
        await SyncStoreAsync(data.Connections, (id, doc, ct) => SaveConnectionAsync(data, id, doc, ct), cancellationToken).ConfigureAwait(false);
        await SyncStoreAsync(data.Scenarios, (id, doc, ct) => SaveScenarioAsync(data, id, doc, ct), cancellationToken).ConfigureAwait(false);
    }

    private static async Task SyncStoreAsync<T>(DocumentStore<T> store, Func<string, T, CancellationToken, Task<string>> saveAsync, CancellationToken cancellationToken)
        where T : NamedDocument
    {
        DocumentListing<T> listing = await store.ListAsync(cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < listing.Items.Count; i++)
        {
            StoredDocument<T> item = listing.Items[i];

            if (!DefaultDocuments.IsBuiltIn(item.Id) && !string.Equals(Slug.RenamedId(item.Id, item.Document.Name), item.Id, StringComparison.Ordinal))
                _ = await saveAsync(item.Id, item.Document, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Saves a world, renaming its folder and updating character/persona world references when its name slug changed.</summary>
    public static async Task<string> SaveWorldAsync(DataFolder data, string id, World world, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(world);

        var newId = await data.Worlds.WriteRenamedAsync(id, world, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(newId, id, StringComparison.Ordinal))
        {
            DocumentListing<Character> characters = await data.Characters.ListAsync(cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < characters.Items.Count; i++)
            {
                StoredDocument<Character> stored = characters.Items[i];

                if (string.Equals(stored.Document.World, id, StringComparison.Ordinal))
                    await data.Characters.WriteAsync(stored.Id, stored.Document with { World = newId }, cancellationToken).ConfigureAwait(false);
            }

            DocumentListing<Persona> personas = await data.Personas.ListAsync(cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < personas.Items.Count; i++)
            {
                StoredDocument<Persona> stored = personas.Items[i];

                if (string.Equals(stored.Document.World, id, StringComparison.Ordinal))
                    await data.Personas.WriteAsync(stored.Id, stored.Document with { World = newId }, cancellationToken).ConfigureAwait(false);
            }
        }

        return newId;
    }

    /// <summary>Saves a persona, renaming its folder and updating default, character and chat persona references when its name slug changed.</summary>
    public static async Task<string> SavePersonaAsync(DataFolder data, string id, Persona persona, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(persona);

        var newId = await data.Personas.WriteRenamedAsync(id, persona, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(newId, id, StringComparison.Ordinal))
        {
            AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

            if (string.Equals(settings.Persona, id, StringComparison.Ordinal))
                await data.WriteSettingsAsync(settings with { Persona = newId }, cancellationToken).ConfigureAwait(false);

            DocumentListing<Character> characters = await data.Characters.ListAsync(cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < characters.Items.Count; i++)
            {
                StoredDocument<Character> stored = characters.Items[i];

                if (string.Equals(stored.Document.Persona, id, StringComparison.Ordinal))
                    await data.Characters.WriteAsync(stored.Id, stored.Document with { Persona = newId }, cancellationToken).ConfigureAwait(false);
            }

            DocumentListing<Chat> chats = await data.Chats.ListAsync(cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < chats.Items.Count; i++)
            {
                StoredDocument<Chat> stored = chats.Items[i];

                if (string.Equals(stored.Document.Persona, id, StringComparison.Ordinal))
                    await data.Chats.WriteAsync(stored.Id, stored.Document with { Persona = newId }, cancellationToken).ConfigureAwait(false);
            }
        }

        return newId;
    }

    /// <summary>Saves a character, renaming its folder and updating chat and scenario gallery references when its name slug changed.</summary>
    public static async Task<string> SaveCharacterAsync(DataFolder data, string id, Character character, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(character);

        var newId = await data.Characters.WriteRenamedAsync(id, character, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(newId, id, StringComparison.Ordinal))
        {
            await UpdateChatsForCharacterAsync(data, id, newId, cancellationToken).ConfigureAwait(false);
            await UpdateScenarioStringValuesAsync(data, null, id + ".", newId + ".", prefixMatch: true, cancellationToken).ConfigureAwait(false);
        }

        return newId;
    }

    private static async Task UpdateChatsForCharacterAsync(DataFolder data, string oldId, string newId, CancellationToken cancellationToken)
    {
        var oldPrefix = oldId + ".";
        var newPrefix = newId + ".";
        DocumentListing<Chat> chats = await data.Chats.ListAsync(cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < chats.Items.Count; i++)
        {
            StoredDocument<Chat> stored = chats.Items[i];
            Chat chat = stored.Document;
            var changed = false;
            var character = chat.Character;

            if (string.Equals(character, oldId, StringComparison.Ordinal))
            {
                character = newId;
                changed = true;
            }

            List<ChatVisual>? updatedVisuals = null;

            for (var v = 0; v < chat.Visuals.Count; v++)
            {
                ChatVisual visual = chat.Visuals[v];

                if (visual.Image is string img && img.StartsWith(oldPrefix, StringComparison.Ordinal))
                {
                    updatedVisuals ??= [.. chat.Visuals];
                    updatedVisuals[v] = visual with { Image = string.Concat(newPrefix, img.AsSpan(oldPrefix.Length)) };
                    changed = true;
                }
            }

            if (changed)
                await data.Chats.WriteAsync(stored.Id, chat with { Character = character, Visuals = updatedVisuals ?? chat.Visuals }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UpdateScenarioStringValuesAsync(DataFolder data, string? pinName, string oldValue, string newValue, bool prefixMatch, CancellationToken cancellationToken)
    {
        DocumentListing<Scenario> scenarios = await data.Scenarios.ListAsync(cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < scenarios.Items.Count; i++)
        {
            StoredDocument<Scenario> stored = scenarios.Items[i];

            if (stored.Document.Sheet is not JsonElement sheet || sheet.ValueKind != JsonValueKind.Object)
                continue;

            JsonNode? root = JsonNode.Parse(sheet.GetRawText());

            if (root?["nodes"] is not JsonArray nodes)
                continue;

            var changed = false;

            for (var n = 0; n < nodes.Count; n++)
            {
                if (nodes[n]?["values"] is not JsonObject values)
                    continue;

                if (pinName is not null)
                {
                    if (TryReplaceNodeString(values, pinName, oldValue, newValue, prefixMatch))
                        changed = true;

                    continue;
                }

                List<string>? keys = null;

                foreach (KeyValuePair<string, JsonNode?> pair in values)
                {
                    if (pair.Value?.GetValueKind() == JsonValueKind.String)
                    {
                        keys ??= [];
                        keys.Add(pair.Key);
                    }
                }

                if (keys is null)
                    continue;

                for (var k = 0; k < keys.Count; k++)
                {
                    if (TryReplaceNodeString(values, keys[k], oldValue, newValue, prefixMatch))
                        changed = true;
                }
            }

            if (changed)
            {
                JsonElement updatedSheet = JsonSerializer.SerializeToElement(root, DocumentJson.Options);
                await data.Scenarios.WriteAsync(stored.Id, stored.Document with { Sheet = updatedSheet }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool TryReplaceNodeString(JsonObject values, string key, string oldValue, string newValue, bool prefixMatch)
    {
        if (values[key]?.GetValueKind() != JsonValueKind.String)
            return false;

        var current = values[key]!.GetValue<string>();

        if (prefixMatch)
        {
            if (!current.StartsWith(oldValue, StringComparison.Ordinal))
                return false;

            values[key] = string.Concat(newValue, current.AsSpan(oldValue.Length));
            return true;
        }

        if (!string.Equals(current, oldValue, StringComparison.Ordinal))
            return false;

        values[key] = newValue;
        return true;
    }

    /// <summary>Saves a model format, renaming non-default files and updating connection references when its name slug changed.</summary>
    public static Task<string> SaveFormatAsync(DataFolder data, string id, ModelFormat format, CancellationToken cancellationToken)
        => SavePresetAsync(data, data?.ModelFormats!, id, format, (c, newId) => string.Equals(c.Instruct, id, StringComparison.Ordinal) ? c with { Instruct = newId } : null, scenarioPin: null, cancellationToken);

    private static async Task<string> SavePresetAsync<T>(DataFolder data, DocumentStore<T> store, string id, T preset, Func<ConnectionProfile, string, ConnectionProfile?> updateConnection, string? scenarioPin, CancellationToken cancellationToken)
        where T : NamedDocument
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(preset);

        if (DefaultDocuments.IsBuiltIn(id))
        {
            await store.WriteAsync(id, preset, cancellationToken).ConfigureAwait(false);
            return id;
        }

        var newId = await store.WriteRenamedAsync(id, preset, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(newId, id, StringComparison.Ordinal))
        {
            await UpdateConnectionsAsync(data, c => updateConnection(c, newId), cancellationToken).ConfigureAwait(false);

            if (scenarioPin is not null)
                await UpdateScenarioStringValuesAsync(data, scenarioPin, id, newId, prefixMatch: false, cancellationToken).ConfigureAwait(false);
        }

        return newId;
    }

    private static async Task UpdateConnectionsAsync(DataFolder data, Func<ConnectionProfile, ConnectionProfile?> transform, CancellationToken cancellationToken)
    {
        DocumentListing<ConnectionProfile> connections = await data.Connections.ListAsync(cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < connections.Items.Count; i++)
        {
            StoredDocument<ConnectionProfile> stored = connections.Items[i];

            if (transform(stored.Document) is ConnectionProfile updated)
                await data.Connections.WriteAsync(stored.Id, updated, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Saves a system prompt preset, renaming non-default files and updating connection references when its name slug changed.</summary>
    public static Task<string> SaveSystemPromptAsync(DataFolder data, string id, SystemPromptPreset preset, CancellationToken cancellationToken)
        => SavePresetAsync(data, data?.SystemPrompts!, id, preset, (c, newId) => string.Equals(c.SystemPrompt, id, StringComparison.Ordinal) ? c with { SystemPrompt = newId } : null, scenarioPin: null, cancellationToken);

    /// <summary>Saves a prompt preset, renaming non-default files and updating connection references when its name slug changed.</summary>
    public static Task<string> SavePromptAsync(DataFolder data, string id, PromptPreset preset, CancellationToken cancellationToken)
        => SavePresetAsync(data, data?.Prompts!, id, preset, (c, newId) => string.Equals(c.Prompt, id, StringComparison.Ordinal) ? c with { Prompt = newId } : null, scenarioPin: null, cancellationToken);

    /// <summary>Saves a sampler preset, renaming non-default files and updating connection/scenario references when its name slug changed.</summary>
    public static Task<string> SaveSamplersAsync(DataFolder data, string id, SamplerPreset preset, CancellationToken cancellationToken)
        => SavePresetAsync(data, data?.Samplers!, id, preset, (c, newId) => string.Equals(c.Samplers, id, StringComparison.Ordinal) ? c with { Samplers = newId } : null, scenarioPin: "Samplers", cancellationToken);

    /// <summary>Saves a connection profile, renaming non-default files and updating active/chat/scenario references when its name slug changed.</summary>
    public static async Task<string> SaveConnectionAsync(DataFolder data, string id, ConnectionProfile connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(connection);

        if (DefaultDocuments.IsBuiltIn(id))
        {
            await data.Connections.WriteAsync(id, connection, cancellationToken).ConfigureAwait(false);
            return id;
        }

        var newId = await data.Connections.WriteRenamedAsync(id, connection, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(newId, id, StringComparison.Ordinal))
        {
            AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

            if (string.Equals(settings.Connection, id, StringComparison.Ordinal))
                await data.WriteSettingsAsync(settings with { Connection = newId }, cancellationToken).ConfigureAwait(false);

            DocumentListing<Chat> chats = await data.Chats.ListAsync(cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < chats.Items.Count; i++)
            {
                StoredDocument<Chat> stored = chats.Items[i];
                List<GenerationRecord>? updatedGens = null;

                for (var g = 0; g < stored.Document.Generations.Count; g++)
                {
                    GenerationRecord gen = stored.Document.Generations[g];

                    if (string.Equals(gen.Connection, id, StringComparison.Ordinal))
                    {
                        updatedGens ??= [.. stored.Document.Generations];
                        updatedGens[g] = gen with { Connection = newId };
                    }
                }

                if (updatedGens is not null)
                    await data.Chats.WriteAsync(stored.Id, stored.Document with { Generations = updatedGens }, cancellationToken).ConfigureAwait(false);
            }

            await UpdateScenarioStringValuesAsync(data, "Connection", id, newId, prefixMatch: false, cancellationToken).ConfigureAwait(false);
        }

        return newId;
    }

    /// <summary>Saves a scenario, renaming non-default/non-system files and updating chat record/visual references when its name slug changed.</summary>
    public static async Task<string> SaveScenarioAsync(DataFolder data, string id, Scenario scenario, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(scenario);

        if (DefaultDocuments.IsBuiltIn(id) || scenario.System)
        {
            await data.Scenarios.WriteAsync(id, scenario, cancellationToken).ConfigureAwait(false);
            return id;
        }

        var newId = await data.Scenarios.WriteRenamedAsync(id, scenario, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(newId, id, StringComparison.Ordinal))
        {
            DocumentListing<Chat> chats = await data.Chats.ListAsync(cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < chats.Items.Count; i++)
            {
                StoredDocument<Chat> stored = chats.Items[i];
                Chat chat = stored.Document;
                List<ChatRecord>? updatedRecords = null;
                List<ChatVisual>? updatedVisuals = null;

                for (var r = 0; r < chat.Records.Count; r++)
                {
                    ChatRecord record = chat.Records[r];

                    if (string.Equals(record.Scenario, id, StringComparison.Ordinal))
                    {
                        updatedRecords ??= [.. chat.Records];
                        updatedRecords[r] = record with { Scenario = newId };
                    }
                }

                for (var v = 0; v < chat.Visuals.Count; v++)
                {
                    ChatVisual visual = chat.Visuals[v];

                    if (string.Equals(visual.Scenario, id, StringComparison.Ordinal))
                    {
                        updatedVisuals ??= [.. chat.Visuals];
                        updatedVisuals[v] = visual with { Scenario = newId };
                    }
                }

                if (updatedRecords is not null || updatedVisuals is not null)
                {
                    await data.Chats.WriteAsync(
                        stored.Id,
                        chat with { Records = updatedRecords ?? chat.Records, Visuals = updatedVisuals ?? chat.Visuals },
                        cancellationToken
                    ).ConfigureAwait(false);
                }
            }
        }

        return newId;
    }
}

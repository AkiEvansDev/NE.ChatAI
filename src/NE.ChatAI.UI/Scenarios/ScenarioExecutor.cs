using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Backends.KoboldCpp;
using NE.ChatAI.Characters;
using NE.ChatAI.Chats;
using NE.ChatAI.Connections;
using NE.ChatAI.Hosting;
using NE.ChatAI.Settings;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Scenarios;

/// <summary>What running one scenario on a chat produced: the updated chat when it succeeded, any staged composer draft, or the error phrase when it failed.</summary>
internal sealed record ScenarioExecutionOutcome(bool Succeeded, Chat Chat, UIPhrase? Error = null, bool Cancelled = false, string? Draft = null, string? DraftGenerationId = null);

/// <summary>Converts between <see cref="Scenario.Sheet"/> JSON and <see cref="UINodeDocument"/>, and executes scenarios headlessly on a chat.</summary>
internal static class ScenarioExecutor
{
    public static UINodeDocument ReadSheet(Scenario scenario, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        if (scenario.Sheet is not JsonElement element || element.ValueKind != JsonValueKind.Object)
            return UINodeDocument.Empty.WithKey(key);

        try
        {
            UINodeDocument document = element.Deserialize<UINodeDocument>(DocumentJson.Options) ?? UINodeDocument.Empty;

            return key is null ? document : document.WithKey(key);
        }
        catch (JsonException)
        {
            return UINodeDocument.Empty.WithKey(key);
        }
    }

    public static JsonElement WriteSheet(UINodeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return JsonSerializer.SerializeToElement(document.WithKey(null), DocumentJson.Options);
    }

    /// <summary>Orders scenarios as they run after a reply and appear in menus: by <see cref="Scenario.Order"/>, then by name.</summary>
    public static List<StoredDocument<Scenario>> Order(IReadOnlyList<StoredDocument<Scenario>> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        List<StoredDocument<Scenario>> ordered = [.. items];
        ordered.Sort(static (left, right) =>
        {
            var byOrder = left.Document.Order.CompareTo(right.Document.Order);

            return byOrder != 0 ? byOrder : string.Compare(left.Document.Name, right.Document.Name, StringComparison.OrdinalIgnoreCase);
        });

        return ordered;
    }

    /// <summary>Runs <paramref name="scenario"/> on <paramref name="chat"/> and commits staged records, drafts and generations only when every node succeeds.</summary>
    public static async Task<ScenarioExecutionOutcome> RunOnChatAsync(IServiceProvider services, DataFolder data, KoboldCppClients clients, Chat chat, StoredDocument<Scenario> scenario, Action<KoboldCppClient?, string?>? onActiveGeneration, Func<string, string, string, ValueTask>? onStreamChunkAsync, CancellationToken cancellationToken, string initialDraft = "", Func<string, ValueTask>? onDraftChunkAsync = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(scenario);

        UINodeDocument document = ReadSheet(scenario.Document);
        ScenarioRunScope scope = await CreateScopeAsync(services, data, clients, chat, scenario.Id, scenario.Document.Name, document, commitToChat: true, onActiveGeneration, onStreamChunkAsync, cancellationToken, initialDraft, onDraftChunkAsync).ConfigureAwait(false);
        UINodeRunner runner = new(ScenarioCatalog.Instance, scope);

        try
        {
            UINodeRunResult result = await runner.RunAsync(document, cancellationToken).ConfigureAwait(false);

            if (!result.Success)
                return new ScenarioExecutionOutcome(false, chat, result.Error);

            Chat updated = ChatRecords.PutMany(chat, scope.StagedRecords, scope.Generations);

            return new ScenarioExecutionOutcome(true, updated, Draft: scope.StagedDraft, DraftGenerationId: scope.StagedDraftGenerationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ScenarioExecutionOutcome(false, chat, Cancelled: true);
        }
    }

    /// <summary>Builds a <see cref="ScenarioRunScope"/> for <paramref name="chat"/>, reading its character, persona, world and active connection.</summary>
    public static async Task<ScenarioRunScope> CreateScopeAsync(IServiceProvider services, DataFolder data, KoboldCppClients clients, Chat chat, string scenarioId, string scenarioName, UINodeDocument document, bool commitToChat, Action<KoboldCppClient?, string?>? onActiveGeneration, Func<string, string, string, ValueTask>? onStreamChunkAsync, CancellationToken cancellationToken, string initialDraft = "", Func<string, ValueTask>? onDraftChunkAsync = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(document);

        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        ActiveProfile? activeProfile = await Profiles.LoadActiveAsync(data, cancellationToken).ConfigureAwait(false);
        Character? character = chat.Character is string characterId && Slug.IsValidId(characterId) ? await data.Characters.ReadAsync(characterId, cancellationToken).ConfigureAwait(false) : null;
        var personaId = chat.Persona is string ownPersona && Slug.IsValidId(ownPersona) && data.Personas.Exists(ownPersona) ? ownPersona : settings.Persona;
        Persona? persona = personaId is not null && Slug.IsValidId(personaId) ? await data.Personas.ReadAsync(personaId, cancellationToken).ConfigureAwait(false) : null;
        World? world = await WorldInfo.OfAsync(data, character, persona, cancellationToken).ConfigureAwait(false);
        var characterName = string.IsNullOrWhiteSpace(character?.Name) ? Controllers.ShellController.AppName : character.Name;
        var userName = string.IsNullOrWhiteSpace(persona?.Name) ? "User" : persona.Name;

        return new ScenarioRunScope(services)
        {
            Data = data,
            Clients = clients,
            ActiveProfile = activeProfile,
            Settings = settings,
            Chat = chat,
            Character = character,
            Persona = persona,
            World = world,
            CharacterName = characterName,
            UserName = userName,
            ScenarioId = scenarioId,
            ScenarioName = scenarioName,
            Document = document,
            CommitToChat = commitToChat,
            InitialDraft = initialDraft,
            OnActiveGeneration = onActiveGeneration,
            OnStreamChunkAsync = onStreamChunkAsync,
            OnDraftChunkAsync = onDraftChunkAsync
        };
    }
}

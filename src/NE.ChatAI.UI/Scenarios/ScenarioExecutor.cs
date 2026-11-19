using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Characters;
using NE.ChatAI.Chats;
using NE.ChatAI.Connections;
using NE.ChatAI.Hosting;
using NE.ChatAI.Samplers;
using NE.ChatAI.Settings;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Scenarios;

/// <summary>What running one scenario on a chat produced: the updated chat and staged delta when it succeeded, or the error phrase when it failed.</summary>
internal sealed record ScenarioExecutionOutcome(bool Succeeded, Chat Chat, ScenarioDelta Delta, UIPhrase? Error = null, bool Cancelled = false, string? Draft = null, string? DraftGenerationId = null, bool SkippedByGate = false);

/// <summary>Hooks observed while a batch of scenarios executes on a chat.</summary>
internal sealed record ScenarioBatchCallbacks
{
    public ActiveGenerations? ActiveGenerations { get; init; }

    /// <summary>Whether trigger gates and single-branch pre-model <c>When</c> guards pass unconditionally because the batch was started by hand.</summary>
    public bool BypassGates { get; init; }

    /// <summary>Optional snapshot of the chat before the current turn started, used by <see cref="ContextNode"/>.</summary>
    public Chat? PreTurnChat { get; init; }

    /// <summary>Optional deterministic <c>[0, maxExclusive)</c> generator for testing <see cref="ScenarioGate"/> and <see cref="CueMenuNode"/>.</summary>
    public Func<int, int>? RandomNext { get; init; }

    public Func<StoredDocument<Scenario>, Chat, CancellationToken, ValueTask<bool>>? ShouldRunAsync { get; init; }

    public Func<StoredDocument<Scenario>, string, string, string, string, ValueTask>? OnStreamChunkAsync { get; init; }

    public Func<StoredDocument<Scenario>, string, ValueTask>? OnDraftChunkAsync { get; init; }

    public Func<StoredDocument<Scenario>, ScenarioExecutionOutcome, Chat, ValueTask>? OnScenarioFinishedAsync { get; init; }
}

/// <summary>Converts between <see cref="Scenario.Sheet"/> JSON and <see cref="UINodeDocument"/>, and executes scenarios headlessly on a chat.</summary>
internal static class ScenarioExecutor
{
    public static UINodeDocument ReadSheet(Scenario scenario, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        Scenario normalized = scenario.UpgradeLegacyTrigger();

        if (normalized.Sheet is not JsonElement element || element.ValueKind != JsonValueKind.Object)
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

    /// <summary>Orders scenarios as they run on a chat and appear in menus: by <see cref="Scenario.Order"/>, then by name.</summary>
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
    public static async Task<ScenarioExecutionOutcome> RunOnChatAsync(IServiceProvider services, DataFolder data, KoboldCppClients clients, Chat chat, StoredDocument<Scenario> scenario, ActiveGenerations? activeGenerations, Func<string, string, string, string, ValueTask>? onStreamChunkAsync, CancellationToken cancellationToken, string initialDraft = "", Func<string, ValueTask>? onDraftChunkAsync = null, string? chatId = null, bool bypassGates = true, Chat? preTurnChat = null, Func<int, int>? randomNext = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(scenario);

        Scenario normalized = scenario.Document.UpgradeLegacyTrigger();
        UINodeDocument document = ReadSheet(normalized);
        ScenarioRunScope scope = await CreateScopeAsync(services, data, clients, chat, scenario.Id, normalized.Name, document, commitToChat: true, activeGenerations, onStreamChunkAsync, cancellationToken, initialDraft, onDraftChunkAsync, chatId, bypassGates, preTurnChat, randomNext, normalized.Trigger).ConfigureAwait(false);

        if (!scope.CheckTriggerGateBeforeRun())
            return new ScenarioExecutionOutcome(true, chat, ScenarioDelta.Empty, SkippedByGate: true);

        return await ExecuteScopeAsync(chat, document, scope, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ScenarioExecutionOutcome> ExecuteScopeAsync(Chat inputChat, UINodeDocument document, ScenarioRunScope scope, CancellationToken cancellationToken)
    {
        UINodeRunner runner = new(ScenarioCatalog.Resolve(scope), scope);

        try
        {
            UINodeRunResult result = await runner.RunAsync(document, cancellationToken).ConfigureAwait(false);

            if (!result.Success)
                return new ScenarioExecutionOutcome(false, inputChat, ScenarioDelta.Empty, result.Error);

            if (scope.WasSkippedByCondition)
                return new ScenarioExecutionOutcome(true, inputChat, ScenarioDelta.Empty, SkippedByGate: true);

            ScenarioDelta delta = scope.CreateDelta();
            Chat updated = delta.ApplyTo(inputChat);

            return new ScenarioExecutionOutcome(true, updated, delta, Draft: delta.Draft, DraftGenerationId: delta.DraftGenerationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ScenarioExecutionOutcome(false, inputChat, ScenarioDelta.Empty, Cancelled: true);
        }
    }

    /// <summary>
    /// Runs an ordered batch of scenarios concurrently across independent connections while respecting data dependencies and
    /// serializing calls that target the same physical server.
    /// </summary>
    public static async Task<Chat> RunBatchAsync(IServiceProvider services, DataFolder data, KoboldCppClients clients, Chat initialChat, IReadOnlyList<StoredDocument<Scenario>> scenarios, ScenarioBatchCallbacks callbacks, CancellationToken cancellationToken, string initialDraft = "", string? chatId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(initialChat);
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(callbacks);

        var count = scenarios.Count;

        if (count == 0)
            return initialChat;

        ScenarioEnvironment environment = await LoadEnvironmentAsync(data, initialChat, cancellationToken).ConfigureAwait(false);
        StoredDocument<Scenario>[] normalizedScenarios = new StoredDocument<Scenario>[count];
        UINodeDocument[] documents = new UINodeDocument[count];

        for (var i = 0; i < count; i++)
        {
            Scenario normalized = scenarios[i].Document.UpgradeLegacyTrigger();
            normalizedScenarios[i] = new StoredDocument<Scenario>(scenarios[i].Id, normalized);
            documents[i] = ReadSheet(normalized);
        }

        var dependencies = ScenarioDependencies.Analyze(normalizedScenarios, documents);
        Lock stateLock = new();
        using SemaphoreSlim commitGate = new(1, 1);
        ScenarioDelta?[] completedDeltas = new ScenarioDelta?[count];
        Chat latestChat = initialChat;
        Task[] tasks = new Task[count];

        for (var i = 0; i < count; i++)
            tasks[i] = RunBatchItemAsync(i);

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return latestChat;

        async Task RunBatchItemAsync(int index)
        {
            var upstream = dependencies[index];

            for (var d = 0; d < upstream.Length; d++)
                await tasks[upstream[d]].ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
                return;

            Chat inputChat;
            string inputDraft;

            lock (stateLock)
                (inputChat, inputDraft) = RebuildState(initialChat, initialDraft, completedDeltas, index);

            StoredDocument<Scenario> scenario = normalizedScenarios[index];

            if (callbacks.ShouldRunAsync is not null && !await callbacks.ShouldRunAsync(scenario, inputChat, cancellationToken).ConfigureAwait(false))
                return;

            Func<string, string, string, string, ValueTask>? streamCallback = callbacks.OnStreamChunkAsync is null ? null : (nodeId, anchorId, text, thought) => callbacks.OnStreamChunkAsync(scenario, nodeId, anchorId, text, thought);
            Func<string, ValueTask>? draftCallback = callbacks.OnDraftChunkAsync is null ? null : partial => callbacks.OnDraftChunkAsync(scenario, partial);
            ScenarioRunScope scope = CreateScopeFromEnvironment(services, data, clients, environment, inputChat, scenario.Id, scenario.Document.Name, documents[index], commitToChat: true, callbacks.ActiveGenerations, streamCallback, inputDraft, draftCallback, chatId, callbacks.BypassGates, callbacks.PreTurnChat, callbacks.RandomNext, scenario.Document.Trigger);

            if (!scope.CheckTriggerGateBeforeRun())
                return;

            ScenarioExecutionOutcome outcome = await ExecuteScopeAsync(inputChat, documents[index], scope, cancellationToken).ConfigureAwait(false);

            if (outcome.SkippedByGate)
                return;

            await commitGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

            try
            {
                Chat currentLive;

                lock (stateLock)
                {
                    if (outcome.Succeeded)
                    {
                        completedDeltas[index] = outcome.Delta;
                        (latestChat, _) = RebuildState(initialChat, initialDraft, completedDeltas, count);
                    }

                    currentLive = latestChat;
                }

                if (callbacks.OnScenarioFinishedAsync is not null)
                    await callbacks.OnScenarioFinishedAsync(scenario, outcome, currentLive).ConfigureAwait(false);
            }
            finally
            {
                _ = commitGate.Release();
            }
        }
    }

    private static (Chat Chat, string Draft) RebuildState(Chat initialChat, string initialDraft, ScenarioDelta?[] deltas, int limit)
    {
        Chat chat = initialChat;
        var draft = initialDraft;

        for (var i = 0; i < limit; i++)
        {
            if (deltas[i] is not ScenarioDelta delta)
                continue;

            chat = delta.ApplyTo(chat);

            if (delta.Draft is string updatedDraft)
                draft = updatedDraft;
        }

        return (chat, draft);
    }

    /// <summary>Builds a <see cref="ScenarioRunScope"/> for <paramref name="chat"/>, reading its character, persona, world and active connection.</summary>
    public static async Task<ScenarioRunScope> CreateScopeAsync(IServiceProvider services, DataFolder data, KoboldCppClients clients, Chat chat, string scenarioId, string scenarioName, UINodeDocument document, bool commitToChat, ActiveGenerations? activeGenerations, Func<string, string, string, string, ValueTask>? onStreamChunkAsync, CancellationToken cancellationToken, string initialDraft = "", Func<string, ValueTask>? onDraftChunkAsync = null, string? chatId = null, bool bypassGates = false, Chat? preTurnChat = null, Func<int, int>? randomNext = null, ScenarioTrigger? trigger = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(document);

        ScenarioEnvironment environment = await LoadEnvironmentAsync(data, chat, cancellationToken).ConfigureAwait(false);

        return CreateScopeFromEnvironment(services, data, clients, environment, chat, scenarioId, scenarioName, document, commitToChat, activeGenerations, onStreamChunkAsync, initialDraft, onDraftChunkAsync, chatId, bypassGates, preTurnChat, randomNext, trigger ?? new ScenarioTrigger());
    }

    private static async Task<ScenarioEnvironment> LoadEnvironmentAsync(DataFolder data, Chat chat, CancellationToken cancellationToken)
    {
        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        ActiveProfile? activeProfile = await Profiles.LoadActiveAsync(data, cancellationToken).ConfigureAwait(false);
        Character? character = chat.Character is string characterId && Slug.IsValidId(characterId) ? await data.Characters.ReadAsync(characterId, cancellationToken).ConfigureAwait(false) : null;
        var personaId = chat.Persona is string ownPersona && Slug.IsValidId(ownPersona) && data.Personas.Exists(ownPersona) ? ownPersona : settings.Persona;
        Persona? persona = personaId is not null && Slug.IsValidId(personaId) ? await data.Personas.ReadAsync(personaId, cancellationToken).ConfigureAwait(false) : null;
        World? world = await WorldInfo.OfAsync(data, character, persona, cancellationToken).ConfigureAwait(false);
        var characterName = string.IsNullOrWhiteSpace(character?.Name) ? Controllers.ShellController.AppName : character.Name;
        var userName = string.IsNullOrWhiteSpace(persona?.Name) ? "User" : persona.Name;

        IReadOnlyList<StoredDocument<ConnectionProfile>> connections = (await data.Connections.ListAsync(cancellationToken).ConfigureAwait(false)).Items;
        List<(string Id, ActiveProfile Profile)> resolvedProfiles = [with(connections.Count)];

        for (var i = 0; i < connections.Count; i++)
        {
            StoredDocument<ConnectionProfile> stored = connections[i];
            ActiveProfile resolved = activeProfile is not null && string.Equals(activeProfile.ConnectionId, stored.Id, StringComparison.Ordinal)
                ? activeProfile
                : await Profiles.ResolveAsync(data, stored.Id, stored.Document, cancellationToken).ConfigureAwait(false);
            resolvedProfiles.Add((stored.Id, resolved));
        }

        IReadOnlyList<StoredDocument<SamplerPreset>> samplers = (await data.Samplers.ListAsync(cancellationToken).ConfigureAwait(false)).Items;

        return new ScenarioEnvironment(settings, activeProfile, resolvedProfiles, samplers, character, persona, world, characterName, userName);
    }

    private static ScenarioRunScope CreateScopeFromEnvironment(IServiceProvider services, DataFolder data, KoboldCppClients clients, ScenarioEnvironment environment, Chat chat, string scenarioId, string scenarioName, UINodeDocument document, bool commitToChat, ActiveGenerations? activeGenerations, Func<string, string, string, string, ValueTask>? onStreamChunkAsync, string initialDraft, Func<string, ValueTask>? onDraftChunkAsync, string? chatId, bool bypassGates, Chat? preTurnChat, Func<int, int>? randomNext, ScenarioTrigger trigger)
        => new(services)
        {
            Data = data,
            Clients = clients,
            ActiveProfile = environment.ActiveProfile,
            PreloadedProfiles = environment.Profiles,
            PreloadedSamplers = environment.Samplers,
            Settings = environment.Settings,
            ChatId = chatId,
            Chat = chat,
            PreTurnChat = preTurnChat,
            Character = environment.Character,
            Persona = environment.Persona,
            World = environment.World,
            CharacterName = environment.CharacterName,
            UserName = environment.UserName,
            ScenarioId = scenarioId,
            ScenarioName = scenarioName,
            Trigger = trigger,
            Document = document,
            CommitToChat = commitToChat,
            BypassGates = bypassGates,
            RandomNext = randomNext,
            InitialDraft = initialDraft,
            ActiveGenerations = activeGenerations,
            OnStreamChunkAsync = onStreamChunkAsync,
            OnDraftChunkAsync = onDraftChunkAsync
        };

    private sealed record ScenarioEnvironment(AppSettings Settings, ActiveProfile? ActiveProfile, IReadOnlyList<(string Id, ActiveProfile Profile)> Profiles, IReadOnlyList<StoredDocument<SamplerPreset>> Samplers, Character? Character, Persona? Persona, World? World, string CharacterName, string UserName);
}

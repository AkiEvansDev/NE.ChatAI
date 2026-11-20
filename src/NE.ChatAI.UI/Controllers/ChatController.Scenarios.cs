using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NE.ChatAI.Chats;
using NE.ChatAI.Connections;
using NE.ChatAI.Hosting;
using NE.ChatAI.Scenarios;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Controllers;

/// <summary>One scenario record shown as a folded block under the message it follows or replaces up to.</summary>
internal sealed partial class ChatRecordItem : RecursiveObservable, IBindableItem, IContentItem
{
    [RecursiveMember(false)]
    public required string Id { get; init; }

    public bool IsContent => true;

    [RecursiveMember]
    public partial UIPhrase Title { get; set; } = new("chatai.scenarios.record");

    [RecursiveMember]
    public partial string Text { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIVisibility TextVisibility { get; set; } = UIVisibility.Visible;

    [RecursiveMember]
    public partial string EditDraft { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIVisibility EditorVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember]
    public partial UIVisibility ToolsVisibility { get; set; } = UIVisibility.Visible;

    [RecursiveMember]
    public partial UIVisibility ActionsVisibility { get; set; } = UIVisibility.Visible;

    public void ShowEditor(string? draft)
    {
        EditDraft = draft ?? string.Empty;
        TextVisibility = draft is null ? UIVisibility.Visible : UIVisibility.Collapsed;
        EditorVisibility = draft is null ? UIVisibility.Collapsed : UIVisibility.Visible;
        ToolsVisibility = TextVisibility;
    }
}

/// <summary>Scenario menus, manual and triggered batch execution, live record/draft streaming, and inline record editing.</summary>
internal sealed partial class ChatController
{
    public const string MessageRecordsId = "chatai-message-records";
    public const string ScenariosAction = "scenarios";
    public const string ImpersonateAction = "impersonate";
    public const string EditScenariosAction = "scenarios:edit";
    public const string ScenarioTogglePrefix = "scenario-toggle:";
    public const string ScenarioRunPrefix = "scenario-run:";
    public const string PinnedScenarioPrefix = "scenario-pinned:";
    private const string StreamRecordPrefix = "__scenario_stream__:";

    private List<StoredDocument<Scenario>> _knownScenarios = [];
    private string? _editingRecordId;
    private readonly List<StreamingRecord> _streamingRecords = [];
    private string? _draftGenerationId;
    private readonly List<string> _draftContinuations = [];
    private readonly HashSet<string> _hiddenBackupRecordIds = [with(StringComparer.Ordinal)];

    private void RebuildScenarioMenu()
    {
        var canManage = !_generating;
        var canRun = canManage && _chat is not null;

        for (var i = Extras.Count - 1; i >= 0; i--)
        {
            if (Extras[i].Id.StartsWith(PinnedScenarioPrefix, StringComparison.Ordinal))
                Extras.RemoveAt(i);
        }

        var insertAt = 0;

        for (var i = 0; i < Extras.Count; i++)
        {
            if (Extras[i].Id == ImpersonateAction)
            {
                insertAt = i + 1;
                break;
            }
        }

        for (var i = 0; i < _knownScenarios.Count; i++)
        {
            StoredDocument<Scenario> stored = _knownScenarios[i];

            if (stored.Document.System || !stored.Document.Pinned)
                continue;

            Extras.Insert(insertAt++, new MenuItem
            {
                Id = PinnedScenarioPrefix + stored.Id,
                Title = stored.Document.Name,
                IsContent = true,
                Icon = AppIcons.Outline(AppIcons.Run),
                Enabled = canRun
            });
        }

        MenuItem menu = ExtraOf(ScenariosAction);

        menu.Enabled = canManage;
        menu.Items.Clear();

        var hasBuiltIn = _knownScenarios.Any(static stored => !stored.Document.System && DefaultDocuments.IsBuiltIn(stored.Id));
        var hasCustom = _knownScenarios.Any(static stored => !stored.Document.System && !DefaultDocuments.IsBuiltIn(stored.Id));

        for (var i = 0; i < _knownScenarios.Count; i++)
        {
            StoredDocument<Scenario> stored = _knownScenarios[i];

            if (stored.Document.System || !DefaultDocuments.IsBuiltIn(stored.Id))
                continue;

            menu.Items.Add(new MenuItem
            {
                Id = ScenarioTogglePrefix + stored.Id,
                Title = stored.Document.Name,
                IsContent = true,
                Kind = UIMenuItemKind.Check,
                Checked = stored.Document.Enabled,
                Enabled = canManage
            });
        }

        if (hasBuiltIn && hasCustom)
            menu.Items.Add(new MenuItem { Id = "scenarios-toggle-custom-separator", Kind = UIMenuItemKind.Separator });

        for (var i = 0; i < _knownScenarios.Count; i++)
        {
            StoredDocument<Scenario> stored = _knownScenarios[i];

            if (stored.Document.System || DefaultDocuments.IsBuiltIn(stored.Id))
                continue;

            menu.Items.Add(new MenuItem
            {
                Id = ScenarioTogglePrefix + stored.Id,
                Title = stored.Document.Name,
                IsContent = true,
                Kind = UIMenuItemKind.Check,
                Checked = stored.Document.Enabled,
                Enabled = canManage
            });
        }

        if (hasBuiltIn || hasCustom)
        {
            menu.Items.Add(new MenuItem { Id = "scenarios-run-separator", Kind = UIMenuItemKind.Separator });
            menu.Items.Add(new MenuItem { Id = "scenarios-run-header", Title = "chatai.scenarios.run-now", Kind = UIMenuItemKind.Header });

            for (var i = 0; i < _knownScenarios.Count; i++)
            {
                StoredDocument<Scenario> stored = _knownScenarios[i];

                if (stored.Document.System || !DefaultDocuments.IsBuiltIn(stored.Id))
                    continue;

                menu.Items.Add(new MenuItem
                {
                    Id = ScenarioRunPrefix + stored.Id,
                    Title = stored.Document.Name,
                    IsContent = true,
                    Icon = AppIcons.Outline(AppIcons.Run),
                    Enabled = canRun
                });
            }

            if (hasBuiltIn && hasCustom)
                menu.Items.Add(new MenuItem { Id = "scenarios-run-custom-separator", Kind = UIMenuItemKind.Separator });

            for (var i = 0; i < _knownScenarios.Count; i++)
            {
                StoredDocument<Scenario> stored = _knownScenarios[i];

                if (stored.Document.System || DefaultDocuments.IsBuiltIn(stored.Id))
                    continue;

                menu.Items.Add(new MenuItem
                {
                    Id = ScenarioRunPrefix + stored.Id,
                    Title = stored.Document.Name,
                    IsContent = true,
                    Icon = AppIcons.Outline(AppIcons.Run),
                    Enabled = canRun
                });
            }

            menu.Items.Add(new MenuItem { Id = "scenarios-edit-separator", Kind = UIMenuItemKind.Separator });
        }

        menu.Items.Add(new MenuItem
        {
            Id = EditScenariosAction,
            Title = "chatai.scenarios.edit",
            Icon = AppIcons.Outline(AppIcons.Scenarios),
            Url = AppRoutes.Scenarios,
            Enabled = canManage
        });
    }

    private void RefreshScenarioMenuEnabled()
    {
        var canManage = !_generating;
        var canRun = canManage && _chat is not null;
        MenuItem menu = ExtraOf(ScenariosAction);

        menu.Enabled = canManage;

        for (var i = 0; i < menu.Items.Count; i++)
        {
            MenuItem item = menu.Items[i];

            if (item.Kind is UIMenuItemKind.Separator or UIMenuItemKind.Header)
                continue;

            item.Enabled = item.Id.StartsWith(ScenarioRunPrefix, StringComparison.Ordinal) ? canRun : canManage;
        }
    }

    private void RefreshRealizedRecords()
    {
        foreach (ChatMessageItem item in Messages.Items)
            SyncRecords(item);
    }

    private void SyncRecords(ChatMessageItem item)
    {
        List<(string Id, UIPhrase Title, string Text, bool Streaming)> desired = [];
        var hideSavedInsertions = (ReferenceEquals(item, Messages.Live) && _liveKind is ReplyKind.Variant or ReplyKind.Remake)
            || (_editingId == item.Id && _editingNew);

        if (_chat is { Records.Count: > 0 } chat)
        {
            for (var i = 0; i < chat.Records.Count; i++)
            {
                ChatRecord record = chat.Records[i];

                if (hideSavedInsertions && record.Replaces is not { Count: >= 2 })
                    continue;

                if (_generating && _hiddenBackupRecordIds.Contains(record.Id))
                    continue;

                if (ChatRecords.VisibleAnchorMessageId(record, chat.Messages, chat.Records) == item.Id)
                    desired.Add((record.Id, FormatRecordTitle(record, ResolveRecordName(record)), record.Text, false));
            }
        }

        for (var i = 0; i < _streamingRecords.Count; i++)
        {
            StreamingRecord stream = _streamingRecords[i];

            if (stream.AnchorMessageId == item.Id)
                desired.Add((stream.StreamId, UIPhrase.Of("chatai.scenarios.running", ("name", stream.ScenarioName)), stream.Text, true));
        }

        HashSet<string> kept = [with(StringComparer.Ordinal)];

        for (var i = 0; i < desired.Count; i++)
            _ = kept.Add(desired[i].Id);

        for (var i = item.Records.Count - 1; i >= 0; i--)
        {
            var existingId = item.Records[i].Id;

            if (!kept.Contains(existingId))
            {
                if (_editingRecordId == existingId)
                    _editingRecordId = null;

                _ = item.Records.Remove(existingId);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            (var id, UIPhrase title, var text, var streaming) = desired[i];
            var hideActions = streaming || _generating;

            if (item.Records.TryGet(id, out ChatRecordItem? existing))
            {
                existing.Title = title;
                existing.Text = text;
                existing.ActionsVisibility = hideActions ? UIVisibility.Collapsed : UIVisibility.Visible;

                if (hideActions)
                {
                    existing.ToolsVisibility = UIVisibility.Collapsed;
                    existing.EditorVisibility = UIVisibility.Collapsed;
                    existing.TextVisibility = UIVisibility.Visible;
                }
                else if (_editingRecordId != id)
                {
                    existing.ToolsVisibility = UIVisibility.Visible;
                    existing.EditorVisibility = UIVisibility.Collapsed;
                    existing.TextVisibility = UIVisibility.Visible;
                }

                if (!string.Equals(item.Records[i].Id, id, StringComparison.Ordinal))
                    _ = item.Records.Move(id, i);
            }
            else
            {
                item.Records.Insert(i, new ChatRecordItem
                {
                    Id = id,
                    Title = title,
                    Text = text,
                    ActionsVisibility = hideActions ? UIVisibility.Collapsed : UIVisibility.Visible,
                    ToolsVisibility = hideActions ? UIVisibility.Collapsed : UIVisibility.Visible
                });
            }
        }
    }

    private static UIPhrase FormatRecordTitle(ChatRecord record, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return record.Stale ? UIPhrase.Of("chatai.scenarios.stale-title", ("name", new UIPhrase("chatai.scenarios.record"))) : new UIPhrase("chatai.scenarios.record");

        return record.Stale ? UIPhrase.Of("chatai.scenarios.stale-title", ("name", name)) : UIPhrase.Text(name);
    }

    private string? ResolveRecordName(ChatRecord record)
    {
        if (record.Scenario is string scenarioId)
        {
            for (var i = 0; i < _knownScenarios.Count; i++)
            {
                if (_knownScenarios[i].Id == scenarioId)
                    return _knownScenarios[i].Document.Name;
            }
        }

        if (_chat is not null && record.Generation is string generationId)
        {
            for (var i = _chat.Generations.Count - 1; i >= 0; i--)
            {
                GenerationRecord generation = _chat.Generations[i];

                if (generation.Id == generationId && !string.IsNullOrWhiteSpace(generation.Purpose) && generation.Purpose != GenerationPurpose.Reply && generation.Purpose != GenerationPurpose.Impersonate)
                    return generation.Purpose;
            }
        }

        return string.IsNullOrWhiteSpace(record.Tag) ? null : record.Tag;
    }

    private async Task<UICommandResult?> ScenarioActionAsync(string action, CancellationToken cancellationToken)
    {
        if (action == ImpersonateAction)
            return await ImpersonateAsync(cancellationToken).ConfigureAwait(false);

        if (action.StartsWith(ScenarioTogglePrefix, StringComparison.Ordinal))
            return await ToggleScenarioAsync(action[ScenarioTogglePrefix.Length..], cancellationToken).ConfigureAwait(false);

        if (action.StartsWith(PinnedScenarioPrefix, StringComparison.Ordinal))
            return await RunScenarioByHandAsync(action[PinnedScenarioPrefix.Length..], cancellationToken).ConfigureAwait(false);

        if (action.StartsWith(ScenarioRunPrefix, StringComparison.Ordinal))
            return await RunScenarioByHandAsync(action[ScenarioRunPrefix.Length..], cancellationToken).ConfigureAwait(false);

        return null;
    }

    private async Task<UICommandResult> ImpersonateAsync(CancellationToken cancellationToken)
    {
        _ = await DefaultDocuments.EnsureScenarioAsync(Data, DefaultDocuments.ImpersonateScenarioId, cancellationToken).ConfigureAwait(false);

        return await RunScenarioByHandAsync(DefaultDocuments.ImpersonateScenarioId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UICommandResult> RunScenarioByHandAsync(string scenarioId, CancellationToken cancellationToken)
    {
        if (!Slug.IsValidId(scenarioId) || await Data.Scenarios.ReadAsync(scenarioId, cancellationToken).ConfigureAwait(false) is not Scenario scenario)
            return UICommandResult.Ok();

        string? chatId = null;
        var busy = false;
        var noChat = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            busy = _generating;

            if (busy)
                return;

            if (_chat is null || OpenChatId is null)
            {
                noChat = true;
                return;
            }

            chatId = OpenChatId;
            EnterGeneratingState(_chat);
        }, cancellationToken).ConfigureAwait(false);

        if (busy)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        if (noChat)
            return Notify("chatai.chat.none", UIColorStyle.Info);

        if (chatId is not null)
        {
            StoredDocument<Scenario> stored = new(scenarioId, scenario);
            Context.Runtime.Start(lifetime => RunManualScenarioJobAsync(chatId, stored, lifetime));
        }

        return UICommandResult.Ok();
    }

    private void CaptureHiddenBackupRecords(Chat chat)
    {
        _hiddenBackupRecordIds.Clear();

        for (var i = 0; i < chat.Records.Count; i++)
        {
            ChatRecord record = chat.Records[i];

            if (ChatRecords.AnchorMessageId(record, chat.Messages) is not null
                && ChatRecords.VisibleAnchorMessageId(record, chat.Messages, chat.Records) is null)
            {
                _ = _hiddenBackupRecordIds.Add(record.Id);
            }
        }
    }

    private async Task RunManualScenarioJobAsync(string chatId, StoredDocument<Scenario> scenario, CancellationToken lifetime)
    {
        using CancellationTokenSource generation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        Chat? startChat = null;

        try
        {
            await Context.Runtime.InvokeAsync(() =>
            {
                _generation = generation;
                startChat = _chat;
            }, lifetime).ConfigureAwait(false);

            if (startChat is not null)
                _ = await RunScenarioBatchOnOpenChatAsync(chatId, startChat, [scenario], bypassGates: true, commitImmediately: true, preTurnChat: null, generation.Token).ConfigureAwait(false);
        }
        finally
        {
            if (lifetime.IsCancellationRequested)
                await _activeGenerations.AbortAllAsync(exception => Context.Logger.LogBackendFailure(exception), CancellationToken.None).ConfigureAwait(false);
            else
                await ReleaseGenerationAsync().ConfigureAwait(false);
        }
    }

    private async Task<UICommandResult> ToggleScenarioAsync(string scenarioId, CancellationToken cancellationToken)
    {
        var busy = false;

        await Context.Runtime.InvokeAsync(() => busy = _generating, cancellationToken).ConfigureAwait(false);

        if (busy)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        if (!Slug.IsValidId(scenarioId) || await Data.Scenarios.ReadAsync(scenarioId, cancellationToken).ConfigureAwait(false) is not Scenario scenario)
            return UICommandResult.Ok();

        Scenario updated = scenario with { Enabled = !scenario.Enabled };
        await Data.Scenarios.WriteAsync(scenarioId, updated, cancellationToken).ConfigureAwait(false);

        await Context.Runtime.InvokeAsync(() =>
        {
            var toggleId = ScenarioTogglePrefix + scenarioId;
            MenuItem menu = ExtraOf(ScenariosAction);

            for (var i = 0; i < _knownScenarios.Count; i++)
            {
                if (_knownScenarios[i].Id == scenarioId)
                    _knownScenarios[i] = new StoredDocument<Scenario>(scenarioId, updated);
            }

            if (menu.Items.TryGet(toggleId, out MenuItem? item))
                item.Checked = updated.Enabled;
        }, cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok();
    }

    private async Task<List<StoredDocument<Scenario>>> CandidatesForAsync(ScenarioTriggerKind kind, CancellationToken cancellationToken)
    {
        List<StoredDocument<Scenario>> ordered = ScenarioExecutor.Order((await Data.Scenarios.ListAsync(cancellationToken).ConfigureAwait(false)).Items);
        List<StoredDocument<Scenario>> candidates = [];

        for (var i = 0; i < ordered.Count; i++)
        {
            StoredDocument<Scenario> stored = ordered[i];

            if (!stored.Document.System && stored.Document.Enabled && stored.Document.Trigger.Kind == kind)
                candidates.Add(stored);
        }

        return candidates;
    }

    private Task<ScenarioBatchResult> RunBeforeSendBatchAsync(string chatId, Chat startChat, IReadOnlyList<StoredDocument<Scenario>> scenarios, CancellationToken cancellationToken)
        => RunScenarioBatchOnOpenChatAsync(chatId, startChat, scenarios, bypassGates: false, commitImmediately: false, startChat, cancellationToken);

    private async Task<ScenarioBatchResult> RunScenarioBatchOnOpenChatAsync(string chatId, Chat startChat, IReadOnlyList<StoredDocument<Scenario>> scenarios, bool bypassGates, bool commitImmediately, Chat? preTurnChat, CancellationToken cancellationToken)
    {
        KoboldCppClients clients = Context.Services.GetRequiredService<KoboldCppClients>();
        var initialDraft = string.Empty;
        string? initialDraftGen = null;
        List<string> initialContinuations = [];

        await Context.Runtime.InvokeAsync(() =>
        {
            initialDraft = Draft;
            initialDraftGen = _draftGenerationId;
            initialContinuations = [.. _draftContinuations];
        }, CancellationToken.None).ConfigureAwait(false);

        DraftStreamState draftState = new(initialDraft, initialDraftGen, initialContinuations);
        var allSucceeded = true;

        ScenarioBatchCallbacks callbacks = new()
        {
            ActiveGenerations = _activeGenerations,
            BypassGates = bypassGates,
            PreTurnChat = preTurnChat,
            OnStreamChunkAsync = (scenario, nodeId, anchorId, text, thought) => OnScenarioStreamChunkAsync(scenario.Id, scenario.Document.Name, nodeId, anchorId, text, thought),
            OnDraftChunkAsync = (scenario, partial) => OnScenarioDraftChunkAsync(draftState, scenario.Id, partial),
            OnScenarioFinishedAsync = async (scenario, outcome, liveChat) =>
            {
                if (!outcome.Succeeded || outcome.Cancelled)
                    allSucceeded = false;

                await OnScenarioFinishedAsync(chatId, draftState, commitImmediately, scenario, outcome, liveChat).ConfigureAwait(false);
            }
        };

        Chat updated = await ScenarioExecutor.RunBatchAsync(Context.Services, Data, clients, startChat, scenarios, callbacks, cancellationToken, initialDraft, chatId).ConfigureAwait(false);
        var succeeded = allSucceeded && !cancellationToken.IsCancellationRequested;

        if (!commitImmediately && !succeeded)
        {
            await Context.Runtime.InvokeAsync(() =>
            {
                Draft = initialDraft;
                _streamingRecords.Clear();
                RefreshRealizedRecords();
            }, CancellationToken.None).ConfigureAwait(false);
        }

        return new ScenarioBatchResult(updated, succeeded, draftState.CommittedDraft, draftState.DraftGenerationId, draftState.DraftContinuations, draftState.ReopenStage);
    }

    private void ApplyCommittedBeforeSendBatch(ScenarioBatchResult batch)
    {
        _chat = batch.Chat;
        Draft = batch.FinalDraft;
        _draftGenerationId = batch.DraftGenerationId;
        _draftContinuations.Clear();
        _draftContinuations.AddRange(batch.DraftContinuations);

        if (batch.ReopenStage)
            _stageHiddenByUser = false;
    }

    private async ValueTask OnScenarioStreamChunkAsync(string scenarioId, string scenarioName, string nodeId, string anchorMessageId, string text, string thought)
    {
        var shown = text.Length > 0 ? text : thought;
        var streamId = $"{StreamRecordPrefix}{scenarioId}:{nodeId}";

        await Context.Runtime.InvokeAsync(() =>
        {
            string? previousAnchorId = null;
            var updated = false;

            for (var i = 0; i < _streamingRecords.Count; i++)
            {
                if (_streamingRecords[i].StreamId != streamId)
                    continue;

                previousAnchorId = _streamingRecords[i].AnchorMessageId;
                _streamingRecords[i] = new StreamingRecord(streamId, scenarioId, anchorMessageId, scenarioName, shown);
                updated = true;
                break;
            }

            if (!updated)
                _streamingRecords.Add(new StreamingRecord(streamId, scenarioId, anchorMessageId, scenarioName, shown));

            if (previousAnchorId is not null && previousAnchorId != anchorMessageId && Messages.Held(previousAnchorId) is ChatMessageItem previousItem)
                SyncRecords(previousItem);

            if (Messages.Held(anchorMessageId) is ChatMessageItem item)
                SyncRecords(item);
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask OnScenarioDraftChunkAsync(DraftStreamState draftState, string scenarioId, string partialDraft)
        => await Context.Runtime.InvokeAsync(() =>
        {
            draftState.StreamingScenarioId = scenarioId;
            Draft = partialDraft;
        }, CancellationToken.None).ConfigureAwait(false);

    private async ValueTask OnScenarioFinishedAsync(string chatId, DraftStreamState draftState, bool commitImmediately, StoredDocument<Scenario> scenario, ScenarioExecutionOutcome outcome, Chat liveChat)
    {
        if (outcome.Cancelled || !outcome.Succeeded)
        {
            await Context.Runtime.InvokeAsync(() =>
            {
                if (!commitImmediately)
                {
                    Draft = draftState.InitialDraft;
                    draftState.StreamingScenarioId = null;
                }
                else if (draftState.StreamingScenarioId == scenario.Id)
                {
                    Draft = draftState.CommittedDraft;
                    draftState.StreamingScenarioId = null;
                }

                ClearScenarioStreams(scenario.Id);
                RefreshRealizedRecords();
            }, CancellationToken.None).ConfigureAwait(false);

            if (!outcome.Cancelled)
            {
                UIPhrase failure = FormatScenarioFailure(scenario.Document.Name, outcome.Error);
                await Context.SendEffectsToAllAsync([new ShowNotificationEffect(failure, UIColorStyle.Danger)], CancellationToken.None).ConfigureAwait(false);
            }

            return;
        }

        await Context.Runtime.InvokeAsync(() =>
        {
            ClearScenarioStreams(scenario.Id);

            if (_chat is null || OpenChatId != chatId)
                return;

            if (outcome.Delta.Draft is string stagedDraft)
            {
                Draft = stagedDraft;
                draftState.ApplyDraft(stagedDraft, outcome.Delta.DraftGenerationId, outcome.Delta.DraftContinued);

                if (commitImmediately)
                {
                    _draftGenerationId = draftState.DraftGenerationId;
                    _draftContinuations.Clear();
                    _draftContinuations.AddRange(draftState.DraftContinuations);
                }
            }

            if (outcome.Delta.Visuals is { Count: > 0 } stagedVisuals)
            {
                for (var i = 0; i < stagedVisuals.Count; i++)
                {
                    if (stagedVisuals[i].Target == ChatVisualTarget.Stage && !string.IsNullOrWhiteSpace(stagedVisuals[i].Image))
                    {
                        draftState.ReopenStage = true;

                        if (commitImmediately)
                            _stageHiddenByUser = false;

                        break;
                    }
                }
            }

            if (commitImmediately)
            {
                _chat = liveChat;
                RefreshVisuals();
                RefreshLoadedStatistics();
            }

            RefreshRealizedRecords();
        }, CancellationToken.None).ConfigureAwait(false);

        if (commitImmediately && Data.Chats.Exists(chatId))
            await Chats.WriteAsync(chatId, liveChat, CancellationToken.None).ConfigureAwait(false);
    }

    private void ClearScenarioStreams(string scenarioId)
    {
        for (var i = _streamingRecords.Count - 1; i >= 0; i--)
        {
            if (_streamingRecords[i].ScenarioId == scenarioId)
                _streamingRecords.RemoveAt(i);
        }
    }

    private static UIPhrase FormatScenarioFailure(string scenarioName, UIPhrase? error)
        => UIPhrase.Of("chatai.scenarios.failed", ("name", scenarioName), ("error", error ?? UIPhrase.Text("Error")));

    private async Task ReleaseGenerationAsync()
    {
        await Context.Runtime.InvokeAsync(() =>
        {
            _streamingRecords.Clear();
            _generating = false;
            _generation = null;
            _hiddenBackupRecordIds.Clear();
            ShowSendButton(stops: false);
            RefreshRealizedMessages();
            RefreshRealizedRecords();
            ShowLastActions();
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RunTriggeredScenariosAsync(ReplyJob job, Chat afterReplyChat, List<StoredDocument<Scenario>> candidates, CancellationToken cancellationToken)
        => _ = await RunScenarioBatchOnOpenChatAsync(job.ChatId, afterReplyChat, candidates, bypassGates: false, commitImmediately: true, job.PreTurnChat, cancellationToken).ConfigureAwait(false);

    private void CloseOpenRecordEditor()
    {
        if (_editingRecordId is string openId)
        {
            FindRealizedRecordItem(openId)?.ShowEditor(null);
            _editingRecordId = null;
        }
    }

    private ChatRecordItem? FindRealizedRecordItem(string recordId)
    {
        foreach (ChatMessageItem messageItem in Messages.Items)
        {
            if (messageItem.Records.TryGet(recordId, out ChatRecordItem? recordItem))
                return recordItem;
        }

        return null;
    }

    [UICommand]
    public async Task<UICommandResult> EditRecordAsync(string recordId, CancellationToken cancellationToken)
    {
        var busy = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            busy = _generating;

            if (busy || _chat?.Records.FirstOrDefault(record => record.Id == recordId) is not ChatRecord record)
                return;

            if (_editingId is string messageId)
            {
                Messages.Held(messageId)?.ShowEditor(null);
                _editingId = null;
                _editingNew = false;
            }

            if (_editingRecordId is string otherId && otherId != recordId)
                FindRealizedRecordItem(otherId)?.ShowEditor(null);

            _editingRecordId = recordId;
            FindRealizedRecordItem(recordId)?.ShowEditor(record.Text);
        }, cancellationToken).ConfigureAwait(false);

        return busy ? Notify("chatai.chat.busy", UIColorStyle.Info) : UICommandResult.Ok();
    }

    [UICommand]
    public async Task<UICommandResult> SaveRecordEditAsync(string recordId, CancellationToken cancellationToken)
    {
        string? chatId = null;
        Chat? changed = null;
        var empty = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            if (_generating || _chat is null || _editingRecordId != recordId || FindRealizedRecordItem(recordId) is not ChatRecordItem item)
                return;

            var text = item.EditDraft.Trim();

            if (text.Length == 0)
            {
                empty = true;
                return;
            }

            chatId = OpenChatId;
            _chat = changed = ChatRecords.Edit(_chat, recordId, text);
            _editingRecordId = null;
            item.ShowEditor(null);
            RefreshRealizedRecords();
        }, cancellationToken).ConfigureAwait(false);

        if (empty)
            return Notify("chatai.scenarios.record-empty", UIColorStyle.Warning);

        if (changed is not null && chatId is not null)
            await Chats.WriteAsync(chatId, changed, cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok();
    }

    [UICommand]
    public async Task CancelRecordEditAsync(string recordId, CancellationToken cancellationToken)
        => await Context.Runtime.InvokeAsync(() =>
        {
            if (_editingRecordId == recordId)
                _editingRecordId = null;

            FindRealizedRecordItem(recordId)?.ShowEditor(null);
        }, cancellationToken).ConfigureAwait(false);

    [UICommand]
    public async Task<UICommandResult> DeleteRecordAsync(string recordId, CancellationToken cancellationToken)
    {
        string? chatId = null;
        Chat? changed = null;
        var busy = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            busy = _generating;

            if (busy || _chat is null)
                return;

            if (_editingRecordId == recordId)
                _editingRecordId = null;

            chatId = OpenChatId;
            _chat = changed = ChatRecords.Delete(_chat, recordId);
            RefreshRealizedRecords();
            RefreshLoadedStatistics();
        }, cancellationToken).ConfigureAwait(false);

        if (busy)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        if (changed is not null && chatId is not null)
            await Chats.WriteAsync(chatId, changed, cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok();
    }

    private sealed record StreamingRecord(string StreamId, string ScenarioId, string AnchorMessageId, string ScenarioName, string Text);

    private sealed record ScenarioBatchResult(Chat Chat, bool AllSucceeded, string FinalDraft, string? DraftGenerationId, IReadOnlyList<string> DraftContinuations, bool ReopenStage);

    private sealed class DraftStreamState(string initialDraft, string? initialGenerationId, List<string> initialContinuations)
    {
        public string InitialDraft { get; } = initialDraft;

        public string CommittedDraft { get; private set; } = initialDraft;

        public string? DraftGenerationId { get; private set; } = initialGenerationId;

        public List<string> DraftContinuations { get; } = initialContinuations;

        public string? StreamingScenarioId { get; set; }

        public bool ReopenStage { get; set; }

        public void ApplyDraft(string draft, string? generationId, bool continued)
        {
            CommittedDraft = draft;
            StreamingScenarioId = null;

            if (generationId is not null)
            {
                if (continued && DraftGenerationId is not null)
                {
                    DraftContinuations.Add(generationId);
                }
                else
                {
                    DraftGenerationId = generationId;
                    DraftContinuations.Clear();
                }
            }
            else if (!continued)
            {
                DraftGenerationId = null;
                DraftContinuations.Clear();
            }
        }
    }
}

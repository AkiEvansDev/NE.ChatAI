using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NE.ChatAI.Backends.KoboldCpp;
using NE.ChatAI.Chats;
using NE.ChatAI.Connections;
using NE.ChatAI.Hosting;
using NE.ChatAI.Scenarios;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Controllers;

/// <summary>
/// The scenarios page (<c>/scenarios</c>): chooses, creates, copies, renames and removes scenario documents in <c>graphs/</c>,
/// auto-saves their trigger and node sheet as the reader edits them, and runs preview executions on the canvas against the last
/// open chat without committing records to it.
/// </summary>
internal sealed partial class ScenarioController : ShellController
{
    public const string PageId = "chatai-scenarios-page";
    public const string CanvasId = "chatai-scenario-canvas";
    public const string SaveFormId = "chatai-scenario-form";
    public const string RenameDialogKey = "chatai-scenario-rename";
    public const string DeleteDialogKey = "chatai-scenario-delete";

    public const string RenameAction = "rename";
    public const string CopyAction = "copy";
    public const string DeleteAction = "delete";

    private readonly Lock _abortLock = new();
    private readonly UINodeRuns _runs;
    private KoboldCppClient? _kobold;
    private string? _genKey;
    private string? _renamingId;
    private string? _deletingId;
    private bool _creatingScenario;
    private string? _copyingScenarioId;

    public ScenarioController()
    {
        _runs = new UINodeRuns(CanvasId, ScenarioCatalog.Instance, () => Sheet, sheet => Sheet = sheet) { ShowRunPanel = true };
    }

    [RecursiveMember(false)]
    public RecursiveCollection<OptionItem> ScenarioOptions { get; } = [];

    [RecursiveMember(false)]
    public RecursiveCollection<MenuItem> ScenarioActions { get; } =
    [
        UIMenuItems.Rename(RenameAction),
        UIMenuItems.Duplicate(CopyAction),
        UIMenuItems.Delete(DeleteAction)
    ];

    [RecursiveMember]
    public partial string? ScenarioId { get; set; }

    [RecursiveMember]
    public partial bool ScenarioEnabled { get; set; }

    [RecursiveMember]
    public partial string TriggerKind { get; set; } = nameof(ScenarioTriggerKind.Manual);

    [RecursiveMember]
    public partial decimal TriggerSharePercent { get; set; } = 50;

    [RecursiveMember]
    public partial UIVisibility TriggerVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember]
    public partial UIPhrase? SheetTitle { get; set; }

    [RecursiveMember]
    public partial UINodeDocument Sheet { get; set; } = UINodeDocument.Empty;

    [RecursiveMember]
    public partial IReadOnlyDictionary<string, IReadOnlyList<UIChoice>> PinChoices { get; set; } = new Dictionary<string, IReadOnlyList<UIChoice>>();

    [RecursiveMember]
    public partial UIVisibility EditorVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember]
    public partial UIVisibility EmptyVisibility { get; set; } = UIVisibility.Visible;

    [RecursiveMember]
    public partial string RenameName { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIPhrase? DeleteQuestion { get; set; }

    protected override void OnProfileLoaded(ActiveProfile? profile)
    {
        PinChoices = new Dictionary<string, IReadOnlyList<UIChoice>>(StringComparer.Ordinal)
        {
            [ModelCallNode.ConnectionChoicesKey] = ToChoices(ConnectionOptions),
            [ModelCallNode.SamplerChoicesKey] = ToChoices(SamplerOptions)
        };
    }

    private static List<UIChoice> ToChoices(RecursiveCollection<OptionItem> options)
    {
        List<UIChoice> choices = new(options.Count);

        for (var i = 0; i < options.Count; i++)
        {
            OptionItem option = options[i];
            choices.Add(new UIChoice(option.Id, option.Title ?? option.Id));
        }

        return choices;
    }

    protected override async Task OnNavigatedAsync(UINavigationRequest navigation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(navigation);

        _ = await Profiles.LoadActiveAsync(Data, cancellationToken).ConfigureAwait(false);
        var named = navigation.TryGetParameter(AppRoutes.CardParameter, out var id) ? id : null;
        await LoadScenariosAsync(named, cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadScenariosAsync(string? preferredId, CancellationToken cancellationToken)
    {
        List<StoredDocument<Scenario>> ordered = ScenarioExecutor.Order((await Data.Scenarios.ListAsync(cancellationToken).ConfigureAwait(false)).Items);
        ScenarioOptions.Clear();

        var hasBuiltIn = false;

        for (var i = 0; i < ordered.Count; i++)
        {
            if (!DefaultDocuments.IsBuiltIn(ordered[i].Id))
                continue;

            hasBuiltIn = true;
            ScenarioOptions.Add(new OptionItem { Id = ordered[i].Id, Title = ordered[i].Document.Name, IsContent = true });
        }

        var customGroup = hasBuiltIn ? CustomOptionGroup : null;

        for (var i = 0; i < ordered.Count; i++)
        {
            if (DefaultDocuments.IsBuiltIn(ordered[i].Id))
                continue;

            ScenarioOptions.Add(new OptionItem { Id = ordered[i].Id, Title = ordered[i].Document.Name, Group = customGroup, IsContent = true });
        }

        StoredDocument<Scenario>? chosen = FindScenario(ordered, preferredId) ?? FindScenario(ordered, ScenarioId) ?? (ordered.Count > 0 ? ordered[0] : null);
        ShowScenario(chosen);
    }

    private static StoredDocument<Scenario>? FindScenario(List<StoredDocument<Scenario>> items, string? id)
    {
        if (id is null)
            return null;

        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Id, id, StringComparison.Ordinal))
                return items[i];
        }

        return null;
    }

    private void ShowScenario(StoredDocument<Scenario>? chosen)
    {
        _runs.Cache.Clear();

        if (chosen is null)
        {
            ScenarioId = null;
            SheetTitle = null;
            Sheet = UINodeDocument.Empty;
            TriggerVisibility = UIVisibility.Collapsed;
            EditorVisibility = UIVisibility.Collapsed;
            EmptyVisibility = UIVisibility.Visible;
            return;
        }

        var isSystem = chosen.Document.System;
        ScenarioId = chosen.Id;
        ScenarioEnabled = chosen.Document.Enabled;
        TriggerKind = chosen.Document.Trigger.Kind.ToString();
        TriggerSharePercent = Math.Clamp(Math.Round((decimal)chosen.Document.Trigger.Share * 100m), 5m, 95m);
        TriggerVisibility = isSystem ? UIVisibility.Collapsed : UIVisibility.Visible;
        SheetTitle = UIPhrase.Text(chosen.Document.Name);
        Sheet = ScenarioExecutor.ReadSheet(chosen.Document, key: chosen.Id);
        EditorVisibility = UIVisibility.Visible;
        EmptyVisibility = UIVisibility.Collapsed;

        if (ScenarioActions.TryGet(RenameAction, out MenuItem? rename))
            rename.Enabled = !isSystem;

        if (ScenarioActions.TryGet(DeleteAction, out MenuItem? delete))
            delete.Enabled = !isSystem;
    }

    /// <summary>Switches the canvas to the scenario chosen in the selector.</summary>
    [UICommand]
    public async Task<UICommandResult> SelectScenarioAsync(CancellationToken cancellationToken)
    {
        if (_runs.IsRunning)
            _runs.Stop();

        if (ScenarioId is string id && Slug.IsValidId(id) && await Data.Scenarios.ReadAsync(id, cancellationToken).ConfigureAwait(false) is Scenario scenario)
        {
            ShowScenario(new StoredDocument<Scenario>(id, scenario));

            return UICommandResult.Ok([new PushAddressEffect(new Dictionary<string, object?> { [AppRoutes.CardParameter] = id })]);
        }

        await LoadScenariosAsync(null, cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok();
    }

    /// <summary>Opens the name dialog to create a blank scenario — or a copy of the open one — at the end of the order.</summary>
    [UICommand]
    public async Task<UICommandResult> CreateScenarioAsync(bool copy, CancellationToken cancellationToken)
    {
        if (_runs.IsRunning)
            _runs.Stop();

        _renamingId = null;
        _creatingScenario = true;
        _copyingScenarioId = copy ? ScenarioId : null;

        if (copy && ScenarioId is string sourceId && Slug.IsValidId(sourceId) && await Data.Scenarios.ReadAsync(sourceId, cancellationToken).ConfigureAwait(false) is Scenario source)
        {
            var copyPattern = Context.Translate("chatai.settings.copy-name") ?? "{name} (copy)";
            RenameName = copyPattern.Replace("{name}", source.Name, StringComparison.Ordinal);
        }
        else
        {
            RenameName = string.Empty;
        }

        return UICommandResult.Ok([new OpenDialogEffect(RenameDialogKey)]);
    }

    /// <summary>Dispatches Rename, Copy and Delete from the scenario selector's overflow menu.</summary>
    [UICommand]
    public Task<UICommandResult> ScenarioMenuActionAsync(string action, CancellationToken cancellationToken)
        => action switch
        {
            RenameAction => AskRenameAsync(cancellationToken),
            CopyAction => CreateScenarioAsync(copy: true, cancellationToken),
            DeleteAction => AskDeleteAsync(cancellationToken),
            _ => Task.FromResult(UICommandResult.Ok())
        };

    /// <summary>Keeps the open scenario's enabled switch and trigger settings as they change.</summary>
    [UICommand]
    public async Task SaveMetaAsync(CancellationToken cancellationToken)
    {
        if (ScenarioId is not string id || !Slug.IsValidId(id) || await Data.Scenarios.ReadAsync(id, cancellationToken).ConfigureAwait(false) is not { System: false } current)
            return;

        ScenarioTriggerKind kind = Enum.TryParse(TriggerKind, out ScenarioTriggerKind parsed) ? parsed : current.Trigger.Kind;
        var share = Math.Clamp((double)(TriggerSharePercent / 100m), 0.05, 0.95);
        Scenario updated = current with { Enabled = ScenarioEnabled, Trigger = new ScenarioTrigger { Kind = kind, Share = share } };

        await Data.Scenarios.WriteAsync(id, updated, cancellationToken).ConfigureAwait(false);
    }

    [UICommand]
    public async Task<UICommandResult> AskRenameAsync(CancellationToken cancellationToken)
    {
        if (ScenarioId is not string id || !Slug.IsValidId(id) || await Data.Scenarios.ReadAsync(id, cancellationToken).ConfigureAwait(false) is not { System: false } current)
            return UICommandResult.Ok();

        _creatingScenario = false;
        _copyingScenarioId = null;
        _renamingId = id;
        RenameName = current.Name;

        return UICommandResult.Ok([new OpenDialogEffect(RenameDialogKey)]);
    }

    [UICommand]
    public async Task<UICommandResult> RenameAsync(CancellationToken cancellationToken)
    {
        var creating = _creatingScenario;
        var copySourceId = _copyingScenarioId;
        var id = _renamingId;
        var name = RenameName.Trim();

        _creatingScenario = false;
        _copyingScenarioId = null;
        _renamingId = null;

        if (creating)
        {
            List<StoredDocument<Scenario>> ordered = ScenarioExecutor.Order((await Data.Scenarios.ListAsync(cancellationToken).ConfigureAwait(false)).Items);
            var nextOrder = ordered.Count == 0 ? 10 : ordered[^1].Document.Order + 10;
            Scenario created;

            if (copySourceId is not null && Slug.IsValidId(copySourceId) && await Data.Scenarios.ReadAsync(copySourceId, cancellationToken).ConfigureAwait(false) is Scenario source)
            {
                var copyPattern = Context.Translate("chatai.settings.copy-name") ?? "{name} (copy)";
                var chosenName = name.Length > 0 ? name : copyPattern.Replace("{name}", source.Name, StringComparison.Ordinal);
                created = source with { Name = chosenName, System = false, Order = nextOrder };
            }
            else
            {
                var chosenName = name.Length > 0 ? name : Context.Translate("chatai.scenarios.new-name") ?? "New scenario";
                created = new Scenario
                {
                    Name = chosenName,
                    Enabled = false,
                    Order = nextOrder,
                    Trigger = new ScenarioTrigger { Kind = ScenarioTriggerKind.Manual, Share = 0.5 },
                    Sheet = ScenarioExecutor.WriteSheet(UINodeDocument.Empty)
                };
            }

            var createdId = await Data.Scenarios.CreateAsync(created, cancellationToken).ConfigureAwait(false);
            await LoadScenariosAsync(createdId, cancellationToken).ConfigureAwait(false);

            return UICommandResult.Ok([new CloseDialogEffect(RenameDialogKey), new PushAddressEffect(new Dictionary<string, object?> { [AppRoutes.CardParameter] = createdId })]);
        }

        if (id is not null && name.Length > 0 && Slug.IsValidId(id) && await Data.Scenarios.ReadAsync(id, cancellationToken).ConfigureAwait(false) is { System: false } current)
        {
            await Data.Scenarios.WriteAsync(id, current with { Name = name }, cancellationToken).ConfigureAwait(false);
            await LoadScenariosAsync(id, cancellationToken).ConfigureAwait(false);
        }

        return UICommandResult.Ok([new CloseDialogEffect(RenameDialogKey)]);
    }

    [UICommand]
    public async Task<UICommandResult> AskDeleteAsync(CancellationToken cancellationToken)
    {
        if (ScenarioId is not string id || !Slug.IsValidId(id) || await Data.Scenarios.ReadAsync(id, cancellationToken).ConfigureAwait(false) is not { System: false } current)
            return UICommandResult.Ok();

        _deletingId = id;
        DeleteQuestion = UIPhrase.Of("chatai.scenarios.delete.question", ("name", current.Name));

        return UICommandResult.Ok([new OpenDialogEffect(DeleteDialogKey)]);
    }

    [UICommand]
    public async Task<UICommandResult> DeleteAsync(CancellationToken cancellationToken)
    {
        var id = _deletingId;
        _deletingId = null;

        if (id is not null && Slug.IsValidId(id) && await Data.Scenarios.ReadAsync(id, cancellationToken).ConfigureAwait(false) is { System: false })
        {
            if (_runs.IsRunning)
                _runs.Stop();

            _ = Data.Scenarios.Delete(id);
            ScenarioId = null;
            await LoadScenariosAsync(null, cancellationToken).ConfigureAwait(false);
        }

        Dictionary<string, object?> parameters = ScenarioId is string nextId ? new Dictionary<string, object?> { [AppRoutes.CardParameter] = nextId } : [];

        return UICommandResult.Ok([new CloseDialogEffect(DeleteDialogKey), new PushAddressEffect(parameters)]);
    }

    /// <summary>
    /// Auto-saves the node sheet under the document key it was opened with, and — when the save was raised by the run panel's Run or
    /// Run all — runs a preview execution against the last open chat without writing records to it.
    /// </summary>
    [UICommand(ConcurrencyMode = UICommandConcurrencyMode.Background, MaxConcurrent = 4)]
    public async Task<UICommandResult> SaveSheetAsync(string? reason, CancellationToken cancellationToken)
    {
        string? activeId = null;
        UINodeDocument sheet = UINodeDocument.Empty;

        await Context.Runtime.InvokeAsync(() =>
        {
            activeId = ScenarioId;
            sheet = Sheet;
        }, cancellationToken).ConfigureAwait(false);

        var targetId = sheet.Key ?? activeId;

        if (targetId is null || !Slug.IsValidId(targetId) || await Data.Scenarios.ReadAsync(targetId, cancellationToken).ConfigureAwait(false) is not Scenario current)
            return UICommandResult.Ok();

        await Data.Scenarios.WriteAsync(targetId, current with { Sheet = ScenarioExecutor.WriteSheet(sheet) }, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(reason, UIGraphArguments.RunReason, StringComparison.Ordinal) && !string.Equals(reason, UIGraphArguments.RunAllReason, StringComparison.Ordinal))
            return UICommandResult.Ok();

        var lastChatId = (await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false)).LastChat;
        Chat previewChat = (lastChatId is not null ? await Chats.ReadAsync(lastChatId, cancellationToken).ConfigureAwait(false) : null) ?? new Chat();
        KoboldCppClients clients = Context.Services.GetRequiredService<KoboldCppClients>();
        ScenarioRunScope scope = await ScenarioExecutor.CreateScopeAsync(Context.Services, Data, clients, previewChat, targetId, current.Name, sheet, commitToChat: false, SetActiveGeneration, onStreamChunkAsync: null, cancellationToken).ConfigureAwait(false);

        try
        {
            _ = await _runs.SavedAsync(Context.SendEffectsAsync, Context.Runtime.InvokeAsync, scope, reason ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SetActiveGeneration(null, null);
        }

        UINodeDocument? afterRun = null;

        await Context.Runtime.InvokeAsync(() =>
        {
            if (string.Equals(Sheet.Key, targetId, StringComparison.Ordinal))
                afterRun = Sheet;
        }, cancellationToken).ConfigureAwait(false);

        if (afterRun is not null && await Data.Scenarios.ReadAsync(targetId, cancellationToken).ConfigureAwait(false) is Scenario latest)
            await Data.Scenarios.WriteAsync(targetId, latest with { Sheet = ScenarioExecutor.WriteSheet(afterRun) }, cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok();
    }

    private void SetActiveGeneration(KoboldCppClient? kobold, string? genKey)
    {
        lock (_abortLock)
        {
            _kobold = kobold;
            _genKey = genKey;
        }
    }

    /// <summary>Stops the preview run on the canvas and aborts any model call it had in flight.</summary>
    [UICommand]
    public async Task StopRunAsync(CancellationToken cancellationToken)
    {
        _runs.Stop();

        KoboldCppClient? kobold;
        string? genKey;

        lock (_abortLock)
        {
            kobold = _kobold;
            genKey = _genKey;
        }

        if (kobold is null || genKey is null)
            return;

        try
        {
            await kobold.AbortAsync(genKey, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            Context.Logger.LogBackendFailure(exception);
        }
    }
}

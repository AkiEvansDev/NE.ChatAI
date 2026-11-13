using System;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Characters;
using NE.ChatAI.Connections;
using NE.ChatAI.Prompts;
using NE.ChatAI.Samplers;
using NE.ChatAI.Settings;

namespace NE.ChatAI.Controllers;

/// <summary>
/// The deep settings: every connection, model format, system prompt, prompt and sampler preset, each edited in full — chosen from its list,
/// made, copied and removed — and kept as it changes, as the generation panel keeps its own; and the import of SillyTavern's files.
/// </summary>
internal sealed partial class SettingsController : ShellController
{
    public const string DeleteDialogKey = "chatai-settings-delete";
    public const string RenameDialogKey = "chatai-settings-rename";
    public const string RestoreDefaultsDialogKey = "chatai-settings-restore-defaults";
    public const string DimSliderId = "chatai-settings-background-dim";

    public const string ConnectionKind = "connection";
    public const string FormatKind = "format";
    public const string SystemPromptKind = "system-prompt";
    public const string PromptKind = "prompt";
    public const string SamplersKind = "samplers";

    public const string RenameAction = "rename";
    public const string CopyAction = "copy";
    public const string DeleteAction = "delete";

    private static readonly string[] Kinds = [ConnectionKind, FormatKind, SystemPromptKind, PromptKind, SamplersKind];

    /// <summary>What the delete dialog was opened for.</summary>
    private string? _deleting;

    /// <summary>The kind whose document the rename dialog renames.</summary>
    private string? _renaming;

    /// <summary>The kind for which the name dialog is creating a new document or a copy.</summary>
    private string? _creatingKind;
    private bool _creatingCopy;

    [RecursiveMember]
    public partial DocumentEditor<ConnectionProfile, ConnectionDraft> Connection { get; set; } = new();

    [RecursiveMember]
    public partial DocumentEditor<ModelFormat, FormatDraft> Format { get; set; } = new();

    [RecursiveMember]
    public partial DocumentEditor<SystemPromptPreset, SystemPromptDraft> SystemPrompt { get; set; } = new();

    [RecursiveMember]
    public partial DocumentEditor<PromptPreset, PromptDraft> Prompt { get; set; } = new();

    [RecursiveMember]
    public partial DocumentEditor<SamplerPreset, SamplerDraft> Samplers { get; set; } = new();

    [RecursiveMember]
    public partial UIPhrase? DeleteQuestion { get; set; }

    /// <summary>The name the rename dialog edits, kept on its Rename.</summary>
    [RecursiveMember]
    public partial string RenameName { get; set; } = string.Empty;

    /// <summary>The picture behind a chat whose character has none.</summary>
    [RecursiveMember]
    public partial PictureSlot Background { get; set; } = new();

    /// <summary>Whether each message in the chat stands on a subtle surface of its own over the background picture.</summary>
    [RecursiveMember]
    public partial bool MessageBackground { get; set; }

    /// <summary>The global author's note, added to a character's and a chat's.</summary>
    [RecursiveMember]
    public partial string AuthorsNote { get; set; } = string.Empty;

    /// <summary>How many messages from the history's end the notes stand: 0 after the last, 1 before it.</summary>
    [RecursiveMember]
    public partial decimal NoteDepth { get; set; } = 4;

    /// <summary>Whose turn the notes are written as, a <see cref="PromptRole"/>'s name.</summary>
    [RecursiveMember]
    public partial string NoteRole { get; set; } = nameof(PromptRole.System);

    /// <summary>Opens each editor on the document the active connection uses, or the first of its kind.</summary>
    protected override async Task OnAttachedAsync(UINavigationRequest navigation, CancellationToken cancellationToken)
    {
        await base.OnAttachedAsync(navigation, cancellationToken).ConfigureAwait(false);

        Connection.Attach(Data.Connections, NewConnection, () => ConnectionOptions);
        Format.Attach(Data.ModelFormats, static () => ModelFormat.Gemma4, () => FormatOptions);
        SystemPrompt.Attach(Data.SystemPrompts, static () => new SystemPromptPreset(), () => SystemPromptOptions);
        Prompt.Attach(Data.Prompts, static () => new PromptPreset(), () => PromptOptions);
        Samplers.Attach(Data.Samplers, static () => new SamplerPreset(), () => SamplerOptions);

        ActiveProfile? profile = await Profiles.LoadActiveAsync(Data, cancellationToken).ConfigureAwait(false);

        Connection.Choose(profile?.ConnectionId ?? Connection.Id);
        await Connection.ShowAsync(cancellationToken).ConfigureAwait(false);

        if (Connection.Visibility == UIVisibility.Visible)
        {
            await SyncConnectionPresetsAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Format.Choose(Format.Id);
            SystemPrompt.Choose(SystemPrompt.Id);
            Prompt.Choose(Prompt.Id);
            Samplers.Choose(Samplers.Id);

            await Format.ShowAsync(cancellationToken).ConfigureAwait(false);
            await SystemPrompt.ShowAsync(cancellationToken).ConfigureAwait(false);
            await Prompt.ShowAsync(cancellationToken).ConfigureAwait(false);
            await Samplers.ShowAsync(cancellationToken).ConfigureAwait(false);
        }

        await LoadSettingsFieldsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A new connection names the documents the other editors show.</summary>
    private ConnectionProfile NewConnection()
        => new() { Address = new Uri("http://localhost:5001/"), Instruct = Format.Id, SystemPrompt = SystemPrompt.Id, Prompt = Prompt.Id, Samplers = Samplers.Id };

    private async Task SyncConnectionPresetsAsync(CancellationToken cancellationToken)
    {
        if (Connection.Visibility != UIVisibility.Visible)
            return;

        Format.Choose(Connection.Draft.Instruct);
        SystemPrompt.Choose(Connection.Draft.SystemPrompt);
        Prompt.Choose(Connection.Draft.Prompt);
        Samplers.Choose(Connection.Draft.Samplers);

        await Format.ShowAsync(cancellationToken).ConfigureAwait(false);
        await SystemPrompt.ShowAsync(cancellationToken).ConfigureAwait(false);
        await Prompt.ShowAsync(cancellationToken).ConfigureAwait(false);
        await Samplers.ShowAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadSettingsFieldsAsync(CancellationToken cancellationToken)
    {
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

        ApplyChatSurface(settings.MessageBackground);
        BackgroundDim = Math.Clamp(settings.BackgroundDim, 0, 1);
        MessageBackground = settings.MessageBackground;
        AuthorsNote = settings.AuthorsNote?.TrimEnd('\r', '\n') ?? string.Empty;
        NoteDepth = settings.AuthorsNotePlacement.Depth;
        NoteRole = settings.AuthorsNotePlacement.Role.ToString();
        ShowPhrases(settings);
        Background.Show(Pictures.AppBackground());
    }

    /// <summary>Shows the document chosen in a kind's list in its editor; choosing a connection also selects its presets on the other tabs.</summary>
    [UICommand]
    public async Task ShowAsync(string kind, CancellationToken cancellationToken)
    {
        await EditorOf(kind).ShowAsync(cancellationToken).ConfigureAwait(false);

        if (kind == ConnectionKind)
            await SyncConnectionPresetsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The editor of a kind by the name the page's buttons carry.</summary>
    private IDocumentEditor EditorOf(string kind)
        => kind switch
        {
            ConnectionKind => Connection,
            FormatKind => Format,
            SystemPromptKind => SystemPrompt,
            PromptKind => Prompt,
            SamplersKind => Samplers,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a kind of document the settings edit.")
        };

    /// <summary>Writes a kind's editor into its document, as a field lets go of its value.</summary>
    [UICommand]
    public async Task SaveAsync(string kind, CancellationToken cancellationToken)
    {
        await EditorOf(kind).SaveAsync(cancellationToken).ConfigureAwait(false);

        // The panel shows the same documents: its lists — a renamed one — and the active profile follow the change.
        await LoadGenerationAsync(cancellationToken).ConfigureAwait(false);

        if (kind == ConnectionKind)
            await SyncConnectionPresetsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves the edited connection and checks whether its server answers.</summary>
    [UICommand]
    public async Task<UICommandResult> CheckEditedConnectionAsync(CancellationToken cancellationToken)
    {
        await SaveAsync(ConnectionKind, cancellationToken).ConfigureAwait(false);

        return Uri.TryCreate(Connection.Draft.Address.Trim(), UriKind.Absolute, out Uri? address) && address.Scheme is "http" or "https"
            ? await ProbeConnectionAsync(address, updateModelLine: Connection.Id == ConnectionId, cancellationToken).ConfigureAwait(false)
            : Notify("chatai.backend.unreachable", UIColorStyle.Danger);
    }

    /// <summary>Keeps whether the central chat column stands on a subtle surface of its own.</summary>
    [UICommand]
    public async Task SaveMessageBackgroundAsync(CancellationToken cancellationToken)
    {
        ApplyChatSurface(MessageBackground);
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

        await Data.WriteSettingsAsync(settings with { MessageBackground = MessageBackground }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Keeps how far the picture behind the chat is dimmed toward the theme's ground.</summary>
    [UICommand]
    public async Task SaveBackgroundDimAsync(CancellationToken cancellationToken)
    {
        var dim = Math.Clamp(BackgroundDim, 0, 1);
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

        await Data.WriteSettingsAsync(settings with { BackgroundDim = dim }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens the name dialog to create a new document of a kind — the defaults, or a copy of the one shown.</summary>
    [UICommand]
    public UICommandResult CreateAsync(string kind, bool copy)
    {
        IDocumentEditor editor = EditorOf(kind);

        _renaming = null;
        _creatingKind = kind;
        _creatingCopy = copy;

        if (copy && editor.Name is string sourceName)
        {
            var copyPattern = Context.Translate("chatai.settings.copy-name") ?? "{name} (copy)";
            RenameName = copyPattern.Replace("{name}", sourceName, StringComparison.Ordinal);
        }
        else
        {
            RenameName = string.Empty;
        }

        return UICommandResult.Ok([new OpenDialogEffect(RenameDialogKey)]);
    }

    /// <summary>Dispatches Rename, Copy and Delete from a document editor's overflow menu.</summary>
    [UICommand]
    public UICommandResult DocumentActionAsync(string kind, string action)
        => action switch
        {
            RenameAction => AskRename(kind),
            CopyAction => CreateAsync(kind, copy: true),
            DeleteAction => AskDelete(kind),
            _ => UICommandResult.Ok()
        };

    /// <summary>Takes in the picture the field just uploaded as the one behind every chat whose character has none.</summary>
    [UICommand]
    public async Task SaveBackgroundAsync(CancellationToken cancellationToken)
    {
        if (await TakePictureAsync(Background, cancellationToken).ConfigureAwait(false) is not var (content, contentType))
            return;

        await PictureFiles.WriteAsync(Data.Root, CardPictures.Background, content, contentType, cancellationToken).ConfigureAwait(false);
        Background.Show(Pictures.AppBackground());
    }

    /// <summary>Takes the picture away; such chats stand on the theme.</summary>
    [UICommand]
    public void RemoveBackground()
    {
        PictureFiles.Delete(Data.Root, CardPictures.Background);
        Background.Show(Pictures.AppBackground());
    }

    /// <summary>Keeps the global author's note and where the notes go as each lets go of its value; an empty note is none.</summary>
    [UICommand]
    public async Task SaveAuthorsNoteAsync(CancellationToken cancellationToken)
    {
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        AuthorsNotePlacement placement = new()
        {
            Depth = (int)Math.Clamp(NoteDepth, 0, 100),
            Role = Enum.TryParse(NoteRole, out PromptRole role) ? role : settings.AuthorsNotePlacement.Role
        };

        await Data.WriteSettingsAsync(settings with { AuthorsNote = string.IsNullOrWhiteSpace(AuthorsNote) ? null : AuthorsNote.TrimEnd('\r', '\n'), AuthorsNotePlacement = placement }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens the rename dialog on the name of the document a kind's editor shows.</summary>
    [UICommand]
    public UICommandResult AskRename(string kind)
    {
        if (EditorOf(kind).Name is not string name)
            return UICommandResult.Ok();

        _creatingKind = null;
        _creatingCopy = false;
        _renaming = kind;
        RenameName = name;

        return UICommandResult.Ok([new OpenDialogEffect(RenameDialogKey)]);
    }

    /// <summary>Keeps the name typed in the rename dialog — creating the pending document or renaming the open one.</summary>
    [UICommand]
    public async Task<UICommandResult> RenameAsync(CancellationToken cancellationToken)
    {
        var createKind = _creatingKind;
        var createCopy = _creatingCopy;
        var kind = _renaming;
        var name = RenameName.Trim();

        _creatingKind = null;
        _creatingCopy = false;
        _renaming = null;

        if (createKind is not null)
        {
            var chosenName = name.Length > 0 ? name : Context.Translate(createCopy ? "chatai.settings.copy-name" : "chatai.settings.new-name") ?? string.Empty;
            IDocumentEditor editor = EditorOf(createKind);

            await editor.CreateAsync(createCopy, chosenName, cancellationToken).ConfigureAwait(false);
            await LoadGenerationAsync(cancellationToken).ConfigureAwait(false);
            await ShowAsync(createKind, cancellationToken).ConfigureAwait(false);

            return UICommandResult.Ok([new CloseDialogEffect(RenameDialogKey)]);
        }

        if (kind is not null && name.Length > 0)
        {
            EditorOf(kind).Rename(name);
            await SaveAsync(kind, cancellationToken).ConfigureAwait(false);
        }

        return UICommandResult.Ok([new CloseDialogEffect(RenameDialogKey)]);
    }

    /// <summary>Asks before a document is removed; a connection's presets are not removed with it.</summary>
    [UICommand]
    public UICommandResult AskDelete(string kind)
    {
        if (EditorOf(kind).Name is not string name)
            return UICommandResult.Ok();

        _deleting = kind;
        DeleteQuestion = UIPhrase.Of("chatai.settings.delete.question", ("name", name));

        return UICommandResult.Ok([new OpenDialogEffect(DeleteDialogKey)]);
    }

    /// <summary>Removes the document asked about; the editor moves on to what is left of the kind.</summary>
    [UICommand]
    public async Task<UICommandResult> DeleteAsync(CancellationToken cancellationToken)
    {
        var kind = _deleting;

        _deleting = null;

        if (kind is not null)
        {
            IDocumentEditor editor = EditorOf(kind);

            editor.Delete();
            await LoadGenerationAsync(cancellationToken).ConfigureAwait(false);
            editor.Choose(null);
            await ShowAsync(kind, cancellationToken).ConfigureAwait(false);
        }

        return UICommandResult.Ok([new CloseDialogEffect(DeleteDialogKey)]);
    }
}

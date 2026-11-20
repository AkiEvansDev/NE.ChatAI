using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Connections;
using NE.ChatAI.Import;
using NE.ChatAI.Prompts;
using NE.ChatAI.Samplers;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Controllers;

/// <summary>Result line for one imported file on <c>/settings</c>.</summary>
internal sealed partial class ImportLine : RecursiveObservable, IBindableItem
{
    [RecursiveMember(false)]
    public required string Id { get; init; }

    [RecursiveMember]
    public partial string FileName { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIPhrase? Result { get; set; }
}

/// <summary>Import, archive export, and built-in defaults restoration on <c>/settings</c>.</summary>
internal sealed partial class SettingsController
{
    public const long MaxImportBytes = 64 * 1024 * 1024;

    [RecursiveMember]
    public partial string? ImportSelectionId { get; set; }

    [RecursiveMember(false)]
    public RecursiveCollection<ImportLine> ImportResults { get; } = [];

    [RecursiveMember]
    public partial bool ExportSettings { get; set; } = true;

    [RecursiveMember]
    public partial bool ExportContent { get; set; } = true;

    [RecursiveMember]
    public partial bool ExportChats { get; set; } = true;

    [UICommand]
    public async Task ImportAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ImportSelectionId))
            return;

        UIUploadSelection selection = await Context.Uploads.GetSelectionAsync(Context.Handle, ImportSelectionId, cancellationToken).ConfigureAwait(false);

        ImportSelectionId = null;
        ImportResults.Clear();
        var importedArchiveSettings = false;

        foreach (UIUploadFile file in selection.Files)
        {
            if (await ImportFileAsync(file, cancellationToken).ConfigureAwait(false))
                importedArchiveSettings = true;
        }

        await LoadGenerationAsync(cancellationToken).ConfigureAwait(false);
        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
        await LoadSettingsFieldsAsync(cancellationToken).ConfigureAwait(false);

        ActiveProfile? profile = await Profiles.LoadActiveAsync(Data, cancellationToken).ConfigureAwait(false);

        if (importedArchiveSettings || Connection.Id is null || !Data.Connections.Exists(Connection.Id))
        {
            Connection.Choose(profile?.ConnectionId);
            await ShowAsync(ConnectionKind, cancellationToken).ConfigureAwait(false);
        }

        if (!importedArchiveSettings || Connection.Visibility != UIVisibility.Visible)
        {
            Format.Choose(Format.Id ?? profile?.Connection.Instruct);
            SystemPrompt.Choose(SystemPrompt.Id ?? profile?.Connection.SystemPrompt);
            Prompt.Choose(Prompt.Id ?? profile?.Connection.Prompt);
            Samplers.Choose(Samplers.Id ?? profile?.Connection.Samplers);

            foreach (var kind in Kinds)
                await EditorOf(kind).ShowAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> ImportFileAsync(UIUploadFile file, CancellationToken cancellationToken)
    {
        if (file.Size > MaxImportBytes)
        {
            Report(file, new UIPhrase("chatai.import.too-large"));
            return false;
        }

        byte[] content;
        UIUploadedFile opened = await Context.Uploads.OpenAsync(Context.Handle, file.FileId, cancellationToken: cancellationToken).ConfigureAwait(false);

        await using (opened.ConfigureAwait(false))
        {
            using MemoryStream buffer = new();
            await opened.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            content = buffer.ToArray();
        }

        if (DataArchive.IsZip(content))
            return await ImportArchiveAsync(file, content, cancellationToken).ConfigureAwait(false);

        if (PngText.IsPng(content) || LooksLikeCard(content))
        {
            await ImportCardAsync(file, content, cancellationToken).ConfigureAwait(false);
            return false;
        }

        SillyTavernPresets presets;

        try
        {
            using JsonDocument json = JsonDocument.Parse(DocumentJson.StripBom(content).ToArray());
            presets = SillyTavernImport.ReadPresets(json.RootElement, Path.GetFileNameWithoutExtension(file.FileName));
        }
        catch (JsonException)
        {
            presets = new SillyTavernPresets();
        }

        if (presets.IsEmpty)
            Report(file, new UIPhrase("chatai.import.unknown"));
        else
            await ImportPresetsAsync(file, presets, cancellationToken).ConfigureAwait(false);

        return false;
    }

    private void Report(UIUploadFile file, UIPhrase result)
        => ImportResults.Add(new ImportLine { Id = ImportResults.Count.ToString(CultureInfo.InvariantCulture), FileName = file.FileName, Result = result });

    private async Task<bool> ImportArchiveAsync(UIUploadFile file, byte[] content, CancellationToken cancellationToken)
    {
        DataArchiveImportSummary summary = await DataArchive.ImportAsync(Data, content, cancellationToken).ConfigureAwait(false);

        if (summary.IsEmpty)
        {
            Report(file, new UIPhrase("chatai.import.unknown"));
            return false;
        }

        if (summary.Settings || summary.Presets > 0)
            Report(file, UIPhrase.Of("chatai.import.archive.settings", ("count", summary.Presets)));

        if (summary.Characters > 0 || summary.Personas > 0 || summary.Worlds > 0)
        {
            Report(file, UIPhrase.Of(
                "chatai.import.archive.content",
                ("characters", summary.Characters),
                ("personas", summary.Personas),
                ("worlds", summary.Worlds)
            ));
        }

        if (summary.Chats > 0)
            Report(file, UIPhrase.Of("chatai.import.archive.chats", ("count", summary.Chats)));

        return summary.Settings || summary.Presets > 0;
    }

    private static bool LooksLikeCard(byte[] content)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(DocumentJson.StripBom(content).ToArray());
            JsonElement root = json.RootElement;

            return root.ValueKind == JsonValueKind.Object && (root.TryGetProperty("spec", out _) || root.TryGetProperty("first_mes", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task ImportCardAsync(UIUploadFile file, byte[] content, CancellationToken cancellationToken)
    {
        try
        {
            var id = await CardImport.ImportAsync(Data, content, cancellationToken).ConfigureAwait(false);
            var name = (await Data.Characters.ReadAsync(id, cancellationToken).ConfigureAwait(false))?.Name ?? id;

            Report(file, UIPhrase.Of("chatai.import.character", ("name", name)));
        }
        catch (CardFormatException)
        {
            Report(file, new UIPhrase("chatai.import.unknown"));
        }
    }

    private async Task ImportPresetsAsync(UIUploadFile file, SillyTavernPresets presets, CancellationToken cancellationToken)
    {
        if (presets.ModelFormat is ModelFormat format)
        {
            Format.Id = await Data.ModelFormats.CreateAsync(format, cancellationToken).ConfigureAwait(false);
            Report(file, UIPhrase.Of("chatai.import.format", ("name", format.Name)));
        }

        if (presets.Prompt is PromptPreset prompt)
        {
            Prompt.Id = await Data.Prompts.CreateAsync(prompt, cancellationToken).ConfigureAwait(false);
            Report(file, UIPhrase.Of("chatai.import.prompt", ("name", prompt.Name)));
        }

        if (presets.SystemPrompt is SystemPromptPreset systemPrompt)
        {
            SystemPrompt.Id = await Data.SystemPrompts.CreateAsync(systemPrompt, cancellationToken).ConfigureAwait(false);
            Report(file, UIPhrase.Of("chatai.import.system-prompt", ("name", systemPrompt.Name)));
        }

        if (presets.Samplers is SamplerPreset samplers)
        {
            Samplers.Id = await Data.Samplers.CreateAsync(samplers, cancellationToken).ConfigureAwait(false);
            Report(file, UIPhrase.Of("chatai.import.samplers", ("name", samplers.Name)));
        }

        if (presets.Thought is ThoughtFormat thought)
            await ImportThoughtAsync(file, thought, cancellationToken).ConfigureAwait(false);
    }

    private async Task ImportThoughtAsync(UIUploadFile file, ThoughtFormat thought, CancellationToken cancellationToken)
    {
        if (Format.Id is not string formatId || await Data.ModelFormats.ReadAsync(formatId, cancellationToken).ConfigureAwait(false) is not ModelFormat edited)
        {
            Report(file, new UIPhrase("chatai.import.thought-no-format"));
            return;
        }

        await Data.ModelFormats.WriteAsync(formatId, edited with { Thought = thought with { Enabled = edited.Thought.Enabled } }, cancellationToken).ConfigureAwait(false);
        Report(file, UIPhrase.Of("chatai.import.thought", ("name", edited.Name)));
    }

    [UICommand]
    public async Task<UICommandResult> ExportAsync(CancellationToken cancellationToken)
    {
        DataArchiveScopes scopes = DataArchiveScopes.None;

        if (ExportSettings)
            scopes |= DataArchiveScopes.Settings;

        if (ExportContent)
            scopes |= DataArchiveScopes.Content;

        if (ExportChats)
            scopes |= DataArchiveScopes.Chats;

        if (scopes == DataArchiveScopes.None)
            return Notify("chatai.export.none", UIColorStyle.Info);

        var archive = await DataArchive.ExportAsync(Data, scopes, cancellationToken).ConfigureAwait(false);
        var fileName = string.Create(CultureInfo.InvariantCulture, $"chatai-{DateTimeOffset.UtcNow:yyyy-MM-dd}.zip");

        _ = await Context.Downloads.DownloadAsync(Context.Handle, fileName, "application/zip", archive, cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok();
    }

    [UICommand]
    public async Task<UICommandResult> LayDownDefaultsAsync(CancellationToken cancellationToken)
    {
        var written = await ApplyDefaultsAsync(overwrite: false, cancellationToken).ConfigureAwait(false);
        UIPhrase said = written == 0 ? new UIPhrase("chatai.import.defaults.none") : UIPhrase.Of("chatai.import.defaults.added", ("count", written));

        return UICommandResult.Ok([new ShowNotificationEffect(said, UIColorStyle.Success)]);
    }

    private async Task<int> ApplyDefaultsAsync(bool overwrite, CancellationToken cancellationToken)
    {
        var written = await DefaultDocuments.LayDownAsync(Data, cancellationToken, overwrite).ConfigureAwait(false);

        await LoadGenerationAsync(cancellationToken).ConfigureAwait(false);
        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var kind in Kinds)
        {
            IDocumentEditor editor = EditorOf(kind);
            editor.Choose(editor.Id);
            await editor.ShowAsync(cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    [UICommand]
    public async Task<UICommandResult> RestoreDefaultsAsync(CancellationToken cancellationToken)
    {
        var written = await ApplyDefaultsAsync(overwrite: true, cancellationToken).ConfigureAwait(false);
        UIPhrase said = UIPhrase.Of("chatai.import.defaults.restored", ("count", written));

        return UICommandResult.Ok([new CloseDialogEffect(RestoreDefaultsDialogKey), new ShowNotificationEffect(said, UIColorStyle.Success)]);
    }
}

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

namespace NE.ChatAI.Controllers;

/// <summary>What one imported file became: the file's name and a phrase saying it, one line per document made.</summary>
internal sealed partial class ImportLine : RecursiveObservable, IBindableItem
{
    [RecursiveMember(false)]
    public required string Id { get; init; }

    [RecursiveMember]
    public partial string FileName { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIPhrase? Result { get; set; }
}

/// <summary>
/// The import and export: ChatAI archives (<c>.zip</c>) and SillyTavern's files dropped or picked — character cards (PNG or JSON)
/// and preset files — each taken into the data folder as the documents it holds, and a <c>.zip</c> archive downloaded by scope.
/// </summary>
internal sealed partial class SettingsController
{
    /// <summary>An archive with cards and backgrounds, a card or a preset file.</summary>
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

    /// <summary>Takes in every file of the selection the field just uploaded.</summary>
    [UICommand]
    public async Task ImportAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ImportSelectionId))
            return;

        UIUploadSelection selection = await Context.Uploads.GetSelectionAsync(Context.Handle, ImportSelectionId, cancellationToken).ConfigureAwait(false);

        ImportSelectionId = null;
        ImportResults.Clear();

        foreach (UIUploadFile file in selection.Files)
            await ImportFileAsync(file, cancellationToken).ConfigureAwait(false);

        await LoadGenerationAsync(cancellationToken).ConfigureAwait(false);
        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
        await LoadSettingsFieldsAsync(cancellationToken).ConfigureAwait(false);

        ActiveProfile? profile = await Profiles.LoadActiveAsync(Data, cancellationToken).ConfigureAwait(false);

        if (Connection.Id is null)
            Connection.Choose(profile?.ConnectionId);

        if (Format.Id is null)
            Format.Choose(profile?.Connection.Instruct);

        if (SystemPrompt.Id is null)
            SystemPrompt.Choose(profile?.Connection.SystemPrompt);

        if (Prompt.Id is null)
            Prompt.Choose(profile?.Connection.Prompt);

        if (Samplers.Id is null)
            Samplers.Choose(profile?.Connection.Samplers);

        foreach (var kind in Kinds)
            await ShowAsync(kind, cancellationToken).ConfigureAwait(false);
    }

    private async Task ImportFileAsync(UIUploadFile file, CancellationToken cancellationToken)
    {
        if (file.Size > MaxImportBytes)
        {
            Report(file, new UIPhrase("chatai.import.too-large"));
            return;
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
        {
            await ImportArchiveAsync(file, content, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (PngText.IsPng(content) || LooksLikeCard(content))
        {
            await ImportCardAsync(file, content, cancellationToken).ConfigureAwait(false);
            return;
        }

        SillyTavernPresets presets;

        try
        {
            using JsonDocument json = JsonDocument.Parse(content);
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
    }

    private void Report(UIUploadFile file, UIPhrase result)
        => ImportResults.Add(new ImportLine { Id = ImportResults.Count.ToString(CultureInfo.InvariantCulture), FileName = file.FileName, Result = result });

    private async Task ImportArchiveAsync(UIUploadFile file, byte[] content, CancellationToken cancellationToken)
    {
        DataArchiveImportSummary summary = await DataArchive.ImportAsync(Data, content, cancellationToken).ConfigureAwait(false);

        if (summary.IsEmpty)
        {
            Report(file, new UIPhrase("chatai.import.unknown"));
            return;
        }

        if (summary.Settings || summary.Presets > 0)
            Report(file, UIPhrase.Of("chatai.import.archive.settings", ("count", summary.Presets.ToString(CultureInfo.InvariantCulture))));

        if (summary.Characters > 0 || summary.Personas > 0 || summary.Worlds > 0)
        {
            Report(file, UIPhrase.Of(
                "chatai.import.archive.content",
                ("characters", summary.Characters.ToString(CultureInfo.InvariantCulture)),
                ("personas", summary.Personas.ToString(CultureInfo.InvariantCulture)),
                ("worlds", summary.Worlds.ToString(CultureInfo.InvariantCulture))
            ));
        }

        if (summary.Chats > 0)
            Report(file, UIPhrase.Of("chatai.import.archive.chats", ("count", summary.Chats.ToString(CultureInfo.InvariantCulture))));
    }

    /// <summary>A card's JSON names its spec, or carries the V1 fields a preset never has.</summary>
    private static bool LooksLikeCard(byte[] content)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(content);
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

    /// <summary>Keeps each part a preset file held as a document of its own; a reasoning template alone goes into the format edited.</summary>
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

    /// <summary>A reasoning template alone: its markers go into the format the editor shows, keeping whether thinking is on.</summary>
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

    /// <summary>Packs the selected parts of the data folder into a <c>.zip</c> archive and sends it to the browser.</summary>
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

    /// <summary>The base set of presets the application comes with, those the data folder lacks laid down again; the count said.</summary>
    [UICommand]
    public async Task<UICommandResult> LayDownDefaultsAsync(CancellationToken cancellationToken)
    {
        var written = await DefaultDocuments.LayDownAsync(Data, cancellationToken).ConfigureAwait(false);

        await LoadGenerationAsync(cancellationToken).ConfigureAwait(false);
        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var kind in Kinds)
            await ShowAsync(kind, cancellationToken).ConfigureAwait(false);

        UIPhrase said = written == 0 ? new UIPhrase("chatai.import.defaults.none") : UIPhrase.Of("chatai.import.defaults.added", ("count", written.ToString(CultureInfo.InvariantCulture)));

        return UICommandResult.Ok([new ShowNotificationEffect(said, UIColorStyle.Success)]);
    }
}

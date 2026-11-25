using System.Collections.Generic;
using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

internal sealed class SettingsView : ShellView, IUIViewDefinition
{
    public static string ViewKey => "chatai.settings";

    protected override string PageTitle => "chatai.nav.settings";

    protected override IVisualComponent CreatePage()
        => UIPage.Column(UIColumnWidth.Wide, CreateTabs(), centered: true);

    internal static TabsComponent CreateTabs()
        => new TabsComponent(SettingsController.TabsId)
            .BindSelectedKey<SettingsController>(static x => x.Tab)
            .AddTab(SettingsController.ConnectionsTab, "chatai.settings.connections", SettingsEditors.CreateConnection())
            .AddTab(SettingsController.GeneralTab, "chatai.settings.general", SettingsEditors.CreateGeneral())
            .AddTab(SettingsController.FormatsTab, "chatai.settings.formats", SettingsEditors.CreateFormat())
            .AddTab(SettingsController.SystemPromptsTab, "chatai.settings.system-prompts", SettingsEditors.CreateSystemPrompt())
            .AddTab(SettingsController.PromptsTab, "chatai.settings.prompts", SettingsEditors.CreatePrompt())
            .AddTab(SettingsController.SamplersTab, "chatai.settings.samplers", SettingsEditors.CreateSamplers())
            .AddTab(SettingsController.NotesTab, "chatai.settings.notes", SettingsEditors.CreateAuthorsNote())
            .AddTab(SettingsController.PhrasesTab, "chatai.settings.phrases", SettingsEditors.CreatePhrases())
            .AddTab(SettingsController.GalleryTab, "chatai.settings.gallery", SettingsEditors.CreateGallery())
            .AddTab(SettingsController.SecretsTab, "chatai.settings.secrets", SettingsEditors.CreateSecrets())
            .AddTab(SettingsController.ImportTab, "chatai.settings.import", SettingsEditors.CreateImport());

    protected override IReadOnlyList<UIDialog> CreatePageDialogs()
        =>
        [
            UIDialogs.BoundForm<SettingsController>(
                SettingsController.RenameDialogKey,
                static x => x.RenameDialogTitle,
                null,
                new TextInputComponent()
                    .SetTitle("chatai.settings.name")
                    .BindValue<SettingsController>(static x => x.RenameName, mode: UIBindingMode.OnSubmit),
                UIButtons.Primary("chatai.settings.rename")
                    .BindTitle<SettingsController>(static x => x.RenameDialogAction)
                    .OnClick(nameof(SettingsController.RenameAsync))
            ),
            UIDialogs.BoundForm<SettingsController>(SettingsController.SecretDialogKey, static x => x.SecretDialogTitle, null,
                UILayout.Stack(12,
                    new TextInputComponent()
                        .SetTitle("chatai.secrets.name")
                        .SetPlaceholder("deepl-api-key")
                        .AsContent(TextInputComponent.PlaceholderProperty)
                        .SetAutocomplete(UIAutocomplete.Off)
                        .BindIsReadOnly<SettingsController>(static x => x.SecretNameReadOnly)
                        .BindValue<SettingsController>(static x => x.SecretName, mode: UIBindingMode.OnSubmit),
                    new TextInputComponent()
                        .SetTitle("chatai.secrets.value")
                        .SetType(UITextInputType.Password)
                        .SetAutocomplete(UIAutocomplete.NewPassword)
                        .BindValue<SettingsController>(static x => x.SecretValue, mode: UIBindingMode.OnSubmit)
                ),
                UIButtons.Primary(UIStandardAction.Save).OnClickShowingLoading(nameof(SettingsController.SaveSecretAsync))
            ),
            UIDialogs.BoundConfirm<SettingsController>(SettingsController.DeleteDialogKey, "chatai.settings.delete.title", static x => x.DeleteQuestion, UIStandardAction.Delete.Title, nameof(SettingsController.DeleteAsync)),
            UIDialogs.Confirm(SettingsController.RestoreDefaultsDialogKey, "chatai.import.defaults.restore.title", "chatai.import.defaults.restore.question", "chatai.import.defaults.restore.confirm", nameof(SettingsController.RestoreDefaultsAsync))
        ];
}

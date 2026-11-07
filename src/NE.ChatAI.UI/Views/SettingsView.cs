using System.Collections.Generic;
using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

/// <summary>The deep settings, reached from the generation panel's foot: a tab per kind of document, and the import.</summary>
internal sealed class SettingsView : ShellView, IUIViewDefinition
{
    public static string ViewKey => "chatai.settings";

    protected override string PageTitle => "chatai.nav.settings";

    protected override IVisualComponent CreatePage()
        => UIPage.Column(UIColumnWidth.Wide, CreateTabs(), centered: true);

    /// <summary>The tabs: what concerns the whole application first, then a tab per kind of document, and the import.</summary>
    private static TabsComponent CreateTabs()
        => new TabsComponent("chatai-settings-tabs")
            .AddTab("general", "chatai.settings.general", SettingsEditors.CreateGeneral())
            .AddTab("connections", "chatai.settings.connections", SettingsEditors.CreateConnection())
            .AddTab("formats", "chatai.settings.formats", SettingsEditors.CreateFormat())
            .AddTab("system-prompts", "chatai.settings.system-prompts", SettingsEditors.CreateSystemPrompt())
            .AddTab("prompts", "chatai.settings.prompts", SettingsEditors.CreatePrompt())
            .AddTab("samplers", "chatai.settings.samplers", SettingsEditors.CreateSamplers())
            .AddTab("notes", "chatai.settings.notes", SettingsEditors.CreateAuthorsNote())
            .AddTab("phrases", "chatai.settings.phrases", SettingsEditors.CreatePhrases())
            .AddTab("import", "chatai.settings.import", SettingsEditors.CreateImport());

    protected override IReadOnlyList<UIDialog> CreatePageDialogs()
        =>
        [
            UIDialogs.Form(SettingsController.RenameDialogKey, "chatai.settings.rename.title", null,
                new TextInputComponent()
                    .SetTitle("chatai.settings.name")
                    .BindValue<SettingsController>(static x => x.RenameName)
                    .OnEnter(nameof(SettingsController.RenameAsync)),
                UIButtons.Primary("chatai.settings.rename").OnClick(nameof(SettingsController.RenameAsync))
            ),
            UIDialogs.BoundConfirm<SettingsController>(SettingsController.DeleteDialogKey, "chatai.settings.delete.title", static x => x.DeleteQuestion, "chatai.settings.delete", nameof(SettingsController.DeleteAsync))
        ];
}

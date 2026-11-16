using System.Collections.Generic;
using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

/// <summary>The <c>/plugins</c> page: installing scenario plugins from GitHub releases, checking updates and managing installed plugins.</summary>
internal sealed class PluginsView : ShellView, IUIViewDefinition
{
    public static string ViewKey => "chatai.plugins";

    protected override string PageTitle => "chatai.nav.plugins";

    protected override IVisualComponent CreatePage()
        => UIPage.Column(UIColumnWidth.Wide,
            UILayout.Stack(16,
                CreateInstallCard(),
                new ItemsViewComponent("chatai-plugins-list")
                    .SetVerticalScroll(UIScrollMode.Disabled)
                    .BindItems<PluginsController>(static x => x.Plugins)
                    .SetSpacing(12)
                    .SetEmptyTemplate(UIPage.Empty(AppIcons.Outline(AppIcons.Plugins), "chatai.plugins.none", "chatai.plugins.none.hint"))
                    .SetTemplate(CreatePluginRow())
            ),
            centered: true
        );

    private static CardComponent CreateInstallCard()
        => UICard.Create("chatai.plugins.install.title", null,
            UILayout.Stack(16,
                UIText.Note("chatai.plugins.install.description"),
                new TextInputComponent()
                    .SetTitle("chatai.plugins.repository")
                    .SetPlaceholder("owner/repo or https://github.com/owner/repo")
                    .AsContent(TextInputComponent.PlaceholderProperty)
                    .SetAutocomplete(UIAutocomplete.Off)
                    .BindValue<PluginsController>(static x => x.RepositoryInput)
                    .OnEnter(nameof(PluginsController.InstallPluginAsync))
                    .AddTrailingAction(UIIconButtons.New()
                        .SetTooltip("chatai.plugins.install")
                        .OnClickShowingLoading(nameof(PluginsController.InstallPluginAsync))
                    )
            ),
            null,
            UIButtons.Icon(AppIcons.Outline(AppIcons.Connect), "chatai.plugins.check-updates")
                .SetSize(UISize.Small)
                .OnClickShowingLoading(nameof(PluginsController.CheckUpdatesAsync))
        );

    private static CardComponent CreatePluginRow()
        => UICard.Create(null, null,
            UILayout.Stack(8,
                UILayout.Ends(
                    new WrapPanelComponent()
                        .SetSpacing(8)
                        .SetLineSpacing(6)
                        .SetVerticalAlignment(UIAlignment.Center)
                        .AddChild(new LinkComponent()
                            .BindTitle<PluginItem>(static x => x.Name, UIBindingScope.Relative)
                            .SetTitleType(UITextAppearance.Subtitle)
                            .BindUrl<PluginItem>(static x => x.RepositoryUrl, UIBindingScope.Relative)
                            .BindTooltip<PluginItem>(static x => x.Repository, UIBindingScope.Relative)
                            .SetTooltipPlain(true)
                            .AsContent(LinkComponent.TitleProperty, LinkComponent.TooltipProperty)
                        )
                        .AddChild(new ItemsViewComponent()
                            .SetVerticalScroll(UIScrollMode.Disabled)
                            .SetLayoutType(UIItemsLayoutType.Wrap)
                            .SetVerticalAlignment(UIAlignment.Start)
                            .SetSpacing(6)
                            .SetShowEmptyTemplate(false)
                            .BindItems<PluginItem>(static x => x.Badges, UIBindingScope.Relative)
                            .SetTemplate(new BadgeComponent()
                                .SetVariant(UIBadgeVariant.Surface)
                                .SetFill(UIBadgeFill.Tinted)
                                .SetTitleType(UITextAppearance.Caption)
                                .BindTitle<BadgeItem>(static x => x.BadgeTitle, UIBindingScope.Relative)
                                .BindColor<BadgeItem>(static x => x.BadgeColor, UIBindingScope.Relative)
                                .AsContent(BadgeComponent.TitleProperty)
                            )
                        ),
                    UIButtons.Accent("chatai.plugins.update", AppIcons.Outline(AppIcons.Connect))
                        .SetSize(UISize.Small)
                        .BindTitle<PluginItem>(static x => x.UpdateTitle, UIBindingScope.Relative)
                        .AsContent(ButtonComponent.TitleProperty)
                        .BindVisibility<PluginItem>(static x => x.UpdateVisibility, UIBindingScope.Relative)
                        .OnClickShowingLoading(nameof(PluginsController.UpdatePluginAsync), UIAction.ArgCurrentItemKey("pluginId")),
                    new SwitchComponent()
                        .SetAccessibleName("chatai.plugins.enabled")
                        .BindValue<PluginItem>(static x => x.Enabled, UIBindingScope.Relative)
                        .OnChange(nameof(PluginsController.TogglePluginEnabledAsync), UIAction.ArgCurrentItemKey("pluginId")),
                    UIIconButtons.Delete()
                        .OnClick(nameof(PluginsController.AskUninstallPlugin), UIAction.ArgCurrentItemKey("pluginId"))
                ),
                UILayout.Stack(2,
                    UIText.Body()
                        .BindDescription<PluginItem>(static x => x.Description, UIBindingScope.Relative)
                        .AsContent(TextComponent.DescriptionProperty)
                        .SetWrapMode(UITextWrapMode.Wrap)
                        .BindVisibility<PluginItem>(static x => x.DescriptionVisibility, UIBindingScope.Relative),
                    UIText.Caption()
                        .Muted()
                        .BindTitle<PluginItem>(static x => x.NodesLine, UIBindingScope.Relative)
                        .AsContent(TextComponent.TitleProperty)
                        .SetWrapMode(UITextWrapMode.Wrap)
                        .BindVisibility<PluginItem>(static x => x.NodesVisibility, UIBindingScope.Relative)
                ),
                UIText.Caption()
                    .SetTitleColor(UIThemeColor.Danger)
                    .BindTitle<PluginItem>(static x => x.LoadError, UIBindingScope.Relative)
                    .AsContent(TextComponent.TitleProperty)
                    .SetWrapMode(UITextWrapMode.Wrap)
                    .BindVisibility<PluginItem>(static x => x.ErrorVisibility, UIBindingScope.Relative)
            )
        );

    protected override IReadOnlyList<UIDialog> CreatePageDialogs()
        =>
        [
            UIDialogs.BoundConfirm<PluginsController>(
                PluginsController.UninstallDialogKey,
                "chatai.plugins.uninstall.title",
                static x => x.UninstallQuestion,
                "chatai.plugins.uninstall.confirm",
                nameof(PluginsController.UninstallPluginAsync)
            )
        ];
}

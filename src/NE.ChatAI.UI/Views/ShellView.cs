using System.Collections.Generic;
using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

internal abstract class ShellView : UIViewBase
{
    public const string GenerationPanelId = "chatai-generation-panel";
    public const string ChatsPanelId = "chatai-chats-panel";

    // Notifications stack at the top so mobile toasts never cover the bottom chat composer.
    public override UIViewOptions Options { get; } = new()
    {
        ScrollContentOnly = true,
        ShellLayout = UIShellLayout.FullHeightSides,
        SideDrawers = UISideDrawers.Always,
        NotificationPlacement = UINotificationPlacement.Top
    };

    public override string Title => "chatai.title";

    public override IReadOnlyDictionary<string, object?>? TitleArguments
        => new Dictionary<string, object?> { ["page"] = new UIPhrase(PageTitle) };

    protected abstract string PageTitle { get; }

    protected virtual bool HasPageHead => true;

    protected virtual IVisualComponent[] HeaderActions => [];

    protected virtual UIColumnWidth? HeadColumnWidth => UIColumnWidth.Wide;

    protected virtual bool FillsHeight => false;

    protected virtual UIResponsive<UIThickness> ContentPadding
        => UIResponsive<UIThickness>.Create(UIThickness.All(12, 4, 12, 32), md: UIThickness.All(24, 8, 24, 48));

    protected virtual bool HasGenerationPanel => false;

    protected sealed override IVisualComponent? CreateHeader()
    {
        if (!HasPageHead)
            return null;

        ContainerComponent header = UIPage.Header(UIIconButtons.Back().SetUrl(AppRoutes.Home), PageTitle, null, [.. HeaderActions, CreateChatsButton()]);

        return HeadColumnWidth is UIColumnWidth width ? header.InColumn(width) : header;
    }

    protected static ButtonComponent CreateChatsButton()
        => UIButtons.Icon(AppIcons.Outline(AppIcons.Chats), "chatai.panel.chats").SetOpensDrawer(UISide.Right);

    protected static ButtonComponent CreateGenerationButton()
        => UIButtons.Icon(AppIcons.Outline(AppIcons.Generation), "chatai.panel.generation").SetOpensDrawer(UISide.Left);

    protected override IVisualComponent CreateContent()
    {
        StackPanelComponent content = UILayout.Stack(0, CreatePage()).SetPadding(ContentPadding);

        return FillsHeight ? content.SetHeight(UILayoutLength.Fill()) : content;
    }

    protected abstract IVisualComponent CreatePage();

    protected sealed override IReadOnlyList<UIDialog> CreateDialogs()
        => [.. CreatePageDialogs(), .. ChatsPanel.CreateDialogs()];

    protected virtual IReadOnlyList<UIDialog> CreatePageDialogs()
        => [];

    protected sealed override IVisualComponent? CreateLeftSide()
        => !HasGenerationPanel
            ? CreatePageLeftSide()
            : UIPage.Drawer(
                    GenerationPanelId,
                    UISide.Left,
                    GenerationPanel.CreateHeading(),
                    subheader: null,
                    GenerationPanel.Create(),
                    footer: new WrapPanelComponent()
                        .SetSpacing(4)
                        .SetLineSpacing(4)
                        .AddChild(CreateDrawerNavButton("chatai.nav.settings", AppIcons.Settings, AppRoutes.Settings))
                        .AddChild(CreateDrawerNavButton("chatai.nav.worlds", AppIcons.Bindings, AppRoutes.Worlds))
                        .AddChild(CreateDrawerNavButton("chatai.nav.scenarios", AppIcons.Scenarios, AppRoutes.Scenarios))
                        .AddChild(CreateDrawerNavButton("chatai.nav.plugins", AppIcons.Plugins, AppRoutes.Plugins)),
                    pinnable: true
                )
                .OnDrawerOpen(nameof(ShellController.LoadGenerationPanel));

    private static ButtonComponent CreateDrawerNavButton(string title, string icon, string url)
        => UIButtons.Ghost(title, AppIcons.Outline(icon))
            .SetSize(UISize.Small)
            .SetTitleColor(UIThemeColor.Muted)
            .SetIconColor(UIThemeColor.Muted)
            .SetUrl(url);

    protected virtual IVisualComponent? CreatePageLeftSide()
        => null;

    protected sealed override IVisualComponent? CreateRightSide()
        => UIPage.Drawer(
                ChatsPanelId,
                UISide.Right,
                ChatsPanel.CreateHeading(),
                subheader: null,
                ChatsPanel.CreateBody(),
                footer: null,
                pinnable: true,
                ChatsPanel.CreateActions()
            )
            .OnDrawerOpen(nameof(ShellController.LoadChatsPanelAsync));
}

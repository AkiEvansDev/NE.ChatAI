using System.Collections.Generic;
using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

/// <summary>
/// The frame every page wears: a pinned panel the whole height beside the page — the generation settings at its left and the chats
/// at its right — slid out by buttons the page places itself: the chat, which takes the whole height, in its composer; any other
/// page in the head over its content.
/// </summary>
/// <remarks>
/// The panels are the view's sides, modal drawers at every width — the tablet held upright this is used on gives the chat its whole
/// width — that dim the page and close on a press beside them until the reader pins one, which then stands in its column.
/// </remarks>
internal abstract class ShellView : UIViewBase
{
    /// <summary>A panel's width, the same slid out and pinned.</summary>
    private static readonly UIResponsive<UILayoutLength> PanelWidth = UIResponsive<UILayoutLength>.Create(UILayoutLength.Absolute(320), md: UILayoutLength.Absolute(380));

    /// <summary>A panel's padding, in which its body's scrollbar stands, so the body's rows end at the head's edge.</summary>
    private const double PanelPadding = 12;

    /// <remarks>Notifications stack at the top: at the bottom, on a phone, they cover the composer and its Send.</remarks>
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

    /// <summary>The page's name, for the browser's tab and history.</summary>
    protected abstract string PageTitle { get; }

    /// <summary>Whether the page has the generation panel at its left: the chat has; a page of settings or of a character has not.</summary>
    protected virtual bool HasGenerationPanel => false;

    /// <summary>The generation panel's button; gone while the panel is pinned.</summary>
    protected static ButtonComponent CreateGenerationButton()
        => UIButtons.Icon(AppIcons.Outline(AppIcons.Generation), "chatai.panel.generation").SetOpensDrawer(UISide.Left);

    /// <summary>The chats panel's button, which reads the chats as it opens them; gone while the panel is pinned.</summary>
    protected static ButtonComponent CreateChatsButton()
        => UIButtons.Icon(AppIcons.Outline(AppIcons.Chats), "chatai.panel.chats")
            .SetOpensDrawer(UISide.Right)
            .OnClick(nameof(ShellController.LoadChatsPanelAsync));

    /// <summary>Whether the page wears a head; the chat has its composer instead.</summary>
    protected virtual bool HasPageHead => true;

    /// <summary>What the page itself may do, at its head before the chats panel's button: a card's delete.</summary>
    protected virtual IVisualComponent[] HeaderActions => [];

    /// <summary>The column the page's head keeps to; <see langword="null"/> on a full-bleed canvas page so its head spans the page.</summary>
    protected virtual UIColumnWidth? HeadColumnWidth => UIColumnWidth.Wide;

    /// <summary>The page's head: back to the chat, the page's title, the chats panel's button.</summary>
    /// <remarks>
    /// In the view's header rather than over the page, since the band's insets are measured for that region; kept to the page's
    /// reading column, so the title stands over the content's edge on a wide screen too.
    /// </remarks>
    protected sealed override IVisualComponent? CreateHeader()
    {
        if (!HasPageHead)
            return null;

        ContainerComponent header = UIPage.Header(UIIconButtons.Back().SetUrl(AppRoutes.Home), PageTitle, null, [.. HeaderActions, CreateChatsButton()]);

        return HeadColumnWidth is UIColumnWidth width ? header.InColumn(width) : header;
    }

    /// <summary>
    /// The page in the region it scrolls in; a page that wants the whole height — the chat — fills it and scrolls inside, any other
    /// grows and the region scrolls it.
    /// </summary>
    protected override IVisualComponent CreateContent()
    {
        StackPanelComponent content = new StackPanelComponent()
            .SetOrientation(UIOrientation.Vertical)
            .SetPadding(ContentPadding)
            .AddChild(CreatePage());

        return FillsHeight ? content.SetHeight(UILayoutLength.Fill()) : content;
    }

    /// <summary>Whether the page takes the region's whole height and scrolls inside itself rather than in the region.</summary>
    protected virtual bool FillsHeight => false;

    protected virtual UIResponsive<UIThickness> ContentPadding
        => UIResponsive<UIThickness>.Create(UIThickness.All(12, 4, 12, 32), md: UIThickness.All(24, 8, 24, 48));

    protected abstract IVisualComponent CreatePage();

    protected sealed override IReadOnlyList<UIDialog> CreateDialogs()
        => [.. CreatePageDialogs(), .. ChatsPanel.CreateDialogs()];

    protected virtual IReadOnlyList<UIDialog> CreatePageDialogs()
        => [];

    /// <summary>The settings changed while chatting, with the ways to the settings and scenarios pages at its foot.</summary>
    protected sealed override IVisualComponent? CreateLeftSide()
        => !HasGenerationPanel
            ? null
            : CreatePanel("chatai-generation-panel",
                UILayout.Ends(
                    UIText.Subtitle("chatai.panel.generation"),
                    UIIconButtons.Pin().SetPinsDrawer(UISide.Left).SetSize(UISize.Small),
                    UIIconButtons.Close().SetOpensDrawer(UISide.Left).SetSize(UISize.Small)
                ),
                GenerationPanel.Create(),
                new WrapPanelComponent()
                    .SetSpacing(16)
                    .SetLineSpacing(8)
                    .AddChild(new LinkComponent().SetTitle("chatai.nav.settings").SetIcon(AppIcons.Outline(AppIcons.Settings)).SetUrl(AppRoutes.Settings))
                    .AddChild(new LinkComponent().SetTitle("chatai.nav.scenarios").SetIcon(AppIcons.Outline(AppIcons.Scenarios)).SetUrl(AppRoutes.Scenarios))
                    .AddChild(new LinkComponent().SetTitle("chatai.nav.plugins").SetIcon(AppIcons.Outline(AppIcons.Plugins)).SetUrl(AppRoutes.Plugins))
            );

    /// <summary>A panel's fixed head, its scrollable body taking the room left, and the links at its foot.</summary>
    /// <remarks>A pin is kept under the first id in its side, so the panel carries its own rather than lend it a field's.</remarks>
    private static ContainerComponent CreatePanel(string id, IVisualComponent head, IVisualComponent body, IVisualComponent? foot)
    {
        UIThickness horizontal = UIThickness.All(PanelPadding, 0, PanelPadding, 0);

        ContainerComponent panel = new ContainerComponent(id)
            .SetRow(1, UIGridUnit.Auto())
            .AddRow(UIGridUnit.Star())
            .AddRow(UIGridUnit.Auto())
            .SetWidth(PanelWidth)
            .SetHeight(UILayoutLength.Fill())
            .SetPadding(UIThickness.All(0, PanelPadding, 0, PanelPadding))
            .SetSpacing(12)
            .AddChild(new ContainerComponent().SetPadding(horizontal).AddChild(head).SetPlacement(1, 1, 24, 1))
            .AddChild(new ScrollContainerComponent()
                .SetHeight(UILayoutLength.Fill())
                .SetPadding(UIThickness.All(PanelPadding, 2, PanelPadding, 2))
                .AddChild(body)
                .SetPlacement(1, 2, 24, 1)
            );

        return foot is null ? panel : panel.AddChild(new ContainerComponent().SetPadding(horizontal).AddChild(foot).SetPlacement(1, 3, 24, 1));
    }

    /// <summary>The chats to pick from.</summary>
    protected sealed override IVisualComponent? CreateRightSide()
        => CreatePanel("chatai-chats-panel", ChatsPanel.CreateHead(), ChatsPanel.CreateBody(), null);
}

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

/// <summary>
/// The home page: the chat. The feed takes the height and stays at its end as a reply streams in; every message stands on the left
/// in one style, its author's mark beside it; the composer is under the feed.
/// </summary>
internal sealed class ChatView : ShellView, IUIViewDefinition
{
    /// <summary>The column the feed keeps to on a wide screen, so a line of prose stays readable.</summary>
    private const double ChatWidth = 860;

    /// <summary>The central backdrop column's width on a wide screen, adding horizontal air around the feed.</summary>
    private const double ChatColumnWidth = ChatWidth + 32;

    private const double AvatarSize = 40;

    public static string ViewKey => "chatai.chat";

    protected override string PageTitle => "chatai.chat.header";

    protected override bool FillsHeight => true;

    protected override bool HasGenerationPanel => true;

    protected override bool HasPageHead => false;

    /// <summary>Zero so the background surface stretches edge-to-edge across the entire viewport; the surface pads its inner column on desktop.</summary>
    protected override UIResponsive<UIThickness> ContentPadding
        => UIResponsive<UIThickness>.Create(UIThickness.Uniform(0));

    /// <summary>The chat's picture behind the whole page, dimmed toward the theme's ground, with the central chat column over it.</summary>
    protected override IVisualComponent CreatePage()
        => new SurfaceComponent()
            .SetSurface(UISurfaceStyle.Background)
            .SetBackgroundImageFit(UIImageFit.Cover)
            .BindBackgroundImage<ChatController>(static x => x.Background)
            .BindBackgroundImageDim<ChatController>(static x => x.BackgroundDim)
            .SetPadding(UIResponsive<UIThickness>.Create(UIThickness.Uniform(0)))
            .SetBorderThickness(UIThickness.Uniform(0))
            .SetBorderRadius(UICornerRadius.Uniform(0))
            .SetHeight(UILayoutLength.Fill())
            .SetWidth(UILayoutLength.Fill())
            .SetContent(new ContainerComponent()
                .BindBackground<ChatController>(static x => x.MessageBackground)
                .SetRow(1, UIGridUnit.Star())
                .AddRow(UIGridUnit.Auto())
                .SetHeight(UILayoutLength.Fill())
                .SetWidth(UILayoutLength.Fill())
                .SetMaxWidth(UILayoutLength.Absolute(ChatColumnWidth))
                .SetHorizontalAlignment(UIAlignment.Center)
                .AddChild(CreateFeed().SetPlacement(1, 1, 24, 1))
                .AddChild(CreateComposer().SetPlacement(1, 2, 24, 1))
            );

    private static ItemsViewComponent CreateFeed()
        => new ItemsViewComponent(ChatController.FeedId)
            .BindSource<ChatController>(static x => x.Messages)
            .SetWindowSize(ChatController.WindowSize)
            // Messages differ in height: no grey rows stand in, a ring turns at the top while older ones are read.
            .SetLoadingLook(UIItemsLoadingLook.Indicator)
            .AnchorToEnd()
            .SetSpacing(16)
            .SetPadding(UIResponsive<UIThickness>.Create(UIThickness.Symmetric(12, 12), xl: UIThickness.Symmetric(16, 12)))
            .SetHeight(UILayoutLength.Fill())
            .SetTextSelectable(true)
            .ConfigureDefaultEmptyTemplate(empty => empty.BindTitle<ChatController>(static x => x.EmptyFeed).SetTitleWrap(true))
            .SetTemplate(CreateMessage());

    /// <summary>
    /// The author's picture — the person's glyph for one with none — and token count on the left, then their name over the words, Edit and
    /// the message's menu beside it, or while it is edited Done, Cancel and Delete; under the words, any scenario records and the compact
    /// variant switcher sharing the top footer line.
    /// </summary>
    /// <remarks>Nothing else under the words: what is done to the last message is in the composer's extras as well.</remarks>
    private static ContainerComponent CreateMessage()
        => new ContainerComponent()
            .SetColumn(1, UIGridUnit.Auto())
            .SetRow(1, UIGridUnit.Auto())
            .AddRow(UIGridUnit.Star())
            .AddChild(CreateAuthorColumn().SetPlacement(1, 1, 1, 2))
            .AddChild(UILayout.Ends(
                    new TextComponent().BindTitle<ChatMessageItem>(static x => x.Author, UIBindingScope.Relative).AsSubtitle(),
                    RowAction(UIIconButtons.Edit(), static x => x.ToolsVisibility, nameof(ChatController.EditAsync)),
                    CreateMessageMenu(),
                    RowAction(UIIconButtons.Done(), static x => x.EditorVisibility, nameof(ChatController.SaveEditAsync)),
                    RowAction(UIIconButtons.Cancel(), static x => x.EditorVisibility, nameof(ChatController.CancelEditAsync)),
                    RowAction(UIIconButtons.Delete(), static x => x.EditorVisibility, nameof(ChatController.AskDeleteAsync))
                )
                .SetPlacement(2, 1, 23, 1)
            )
            .AddChild(UILayout.Stack(4,
                    CreateThought(),
                    CreateLoading(),
                    CreateMessageText().BindVisibility<ChatMessageItem>(static x => x.TextVisibility, UIBindingScope.Relative),
                    CreateEditor(),
                    CreateMessageFooter()
                )
                .SetMargin(UIThickness.All(0, 4, 0, 0))
                .SetPlacement(2, 2, 23, 1)
            );

    /// <summary>The picture at the top, with the message's token count under it.</summary>
    private static StackPanelComponent CreateAuthorColumn()
        => UILayout.Stack(4,
                new ImageComponent()
                    .BindSource<ChatMessageItem>(static x => x.Avatar, UIBindingScope.Relative)
                    .SetAltText("chatai.chat.avatar")
                    .SetShape(UIShape.Circle)
                    .SetWidth(UILayoutLength.Absolute(AvatarSize))
                    .SetMargin(UIThickness.All(0, 2, 0, 0)),
                new TextComponent()
                    .BindTitle<ChatMessageItem>(static x => x.TokenCount, UIBindingScope.Relative)
                    .BindTooltip<ChatMessageItem>(static x => x.TokenCountTooltip, UIBindingScope.Relative)
                    .AsContent(TextComponent.TitleProperty)
                    .AsCaption()
                    .Muted()
                    .SetHorizontalAlignment(UIAlignment.Center)
                    .BindVisibility<ChatMessageItem>(static x => x.TokenCountVisibility, UIBindingScope.Relative)
            )
            .SetMargin(UIThickness.All(0, 0, 12, 0));

    /// <summary>A button beside the author, shown by the row's <paramref name="visibility"/>: Edit at rest; Done, Cancel and Delete while edited.</summary>
    private static ButtonComponent RowAction(ButtonComponent button, Expression<Func<ChatMessageItem, object?>> visibility, string command)
        => button
            .SetSize(UISize.Small)
            .BindVisibility(visibility, UIBindingScope.Relative)
            .OnClick(command, UIAction.ArgCurrentItemKey("messageId"));

    /// <summary>The message's own menu beside its author, its entries the row's: the entry says what, the row says which.</summary>
    private static SplitButtonComponent CreateMessageMenu()
        => UIIconButtons.More()
            .SetSize(UISize.Small)
            .BindItems<ChatMessageItem>(static x => x.Actions, UIBindingScope.Relative)
            .BindVisibility<ChatMessageItem>(static x => x.ToolsVisibility, UIBindingScope.Relative)
            .OnItemClick(nameof(ChatController.MessageActionAsync), UIAction.ArgCurrentItemKey("action"), UIAction.ArgParent<ChatMessageItem>("messageId", static x => x.Id));

    /// <summary>The model's thought, folded over its words; opened, it reads as plain Markdown set off as a quote by a line at its start.</summary>
    private static ExpanderComponent CreateThought()
        => UIPage.BoundFold<ChatMessageItem>(static x => x.ThoughtTitle, UIBindingScope.Relative, false,
                UIText.Quote(new MarkdownDisplayComponent()
                        .BindText<ChatMessageItem>(static x => x.Thought, UIBindingScope.Relative)
                        .SetLineBreaks(true),
                    UIThemeColor.Muted
                )
            )
            .BindVisibility<ChatMessageItem>(static x => x.ThoughtVisibility, UIBindingScope.Relative)
            .SetMargin(UIThickness.All(0, 0, 0, 8));

    /// <summary>Three animated dots shown in the message's place while the model reads the prompt before any words arrive.</summary>
    private static SpinnerComponent CreateLoading()
        => new SpinnerComponent()
            .SetLoadingStyle(UILoadingStyle.Dots)
            .SetColor(UIThemeColor.Muted)
            .BindVisibility<ChatMessageItem>(static x => x.LoadingVisibility, UIBindingScope.Relative);

    /// <summary>
    /// A message's words the way roleplay writes them: narration in <c>*…*</c> muted, speech in quotes in the accent, a lone newline
    /// a line break as it was typed.
    /// </summary>
    private static MarkdownDisplayComponent CreateMessageText()
        => StyleMessageMarkdown(new MarkdownDisplayComponent().BindText<ChatMessageItem>(static x => x.Text, UIBindingScope.Relative));

    /// <summary>Applies roleplay Markdown styling: muted narration and accent speech.</summary>
    public static MarkdownDisplayComponent StyleMessageMarkdown(MarkdownDisplayComponent markdown)
    {
        UIMarkdownTextStyle speech = new() { Color = UIThemeColor.Accent };

        return markdown
            .SetLineBreaks(true)
            .SetMarkStyle(UIMarkdownMark.Emphasis, new UIMarkdownTextStyle { Color = UIThemeColor.Muted })
            .AddSpanStyle("\"", "\"", speech)
            .AddSpanStyle("“", "”", speech)
            .AddSpanStyle("«", "»", speech);
    }

    /// <summary>A message's words edited in their own place, kept or left by the buttons beside the author; Escape leaves them as they were.</summary>
    private static TextAreaComponent CreateEditor()
        => new TextAreaComponent()
            .SetRows(3)
            .SetAutoGrow(20)
            .BindValue<ChatMessageItem>(static x => x.EditDraft, UIBindingScope.Relative)
            .OnEscape(nameof(ChatController.CancelEditAsync), UIAction.ArgCurrentItemKey("messageId"))
            .BindVisibility<ChatMessageItem>(static x => x.EditorVisibility, UIBindingScope.Relative);

    /// <summary>Places scenario records on the left and the variant switcher on the top-right of the same row.</summary>
    private static ContainerComponent CreateMessageFooter()
        => new ContainerComponent()
            .AddChild(CreateRecords().SetPlacement(1, 1, 24, 1))
            .AddChild(CreateVariantBar().SetPlacement(1, 1, 24, 1));

    /// <summary>Scenario records anchored under the message, each folded like a thought and editable or removable inside its body.</summary>
    private static ItemsViewComponent CreateRecords()
        => new ItemsViewComponent(ChatController.MessageRecordsId)
            .SetVerticalScroll(UIScrollMode.Disabled)
            .BindItems<ChatMessageItem>(static x => x.Records, UIBindingScope.Relative)
            .SetSpacing(6)
            .SetShowEmptyTemplate(false)
            .SetTemplate(UIPage.BoundFold<ChatRecordItem>(static x => x.Title, UIBindingScope.Relative, false,
                    new ContainerComponent()
                        .SetColumn(24, UIGridUnit.Auto())
                        .AddChild(UIText.Quote(new MarkdownDisplayComponent()
                                    .BindText<ChatRecordItem>(static x => x.Text, UIBindingScope.Relative)
                                    .SetLineBreaks(true),
                                UIThemeColor.Muted
                            )
                            .BindVisibility<ChatRecordItem>(static x => x.TextVisibility, UIBindingScope.Relative)
                            .SetPlacement(1, 1, 23, 1)
                        )
                        .AddChild(new TextAreaComponent()
                            .SetRows(4)
                            .SetAutoGrow(20)
                            .BindValue<ChatRecordItem>(static x => x.EditDraft, UIBindingScope.Relative)
                            .OnEscape(nameof(ChatController.CancelRecordEditAsync), UIAction.ArgCurrentItemKey("recordId"))
                            .BindVisibility<ChatRecordItem>(static x => x.EditorVisibility, UIBindingScope.Relative)
                            .SetPlacement(1, 1, 23, 1)
                        )
                        .AddChild(UIButtons.Toolbar(
                                RecordAction(UIIconButtons.Edit(), static x => x.ToolsVisibility, nameof(ChatController.EditRecordAsync)),
                                RecordAction(UIIconButtons.Done(), static x => x.EditorVisibility, nameof(ChatController.SaveRecordEditAsync)),
                                RecordAction(UIIconButtons.Cancel(), static x => x.EditorVisibility, nameof(ChatController.CancelRecordEditAsync)),
                                UIIconButtons.Delete().SetSize(UISize.Small).OnClick(nameof(ChatController.DeleteRecordAsync), UIAction.ArgCurrentItemKey("recordId"))
                            )
                            .SetVerticalAlignment(UIAlignment.Start)
                            .SetMargin(UIThickness.All(8, 0, 0, 0))
                            .BindVisibility<ChatRecordItem>(static x => x.ActionsVisibility, UIBindingScope.Relative)
                            .SetPlacement(24, 1, 1, 1)
                        )
                )
            );

    private static ButtonComponent RecordAction(ButtonComponent button, Expression<Func<ChatRecordItem, object?>> visibility, string command)
        => button
            .SetSize(UISize.Small)
            .BindVisibility(visibility, UIBindingScope.Relative)
            .OnClick(command, UIAction.ArgCurrentItemKey("recordId"));

    /// <summary>The compact variant switcher under the message, aligned with the first record's header when records are present.</summary>
    private static StackPanelComponent CreateVariantBar()
        => UILayout.Row(2,
                SwipeArrow(AppIcons.Previous, "chatai.chat.previous", nameof(ChatController.PreviousVariantAsync), static x => x.HasPrevious),
                new TextComponent()
                    .BindTitle<ChatMessageItem>(static x => x.VariantLabel, UIBindingScope.Relative)
                    .AsContent(TextComponent.TitleProperty)
                    .AsCaption()
                    .Muted()
                    .SetVerticalAlignment(UIAlignment.Center),
                SwipeArrow(AppIcons.Next, "chatai.chat.next", nameof(ChatController.NextVariantAsync), static x => x.HasNext)
            )
            .SetGlyphEdge(UIAlignment.End)
            .SetHorizontalAlignment(UIAlignment.End)
            .SetVerticalAlignment(UIAlignment.Start)
            .SetMargin(UIThickness.All(0, -6, 0, -6))
            .BindVisibility<ChatMessageItem>(static x => x.SwipeVisibility, UIBindingScope.Relative);

    /// <summary>A swipe's arrow, sized to match the message's header buttons above it.</summary>
    private static ButtonComponent SwipeArrow(string icon, string label, string command, Expression<Func<ChatMessageItem, object?>> enabled)
        => UIButtons.Icon(AppIcons.Outline(icon), label)
            .SetSize(UISize.Small)
            .SetVerticalAlignment(UIAlignment.Center)
            .BindEnabled(enabled, UIBindingScope.Relative)
            .OnClick(command, UIAction.ArgCurrentItemKey("messageId"));

    /// <summary>
    /// The draft in a composer panel, sent by Enter or the button — while a reply is written the button stops it instead — with the
    /// extras and the panels' buttons at its start, where a hand holding the tablet reaches them; on a phone the panels' buttons stand
    /// in a row under the field, each at its panel's side, the extras and the button kept in it. Square and flush with the foot and
    /// edges of the central chat column at every width.
    /// </summary>
    /// <remarks>One button for Send and Stop: a field holds one action in it at each end.</remarks>
    private static SurfaceComponent CreateComposer()
        => UIForm.Composer(new TextAreaComponent(ChatController.ComposerId)
                .SetPlaceholder("chatai.chat.placeholder")
                .SetAutoGrow(8)
                .OnEnter(nameof(ChatController.SendAsync))
                .SetFormId(ChatController.ComposerFormId)
                .BindValue<ChatController>(static x => x.Draft)
                .AddLeadingAction(CreateExtrasMenu(), inField: true)
                .AddLeadingAction(CreateGenerationButton())
                .AddLeadingAction(CreateChatsButton(), belowAt: UIAlignment.End)
                .AddTrailingAction(UIIconButtons.Send()
                        .BindIcon<ChatController>(static x => x.SendIcon)
                        .BindTooltip<ChatController>(static x => x.SendTooltip)
                        .OnSubmit(ChatController.ComposerFormId, nameof(ChatController.SendOrStopAsync)),
                    inField: true
                )
            )
            .SetBorderRadius(UICornerRadius.Uniform(0));

    /// <summary>
    /// What else may be done from the composer, always at its start, opening over the chat: the last reply made again, given a new
    /// variant or continued, each disabled where the last message does not allow it; then what is still to come.
    /// </summary>
    private static SplitButtonComponent CreateExtrasMenu()
        => UIIconButtons.More()
            .SetPopupPlacement(UIPopupPlacement.TopStart)
            .BindItems<ChatController>(static x => x.Extras)
            .OnItemClick(ChatController.PhrasesAction, InsertTextEffect.CurrentItemValue(ChatController.ComposerId))
            .OnItemClick(nameof(ChatController.ExtraActionAsync), UIAction.ArgCurrentItemKey("action"));

    /// <summary>
    /// The chat's own author's note, kept as it lets go of its value; and the questions before messages go: one deleted, the chat
    /// deleted from there on, a message made again with those after it.
    /// </summary>
    protected override IReadOnlyList<UIDialog> CreatePageDialogs()
        =>
        [
            ChatStatisticsDialogs.CreateChatStatistics(),
            ChatStatisticsDialogs.CreateMessageInfo(),
            CreatePromptDialog(),
            UIDialogs.Create(ChatController.NoteDialogKey, "chatai.chat.note.title", UILayout.Stack(16,
                    UIText.Note("chatai.chat.note.description"),
                    SettingsEditors.MacroInput("chatai.chat.note.note", 8)
                        .BindValue<ChatController>(static x => x.ChatNote)
                        .OnChange(nameof(ChatController.SaveChatNoteAsync)),
                    PictureFields.Background<ChatController>("chatai.chat.background", "chatai.chat.background.hint", static x => x.ChatBackground, nameof(ChatController.SaveChatBackgroundAsync), nameof(ChatController.RemoveChatBackgroundAsync), static x => x.ChatBackgroundPreviewAuthor, static x => x.ChatBackgroundPreviewAvatar)
                ),
                width: UIColumnWidth.Form
            ),
            UIDialogs.Confirm(ChatController.DeleteDialogKey, "chatai.chat.delete.title", (UIPhrase?)"chatai.chat.delete.question", "chatai.chat.delete", nameof(ChatController.DeleteMessageAsync)),
            UIDialogs.Confirm(ChatController.DeleteFromDialogKey, "chatai.chat.delete-from.title", (UIPhrase?)"chatai.chat.delete-from.question", "chatai.chat.delete", nameof(ChatController.DeleteFromAsync)),
            UIDialogs.Confirm(ChatController.RefreshDialogKey, "chatai.chat.refresh.title", (UIPhrase?)"chatai.chat.refresh.question", "chatai.chat.regenerate", nameof(ChatController.RefreshMessageAsync))
        ];

    private static UIDialog CreatePromptDialog()
    {
        UIDialog dialog = UIDialogs.Create(ChatController.PromptDialogKey, "chatai.chat.prompt.title", new ContainerComponent(), width: UIColumnWidth.Wide);
        StackPanelComponent original = (StackPanelComponent)dialog.Content;
        IVisualComponent header = original.Children[0];

        return dialog with
        {
            Height = UIResponsive<UILayoutLength>.Create(UILayoutLength.Fill()),
            Content = new ContainerComponent()
                .SetWidth(original.Width)
                .SetRow(1, UIGridUnit.Auto())
                .AddRow(UIGridUnit.Auto())
                .AddRow(UIGridUnit.Star())
                .SetSpacing(12)
                .SetHeight(UILayoutLength.Fill())
                .AddChild(new ContainerComponent().AddChild(header).SetPlacement(1, 1, 24, 1))
                .AddChild(UIText.Note().BindDescription<ChatController>(static x => x.PromptSummary).SetPlacement(1, 2, 24, 1))
                .AddChild(SettingsEditors.StyleMacroCode(new CodeInputComponent())
                    .SetAppearance(UIInputAppearance.Filled)
                    .SetWrapLines(true)
                    .SetStatusBar(false)
                    .SetLineNumbers(true)
                    .SetCompletions(false)
                    .SetIsReadOnly(true)
                    .SetHeight(UILayoutLength.Fill())
                    .SetVerticalAlignment(UIAlignment.Stretch)
                    .BindValue<ChatController>(static x => x.PromptText)
                    .SetPlacement(1, 3, 24, 1)
                )
        };
    }
}

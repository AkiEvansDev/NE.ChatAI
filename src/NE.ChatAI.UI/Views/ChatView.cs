using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

internal sealed class ChatView : ShellView, IUIViewDefinition
{
    private const double ChatWidth = 860;
    private const double ChatColumnWidth = ChatWidth + 32;
    private const double AvatarSize = 40;

    // Matches the small header action buttons so hiding message tools while streaming never shifts the feed.
    private const double MessageHeaderHeight = 28;

    // Reserves the avatar's top offset, height, gap and token caption (2 + 40 + 4 + 16) before tokens are counted.
    private const double AuthorColumnHeight = 62;

    public static string ViewKey => "chatai.chat";

    protected override string PageTitle => "chatai.chat.header";

    protected override bool FillsHeight => true;

    protected override bool HasGenerationPanel => true;

    protected override bool HasPageHead => false;

    // Zero so the background surface stretches edge-to-edge across the viewport.
    protected override UIResponsive<UIThickness> ContentPadding
        => UIResponsive<UIThickness>.Create(UIThickness.Uniform(0));

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
            .SetContent(new ContainerComponent(ChatController.StageSplitId)
                .SetColumn(11, UIGridUnit.Star(), xl: UIGridUnit.Auto())
                .SetRow(1, UIGridUnit.Star())
                .AddRow(UIGridUnit.Auto())
                .AddRow(UIGridUnit.Star(), xl: UIGridUnit.Auto())
                .SetHeight(UILayoutLength.Fill())
                .SetWidth(UILayoutLength.Fill())
                .AddChild(CreateStagePane())
                .AddChild(new GridSplitterComponent(ChatController.StageSplitterId)
                    .SetOrientation(UIOrientation.Horizontal, xl: UIOrientation.Vertical)
                    .BindVisibility<ChatController>(static x => x.StageVisibility)
                    .SetPlacement(1, 2, 24, 1, xl: UIGridPlacement.At(11, 1, 1, 1))
                )
                .AddChild(CreateChatColumn())
            );

    private static ContainerComponent CreateStagePane()
        => new ContainerComponent()
            .BindVisibility<ChatController>(static x => x.StageVisibility)
            .SetPadding(UIResponsive<UIThickness>.Create(UIThickness.Symmetric(12, 8), xl: UIThickness.Symmetric(16, 16)))
            .SetHeight(UILayoutLength.Fill())
            .SetWidth(UILayoutLength.Fill())
            .SetMinHeight(UILayoutLength.Absolute(0))
            .SetOverflow(UIOverflow.Hidden)
            .SetPlacement(UIResponsive<UIGridPlacement>.Create(new UIGridPlacement(1, 1, 24, 1), xl: new UIGridPlacement(1, 1, 10, 1)))
            .AddChild(new SurfaceComponent()
                .SetAccessibleName("chatai.chat.stage")
                .SetBackground(UIThemeColor.Transparent)
                .SetBorderThickness(UIThickness.Uniform(0))
                .SetBorderRadius(UICornerRadius.Uniform(8))
                .SetPadding(UIThickness.Uniform(8))
                .SetHeight(UILayoutLength.Fill())
                .SetWidth(UILayoutLength.Fill())
                .SetMinHeight(UILayoutLength.Absolute(0))
                .SetQuietHover(true)
                .OnClickShowImage()
                .SetContent(new ImageComponent(ChatController.StageImageId)
                    .BindSource<ChatController>(static x => x.StageImage)
                    .SetAltText("chatai.chat.stage")
                    .SetFit(UIImageFit.Contain)
                    .SetHeight(UILayoutLength.Fill())
                    .SetWidth(UILayoutLength.Fill())
                )
                .SetPlacement(1, 1, 24, 1)
            )
            .AddChild(UIIconButtons.Close()
                .SetSize(UISize.Small)
                .SetHorizontalAlignment(UIAlignment.End)
                .SetVerticalAlignment(UIAlignment.Start)
                .SetMargin(UIThickness.Uniform(4))
                .OnClick(nameof(ChatController.HideStageAsync))
                .SetPlacement(1, 1, 24, 1)
            );

    private static ContainerComponent CreateChatColumn()
        => new ContainerComponent()
            .BindBackground<ChatController>(static x => x.MessageBackground)
            .BindPlacement<ChatController>(static x => x.ChatPlacement)
            .SetRow(1, UIGridUnit.Star())
            .AddRow(UIGridUnit.Auto())
            .SetHeight(UILayoutLength.Fill())
            .SetWidth(UILayoutLength.Fill())
            .SetMinHeight(UILayoutLength.Absolute(0))
            .SetMaxWidth(UILayoutLength.Absolute(ChatColumnWidth))
            .SetHorizontalAlignment(UIAlignment.Center)
            .AddChild(CreateFeed().SetPlacement(1, 1, 24, 1))
            .AddChild(CreateComposer().SetPlacement(1, 2, 24, 1));

    // Messages vary widely in height, so use a top loading ring instead of fixed-height skeleton rows.
    private static ItemsViewComponent CreateFeed()
        => new ItemsViewComponent(ChatController.FeedId)
            .BindSource<ChatController>(static x => x.Messages)
            .SetWindowSize(ChatController.WindowSize)
            .SetLoadingLook(UIItemsLoadingLook.Indicator)
            .AnchorToEnd()
            .SetSpacing(16)
            .SetPadding(UIResponsive<UIThickness>.Create(UIThickness.Symmetric(12, 12), xl: UIThickness.Symmetric(16, 12)))
            .SetHeight(UILayoutLength.Fill())
            .SetTextSelectable(true)
            .ConfigureDefaultEmptyTemplate(empty => empty.BindTitle<ChatController>(static x => x.EmptyFeed).SetTitleWrap(true))
            .SetTemplate(CreateMessage());

    private static ContainerComponent CreateMessage()
        => new ContainerComponent()
            .SetColumn(1, UIGridUnit.Auto())
            .SetRow(1, UIGridUnit.Auto())
            .AddRow(UIGridUnit.Star())
            .AddChild(CreateAuthorColumn().SetPlacement(1, 1, 1, 2))
            .AddChild(UILayout.Ends(
                    new TextComponent().BindTitle<ChatMessageItem>(static x => x.Author, UIBindingScope.Relative).AsSubtitle(),
                    ItemAction<ChatMessageItem>(UIIconButtons.Edit(), static x => x.ToolsVisibility, nameof(ChatController.EditAsync), "messageId"),
                    CreateMessageMenu(),
                    ItemAction<ChatMessageItem>(UIIconButtons.Done(), static x => x.EditorVisibility, nameof(ChatController.SaveEditAsync), "messageId"),
                    ItemAction<ChatMessageItem>(UIIconButtons.Cancel(), static x => x.EditorVisibility, nameof(ChatController.CancelEditAsync), "messageId"),
                    ItemAction<ChatMessageItem>(UIIconButtons.Delete(), static x => x.EditorVisibility, nameof(ChatController.AskDeleteAsync), "messageId")
                )
                .SetMinHeight(UILayoutLength.Absolute(MessageHeaderHeight))
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

    private static StackPanelComponent CreateAuthorColumn()
        => UILayout.Stack(4,
                new ImageComponent()
                    .BindAccessibleName<ChatMessageItem>(static x => x.Author, UIBindingScope.Relative)
                    .AsContent(ImageComponent.AccessibleNameProperty)
                    .BindSource<ChatMessageItem>(static x => x.Avatar, UIBindingScope.Relative)
                    .SetAltText("chatai.chat.avatar")
                    .SetShape(UIShape.Circle)
                    .SetWidth(UILayoutLength.Absolute(AvatarSize))
                    .SetHorizontalAlignment(UIAlignment.Center)
                    .SetMargin(UIThickness.All(0, 2, 0, 0))
                    .OnClickWithItemKey(nameof(ChatController.OpenAuthorCardAsync), "messageId")
                    .BindClickable<ChatMessageItem>(static x => x.CanOpenAuthor, UIBindingScope.Relative),
                new TextComponent()
                    .BindTitle<ChatMessageItem>(static x => x.TokenCount, UIBindingScope.Relative)
                    .BindTooltip<ChatMessageItem>(static x => x.TokenCountTooltip, UIBindingScope.Relative)
                    .AsContent(TextComponent.TitleProperty)
                    .AsCaption()
                    .Muted()
                    .SetHorizontalAlignment(UIAlignment.Center)
                    .BindVisibility<ChatMessageItem>(static x => x.TokenCountVisibility, UIBindingScope.Relative)
            )
            .SetMinHeight(UILayoutLength.Absolute(AuthorColumnHeight))
            .SetMargin(UIThickness.All(0, 0, 12, 0));

    private static ButtonComponent ItemAction<TItem>(ButtonComponent button, Expression<Func<TItem, object?>> visibility, string command, string keyName)
        => button
            .SetSize(UISize.Small)
            .BindVisibility(visibility, UIBindingScope.Relative)
            .OnClick(command, UIAction.ArgCurrentItemKey(keyName));

    private static SplitButtonComponent CreateMessageMenu()
        => UIIconButtons.More()
            .SetSize(UISize.Small)
            .BindItems<ChatMessageItem>(static x => x.Actions, UIBindingScope.Relative)
            .BindVisibility<ChatMessageItem>(static x => x.ToolsVisibility, UIBindingScope.Relative)
            .OnItemClick(nameof(ChatController.MessageActionAsync), UIAction.ArgCurrentItemKey("action"), UIAction.ArgParent<ChatMessageItem>("messageId", static x => x.Id));

    private static ExpanderComponent CreateThought()
        => UIPage.BoundFold<ChatMessageItem>(static x => x.ThoughtTitle, UIBindingScope.Relative, false)
            .SetContent(CreateFoldQuote(new MarkdownDisplayComponent()
                    .BindText<ChatMessageItem>(static x => x.Thought, UIBindingScope.Relative)
                    .SetLineBreaks(true)
                )
                .SetMargin(UIThickness.All(0, -4, 0, 0))
            )
            .BindVisibility<ChatMessageItem>(static x => x.ThoughtVisibility, UIBindingScope.Relative)
            .SetMargin(UIThickness.All(0, 0, 0, 8));

    private static SurfaceComponent CreateFoldQuote(IVisualComponent content)
        => UIText.Quote(content, UIThemeColor.Muted)
            .SetPadding(UIThickness.All(10, 0, 0, 0))
            .SetVerticalAlignment(UIAlignment.Start);

    private static SpinnerComponent CreateLoading()
        => new SpinnerComponent()
            .SetLoadingStyle(UILoadingStyle.Dots)
            .SetColor(UIThemeColor.Muted)
            .BindVisibility<ChatMessageItem>(static x => x.LoadingVisibility, UIBindingScope.Relative);

    private static MarkdownDisplayComponent CreateMessageText()
        => StyleMessageMarkdown(new MarkdownDisplayComponent().BindText<ChatMessageItem>(static x => x.Text, UIBindingScope.Relative));

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

    private static TextAreaComponent CreateEditor()
        => new TextAreaComponent()
            .SetRows(3)
            .SetAutoGrow(20)
            .BindValue<ChatMessageItem>(static x => x.EditDraft, UIBindingScope.Relative)
            .OnEscape(nameof(ChatController.CancelEditAsync), UIAction.ArgCurrentItemKey("messageId"))
            .BindVisibility<ChatMessageItem>(static x => x.EditorVisibility, UIBindingScope.Relative);

    private static ContainerComponent CreateMessageFooter()
        => new ContainerComponent()
            .AddChild(CreateRecords().SetPlacement(1, 1, 24, 1))
            .AddChild(CreateVariantBar().SetPlacement(1, 1, 24, 1));

    private static ItemsViewComponent CreateRecords()
        => new ItemsViewComponent(ChatController.MessageRecordsId)
            .SetVerticalScroll(UIScrollMode.Disabled)
            .BindItems<ChatMessageItem>(static x => x.Records, UIBindingScope.Relative)
            .SetSpacing(6)
            .SetShowEmptyTemplate(false)
            .SetTemplate(UIPage.BoundFold<ChatRecordItem>(static x => x.Title, UIBindingScope.Relative, false)
                .SetContent(new ContainerComponent()
                    .SetColumn(24, UIGridUnit.Auto())
                    .SetMargin(UIThickness.All(0, -4, 0, 0))
                    .AddChild(CreateFoldQuote(new MarkdownDisplayComponent()
                                .BindText<ChatRecordItem>(static x => x.Text, UIBindingScope.Relative)
                                .SetLineBreaks(true)
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
                            ItemAction<ChatRecordItem>(UIIconButtons.Edit(), static x => x.ToolsVisibility, nameof(ChatController.EditRecordAsync), "recordId"),
                            ItemAction<ChatRecordItem>(UIIconButtons.Done(), static x => x.EditorVisibility, nameof(ChatController.SaveRecordEditAsync), "recordId"),
                            ItemAction<ChatRecordItem>(UIIconButtons.Cancel(), static x => x.EditorVisibility, nameof(ChatController.CancelRecordEditAsync), "recordId"),
                            UIIconButtons.Delete().SetSize(UISize.Small).OnClick(nameof(ChatController.DeleteRecordAsync), UIAction.ArgCurrentItemKey("recordId"))
                        )
                        .SetVerticalAlignment(UIAlignment.Start)
                        .SetMargin(UIThickness.All(8, 0, 0, 0))
                        .BindVisibility<ChatRecordItem>(static x => x.ActionsVisibility, UIBindingScope.Relative)
                        .SetPlacement(24, 1, 1, 1)
                    )
                )
            );

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

    private static ButtonComponent SwipeArrow(string icon, string label, string command, Expression<Func<ChatMessageItem, object?>> enabled)
        => UIButtons.Icon(AppIcons.Outline(icon), label)
            .SetSize(UISize.Small)
            .SetVerticalAlignment(UIAlignment.Center)
            .BindEnabled(enabled, UIBindingScope.Relative)
            .OnClick(command, UIAction.ArgCurrentItemKey("messageId"));

    // Rounded composer card with a borderless auto-growing field above a compact action bar separating drawer toggles and connection picker from message tools.
    private static SurfaceComponent CreateComposer()
        => new SurfaceComponent()
            .SetSurface(UISurfaceStyle.Surface)
            .SetBorderThickness(UIThickness.Uniform(1))
            .SetBorderRadius(UICornerRadius.Uniform(8))
            .SetPadding(UIThickness.Uniform(8))
            .SetMargin(UIResponsive<UIThickness>.Create(UIThickness.All(12, 0, 12, 12), xl: UIThickness.All(16, 0, 16, 12)))
            .SetContent(UILayout.Stack(6,
                new TextAreaComponent(ChatController.ComposerId)
                    .SetAppearance(UIInputAppearance.Ghost)
                    .SetShowFocusEdge(false)
                    .SetPlaceholder("chatai.chat.placeholder")
                    .SetRows(1)
                    .SetAutoGrow(8)
                    .SetDebounceMilliseconds(80)
                    .OnEnter(nameof(ChatController.SendAsync))
                    .SetFormId(ChatController.ComposerFormId)
                    .BindValue<ChatController>(static x => x.Draft),
                CreateComposerBar()
            ));

    private static ContainerComponent CreateComposerBar()
    {
        ButtonComponent sendDraft = CreateSendDraftButton();
        ButtonComponent sendEmpty = CreateSendEmptyButton();

        return new ContainerComponent()
            .SetColumn(24, UIGridUnit.Auto())
            .AddChild(UILayout.Row(4,
                    CreateGenerationButton().SetSize(UISize.Small).SetVerticalAlignment(UIAlignment.Center),
                    CreateChatsButton().SetSize(UISize.Small).SetVerticalAlignment(UIAlignment.Center),
                    CreateConnectionSelect()
                )
                .SetMinWidth(UILayoutLength.Absolute(0))
                .SetVerticalAlignment(UIAlignment.Center)
                .SetPlacement(1, 1, 23, 1)
            )
            .AddChild(UILayout.Row(4,
                    CreateExtrasMenu(),
                    sendDraft,
                    sendEmpty,
                    CreateStopButton(sendDraft, sendEmpty)
                )
                .SetVerticalAlignment(UIAlignment.Center)
                .SetMargin(UIThickness.All(8, 0, 0, 0))
                .SetPlacement(24, 1, 1, 1)
            );
    }

    private static SelectComponent CreateConnectionSelect()
        => new SelectComponent()
            .SetVariant(UISelectVariant.Quiet)
            .SetSize(UISize.Small)
            .SetAccessibleName("chatai.generation.connection")
            .SetTooltip("chatai.generation.connection")
            .SetPopupPlacement(UIPopupPlacement.TopStart)
            .SetGroupTemplate(new DefaultGroupTemplate())
            .SetVerticalAlignment(UIAlignment.Center)
            .BindOptions<ShellController>(static x => x.ConnectionOptions)
            .BindValue<ShellController>(static x => x.ConnectionId)
            .OnChange(nameof(ShellController.SelectConnectionAsync));

    private static SplitButtonComponent CreateExtrasMenu()
        => UIIconButtons.More()
            .SetSize(UISize.Small)
            .SetVerticalAlignment(UIAlignment.Center)
            .SetPopupPlacement(UIPopupPlacement.TopEnd)
            .BindItems<ChatController>(static x => x.Extras)
            .OnItemClick(ChatController.PhrasesAction, InsertTextEffect.CurrentItemValue(ChatController.ComposerId))
            .OnItemClick(nameof(ChatController.ExtraActionAsync), UIAction.ArgCurrentItemKey("action"));

    private static ButtonComponent CreateSendDraftButton()
        => AddOptimisticStopFeedback(CreateComposerPrimaryButton(UIIconButtons.Send()))
            .EnabledWhen(ChatController.ComposerId)
            .BindVisibility<ChatController>(static x => x.SendDraftVisibility)
            .OnSubmit(ChatController.ComposerFormId, nameof(ChatController.SendOrStopAsync));

    private static ButtonComponent CreateSendEmptyButton()
        => AddOptimisticStopFeedback(CreateComposerPrimaryButton(UIIconButtons.Send()))
            .BindVisibility<ChatController>(static x => x.SendEmptyVisibility)
            .OnSubmit(ChatController.ComposerFormId, nameof(ChatController.SendOrStopAsync));

    private static ButtonComponent AddOptimisticStopFeedback(ButtonComponent button)
        => button
            .InteractBeforeClick(IButtonComponent.VariantProperty, UIButtonVariant.Ghost)
            .InteractBeforeClick(ITextBaseComponent.IconProperty, UIStandardAction.Stop.Icon)
            .InteractBeforeClick(ITextBaseComponent.IconColorProperty, UIThemeColor.Danger)
            .InteractAfterClick(IButtonComponent.VariantProperty, UIButtonVariant.Primary)
            .InteractAfterClick(ITextBaseComponent.IconProperty, UIStandardAction.Send.Icon)
            .InteractAfterClick(ITextBaseComponent.IconColorProperty, null)
            .InteractBefore(ChatController.ComposerId, EventNames.Enter, IButtonComponent.VariantProperty, UIButtonVariant.Ghost)
            .InteractBefore(ChatController.ComposerId, EventNames.Enter, ITextBaseComponent.IconProperty, UIStandardAction.Stop.Icon)
            .InteractBefore(ChatController.ComposerId, EventNames.Enter, ITextBaseComponent.IconColorProperty, UIThemeColor.Danger)
            .InteractAfter(ChatController.ComposerId, EventNames.Enter, IButtonComponent.VariantProperty, UIButtonVariant.Primary)
            .InteractAfter(ChatController.ComposerId, EventNames.Enter, ITextBaseComponent.IconProperty, UIStandardAction.Send.Icon)
            .InteractAfter(ChatController.ComposerId, EventNames.Enter, ITextBaseComponent.IconColorProperty, null);

    private static ButtonComponent CreateStopButton(ButtonComponent sendDraft, ButtonComponent sendEmpty)
        => new ButtonComponent(ChatController.StopButtonId)
            .SetIcon(UIStandardAction.Stop.Icon)
            .SetTooltip(UIStandardAction.Stop.Title)
            .SetVariant(UIButtonVariant.Ghost)
            .SetIconColor(UIThemeColor.Danger)
            .SetSize(UISize.Small)
            .SetShape(UIShape.Circle)
            .SetVerticalAlignment(UIAlignment.Center)
            .BindVisibility<ChatController>(static x => x.StopVisibility)
            .OnClick(nameof(ChatController.StopAsync))
            .InteractBeforeClick(sendDraft, IButtonComponent.VariantProperty, UIButtonVariant.Ghost)
            .InteractBeforeClick(sendDraft, ITextBaseComponent.IconColorProperty, UIThemeColor.Danger)
            .InteractBeforeClick(sendEmpty, IButtonComponent.VariantProperty, UIButtonVariant.Ghost)
            .InteractBeforeClick(sendEmpty, ITextBaseComponent.IconColorProperty, UIThemeColor.Danger)
            .InteractBefore(ChatController.ComposerId, EventNames.Enter, IButtonComponent.VariantProperty, UIButtonVariant.Ghost)
            .InteractBefore(ChatController.ComposerId, EventNames.Enter, ITextBaseComponent.IconColorProperty, UIThemeColor.Danger);

    private static ButtonComponent CreateComposerPrimaryButton(ButtonComponent button)
        => button
            .SetVariant(UIButtonVariant.Primary)
            .SetSize(UISize.Small)
            .SetShape(UIShape.Circle)
            .SetVerticalAlignment(UIAlignment.Center);

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
            UIDialogs.Confirm(ChatController.DeleteDialogKey, "chatai.chat.delete.title", "chatai.chat.delete.question", UIStandardAction.Delete.Title, nameof(ChatController.DeleteMessageAsync)),
            UIDialogs.Confirm(ChatController.DeleteFromDialogKey, "chatai.chat.delete-from.title", "chatai.chat.delete-from.question", UIStandardAction.Delete.Title, nameof(ChatController.DeleteFromAsync)),
            UIDialogs.Confirm(ChatController.RefreshDialogKey, "chatai.chat.refresh.title", "chatai.chat.refresh.question", "chatai.chat.regenerate", nameof(ChatController.RefreshMessageAsync))
        ];

    private static UIDialog CreatePromptDialog()
        => UIDialogs.Fill(ChatController.PromptDialogKey, "chatai.chat.prompt.title",
            new ContainerComponent()
                .SetHeight(UILayoutLength.Fill())
                .SetRow(1, UIGridUnit.Auto())
                .AddRow(UIGridUnit.Star())
                .SetSpacing(12)
                .AddChild(UIText.Note().BindDescription<ChatController>(static x => x.PromptSummary).SetPlacement(1, 1, 24, 1))
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
                    .SetPlacement(1, 2, 24, 1)
                ),
            width: UIColumnWidth.Wide
        );
}

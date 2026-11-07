using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

/// <summary>
/// A world's wide editor dialog: the card's head (<see cref="CardEditor"/>), its overview, and its articles — a title, the words,
/// where they go and whether they go — each kept as it lets go of its value.
/// </summary>
internal static class WorldView
{
    private static readonly OptionItem[] Positions =
    [
        new() { Id = "BeforeStory", Title = "chatai.world.position.before" },
        new() { Id = "AfterStory", Title = "chatai.world.position.after" }
    ];

    public static UIDialog CreateDialog()
        => CardEditor.CreateDialog(ShellController.WorldDialogKey, "chatai.world.title",
            CardEditor.Section(null,
                CardEditor.CreateHead(),
                CardEditor.Area<ShellController>("chatai.world.overview", static x => x.CardDescription, 8, "chatai.world.overview.hint")
            ),
            CardEditor.Section("chatai.world.articles",
                CreateArticles()
            )
        );

    /// <summary>The articles, each its fields with its remove inside the title; then a new one.</summary>
    private static StackPanelComponent CreateArticles()
        => UILayout.Stack(16,
            new ItemsViewComponent()
                .SetVerticalScroll(UIScrollMode.Disabled)
                .BindItems<ShellController>(static x => x.WorldArticles)
                .SetSpacing(24)
                .ConfigureDefaultEmptyTemplate(empty => empty.SetTitle("chatai.world.articles.none").SetTitleWrap(true))
                .SetTemplate(UILayout.Stack(12,
                        new TextInputComponent()
                            .SetPlaceholder("chatai.world.article.title")
                            .BindValue<ArticleItem>(static x => x.Title, UIBindingScope.Relative)
                            .OnChange(nameof(ShellController.SaveCardAsync))
                            .AddTrailingAction(UIIconButtons.Delete().OnClick(nameof(ShellController.RemoveArticleAsync), UIAction.ArgCurrentItemKey("articleId"))),
                        UIForm.Row(
                            new SelectComponent()
                                .SetTitle("chatai.world.article.position")
                                .SetOptions(Positions)
                                .BindValue<ArticleItem>(static x => x.Position, UIBindingScope.Relative)
                                .OnChange(nameof(ShellController.SaveCardAsync)),
                            new SwitchComponent()
                                .SetTitle("chatai.world.article.enabled")
                                .BindValue<ArticleItem>(static x => x.Enabled, UIBindingScope.Relative)
                                .OnChange(nameof(ShellController.SaveCardAsync))
                                .SetVerticalAlignment(UIAlignment.End)
                        ),
                        SettingsEditors.MacroInput(null, 12)
                            .SetPlaceholder("chatai.world.article.text")
                            .BindValue<ArticleItem>(static x => x.Text, UIBindingScope.Relative)
                            .OnChange(nameof(ShellController.SaveCardAsync))
                    )
                ),
            UIButtons.Ghost("chatai.world.article.add", UIStandardAction.Add.Icon)
                .OnClick(nameof(ShellController.AddArticle))
                .SetHorizontalAlignment(UIAlignment.Start)
        );
}

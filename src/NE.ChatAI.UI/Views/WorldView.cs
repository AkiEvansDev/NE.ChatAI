using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

/// <summary>
/// A world's wide editor dialog: the card's head (<see cref="CardEditor"/>), its overview, and its articles — a title, the words,
/// where they go and whether they go — each kept as it lets go of its value.
/// </summary>
internal static class WorldView
{
    public const string ArticleSearchId = "chatai-world-article-search";
    public const string ArticlesId = "chatai-world-articles";

    private static readonly OptionItem[] Positions =
    [
        new() { Id = "BeforeStory", Title = "chatai.world.position.before" },
        new() { Id = "AfterStory", Title = "chatai.world.position.after" },
        new() { Id = "InHistory", Title = "chatai.world.position.history" }
    ];

    public static UIDialog CreateDialog()
        => CardEditor.CreateDialog(ShellController.WorldDialogKey, "chatai.world.title",
            new TabsComponent("chatai-world-tabs")
                .AddTab("overview", "chatai.world.overview", UILayout.Stack(16,
                    CardEditor.CreateHead(),
                    CardEditor.Area<ShellController>("chatai.world.overview", static x => x.CardDescription, 20, "chatai.world.overview.hint")
                ))
                .AddTab("articles", "chatai.world.articles", UILayout.Stack(16,
                    UIForm.Field(new NumberInputComponent()
                            .SetTitle("chatai.world.scan-depth")
                            .SetShowStepper(true)
                            .SetMin(1)
                            .SetMax(64)
                            .SetStep(1)
                            .BindValue<ShellController>(static x => x.WorldScanDepth)
                            .OnChange(nameof(ShellController.SaveCardAsync)),
                        "chatai.world.scan-depth.hint"
                    ),
                    CreateArticles()
                ))
        );

    /// <summary>The articles in an accordion, each its fields with its rename and remove in the header; then a new one.</summary>
    private static StackPanelComponent CreateArticles()
        => UILayout.Stack(12,
            new TextInputComponent(ArticleSearchId)
                .SetSearch("chatai.world.article.search")
                .SetDebounceMilliseconds(200)
                .BindVisibility<ShellController>(static x => x.WorldArticleSearchVisibility),
            new ItemsViewComponent(ArticlesId)
                .SetVerticalScroll(UIScrollMode.Disabled)
                .SetLayoutType(UIItemsLayoutType.Accordion)
                .BindItems<ShellController>(static x => x.WorldArticles)
                .FilterBy(ArticleSearchId, IInputComponent.ValueProperty, nameof(ArticleItem.SearchText))
                .ConfigureDefaultEmptyTemplate(empty => empty.SetTitle("chatai.world.articles.none").SetTitleWrap(true))
                .SetTemplate(CardEditor.Fold(nameof(ShellController.SaveCardAsync), nameof(ShellController.RemoveArticleAsync), "articleId",
                        UIForm.Field(new TextInputComponent()
                                .SetTitle("chatai.world.article.triggers")
                                .SetPlaceholder("chatai.world.article.triggers.placeholder")
                                .BindValue<ArticleItem>(static x => x.Triggers, UIBindingScope.Relative)
                                .OnChange(nameof(ShellController.SaveCardAsync)),
                            "chatai.world.article.triggers.hint"
                        ),
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

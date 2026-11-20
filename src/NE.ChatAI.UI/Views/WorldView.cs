using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

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
                .SetHeight(UILayoutLength.Fill())
                .AddTab("overview", "chatai.world.overview",
                    new ContainerComponent()
                        .SetHeight(UILayoutLength.Fill())
                        .SetRow(1, UIGridUnit.Auto())
                        .AddRow(UIGridUnit.Star())
                        .AddChild(CardEditor.CreateHead()
                            .SetMargin(UIThickness.All(0, 0, 0, 16))
                            .SetPlacement(1, 1, 24, 1)
                        )
                        .AddChild(CardEditor.FillArea<ShellController>("chatai.world.overview", static x => x.CardDescription, expandable: true, hint: "chatai.world.overview.hint")
                            .SetPlacement(1, 2, 24, 1)
                        )
                )
                .AddTab("articles", "chatai.world.articles",
                    CardEditor.Scroll(
                        UILayout.Stack(16,
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
                        )
                    )
                )
        );

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
                        ),
                        SettingsEditors.MacroInput("chatai.world.article.text", 12, expandable: true)
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

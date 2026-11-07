using System;
using System.Linq.Expressions;
using NE.ChatAI.Chats;
using NE.ChatAI.Controllers;
using NE.ChatAI.Prompts;

namespace NE.ChatAI.Views;

/// <summary>The chat's statistics dialog and a message's generation Info dialog.</summary>
internal static class ChatStatisticsDialogs
{
    private const double DialogBodyMinHeight = 480;
    private const double StackedChartMinHeight = 280;
    private const double TimingChartMinHeight = 220;

    public static UIDialog CreateChatStatistics()
        => UIDialogs.Create(ChatController.StatisticsDialogKey, "chatai.stats.title", UILayout.Stack(16,
                UIPage.Empty(AppIcons.Outline(AppIcons.Statistics), "chatai.stats.empty", "chatai.stats.empty.description")
                    .SetMinHeight(UILayoutLength.Absolute(DialogBodyMinHeight))
                    .BindVisibility<ChatController>(static x => x.StatisticsEmptyVisibility),
                UILayout.Stack(16,
                        UIDetails.List().BindItems<ChatController>(static x => x.StatisticsSummary),
                        new TabsComponent("chatai-statistics-tabs")
                            .AddTab("tokens", "chatai.stats.tab.tokens", CreateTokensTab())
                            .AddTab("timing", "chatai.stats.tab.timing", CreateTimingTab())
                    )
                    .SetMinHeight(UILayoutLength.Absolute(DialogBodyMinHeight))
                    .BindVisibility<ChatController>(static x => x.StatisticsContentVisibility)
            ),
            width: UIColumnWidth.Reading,
            trailing: [CreateScopeSwitch()]
        );

    private static StackPanelComponent CreateTokensTab()
        => UILayout.Stack(20,
            CreateContextShareBar(static x => x.LatestContextShares, static x => x.LatestContextTitle, static x => x.LatestContextTotal),
            UIPage.Labelled("chatai.stats.turns.title", CreateTokensPerTurnChart(), wrap: true)
        );

    private static ShareBarComponent CreateContextShareBar(Expression<Func<ChatController, object?>> items, Expression<Func<ChatController, object?>> title, Expression<Func<ChatController, object?>> total)
        => new ShareBarComponent()
            .BindItems(items)
            .BindTitle(title)
            .BindTotal(total)
            .SetX<ContextShareItem>(static x => x.BlockKey)
            .SetXAxis(UIChartAxis.Category(translate: true))
            .SetYAxis(UIChartAxis.Linear(format: "N0"))
            .AddSeries<ContextShareItem>("tokens", "chatai.stats.axis.tokens", static x => x.Tokens)
            .SetRemainderCaption("chatai.stats.block.remaining")
            .SetShowLegendValues(true);

    private static BarChartComponent CreateTokensPerTurnChart()
        => new BarChartComponent()
            .BindItems<ChatController>(static x => x.TurnChartRows)
            .SetX<TurnChartItem>(static x => x.Turn)
            .SetXAxis(UIChartAxis.Category())
            .SetYAxis(UIChartAxis.Linear("chatai.stats.axis.tokens", min: 0, format: "N0"))
            .SetStacked(true)
            .SetSharedTooltip(true)
            .SetHideEmptySeries()
            .SetZoomable(true)
            .SetMinHeight(UILayoutLength.Absolute(StackedChartMinHeight))
            .AddSeries<TurnChartItem>(PromptBlocks.System, ChatController.BlockPhraseKey(PromptBlocks.System), static x => x.System)
            .AddSeries<TurnChartItem>(PromptBlocks.Description, ChatController.BlockPhraseKey(PromptBlocks.Description), static x => x.Description)
            .AddSeries<TurnChartItem>(PromptBlocks.Personality, ChatController.BlockPhraseKey(PromptBlocks.Personality), static x => x.Personality)
            .AddSeries<TurnChartItem>(PromptBlocks.Scenario, ChatController.BlockPhraseKey(PromptBlocks.Scenario), static x => x.Scenario)
            .AddSeries<TurnChartItem>(PromptBlocks.Persona, ChatController.BlockPhraseKey(PromptBlocks.Persona), static x => x.Persona)
            .AddSeries<TurnChartItem>(PromptBlocks.Lore, ChatController.BlockPhraseKey(PromptBlocks.Lore), static x => x.Lore)
            .AddSeries<TurnChartItem>(PromptBlocks.Examples, ChatController.BlockPhraseKey(PromptBlocks.Examples), static x => x.Examples)
            .AddSeries<TurnChartItem>(PromptBlocks.Story, ChatController.BlockPhraseKey(PromptBlocks.Story), static x => x.Story)
            .AddSeries<TurnChartItem>(PromptBlocks.AuthorsNote, ChatController.BlockPhraseKey(PromptBlocks.AuthorsNote), static x => x.AuthorsNote)
            .AddSeries<TurnChartItem>(PromptBlocks.History, ChatController.BlockPhraseKey(PromptBlocks.History), static x => x.History)
            .AddSeries<TurnChartItem>(PromptBlocks.PostHistory, ChatController.BlockPhraseKey(PromptBlocks.PostHistory), static x => x.PostHistory)
            .AddSeries<TurnChartItem>(PromptBlocks.Instruction, ChatController.BlockPhraseKey(PromptBlocks.Instruction), static x => x.Instruction)
            .AddSeries<TurnChartItem>(PromptBlocks.Impersonate, ChatController.BlockPhraseKey(PromptBlocks.Impersonate), static x => x.Impersonate)
            .AddSeries<TurnChartItem>(PromptBlocks.Opening, ChatController.BlockPhraseKey(PromptBlocks.Opening), static x => x.Opening)
            .AddSeries<TurnChartItem>(ChatStatistics.ReplyBlock, ChatController.BlockPhraseKey(ChatStatistics.ReplyBlock), static x => x.Reply);

    private static StackPanelComponent CreateTimingTab()
        => UILayout.Stack(20,
            UIPage.Labelled("chatai.stats.speed.title", CreateSpeedChart(), wrap: true),
            UIPage.Labelled("chatai.stats.time.title", CreateTimeChart(), wrap: true)
        );

    private static LineChartComponent CreateSpeedChart()
        => new LineChartComponent()
            .BindItems<ChatController>(static x => x.TurnChartRows)
            .SetX<TurnChartItem>(static x => x.Turn)
            .SetXAxis(UIChartAxis.Category())
            .SetYAxis(UIChartAxis.Linear("chatai.stats.axis.speed", min: 0, format: "F1"))
            .SetZoomable(true)
            .SetMinHeight(UILayoutLength.Absolute(TimingChartMinHeight))
            .AddSeries<TurnChartItem>("speed", "chatai.stats.series.speed", static x => x.TokensPerSecond, color: UIThemeColor.Primary);

    private static BarChartComponent CreateTimeChart()
        => new BarChartComponent()
            .BindItems<ChatController>(static x => x.TurnChartRows)
            .SetX<TurnChartItem>(static x => x.Turn)
            .SetXAxis(UIChartAxis.Category())
            .SetYAxis(UIChartAxis.Linear("chatai.stats.axis.seconds", min: 0, format: "F1"))
            .SetStacked(true)
            .SetSharedTooltip(true)
            .SetZoomable(true)
            .SetMinHeight(UILayoutLength.Absolute(TimingChartMinHeight))
            .AddSeries<TurnChartItem>("promptTime", "chatai.stats.series.prompt-time", static x => x.PromptSeconds, color: UIThemeColor.Accent)
            .AddSeries<TurnChartItem>("generationTime", "chatai.stats.series.generation-time", static x => x.GenerationSeconds, color: UIThemeColor.Primary);

    private static ButtonGroupComponent CreateScopeSwitch()
        => new ButtonGroupComponent()
            .SetSize(UISize.Small)
            .SetItems([
                new ButtonItem { Id = ChatController.ActiveScope, Title = "chatai.stats.scope.active" },
                new ButtonItem { Id = ChatController.AllScope, Title = "chatai.stats.scope.all" }
            ])
            .BindSelectedKey<ChatController>(static x => x.StatisticsScope, mode: UIBindingMode.OneWay)
            .OnItemClick(nameof(ChatController.SwitchStatisticsScopeAsync), UIAction.ArgCurrentItemKey("scope"));

    public static UIDialog CreateMessageInfo()
        => UIDialogs.Create(ChatController.MessageInfoDialogKey, "chatai.stats.info.title", UILayout.Stack(16,
                CreateContextShareBar(static x => x.MessageInfoShares, static x => x.MessageInfoContextTitle, static x => x.MessageInfoContextTotal),
                UILayout.Stack(8,
                    UIDetails.List().BindItems<ChatController>(static x => x.MessageInfoDetails),
                    UIPage.Fold("chatai.stats.samplers", false, UIDetails.List().BindItems<ChatController>(static x => x.MessageInfoSamplers))
                        .BindVisibility<ChatController>(static x => x.MessageInfoSamplersVisibility)
                )
            ),
            width: UIColumnWidth.Reading
        );
}

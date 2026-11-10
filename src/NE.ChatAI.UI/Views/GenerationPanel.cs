using System;
using System.Linq.Expressions;
using NE.ChatAI.Controllers;
using NE.ChatAI.Samplers;

namespace NE.ChatAI.Views;

/// <summary>
/// The generation panel's body: what is changed most — thinking, the context, the reply's length — always in sight; the samplers
/// with their preset in a section open by default; the connection, what the server runs and the other presets in one folded
/// below. Each change is kept as it lands.
/// </summary>
internal static class GenerationPanel
{
    public static StackPanelComponent Create()
    {
        SwitchComponent contextAuto = CreateContextAuto();
        SelectComponent seedMode = new SelectComponent()
            .SetTitle("chatai.samplers.seed")
            .SetOptions(ShellController.SeedModes)
            .BindValue<ShellController>(static x => x.SeedMode)
            .OnChange(nameof(ShellController.SaveSamplersAsync));

        return UILayout.Stack(16,
            UILayout.Stack(12,
                CreateThinking(),
                CreateIncludeNames(),
                contextAuto,
                Number<ShellController>("chatai.generation.context.length", static x => x.ContextValue, 512, 1048576, 1024, nameof(ShellController.SaveContextAsync))
                    .HiddenWhen(contextAuto),
                Number<ShellController>("chatai.samplers.reply-length", static x => x.ReplyLength, 16, 8192, 16, nameof(ShellController.SaveSamplersAsync))
            ),
            Fold("chatai.generation.samplers", true,
                Select("chatai.generation.samplers.preset", static x => x.SamplerOptions, static x => x.SamplersId, nameof(ShellController.SelectPresetsAsync)),
                Slider("chatai.samplers.temperature", static x => x.Temperature, 0, 2, 0.05m),
                Slider("chatai.samplers.min-p", static x => x.MinP, 0, 1, 0.01m),
                Slider("chatai.samplers.top-p", static x => x.TopP, 0, 1, 0.01m),
                Slider("chatai.samplers.repetition-penalty", static x => x.RepetitionPenalty, 1, 2, 0.01m),
                seedMode,
                Number<ShellController>("chatai.samplers.seed-value", static x => x.SeedValue, 0, int.MaxValue, 1, nameof(ShellController.SaveSamplersAsync))
                    .ShownWhen(seedMode, nameof(SeedMode.Fixed)),
                // Only the chat has this panel, so the chat's own seed is bound here though the panel is the shell's.
                Number<ChatController>("chatai.samplers.chat-seed", static x => x.ChatSeed, 0, int.MaxValue, 1, nameof(ChatController.SaveChatSeedAsync))
                    .ShownWhen(seedMode, nameof(SeedMode.PerChat))
            ),
            Fold("chatai.generation.presets", false,
                Select("chatai.generation.connection", static x => x.ConnectionOptions, static x => x.ConnectionId, nameof(ShellController.SelectConnectionAsync))
                    .AddTrailingAction(UIButtons.Icon(AppIcons.Outline(AppIcons.Connect), "chatai.connection.check").OnClick(nameof(ShellController.CheckConnectionAsync))),
                UIText.Note().BindDescription<ShellController>(static x => x.ModelLine),
                Select("chatai.generation.format", static x => x.FormatOptions, static x => x.FormatId, nameof(ShellController.SelectPresetsAsync)),
                Select("chatai.generation.system-prompt", static x => x.SystemPromptOptions, static x => x.SystemPromptId, nameof(ShellController.SelectPresetsAsync)),
                Select("chatai.generation.prompt", static x => x.PromptOptions, static x => x.PromptId, nameof(ShellController.SelectPresetsAsync))
            )
        );
    }

    private static SwitchComponent CreateContextAuto()
        => new SwitchComponent()
            .SetTitle("chatai.generation.context.auto")
            .SetDescription("chatai.generation.context.auto.description")
            .BindValue<ShellController>(static x => x.ContextAuto)
            .OnChange(nameof(ShellController.SaveContextAsync));

    private static SwitchComponent CreateThinking()
        => new SwitchComponent()
            .SetTitle("chatai.generation.thinking")
            .SetDescription("chatai.generation.thinking.description")
            .BindValue<ShellController>(static x => x.Thinking)
            .BindEnabled<ShellController>(static x => x.ThinkingAvailable)
            .OnChange(nameof(ShellController.SaveThinkingAsync));

    private static SwitchComponent CreateIncludeNames()
        => new SwitchComponent()
            .SetTitle("chatai.generation.include-names")
            .SetDescription("chatai.generation.include-names.description")
            .BindValue<ShellController>(static x => x.IncludeNames)
            .OnChange(nameof(ShellController.SaveIncludeNamesAsync));

    /// <remarks>The stepper shown, which a number field leaves off by default.</remarks>
    private static NumberInputComponent Number<TSource>(string title, Expression<Func<TSource, object?>> value, decimal min, decimal max, decimal step, string command)
        => new NumberInputComponent()
            .SetTitle(title)
            .SetShowStepper(true)
            .SetMin(min)
            .SetMax(max)
            .SetStep(step)
            .BindValue(value)
            .OnChange(command);

    private static ExpanderComponent Fold(string title, bool expanded, params IVisualComponent[] children)
        => UIPage.Fold(title, expanded, UILayout.Stack(12, children));

    private static SelectComponent Select(string title, Expression<Func<ShellController, object?>> options, Expression<Func<ShellController, object?>> value, string command)
        => new SelectComponent()
            .SetTitle(title)
            .SetGroupTemplate(new DefaultGroupTemplate())
            .BindOptions(options)
            .BindValue(value)
            .OnChange(command);

    private static SliderComponent Slider(string title, Expression<Func<ShellController, object?>> value, decimal min, decimal max, decimal step)
        => new SliderComponent()
            .SetTitle(title)
            .SetMin(min)
            .SetMax(max)
            .SetStep(step)
            .SetShowValue(true)
            .BindValue(value)
            .OnChange(nameof(ShellController.SaveSamplersAsync));
}

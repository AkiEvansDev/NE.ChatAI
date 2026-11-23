using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using NE.ChatAI.Controllers;
using NE.ChatAI.Samplers;
using NE.ChatAI.Storage;
using NE.Colors;

namespace NE.ChatAI.Views;

internal static class SettingsEditors
{
    private static readonly UIThemeColor MacroValueColor = UIThemeColor.Create(
        UIThemeDefaults.LightPalette.Primary,
        new ColorVariant(UIThemeDefaults.DarkPalette.Primary.Name, ColorAdjustment.Tint, 4)
    );

    private static readonly UIThemeColor MacroConditionColor = UIThemeColor.Create(
        UIThemeDefaults.LightPalette.Info,
        new ColorVariant(UIThemeDefaults.DarkPalette.Info.Name, ColorAdjustment.Tint, 4)
    );

    private static readonly UIThemeColor SpeechColor = UIThemeColor.Create(
        UIThemeDefaults.LightPalette.Accent,
        new ColorVariant(UIThemeDefaults.DarkPalette.Accent.Name, ColorAdjustment.Tint, 4)
    );

    private static readonly OptionItem[] NamesBehaviors =
    [
        new() { Id = "None", Title = "chatai.settings.format.names.none" },
        new() { Id = "Force", Title = "chatai.settings.format.names.force" },
        new() { Id = "Always", Title = "chatai.settings.format.names.always" }
    ];

    private static readonly OptionItem[] Roles =
    [
        new() { Id = "System", Title = "chatai.settings.role.system" },
        new() { Id = "User", Title = "chatai.settings.role.user" },
        new() { Id = "Model", Title = "chatai.settings.role.character" }
    ];

    private static readonly OptionItem[] ThoughtDisplays =
    [
        new() { Id = "Folded", Title = "chatai.settings.prompt.thought.folded" },
        new() { Id = "Hidden", Title = "chatai.settings.prompt.thought.hidden" }
    ];

    private static readonly UICodeCompletion[] MacroCompletions =
    [
        new() { Label = "{{char}}", Detail = "Character name", Kind = "regex" },
        new() { Label = "{{user}}", Detail = "Persona name", Kind = "regex" },
        new() { Label = "{{system}}", Detail = "System prompt", Kind = "regex" },
        new() { Label = "{{original}}", Detail = "Preset system prompt", Kind = "regex" },
        new() { Label = "{{description}}", Detail = "Character description", Kind = "regex" },
        new() { Label = "{{personality}}", Detail = "Character personality", Kind = "regex" },
        new() { Label = "{{scenario}}", Detail = "Character scenario", Kind = "regex" },
        new() { Label = "{{persona}}", Detail = "Persona description", Kind = "regex" },
        new() { Label = "{{loreBefore}}", Detail = "World info before story", Kind = "regex" },
        new() { Label = "{{loreAfter}}", Detail = "World info after story", Kind = "regex" },
        new() { Label = "{{wiBefore}}", Detail = "World info before story", Kind = "regex" },
        new() { Label = "{{wiAfter}}", Detail = "World info after story", Kind = "regex" },
        new() { Label = "{{mesExamples}}", Detail = "Formatted example dialogues", Kind = "regex" },
        new() { Label = "{{mesExamplesRaw}}", Detail = "Raw example dialogues", Kind = "regex" },
        new() { Label = "{{newline}}", Detail = "Line break", Kind = "meta" },
        new() { Label = "{{noop}}", Detail = "Empty placeholder", Kind = "meta" },
        new() { Label = "{{trim}}", Detail = "Trim surrounding whitespace", Kind = "meta" },
        new() { Label = "{{#if …}}", InsertText = "{{#if ", Detail = "Conditional block", Kind = "keyword" },
        new() { Label = "{{else}}", Detail = "Else branch", Kind = "keyword" },
        new() { Label = "{{/if}}", Detail = "End conditional block", Kind = "keyword" }
    ];

    public static StackPanelComponent CreateGeneral()
        => UILayout.Stack(16,
            Card(null,
                UIForm.Row(
                    new LanguageSwitcherComponent().AsField(),
                    new ThemeSwitcherComponent().AsField()
                ),
                new SwitchComponent()
                    .SetTitle("chatai.settings.general.message-background")
                    .SetDescription("chatai.settings.general.message-background.hint")
                    .BindValue<SettingsController>(static x => x.MessageBackground)
                    .OnChange(nameof(SettingsController.SaveMessageBackgroundAsync)),
                new SliderComponent(SettingsController.DimSliderId)
                    .SetTitle("chatai.settings.general.background-dim")
                    .SetRange(0, 1m)
                    .SetStep(0.05m)
                    .SetShowValue(true)
                    .BindValue<SettingsController>(static x => x.BackgroundDim)
                    .OnChange(nameof(SettingsController.SaveBackgroundDimAsync)),
                PictureFields.Background<SettingsController>("chatai.settings.background", "chatai.settings.background.hint", static x => x.Background, nameof(SettingsController.SaveBackgroundAsync), nameof(SettingsController.RemoveBackground), dimSliderId: SettingsController.DimSliderId)
            ),
            CreatePinCard()
        );

    private static CardComponent CreatePinCard()
        => UICard.Create(
            "chatai.settings.pin.title",
            null,
            UILayout.Stack(12,
                UIText.Note().BindDescription<SettingsController>(static x => x.PinStatus),
                UILayout.Ends(
                    new TextInputComponent(SettingsController.PinInputId)
                        .SetFormId(SettingsController.PinFormId)
                        .SetTitle("chatai.settings.pin.field")
                        .SetType(UITextInputType.Password)
                        .SetAutocomplete(UIAutocomplete.NewPassword)
                        .BindValue<SettingsController>(static x => x.NewPin, mode: UIBindingMode.OnSubmit),
                    UIButtons.Primary("chatai.settings.pin.set")
                        .BindTitle<SettingsController>(static x => x.PinSaveLabel)
                        .OnSubmitShowingLoading(SettingsController.PinFormId, nameof(SettingsController.SavePinAsync)),
                    UIButtons.Danger("chatai.settings.pin.remove")
                        .OnClickShowingLoading(nameof(SettingsController.ClearPinAsync))
                        .BindVisibility<SettingsController>(static x => x.PinClearVisibility)
                )
            ),
            icon: AppIcons.Outline(AppIcons.Lock)
        );

    private static CardComponent Card(string? title, params IVisualComponent[] fields)
        => UICard.Create(title is null ? null : new UIPhrase(title), null, UILayout.Stack(16, fields));

    public static StackPanelComponent CreateConnection()
    {
        SwitchComponent contextAuto = Switch("chatai.generation.context.auto", static x => x.Connection.Draft.ContextAuto, SettingsController.ConnectionKind);

        return Editor(SettingsController.ConnectionKind, static x => x.ConnectionOptions, static x => x.Connection,
            Card(null,
                Text("chatai.settings.connection.address", static x => x.Connection.Draft.Address, SettingsController.ConnectionKind)
                    .AddTrailingAction(UIButtons.Icon(AppIcons.Outline(AppIcons.Connect), "chatai.connection.check").OnClickShowingLoading(nameof(SettingsController.CheckEditedConnectionAsync))),
                UIForm.Row(
                    contextAuto,
                    Number("chatai.generation.context.length", static x => x.Connection.Draft.ContextValue, SettingsController.ConnectionKind, 512, 1048576, 1024).EnabledWhen(contextAuto, false)
                ),
                BoundSelect("chatai.generation.format", static x => x.FormatOptions, static x => x.Connection.Draft.Instruct, SettingsController.ConnectionKind),
                BoundSelect("chatai.generation.system-prompt", static x => x.SystemPromptOptions, static x => x.Connection.Draft.SystemPrompt, SettingsController.ConnectionKind),
                BoundSelect("chatai.generation.prompt", static x => x.PromptOptions, static x => x.Connection.Draft.Prompt, SettingsController.ConnectionKind),
                BoundSelect("chatai.generation.samplers", static x => x.SamplerOptions, static x => x.Connection.Draft.Samplers, SettingsController.ConnectionKind)
            )
        );
    }

    private static SwitchComponent Switch(string title, Expression<Func<SettingsController, object?>> value, string kind)
        => new SwitchComponent()
            .SetTitle(title)
            .BindValue(value)
            .OnChange(nameof(SettingsController.SaveAsync), UIAction.Arg("kind", kind));

    private static StackPanelComponent Editor<T, TDraft>(string kind, Expression<Func<SettingsController, object?>> options, Expression<Func<SettingsController, DocumentEditor<T, TDraft>>> editor, params IVisualComponent[] fields)
        where T : NamedDocument
        where TDraft : RecursiveObservable, IDocumentDraft<T>, new()
    {
        RecursivePath path = RecursivePath.FromExpression(editor);

        return UILayout.Stack(16,
            new SelectComponent()
                .SetTitle("chatai.settings.document")
                .SetGroupTemplate(new DefaultGroupTemplate())
                .BindOptions(options)
                .BindValue(path.AppendProperty(nameof(DocumentEditor<,>.Id)))
                .OnChange(nameof(SettingsController.ShowAsync), UIAction.Arg("kind", kind))
                .AddTrailingAction(UIIconButtons.New().OnClick(nameof(SettingsController.CreateAsync), UIAction.Arg("kind", kind), UIAction.Arg("copy", false)))
                .AddTrailingAction(UIIconButtons.More()
                    .SetItems([
                        UIMenuItems.Rename(SettingsController.RenameAction),
                        UIMenuItems.Duplicate(SettingsController.CopyAction),
                        UIMenuItems.Delete(SettingsController.DeleteAction)
                    ])
                    .OnItemClick(nameof(SettingsController.DocumentActionAsync), UIAction.Arg("kind", kind), UIAction.ArgCurrentItemKey("action"))
                    .BindVisibility(path.AppendProperty(nameof(DocumentEditor<,>.Visibility)))
                ),
            UILayout.Stack(16, fields).BindVisibility(path.AppendProperty(nameof(DocumentEditor<,>.Visibility)))
        );
    }

    private static SelectComponent Select(string title, IReadOnlyList<OptionItem> options, Expression<Func<SettingsController, object?>> value, string kind)
        => new SelectComponent()
            .SetTitle(title)
            .SetOptions(options)
            .BindValue(value)
            .OnChange(nameof(SettingsController.SaveAsync), UIAction.Arg("kind", kind));

    private static TextInputComponent Text(string title, Expression<Func<SettingsController, object?>> value, string kind)
        => new TextInputComponent()
            .SetTitle(title)
            .BindValue(value)
            .OnChange(nameof(SettingsController.SaveAsync), UIAction.Arg("kind", kind));

    private static NumberInputComponent Number(string title, Expression<Func<SettingsController, object?>> value, string kind, decimal min, decimal max, decimal step)
        => new NumberInputComponent()
            .SetTitle(title)
            .SetShowStepper(true)
            .SetMin(min)
            .SetMax(max)
            .SetStep(step)
            .BindValue(value)
            .OnChange(nameof(SettingsController.SaveAsync), UIAction.Arg("kind", kind));

    private static SelectComponent BoundSelect(string title, Expression<Func<SettingsController, object?>> options, Expression<Func<SettingsController, object?>> value, string kind)
        => new SelectComponent()
            .SetTitle(title)
            .SetGroupTemplate(new DefaultGroupTemplate())
            .BindOptions(options)
            .BindValue(value)
            .OnChange(nameof(SettingsController.SaveAsync), UIAction.Arg("kind", kind));

    // Thought prefix/suffix stay editable when thinking is off: they still form the empty-thought reply prefill.
    public static StackPanelComponent CreateFormat()
    {
        SwitchComponent thinking = Switch("chatai.settings.format.thought-enabled", static x => x.Format.Draft.ThoughtEnabled, SettingsController.FormatKind);

        return Editor(SettingsController.FormatKind, static x => x.FormatOptions, static x => x.Format,
            Card(null,
                UIText.Note("chatai.settings.format.markers"),
                Markers("chatai.settings.format.system", static x => x.Format.Draft.SystemPrefix, static x => x.Format.Draft.SystemSuffix),
                Markers("chatai.settings.format.user", static x => x.Format.Draft.UserPrefix, static x => x.Format.Draft.UserSuffix),
                Markers("chatai.settings.format.model", static x => x.Format.Draft.ModelPrefix, static x => x.Format.Draft.ModelSuffix),
                UIForm.Row(
                    Marker("chatai.settings.format.first-model", static x => x.Format.Draft.FirstModelPrefix),
                    Marker("chatai.settings.format.last-model", static x => x.Format.Draft.LastModelPrefix)
                ),
                UIForm.Row(
                    Switch("chatai.settings.format.system-same-as-user", static x => x.Format.Draft.SystemSameAsUser, SettingsController.FormatKind),
                    Switch("chatai.settings.format.wrap", static x => x.Format.Draft.Wrap, SettingsController.FormatKind)
                ),
                Select("chatai.settings.format.names", NamesBehaviors, static x => x.Format.Draft.NamesBehavior, SettingsController.FormatKind),
                UIForm.Field(Area("chatai.settings.format.stops", static x => x.Format.Draft.StopSequences, SettingsController.FormatKind, 3), "chatai.settings.format.stops.hint"),
                Switch("chatai.settings.format.sequences-as-stops", static x => x.Format.Draft.SequencesAsStop, SettingsController.FormatKind)
            ),
            Card("chatai.generation.thinking",
                thinking,
                UIForm.Row(
                    Marker("chatai.settings.format.thought-prefix", static x => x.Format.Draft.ThoughtPrefix),
                    Marker("chatai.settings.format.thought-suffix", static x => x.Format.Draft.ThoughtSuffix)
                ),
                Switch("chatai.settings.format.thought-opens-reply", static x => x.Format.Draft.ThoughtOpensReply, SettingsController.FormatKind)
                    .SetDescription("chatai.settings.format.thought-opens-reply.hint")
                    .EnabledWhen(thinking),
                UIForm.Field(Marker("chatai.settings.format.thought-system-mark", static x => x.Format.Draft.ThoughtSystemMark).EnabledWhen(thinking), "chatai.settings.format.thought-system-mark.hint")
            )
        );
    }

    private static ContainerComponent Markers(string title, Expression<Func<SettingsController, object?>> prefix, Expression<Func<SettingsController, object?>> suffix)
        => UIForm.Row(Marker(title, prefix), Marker("chatai.settings.format.suffix", suffix));

    private static TextAreaComponent Marker(string title, Expression<Func<SettingsController, object?>> value)
        => Area(title, value, SettingsController.FormatKind, 1);

    private static TextAreaComponent Area(string title, Expression<Func<SettingsController, object?>> value, string kind, int rows)
        => new TextAreaComponent()
            .SetTitle(title)
            .SetRows(rows)
            .SetAutoGrow(6)
            .BindValue(value)
            .OnChange(nameof(SettingsController.SaveAsync), UIAction.Arg("kind", kind));

    public static StackPanelComponent CreateSystemPrompt()
        => Editor(SettingsController.SystemPromptKind, static x => x.SystemPromptOptions, static x => x.SystemPrompt,
            Card(null,
                Code("chatai.settings.system-prompt.content", static x => x.SystemPrompt.Draft.Content, SettingsController.SystemPromptKind, 24, expandable: true)
            )
        );

    private static CodeInputComponent Code(string title, Expression<Func<SettingsController, object?>> value, string kind, int rows, bool lineNumbers = false, bool expandable = false)
        => MacroInput(title, rows, lineNumbers, expandable)
            .BindValue(value)
            .OnChange(nameof(SettingsController.SaveAsync), UIAction.Arg("kind", kind));

    // CodeInputComponent defaults to Ghost; Filled matches adjacent form fields, while expanded modal editors revert to Ghost.
    public static CodeInputComponent MacroInput(string? title, int rows, bool lineNumbers = false, bool expandable = false)
    {
        CodeInputComponent input = StyleMacroCode(new CodeInputComponent())
            .SetAppearance(UIInputAppearance.Filled)
            .SetRows(rows)
            .SetWrapLines(true)
            .SetStatusBar(false)
            .SetLineNumbers(lineNumbers)
            .SetCompletionTriggers("{{")
            .SetCompletionItems(MacroCompletions);

        if (title is not null)
            _ = input.SetTitle(title);

        if (expandable)
            _ = input.SetExpandable(static expand => expand.SetWidth(UIColumnWidth.Wide).SetLineNumbers(true).SetAppearance(UIInputAppearance.Ghost));

        return input;
    }

    public static CodeInputComponent StyleMacroCode(CodeInputComponent input)
    {
        UIMarkdownTextStyle speech = new() { Color = SpeechColor };
        UIMarkdownTextStyle control = new() { Color = MacroConditionColor };
        UIMarkdownTextStyle directive = new() { Color = UIThemeColor.Muted, Italic = true };
        UIMarkdownTextStyle macro = new() { Color = MacroValueColor, FontWeight = 600 };
        UIMarkdownTextStyle marker = new() { Color = UIThemeColor.Warning, FontWeight = 600 };

        return input
            .SetLanguage(UICodeLanguages.Markdown)
            .SetMarkStyle(UIMarkdownMark.Emphasis, new UIMarkdownTextStyle { Color = UIThemeColor.Muted })
            .SetMarkStyle(UIMarkdownMark.Link, new UIMarkdownTextStyle { Color = UIThemeColor.Primary })
            .AddSpanStyle("\"", "\"", speech)
            .AddSpanStyle("“", "”", speech)
            .AddSpanStyle("«", "»", speech)
            .AddTokenStyle("{{else}}", control)
            .AddTokenStyle("{{trim}}", directive)
            .AddTokenStyle("{{newline}}", directive)
            .AddTokenStyle("{{noop}}", directive)
            .AddSpanStyle("{{#", "}}", control)
            .AddSpanStyle("{{/", "}}", control)
            .AddSpanStyle("{{", "}}", macro)
            .AddTokenStyle("<START>", marker);
    }

    public static StackPanelComponent CreatePrompt()
        => Editor(SettingsController.PromptKind, static x => x.PromptOptions, static x => x.Prompt,
            Card(null,
                UIForm.Field(Code("chatai.settings.prompt.story", static x => x.Prompt.Draft.StoryString, SettingsController.PromptKind, 18, lineNumbers: true), "chatai.settings.prompt.story.hint"),
                UIForm.Row(
                    Text("chatai.settings.prompt.example-separator", static x => x.Prompt.Draft.ExampleSeparator, SettingsController.PromptKind),
                    Text("chatai.settings.prompt.chat-start", static x => x.Prompt.Draft.ChatStart, SettingsController.PromptKind)
                ),
                Switch("chatai.generation.include-names", static x => x.Prompt.Draft.IncludeNames, SettingsController.PromptKind)
                    .SetDescription("chatai.generation.include-names.description")
            ),
            Card("chatai.settings.prompt.thoughts",
                UIForm.Row(
                    Select("chatai.settings.prompt.thought-show", ThoughtDisplays, static x => x.Prompt.Draft.ThoughtShow, SettingsController.PromptKind),
                    Switch("chatai.settings.prompt.keep-thoughts", static x => x.Prompt.Draft.KeepThoughts, SettingsController.PromptKind)
                )
            )
        );

    // Per-chat seeds live on Chat documents and are edited in GenerationPanel.
    public static StackPanelComponent CreateSamplers()
    {
        SwitchComponent dynamic = Switch("chatai.samplers.dynamic-on", static x => x.Samplers.Draft.DynamicTemperature, SettingsController.SamplersKind);
        SelectComponent seedMode = Select("chatai.samplers.seed", ShellController.SeedModes, static x => x.Samplers.Draft.SeedMode, SettingsController.SamplersKind);

        return Editor(SettingsController.SamplersKind, static x => x.SamplerOptions, static x => x.Samplers,
            Card(null,
                UIForm.Row(
                    SamplerNumber("chatai.samplers.reply-length", static x => x.Samplers.Draft.ReplyLength, 16, 8192, 16),
                    SamplerNumber("chatai.samplers.temperature", static x => x.Samplers.Draft.Temperature, 0, 5, 0.05m)
                ),
                UIForm.Row(
                    SamplerNumber("chatai.samplers.top-p", static x => x.Samplers.Draft.TopP, 0, 1, 0.01m),
                    SamplerNumber("chatai.samplers.top-k", static x => x.Samplers.Draft.TopK, 0, 500, 1),
                    SamplerNumber("chatai.samplers.min-p", static x => x.Samplers.Draft.MinP, 0, 1, 0.01m)
                ),
                UIForm.Row(
                    SamplerNumber("chatai.samplers.typical-p", static x => x.Samplers.Draft.TypicalP, 0, 1, 0.01m),
                    SamplerNumber("chatai.samplers.tfs", static x => x.Samplers.Draft.Tfs, 0, 1, 0.01m),
                    SamplerNumber("chatai.samplers.top-a", static x => x.Samplers.Draft.TopA, 0, 1, 0.01m)
                )
            ),
            Card("chatai.samplers.repetition",
                UIForm.Row(
                    SamplerNumber("chatai.samplers.repetition-penalty", static x => x.Samplers.Draft.RepetitionPenalty, 1, 3, 0.01m),
                    SamplerNumber("chatai.samplers.repetition-range", static x => x.Samplers.Draft.RepetitionPenaltyRange, 0, 65536, 64),
                    SamplerNumber("chatai.samplers.repetition-slope", static x => x.Samplers.Draft.RepetitionPenaltySlope, 0, 10, 0.1m)
                ),
                UIForm.Row(
                    SamplerNumber("chatai.samplers.presence-penalty", static x => x.Samplers.Draft.PresencePenalty, -2, 2, 0.05m),
                    SamplerNumber("chatai.samplers.frequency-penalty", static x => x.Samplers.Draft.FrequencyPenalty, -2, 2, 0.05m)
                ),
                UIForm.Row(
                    SamplerNumber("chatai.samplers.smoothing", static x => x.Samplers.Draft.SmoothingFactor, 0, 10, 0.05m),
                    SamplerNumber("chatai.samplers.n-sigma", static x => x.Samplers.Draft.NSigma, 0, 5, 0.05m)
                )
            ),
            Card("chatai.samplers.dry",
                UIForm.Row(
                    SamplerNumber("chatai.samplers.dry-multiplier", static x => x.Samplers.Draft.DryMultiplier, 0, 5, 0.05m),
                    SamplerNumber("chatai.samplers.dry-base", static x => x.Samplers.Draft.DryBase, 1, 4, 0.05m),
                    SamplerNumber("chatai.samplers.dry-allowed", static x => x.Samplers.Draft.DryAllowedLength, 1, 20, 1)
                )
            ),
            Card("chatai.samplers.xtc",
                UIForm.Row(
                    SamplerNumber("chatai.samplers.xtc-threshold", static x => x.Samplers.Draft.XtcThreshold, 0, 0.5m, 0.01m),
                    SamplerNumber("chatai.samplers.xtc-probability", static x => x.Samplers.Draft.XtcProbability, 0, 1, 0.01m)
                )
            ),
            Card("chatai.samplers.dynamic",
                dynamic,
                UIForm.Row(
                    SamplerNumber("chatai.samplers.dynamic-min", static x => x.Samplers.Draft.DynamicMin, 0, 5, 0.05m).EnabledWhen(dynamic),
                    SamplerNumber("chatai.samplers.dynamic-max", static x => x.Samplers.Draft.DynamicMax, 0, 5, 0.05m).EnabledWhen(dynamic)
                )
            ),
            Card("chatai.samplers.seed",
                UIForm.Row(
                    seedMode,
                    SamplerNumber("chatai.samplers.seed-value", static x => x.Samplers.Draft.SeedValue, 0, int.MaxValue, 1)
                        .ShownWhen(seedMode, nameof(SeedMode.Fixed))
                )
            )
        );
    }

    private static NumberInputComponent SamplerNumber(string title, Expression<Func<SettingsController, object?>> value, decimal min, decimal max, decimal step)
        => Number(title, value, SettingsController.SamplersKind, min, max, step);

    public static CardComponent CreateAuthorsNote()
        => Card(null,
            UIText.Note("chatai.settings.notes.description"),
            UIForm.Row(
                UIForm.Field(new NumberInputComponent()
                        .SetTitle("chatai.settings.notes.depth")
                        .SetShowStepper(true)
                        .SetMin(0)
                        .SetMax(100)
                        .SetStep(1)
                        .BindValue<SettingsController>(static x => x.NoteDepth)
                        .OnChange(nameof(SettingsController.SaveAuthorsNoteAsync)),
                    "chatai.settings.notes.depth.hint"
                ),
                new SelectComponent()
                    .SetTitle("chatai.settings.notes.role")
                    .SetOptions(Roles)
                    .BindValue<SettingsController>(static x => x.NoteRole)
                    .OnChange(nameof(SettingsController.SaveAuthorsNoteAsync))
            ),
            MacroInput("chatai.settings.notes.note", 12)
                .BindValue<SettingsController>(static x => x.AuthorsNote)
                .OnChange(nameof(SettingsController.SaveAuthorsNoteAsync))
        );

    public static CardComponent CreatePhrases()
        => Card(null,
            UIText.Note("chatai.settings.phrases.description"),
            new ItemsViewComponent()
                .SetVerticalScroll(UIScrollMode.Disabled)
                .BindItems<SettingsController>(static x => x.Phrases)
                .SetSpacing(12)
                .ConfigureDefaultEmptyTemplate(empty => empty.SetTitle("chatai.settings.phrases.none").SetTitleWrap(true))
                .SetTemplate(new TextAreaComponent()
                    .SetPlaceholder("chatai.settings.phrases.placeholder")
                    .SetRows(2)
                    .SetAutoGrow(8)
                    .BindValue<PhraseItem>(static x => x.Text, UIBindingScope.Relative)
                    .OnChange(nameof(SettingsController.SavePhrasesAsync))
                    .AddTrailingAction(UIIconButtons.Delete().OnClick(nameof(SettingsController.RemovePhraseAsync), UIAction.ArgCurrentItemKey("phraseId")))
                ),
            UIButtons.Ghost("chatai.settings.phrases.add", UIStandardAction.Add.Icon)
                .OnClick(nameof(SettingsController.AddPhrase))
                .SetHorizontalAlignment(UIAlignment.Start)
        );

    public static CardComponent CreateGallery()
        => Card(null,
            PictureFields.Gallery<SettingsController>(
                "chatai-global-gallery-search",
                "chatai.gallery.global.description",
                "chatai.gallery.none",
                static x => x.GallerySelectionId,
                nameof(SettingsController.UploadGlobalGalleryAsync),
                static x => x.GlobalGallery,
                nameof(SettingsController.ReplaceGlobalGalleryAsync),
                nameof(SettingsController.GlobalGalleryActionAsync)
            )
        );

    public static CardComponent CreateSecrets()
        => Card(null,
            UIText.Note("chatai.secrets.description"),
            new TextInputComponent(SettingsController.SecretSearchId)
                .SetSearch()
                .SetDebounceMilliseconds(200)
                .AddTrailingAction(UIIconButtons.New().OnClick(nameof(SettingsController.AskCreateSecret))),
            new ItemsViewComponent()
                .SetVerticalScroll(UIScrollMode.Disabled)
                .BindItems<SettingsController>(static x => x.Secrets)
                .FilterBy(SettingsController.SecretSearchId, IInputComponent.ValueProperty, nameof(SecretItem.Name))
                .SetSpacing(8)
                .ConfigureDefaultEmptyTemplate(empty => empty.SetTitle("chatai.secrets.none").SetTitleWrap(true))
                .SetTemplate(CreateSecretRow())
        );

    private static ContainerComponent CreateSecretRow()
    {
        TextComponent slugText = new TextComponent()
            .BindTitle<SecretItem>(static x => x.Name, UIBindingScope.Relative)
            .AsSubtitle()
            .AsContent(TextComponent.TitleProperty);

        return UILayout.Ends(
            slugText,
            UIIconButtons.Edit()
                .SetSize(UISize.Small)
                .OnClick(nameof(SettingsController.AskEditSecret), UIAction.ArgCurrentItemKey("secretId")),
            UIIconButtons.Copy()
                .SetSize(UISize.Small)
                .InteractOn(EventNames.Click, CopyToClipboardEffect.ValueOf(slugText))
                .InteractOn(EventNames.Click, new ShowNotificationEffect("chatai.copied", UIColorStyle.Success)),
            UIIconButtons.Delete()
                .SetSize(UISize.Small)
                .BindVisibility<SecretItem>(static x => x.DeleteVisibility, UIBindingScope.Relative)
                .OnClick(nameof(SettingsController.DeleteSecretAsync), UIAction.ArgCurrentItemKey("secretId"))
        );
    }

    public static StackPanelComponent CreateImport()
        => UILayout.Stack(16,
            Card("chatai.import.title",
                UIText.Note("chatai.import.description"),
                new FileInputComponent()
                    .SetTitle("chatai.import.files")
                    .SetAccept(".zip,.json,.png,application/zip,application/json,image/png")
                    .SetMultiple(true)
                    .SetMaxFileSize(SettingsController.MaxImportBytes)
                    .BindSelectionId<SettingsController>(static x => x.ImportSelectionId)
                    .OnChange(nameof(SettingsController.ImportAsync))
                    .ShowLoadingDuring(EventNames.Change),
                new ItemsViewComponent()
                    .SetVerticalScroll(UIScrollMode.Disabled)
                    .BindItems<SettingsController>(static x => x.ImportResults)
                    .SetSpacing(6)
                    .SetShowEmptyTemplate(false)
                    .SetTemplate(new TextComponent()
                        .BindTitle<ImportLine>(static x => x.FileName, UIBindingScope.Relative)
                        .AsSubtitle()
                        .AsContent(TextComponent.TitleProperty)
                        .BindDescription<ImportLine>(static x => x.Result, UIBindingScope.Relative)
                        .SetWrapMode(UITextWrapMode.Wrap)
                    )
            ),
            Card("chatai.export.title",
                UIText.Note("chatai.export.description"),
                new SwitchComponent()
                    .SetTitle("chatai.export.settings")
                    .SetDescription("chatai.export.settings.description")
                    .BindValue<SettingsController>(static x => x.ExportSettings),
                new SwitchComponent()
                    .SetTitle("chatai.export.content")
                    .SetDescription("chatai.export.content.description")
                    .BindValue<SettingsController>(static x => x.ExportContent),
                new SwitchComponent()
                    .SetTitle("chatai.export.chats")
                    .SetDescription("chatai.export.chats.description")
                    .BindValue<SettingsController>(static x => x.ExportChats),
                UIButtons.Outline("chatai.export.download", UIStandardAction.Download.Icon)
                    .OnClickShowingLoading(nameof(SettingsController.ExportAsync))
                    .SetHorizontalAlignment(UIAlignment.Start)
            ),
            Card("chatai.import.defaults",
                UIText.Note("chatai.import.defaults.description"),
                UIButtons.Toolbar(
                    UIButtons.Outline("chatai.import.defaults.add")
                        .OnClickShowingLoading(nameof(SettingsController.LayDownDefaultsAsync)),
                    UIButtons.Outline("chatai.import.defaults.restore")
                        .InteractOn(EventNames.Click, new OpenDialogEffect(SettingsController.RestoreDefaultsDialogKey))
                )
            )
        );
}

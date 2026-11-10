using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

/// <summary>
/// A character's wide editor dialog: the card's head (<see cref="CardEditor"/>), its words, its first messages, its examples and the
/// instructions of its own, each kept as it lets go of its value.
/// </summary>
internal static class CharacterView
{
    public static UIDialog CreateDialog()
        => CardEditor.CreateDialog(ShellController.CharacterDialogKey, "chatai.character.title",
            new TabsComponent("chatai-character-tabs")
                .AddTab("card", "chatai.character.card", UILayout.Stack(16,
                    CardEditor.CreateHead(),
                    CardEditor.WorldField("chatai.character.world.hint"),
                    CardEditor.Area<ShellController>("chatai.character.description", static x => x.CardDescription, 20),
                    CardEditor.Area<ShellController>("chatai.character.personality", static x => x.CharacterPersonality, 6),
                    CardEditor.Area<ShellController>("chatai.character.scenario", static x => x.CharacterScenario, 6)
                ))
                .AddTab("greetings", "chatai.character.greetings", CreateGreetings())
                .AddTab("examples", "chatai.character.examples",
                    CardEditor.Area<ShellController>("chatai.character.example-messages", static x => x.CharacterExampleMessages, 24, "chatai.character.example-messages.hint")
                )
                .AddTab("more", "chatai.character.more", UILayout.Stack(16,
                    PictureFields.Background<ShellController>("chatai.character.background", "chatai.character.background.hint", static x => x.CharacterBackground, nameof(ShellController.SaveCharacterBackgroundAsync), nameof(ShellController.RemoveCharacterBackgroundAsync), static x => x.CardName, static x => x.CardAvatar.Value),
                    CardEditor.Area<ShellController>("chatai.character.authors-note", static x => x.CharacterAuthorsNote, 4, "chatai.character.authors-note.hint"),
                    CardEditor.Area<ShellController>("chatai.character.system-prompt", static x => x.CharacterSystemPrompt, 8, "chatai.character.system-prompt.hint"),
                    CardEditor.Area<ShellController>("chatai.character.post-history", static x => x.CharacterPostHistoryInstructions, 8, "chatai.character.post-history.hint"),
                    CardEditor.Area<ShellController>("chatai.character.creator-notes", static x => x.CharacterCreatorNotes, 8, "chatai.character.creator-notes.hint")
                ))
        );

    /// <summary>The first messages in an accordion, the first line and delete in each header and a borderless ghost editor inside, a new chat opening with them as variants.</summary>
    private static StackPanelComponent CreateGreetings()
        => UILayout.Stack(12,
            new ItemsViewComponent()
                .SetVerticalScroll(UIScrollMode.Disabled)
                .SetLayoutType(UIItemsLayoutType.Accordion)
                .BindItems<ShellController>(static x => x.CharacterGreetings)
                .ConfigureDefaultEmptyTemplate(empty => empty.SetTitle("chatai.character.greetings.none").SetTitleWrap(true))
                .SetTemplate(CardEditor.Fold<GreetingItem>(nameof(ShellController.RemoveGreetingAsync), "greetingId",
                        SettingsEditors.MacroInput(null, 12)
                            .SetAppearance(UIInputAppearance.Ghost)
                            .SetPlaceholder("chatai.character.greeting.placeholder")
                            .BindValue<GreetingItem>(static x => x.Text, UIBindingScope.Relative)
                            .OnChange(nameof(ShellController.SaveCardAsync))
                            .SetMargin(UIThickness.All(-16, -11, -16, -16))
                    )
                ),
            UIButtons.Ghost("chatai.character.greeting.add", UIStandardAction.Add.Icon)
                .OnClick(nameof(ShellController.AddGreeting))
                .SetHorizontalAlignment(UIAlignment.Start)
        );
}

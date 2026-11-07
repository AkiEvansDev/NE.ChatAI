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
            CardEditor.Section(null,
                CardEditor.CreateHead(),
                CardEditor.WorldField("chatai.character.world.hint")
            ),
            CardEditor.Section("chatai.character.card",
                CardEditor.Area<ShellController>("chatai.character.description", static x => x.CardDescription, 20),
                CardEditor.Area<ShellController>("chatai.character.personality", static x => x.CharacterPersonality, 6),
                CardEditor.Area<ShellController>("chatai.character.scenario", static x => x.CharacterScenario, 6)
            ),
            CardEditor.Section("chatai.character.greetings",
                CreateGreetings()
            ),
            CardEditor.Section("chatai.character.examples",
                CardEditor.Area<ShellController>("chatai.character.example-messages", static x => x.CharacterExampleMessages, 16, "chatai.character.example-messages.hint")
            ),
            CardEditor.Section("chatai.character.more",
                PictureFields.Background<ShellController>("chatai.character.background", "chatai.character.background.hint", static x => x.CharacterBackground, nameof(ShellController.SaveCharacterBackgroundAsync), nameof(ShellController.RemoveCharacterBackgroundAsync), static x => x.CardName, static x => x.CardAvatar.Value),
                CardEditor.Area<ShellController>("chatai.character.authors-note", static x => x.CharacterAuthorsNote, 4, "chatai.character.authors-note.hint"),
                CardEditor.Area<ShellController>("chatai.character.system-prompt", static x => x.CharacterSystemPrompt, 8, "chatai.character.system-prompt.hint"),
                CardEditor.Area<ShellController>("chatai.character.post-history", static x => x.CharacterPostHistoryInstructions, 8, "chatai.character.post-history.hint"),
                CardEditor.Area<ShellController>("chatai.character.creator-notes", static x => x.CharacterCreatorNotes, 8, "chatai.character.creator-notes.hint")
            )
        );

    /// <summary>The first messages, a field each with its remove beside it, a new chat opening with them as variants; then a new one.</summary>
    private static StackPanelComponent CreateGreetings()
        => UILayout.Stack(12,
            new ItemsViewComponent()
                .SetVerticalScroll(UIScrollMode.Disabled)
                .BindItems<ShellController>(static x => x.CharacterGreetings)
                .SetSpacing(12)
                .ConfigureDefaultEmptyTemplate(empty => empty.SetTitle("chatai.character.greetings.none").SetTitleWrap(true))
                .SetTemplate(new ContainerComponent()
                    .SetColumn(24, UIGridUnit.Auto())
                    .AddChild(SettingsEditors.MacroInput(null, 12)
                        .SetPlaceholder("chatai.character.greeting.placeholder")
                        .BindValue<GreetingItem>(static x => x.Text, UIBindingScope.Relative)
                        .OnChange(nameof(ShellController.SaveCardAsync))
                        .SetPlacement(1, 1, 23, 1)
                    )
                    .AddChild(UIIconButtons.Delete()
                        .OnClick(nameof(ShellController.RemoveGreetingAsync), UIAction.ArgCurrentItemKey("greetingId"))
                        .SetVerticalAlignment(UIAlignment.Start)
                        .SetMargin(UIThickness.All(8, 0, 0, 0))
                        .SetPlacement(24, 1, 1, 1)
                    )
                ),
            UIButtons.Ghost("chatai.character.greeting.add", UIStandardAction.Add.Icon)
                .OnClick(nameof(ShellController.AddGreeting))
                .SetHorizontalAlignment(UIAlignment.Start)
        );
}

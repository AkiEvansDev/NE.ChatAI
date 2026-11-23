using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

internal static class CharacterView
{
    public static UIDialog CreateDialog()
        => CardEditor.CreateDialog(ShellController.CharacterDialogKey, "chatai.character.title",
            new TabsComponent("chatai-character-tabs")
                .SetHeight(UILayoutLength.Fill())
                .AddTab("card", "chatai.character.card",
                    CardEditor.Scroll(
                        UILayout.Stack(16,
                            CardEditor.CreateHead(),
                            UIForm.Row(
                                CardEditor.PersonaField("chatai.character.persona.hint"),
                                CardEditor.WorldField("chatai.character.world.hint")
                            ),
                            CardEditor.Area<ShellController>("chatai.character.description", static x => x.CardDescription, 20, expandable: true),
                            CardEditor.Area<ShellController>("chatai.character.personality", static x => x.CharacterPersonality, 6, expandable: true),
                            CardEditor.Area<ShellController>("chatai.character.scenario", static x => x.CharacterScenario, 6, expandable: true)
                        )
                    )
                )
                .AddTab("greetings", "chatai.character.greetings", CardEditor.Scroll(CreateGreetings()))
                .AddTab("examples", "chatai.character.examples",
                    CardEditor.FillArea<ShellController>("chatai.character.example-messages", static x => x.CharacterExampleMessages, lineNumbers: true, hint: "chatai.character.example-messages.hint")
                )
                .AddTab("gallery", "chatai.character.gallery",
                    CardEditor.Scroll(
                        PictureFields.Gallery<ShellController>(
                            "chatai-character-gallery-search",
                            "chatai.gallery.character.description",
                            "chatai.gallery.none",
                            static x => x.CharacterGallerySelectionId,
                            nameof(ShellController.UploadCharacterGalleryAsync),
                            static x => x.CharacterGallery,
                            nameof(ShellController.ReplaceCharacterGalleryAsync),
                            nameof(ShellController.CharacterGalleryActionAsync)
                        )
                    )
                )
                .AddTab("more", "chatai.character.more",
                    CardEditor.Scroll(
                        UILayout.Stack(16,
                            PictureFields.Background<ShellController>("chatai.character.background", "chatai.character.background.hint", static x => x.CharacterBackground, nameof(ShellController.SaveCharacterBackgroundAsync), nameof(ShellController.RemoveCharacterBackgroundAsync), static x => x.CardName, static x => x.CardAvatar.Value),
                            CardEditor.Area<ShellController>("chatai.character.authors-note", static x => x.CharacterAuthorsNote, 4, hint: "chatai.character.authors-note.hint"),
                            CardEditor.Area<ShellController>("chatai.character.system-prompt", static x => x.CharacterSystemPrompt, 8, hint: "chatai.character.system-prompt.hint"),
                            CardEditor.Area<ShellController>("chatai.character.post-history", static x => x.CharacterPostHistoryInstructions, 8, hint: "chatai.character.post-history.hint")
                        )
                    )
                )
        );

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

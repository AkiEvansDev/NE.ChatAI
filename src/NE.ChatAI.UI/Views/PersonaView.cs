using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

/// <summary>
/// A persona's wide editor dialog: the card's head (<see cref="CardEditor"/>), whether new chats are started as it, and the
/// description the prompt puts in as <c>{{persona}}</c>.
/// </summary>
internal static class PersonaView
{
    public static UIDialog CreateDialog()
        => CardEditor.CreateDialog(ShellController.PersonaDialogKey, "chatai.persona.title",
            CardEditor.Section(null,
                CardEditor.CreateHead(),
                new SwitchComponent()
                    .SetTitle("chatai.persona.default")
                    .SetDescription("chatai.persona.default.hint")
                    .BindValue<ShellController>(static x => x.PersonaIsDefault)
                    .OnChange(nameof(ShellController.SavePersonaDefaultAsync)),
                CardEditor.WorldField("chatai.persona.world.hint"),
                CardEditor.Area<ShellController>("chatai.persona.description", static x => x.CardDescription, 20, "chatai.persona.description.hint")
            )
        );
}

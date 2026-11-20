using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

internal static class PersonaView
{
    public static UIDialog CreateDialog()
        => CardEditor.CreateDialog(ShellController.PersonaDialogKey, "chatai.persona.title",
            new ContainerComponent()
                .SetHeight(UILayoutLength.Fill())
                .SetRow(1, UIGridUnit.Auto())
                .AddRow(UIGridUnit.Auto())
                .AddRow(UIGridUnit.Auto())
                .AddRow(UIGridUnit.Star())
                .AddChild(CardEditor.CreateHead()
                    .SetMargin(UIThickness.All(0, 0, 0, 16))
                    .SetPlacement(1, 1, 24, 1)
                )
                .AddChild(new SwitchComponent()
                    .SetTitle("chatai.persona.default")
                    .SetDescription("chatai.persona.default.hint")
                    .BindValue<ShellController>(static x => x.PersonaIsDefault)
                    .OnChange(nameof(ShellController.SavePersonaDefaultAsync))
                    .SetMargin(UIThickness.All(0, 0, 0, 16))
                    .SetPlacement(1, 2, 24, 1)
                )
                .AddChild(CardEditor.WorldField("chatai.persona.world.hint")
                    .SetMargin(UIThickness.All(0, 0, 0, 16))
                    .SetPlacement(1, 3, 24, 1)
                )
                .AddChild(CardEditor.FillArea<ShellController>("chatai.persona.description", static x => x.CardDescription, expandable: true, hint: "chatai.persona.description.hint")
                    .SetPlacement(1, 4, 24, 1)
                )
        );
}

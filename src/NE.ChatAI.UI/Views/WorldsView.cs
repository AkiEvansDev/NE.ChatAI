using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

internal sealed class WorldsView : ShellView, IUIViewDefinition
{
    public static string ViewKey => "chatai.worlds";

    protected override string PageTitle => "chatai.nav.worlds";

    protected override bool FillsHeight => true;

    protected override UIColumnWidth? HeadColumnWidth => null;

    protected override UIResponsive<UIThickness> ContentPadding
        => UIResponsive<UIThickness>.Create(UIThickness.All(12, 4, 12, 12), md: UIThickness.All(24, 8, 24, 16));

    protected override IVisualComponent[] HeaderActions
        =>
        [
            UIIconButtons.More(
                [
                    new MenuItem { Id = ShellController.CharactersList, Title = "chatai.character.new-name", Icon = AppIcons.Outline(AppIcons.Chats) },
                    new MenuItem { Id = ShellController.PersonasList, Title = "chatai.persona.new-name", Icon = AppIcons.Outline(AppIcons.Persona) },
                    new MenuItem { Id = ShellController.WorldsList, Title = "chatai.world.new-name", Icon = AppIcons.Outline(AppIcons.Worlds) }
                ])
                .SetIcon(UIStandardAction.New.Icon)
                .SetTooltip(UIStandardAction.New.Title)
                .OnItemClick(nameof(WorldsController.CreateFromGraph), UIAction.ArgCurrentItemKey("kind"))
        ];

    protected override IVisualComponent CreatePage()
        => new ContainerComponent(WorldsController.PageId)
            .SetRow(1, UIGridUnit.Star())
            .SetHeight(UILayoutLength.Fill())
            .SetWidth(UILayoutLength.Fill())
            .AddChild(new LayeredGraphComponent(WorldsController.CanvasId)
                .SetDirection(UIGraphDirection.LeftToRight)
                .SetNodeShape(UIGraphNodeShape.Card)
                .SetEditStructure(false)
                .SetShowMenuPanel(false)
                .SetShowMinimap(true)
                .SetHeight(UILayoutLength.Fill())
                .SetWidth(UILayoutLength.Fill())
                .BindItems<WorldsController>(static x => x.Nodes)
                .BindValue<WorldsController>(static x => x.GraphDocument, mode: UIBindingMode.OneWay)
                .OnNodeClick(nameof(WorldsController.OpenNodeAsync), "nodeId")
                .BindVisibility<WorldsController>(static x => x.GraphVisibility)
                .SetPlacement(1, 1, 24, 1)
            )
            .AddChild(UIPage.Empty(AppIcons.Outline(AppIcons.Bindings), "chatai.worlds.empty", "chatai.worlds.empty.hint")
                .BindVisibility<WorldsController>(static x => x.EmptyVisibility)
                .SetPlacement(1, 1, 24, 1)
            );
}

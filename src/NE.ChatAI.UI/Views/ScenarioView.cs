using System.Collections.Generic;
using NE.ChatAI.Controllers;
using NE.ChatAI.Scenarios;

namespace NE.ChatAI.Views;

/// <summary>
/// The scenarios page (<c>/scenarios</c>): a side rail with the scenario selector, Rename/Duplicate/Delete menu and trigger controls
/// beside the full-height node canvas (<see cref="NodesComponent"/>), stacked one over the other on a phone.
/// </summary>
internal sealed class ScenarioView : ShellView, IUIViewDefinition
{
    private const double SidebarWidth = 280;

    private static readonly OptionItem[] TriggerKinds =
    [
        new() { Id = nameof(ScenarioTriggerKind.Manual), Title = "chatai.scenarios.trigger.manual" },
        new() { Id = nameof(ScenarioTriggerKind.BeforeSend), Title = "chatai.scenarios.trigger.before-send" },
        new() { Id = nameof(ScenarioTriggerKind.AfterReply), Title = "chatai.scenarios.trigger.after-reply" },
        new() { Id = nameof(ScenarioTriggerKind.ContextShare), Title = "chatai.scenarios.trigger.context-share" }
    ];

    public static string ViewKey => "chatai.scenarios";

    protected override string PageTitle => "chatai.nav.scenarios";

    protected override bool FillsHeight => true;

    protected override UIColumnWidth? HeadColumnWidth => null;

    protected override UIResponsive<UIThickness> ContentPadding
        => UIResponsive<UIThickness>.Create(UIThickness.All(12, 4, 12, 12), md: UIThickness.All(24, 8, 24, 16));

    protected override IVisualComponent CreatePage()
    {
        SelectComponent trigger = new SelectComponent()
            .SetTitle("chatai.scenarios.trigger")
            .SetOptions(TriggerKinds)
            .BindValue<ScenarioController>(static x => x.TriggerKind)
            .OnChange(nameof(ScenarioController.SaveMetaAsync));

        return new ContainerComponent(ScenarioController.PageId)
            .SetColumn(1, UIGridUnit.Star(), md: UIGridUnit.Auto())
            .SetRow(1, UIGridUnit.Auto(), md: UIGridUnit.Star())
            .AddRow(UIGridUnit.Star(), md: UIGridUnit.Auto())
            .SetHeight(UILayoutLength.Fill())
            .SetWidth(UILayoutLength.Fill())
            .AddChild(UILayout.Stack(16,
                    new SelectComponent()
                        .SetTitle("chatai.scenarios.scenario")
                        .SetGroupTemplate(new DefaultGroupTemplate())
                        .BindOptions<ScenarioController>(static x => x.ScenarioOptions)
                        .BindValue<ScenarioController>(static x => x.ScenarioId)
                        .OnChange(nameof(ScenarioController.SelectScenarioAsync))
                        .AddTrailingAction(UIIconButtons.New().OnClick(nameof(ScenarioController.CreateScenarioAsync), UIAction.Arg("copy", false)))
                        .AddTrailingAction(UIIconButtons.More()
                            .BindItems<ScenarioController>(static x => x.ScenarioActions)
                            .OnItemClick(nameof(ScenarioController.ScenarioMenuActionAsync), UIAction.ArgCurrentItemKey("action"))
                            .BindVisibility<ScenarioController>(static x => x.EditorVisibility)
                        ),
                    UILayout.Stack(16,
                            new SwitchComponent()
                                .SetTitle("chatai.scenarios.enabled")
                                .BindValue<ScenarioController>(static x => x.ScenarioEnabled)
                                .OnChange(nameof(ScenarioController.SaveMetaAsync)),
                            new SwitchComponent()
                                .SetTitle("chatai.scenarios.pinned")
                                .BindValue<ScenarioController>(static x => x.ScenarioPinned)
                                .OnChange(nameof(ScenarioController.SaveMetaAsync)),
                            trigger,
                            new NumberInputComponent()
                                .SetTitle("chatai.scenarios.share")
                                .SetShowStepper(true)
                                .SetMin(5)
                                .SetMax(95)
                                .SetStep(5)
                                .BindValue<ScenarioController>(static x => x.TriggerSharePercent)
                                .OnChange(nameof(ScenarioController.SaveMetaAsync))
                                .ShownWhen(trigger, nameof(ScenarioTriggerKind.ContextShare))
                        )
                        .BindVisibility<ScenarioController>(static x => x.TriggerVisibility)
                )
                .SetWidth(UILayoutLength.Fill(), md: UILayoutLength.Absolute(SidebarWidth))
                .SetMargin(UIThickness.All(0, 0, 0, 12), md: UIThickness.All(0, 0, 16, 0))
                .SetPlacement(1, 1, 24, 1, md: UIGridPlacement.At(1, 1, 1, 1))
            )
            .AddChild(new NodesComponent(ScenarioController.CanvasId)
                .BindCatalog<ScenarioController>(static x => x.Catalog)
                .BindPinChoices<ScenarioController>(static x => x.PinChoices)
                .BindSheetName<ScenarioController>(static x => x.SheetTitle)
                .SetAutoSave(true)
                .SetShowRunPanel(true)
                .SetShowRunProgress(true)
                .SetShowMinimap(true)
                .SetNodeActionBar()
                .SetHeight(UILayoutLength.Fill())
                .SetWidth(UILayoutLength.Fill())
                .SetFormId(ScenarioController.SaveFormId)
                .BindValue<ScenarioController>(static x => x.Sheet, mode: UIBindingMode.OnSubmit)
                .OnSave(nameof(ScenarioController.SaveSheetAsync), UIGraphArguments.Reason("reason"))
                .OnStop(nameof(ScenarioController.StopRunAsync))
                .BindVisibility<ScenarioController>(static x => x.EditorVisibility)
                .SetPlacement(1, 2, 24, 1, md: UIGridPlacement.At(2, 1, 23, 1))
            )
            .AddChild(UIPage.Empty(AppIcons.Outline(AppIcons.Scenarios), "chatai.scenarios.none")
                .BindVisibility<ScenarioController>(static x => x.EmptyVisibility)
                .SetPlacement(1, 2, 24, 1, md: UIGridPlacement.At(2, 1, 23, 1))
            );
    }

    protected override IReadOnlyList<UIDialog> CreatePageDialogs()
        =>
        [
            UIDialogs.Form(ScenarioController.RenameDialogKey, "chatai.settings.rename.title", null,
                new TextInputComponent()
                    .SetTitle("chatai.settings.name")
                    .BindValue<ScenarioController>(static x => x.RenameName),
                UIButtons.Primary("chatai.settings.rename").OnClick(nameof(ScenarioController.RenameAsync))
            ),
            UIDialogs.BoundConfirm<ScenarioController>(ScenarioController.DeleteDialogKey, "chatai.scenarios.delete.title", static x => x.DeleteQuestion, "chatai.settings.delete", nameof(ScenarioController.DeleteAsync))
        ];
}

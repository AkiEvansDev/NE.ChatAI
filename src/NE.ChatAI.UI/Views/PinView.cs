using System.Collections.Generic;
using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

/// <summary>Standalone PIN gate shown whenever a browser has not yet unlocked the configured PIN.</summary>
[UIAllowAnonymous]
internal sealed class PinView : UIViewBase, IUIViewDefinition
{
    public const string FormId = "chatai-pin-form";
    public const string InputId = "chatai-pin-input";

    public static string ViewKey => "chatai.pin";

    public override string Title => "chatai.title";

    public override IReadOnlyDictionary<string, object?>? TitleArguments { get; }
        = new Dictionary<string, object?> { ["page"] = new UIPhrase("chatai.pin.title") };

    protected override IVisualComponent CreateContent()
        => UICard.Centered(
            UICard.Create(
                "chatai.pin.title",
                "chatai.pin.description",
                UILayout.Stack(16,
                    UIMessage.Create(UIColorStyle.Danger, null, UIText.Paragraph().BindDescription<PinController>(static x => x.ErrorMessage))
                        .BindVisibility<PinController>(static x => x.ErrorVisibility),
                    new TextInputComponent(InputId)
                        .SetFormId(FormId)
                        .SetTitle("chatai.pin.field")
                        .SetType(UITextInputType.Password)
                        .SetAutocomplete(UIAutocomplete.CurrentPassword)
                        .BindValue<PinController>(static x => x.Pin, mode: UIBindingMode.OnSubmit),
                    UIButtons.Primary("chatai.pin.unlock")
                        .SetWidth(UILayoutLength.Fill())
                        .OnSubmitShowingLoading(FormId, nameof(PinController.UnlockAsync))
                ),
                icon: AppIcons.Outline(AppIcons.Lock)
            )
        );
}

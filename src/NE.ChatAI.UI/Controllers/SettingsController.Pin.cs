using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NE.ChatAI.Hosting;
using NE.ChatAI.Settings;

namespace NE.ChatAI.Controllers;

internal sealed partial class SettingsController
{
    public const string PinFormId = "chatai-settings-pin-form";
    public const string PinInputId = "chatai-settings-pin-input";

    [RecursiveMember]
    public partial string NewPin { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIPhrase? PinStatus { get; set; } = new("chatai.settings.pin.off");

    [RecursiveMember]
    public partial UIPhrase? PinSaveLabel { get; set; } = new("chatai.settings.pin.set");

    [RecursiveMember]
    public partial UIVisibility PinClearVisibility { get; set; } = UIVisibility.Collapsed;

    private void ShowPinState(string? pinHash)
    {
        var enabled = !string.IsNullOrWhiteSpace(pinHash);
        PinStatus = new UIPhrase(enabled ? "chatai.settings.pin.on" : "chatai.settings.pin.off");
        PinSaveLabel = new UIPhrase(enabled ? "chatai.settings.pin.change" : "chatai.settings.pin.set");
        PinClearVisibility = enabled ? UIVisibility.Visible : UIVisibility.Collapsed;
    }

    /// <summary>Sets or rotates the optional PIN, invalidating earlier browser sessions while keeping the current browser unlocked.</summary>
    [UICommand]
    public async Task<UICommandResult> SavePinAsync(CancellationToken cancellationToken)
    {
        var trimmed = NewPin.Trim();

        if (trimmed.Length == 0)
            return Notify("chatai.settings.pin.empty", UIColorStyle.Warning);

        AppSettings current = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        var previousSessionId = UserSessionResolver.OwnerSessionId(current.PinHash);
        var nextPinHash = PinSecurity.Hash(trimmed);

        await Data.WriteSettingsAsync(current with { PinHash = nextPinHash }, cancellationToken).ConfigureAwait(false);
        Context.Services.GetRequiredService<PinAttemptThrottle>().Reset();
        NewPin = string.Empty;
        ShowPinState(nextPinHash);

        IUISessions sessions = Context.Services.GetRequiredService<IUISessions>();
        IPinCookieBridge? bridge = Context.Services.GetService<IPinCookieBridge>();

        if (bridge is not null)
        {
            await sessions.EndSessionAsync(previousSessionId, Context.Handle, cancellationToken).ConfigureAwait(false);
            var nextSecret = UserSessionResolver.OwnerSecret(nextPinHash);
            return UICommandResult.Ok([new NavigateEffect(bridge.UnlockAddress(nextSecret, AppRoutes.Settings))]);
        }

        await Context.SignInAsync(UserSessionResolver.PinUserKey(nextPinHash), cancellationToken: cancellationToken).ConfigureAwait(false);
        await sessions.EndSessionAsync(previousSessionId, Context.Handle, cancellationToken).ConfigureAwait(false);

        return Notify("chatai.settings.pin.saved", UIColorStyle.Success);
    }

    /// <summary>Removes the configured PIN so every browser on the network opens the application directly again.</summary>
    [UICommand]
    public async Task<UICommandResult> ClearPinAsync(CancellationToken cancellationToken)
    {
        AppSettings current = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(current.PinHash))
        {
            ShowPinState(null);
            return UICommandResult.Ok([]);
        }

        var previousSessionId = UserSessionResolver.OwnerSessionId(current.PinHash);
        await Data.WriteSettingsAsync(current with { PinHash = null }, cancellationToken).ConfigureAwait(false);
        Context.Services.GetRequiredService<PinAttemptThrottle>().Reset();
        NewPin = string.Empty;
        ShowPinState(null);

        IUISessions sessions = Context.Services.GetRequiredService<IUISessions>();
        IPinCookieBridge? bridge = Context.Services.GetService<IPinCookieBridge>();

        if (bridge is not null)
        {
            await sessions.EndSessionAsync(previousSessionId, Context.Handle, cancellationToken).ConfigureAwait(false);
            var defaultSecret = UserSessionResolver.OwnerSecret(null);
            return UICommandResult.Ok([new NavigateEffect(bridge.UnlockAddress(defaultSecret, AppRoutes.Settings))]);
        }

        await Context.SignInAsync(UserSessionResolver.DefaultUserKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        await sessions.EndSessionAsync(previousSessionId, Context.Handle, cancellationToken).ConfigureAwait(false);

        return Notify("chatai.settings.pin.removed", UIColorStyle.Success);
    }
}

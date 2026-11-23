using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NE.ChatAI.Hosting;
using NE.ChatAI.Settings;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Controllers;

/// <summary>Unlocks a browser session when an optional PIN is configured in <see cref="AppSettings.PinHash"/>.</summary>
[UIAllowAnonymous]
internal sealed partial class PinController(DataFolder data, PinAttemptThrottle throttle) : UIControllerBase
{
    public const string ReturnUrlParameter = "returnUrl";

    private string _returnUrl = AppRoutes.Home;

    [RecursiveMember]
    public partial string Pin { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIPhrase? ErrorMessage { get; set; }

    [RecursiveMember]
    public partial UIVisibility ErrorVisibility { get; set; } = UIVisibility.Collapsed;

    protected override async Task OnNavigatedAsync(UINavigationRequest navigation, CancellationToken cancellationToken)
    {
        UpdateReturnUrl(navigation);
        await RedirectIfUnlockedAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task OnAttachedAsync(UINavigationRequest navigation, CancellationToken cancellationToken)
    {
        UpdateReturnUrl(navigation);
        await RedirectIfUnlockedAsync(cancellationToken).ConfigureAwait(false);
    }

    private void UpdateReturnUrl(UINavigationRequest navigation)
    {
        if (navigation.TryGetParameter(ReturnUrlParameter, out var target)
            && !string.IsNullOrWhiteSpace(target)
            && UIRoutePath.IsLocal(target)
            && !target.StartsWith(AppRoutes.Pin, StringComparison.OrdinalIgnoreCase))
        {
            _returnUrl = target;
        }
    }

    private async Task RedirectIfUnlockedAsync(CancellationToken cancellationToken)
    {
        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        UserSessionState? session = await Context.GetSessionAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(settings.PinHash) || session?.IsAuthenticated == true)
            await Context.SendEffectsAsync([new NavigateEffect(_returnUrl)], cancellationToken).ConfigureAwait(false);
    }

    [UICommand]
    public async Task<UICommandResult> UnlockAsync(CancellationToken cancellationToken)
    {
        if (throttle.IsLockedOut(out var lockedSeconds))
        {
            ShowError(UIPhrase.Of("chatai.pin.locked", ("seconds", lockedSeconds)));
            return UICommandResult.Ok([]);
        }

        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(settings.PinHash))
            return UICommandResult.Ok([new NavigateEffect(_returnUrl)]);

        if (!PinSecurity.Verify(Pin, settings.PinHash))
        {
            Pin = string.Empty;
            var lockedNow = throttle.RecordFailure(out var cooldownSeconds);
            ShowError(lockedNow ? UIPhrase.Of("chatai.pin.locked", ("seconds", cooldownSeconds)) : new UIPhrase("chatai.pin.wrong"));
            return UICommandResult.Ok([]);
        }

        throttle.Reset();
        Pin = string.Empty;
        ErrorMessage = null;
        ErrorVisibility = UIVisibility.Collapsed;

        IPinCookieBridge? bridge = Context.Services.GetService<IPinCookieBridge>();

        if (bridge is not null)
        {
            var secret = UserSessionResolver.OwnerSecret(settings.PinHash);
            return UICommandResult.Ok([new NavigateEffect(bridge.UnlockAddress(secret, _returnUrl))]);
        }

        await Context.SignInAsync(UserSessionResolver.PinUserKey(settings.PinHash), cancellationToken: cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok([new NavigateEffect(_returnUrl)]);
    }

    private void ShowError(UIPhrase message)
    {
        ErrorMessage = message;
        ErrorVisibility = UIVisibility.Visible;
    }
}

using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Settings;
using NE.ChatAI.Storage;
using NE.Standard.UI.Sessions;
using NE.Standard.UI.Shell.Localization;

namespace NE.ChatAI.Hosting;

/// <summary>
/// Resolves one deterministic session across all unlocked browsers of the owner, while isolating unverified browsers into anonymous sessions
/// whenever a PIN is configured.
/// </summary>
internal sealed class UserSessionResolver : IUserSessionResolver
{
    public const string CookieName = "ne.chatai.session";
    public const string DefaultUserKey = "default";
    public static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(365);

    private readonly DataFolder _data;
    private readonly IUserSessionStore _store;
    private readonly UIApplication _application;
    private readonly IUserClaimsMapper _claims;

    public UserSessionResolver(DataFolder data, IUserSessionStore store, UIApplication application, IUserClaimsMapper claims)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(claims);

        _data = data;
        _store = store;
        _application = application;
        _claims = claims;
    }

    public string SecretFor(ClaimsPrincipal? principal)
        => SecretForUser(ResolveUserKey(principal));

    public static string SecretForUser(string userKey)
        => FormatSecret(SHA256.HashData(Encoding.UTF8.GetBytes("NE.ChatAI.Session:" + userKey)));

    public static string OwnerSecret(string? pinHash)
        => string.IsNullOrWhiteSpace(pinHash)
            ? SecretForUser(DefaultUserKey)
            : FormatSecret(PinSecurity.TokenBytesOf(pinHash));

    public static string OwnerSessionId(string? pinHash)
        => UISessionSecret.ToSessionId(OwnerSecret(pinHash));

    public static string PinUserKey(string? pinHash)
        => string.IsNullOrWhiteSpace(pinHash)
            ? DefaultUserKey
            : "pin:" + OwnerSessionId(pinHash)[..16];

    private static string FormatSecret(ReadOnlySpan<byte> keyBytes)
    {
        var hex = Convert.ToHexStringLower(keyBytes);

        if (UISessionSecret.TryToSessionId(hex) is not null)
            return hex;

        var base64Url = Base64Url.EncodeToString(keyBytes);

        return UISessionSecret.TryToSessionId(base64Url) is not null
            ? base64Url
            : throw new InvalidOperationException("Session secret encoding is incompatible with UISessionSecret.");
    }

    private string ResolveUserKey(ClaimsPrincipal? principal)
    {
        if (principal is null)
            return DefaultUserKey;

        UserClaimsIdentity identity = _claims.Map(principal);

        return identity.IsAuthenticated && !string.IsNullOrWhiteSpace(identity.UserId)
            ? identity.UserId
            : DefaultUserKey;
    }

    public async Task<IUserSessionContext> ResolveAsync(UserSessionInitData initData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initData);

        AppSettings settings = await _data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        var pinHash = string.IsNullOrWhiteSpace(settings.PinHash) ? null : settings.PinHash;
        DateTime utcNow = DateTime.UtcNow;

        if (pinHash is null)
        {
            var userKey = ResolveUserKey(initData.Principal);
            var sharedId = UISessionSecret.ToSessionId(SecretForUser(userKey));

            return await ResolveAuthenticatedAsync(sharedId, userKey, initData, utcNow, cancellationToken).ConfigureAwait(false);
        }

        var ownerSessionId = OwnerSessionId(pinHash);
        var expectedUserId = PinUserKey(pinHash);

        if (string.Equals(initData.SessionId, ownerSessionId, StringComparison.Ordinal))
            return await ResolveAuthenticatedAsync(ownerSessionId, expectedUserId, initData, utcNow, cancellationToken).ConfigureAwait(false);

        if (initData.SessionId is string presentedId)
        {
            UserSessionState? existing = await _store.TryGetAsync(presentedId, cancellationToken).ConfigureAwait(false);

            if (existing is not null && !existing.IsIdle(_application.Sessions, utcNow))
            {
                if (existing.IsAuthenticated && string.Equals(existing.UserId, expectedUserId, StringComparison.Ordinal))
                    return existing;

                if (!existing.IsAuthenticated)
                    return existing;
            }
        }

        var anonymousId = initData.IssueSessionId();
        UserSessionState anonymous = new()
        {
            SessionId = anonymousId,
            IsAuthenticated = false,
            Language = NegotiateLanguage(initData.Languages),
            CreatedAtUtc = utcNow,
            LastSeenAtUtc = utcNow,
            PendingIdRotation = false,
            IsUnclaimed = false
        };

        await _store.SaveAsync(anonymous, cancellationToken).ConfigureAwait(false);

        return anonymous;
    }

    private async Task<UserSessionState> ResolveAuthenticatedAsync(string sessionId, string userId, UserSessionInitData initData, DateTime utcNow, CancellationToken cancellationToken)
    {
        UserSessionState? stored = await _store.TryGetAsync(sessionId, cancellationToken).ConfigureAwait(false);

        if (stored is not null && stored.IsIdle(_application.Sessions, utcNow))
            stored = null;

        UserSessionState session = stored ?? new UserSessionState
        {
            SessionId = sessionId,
            Language = NegotiateLanguage(initData.Languages),
            CreatedAtUtc = utcNow,
            LastSeenAtUtc = utcNow
        };

        session = StoredUserSessionResolver.ApplyClaims(session, initData.Principal, _application.Security, _claims) with
        {
            IsAuthenticated = true,
            UserId = userId,
            PendingIdRotation = false,
            IsUnclaimed = false
        };

        // Saved before UIHost.PersistSessionAsync runs so TryUpdateAsync finds it in place instead of rotating the deterministic id.
        if (stored is null || !stored.IsAuthenticated || !string.Equals(stored.UserId, userId, StringComparison.Ordinal))
            await _store.SaveAsync(session, cancellationToken).ConfigureAwait(false);

        return session;
    }

    private string NegotiateLanguage(IReadOnlyList<string> wanted)
    {
        ITranslator translator = _application.Translator;

        if (!_application.Localization.NegotiateLanguage)
            return translator.DefaultLanguage;

        for (var i = 0; i < wanted.Count; i++)
        {
            if (FindLanguage(translator.Languages, wanted[i]) is string exact)
                return exact;

            var dash = wanted[i].IndexOf('-', StringComparison.Ordinal);

            if (dash > 0 && FindLanguage(translator.Languages, wanted[i][..dash]) is string primary)
                return primary;
        }

        return translator.DefaultLanguage;
    }

    private static string? FindLanguage(IReadOnlyList<string> languages, string tag)
    {
        for (var i = 0; i < languages.Count; i++)
        {
            if (string.Equals(languages[i], tag, StringComparison.OrdinalIgnoreCase))
                return languages[i];
        }

        return null;
    }
}

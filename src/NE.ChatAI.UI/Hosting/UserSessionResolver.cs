using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NE.Standard.UI.Sessions;
using NE.Standard.UI.Shell.Localization;

namespace NE.ChatAI.Hosting;

/// <summary>
/// Resolves one deterministic session per user (or <see cref="DefaultUserKey"/> when anonymous) so that every browser and device of
/// the same user shares one session and one controller per route.
/// </summary>
internal sealed class UserSessionResolver : IUserSessionResolver
{
    public const string CookieName = "ne.chatai.session";
    public const string DefaultUserKey = "default";

    private readonly IUserSessionStore _store;
    private readonly UIApplication _application;
    private readonly IUserClaimsMapper _claims;

    public UserSessionResolver(IUserSessionStore store, UIApplication application, IUserClaimsMapper claims)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(claims);

        _store = store;
        _application = application;
        _claims = claims;
    }

    public string SecretFor(ClaimsPrincipal? principal)
        => SecretForUser(ResolveUserKey(principal));

    public async Task<IUserSessionContext> ResolveAsync(UserSessionInitData initData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initData);

        var sessionId = UISessionSecret.ToSessionId(SecretFor(initData.Principal));
        DateTime utcNow = DateTime.UtcNow;
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
            PendingIdRotation = false,
            IsUnclaimed = false
        };

        // Saved before UIHost.PersistSessionAsync runs so TryUpdateAsync finds it in place instead of rotating the deterministic id.
        if (stored is null)
            await _store.SaveAsync(session, cancellationToken).ConfigureAwait(false);

        return session;
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

    public static string SecretForUser(string userKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("NE.ChatAI.Session:" + userKey));

        return Convert.ToHexStringLower(bytes);
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

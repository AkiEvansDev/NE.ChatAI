using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Backends.KoboldCpp;

namespace NE.ChatAI.Connections;

/// <summary>The context a generation is fitted into, and where the number came from.</summary>
/// <remarks><paramref name="ServerAway"/>: the server was asked and did not answer — no reason to ask it for a reply next.</remarks>
public readonly record struct ResolvedContext(int Length, bool FromServer, bool ServerAway = false);

/// <summary>A connection's context: the server's own when it is asked for and the server tells, else the profile's number.</summary>
public static class ContextLengths
{
    /// <summary>
    /// Asks koboldcpp for the context it was started with when the profile says <c>auto</c>; a server that does not answer, or
    /// answers nothing usable, leaves the profile's number; one that does not answer at all is said to be away.
    /// </summary>
    public static async Task<ResolvedContext> ResolveAsync(ConnectionProfile profile, KoboldCppClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(client);

        if (!profile.ContextLength.Auto)
            return new ResolvedContext(profile.ContextLength.Value, FromServer: false);

        try
        {
            var length = await client.GetContextLengthAsync(cancellationToken).ConfigureAwait(false);

            if (length > 0)
                return new ResolvedContext(length, FromServer: true);
        }
        catch (HttpRequestException)
        {
            return new ResolvedContext(profile.ContextLength.Value, FromServer: false, ServerAway: true);
        }

        return new ResolvedContext(profile.ContextLength.Value, FromServer: false);
    }
}

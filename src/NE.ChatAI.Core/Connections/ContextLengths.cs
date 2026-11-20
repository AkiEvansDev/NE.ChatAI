using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Backends.KoboldCpp;

namespace NE.ChatAI.Connections;

/// <summary>The context length a generation fits into, whether it came from the server, and whether the server was unreachable.</summary>
public readonly record struct ResolvedContext(int Length, bool FromServer, bool ServerAway = false);

/// <summary>A connection's context: the server's own when it is asked for and the server tells, else the profile's number.</summary>
public static class ContextLengths
{
    /// <summary>Queries koboldcpp's context length when <c>auto</c> is enabled, falling back to the profile's configured length.</summary>
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
        catch (JsonException)
        {
            return new ResolvedContext(profile.ContextLength.Value, FromServer: false, ServerAway: true);
        }

        return new ResolvedContext(profile.ContextLength.Value, FromServer: false);
    }
}

using System;
using System.Collections.Generic;
using System.Threading;

namespace NE.ChatAI.Hosting;

/// <summary>Issues short-lived single-use HTTP redirect tickets so unlocked browsers receive the persistent shared session cookie.</summary>
internal interface IPinCookieBridge
{
    string UnlockAddress(string ownerSecret, string returnUrl);
}

/// <summary>In-memory single-use ticket store consumed by the web host's cookie endpoint.</summary>
internal sealed class PinCookieBridge(TimeProvider clock) : IPinCookieBridge
{
    public const string EndpointPath = "/_chatai/pin";
    private const int MaxTickets = 64;
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(30);

    private readonly Lock _lock = new();
    private readonly Queue<string> _order = new();
    private readonly Dictionary<string, (string Secret, string ReturnUrl, DateTimeOffset ExpiresAt)> _tickets = [with(StringComparer.Ordinal)];

    public string UnlockAddress(string ownerSecret, string returnUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSecret);

        var safeReturnUrl = UIRoutePath.IsLocal(returnUrl) && !returnUrl.StartsWith(AppRoutes.Pin, StringComparison.OrdinalIgnoreCase)
            ? returnUrl
            : AppRoutes.Home;
        var ticket = Guid.CreateVersion7().ToString("N");
        DateTimeOffset now = clock.GetUtcNow();

        lock (_lock)
        {
            PurgeExpiredNoLock(now);

            while (_order.Count >= MaxTickets)
            {
                var oldest = _order.Dequeue();
                _ = _tickets.Remove(oldest);
            }

            _order.Enqueue(ticket);
            _tickets[ticket] = (ownerSecret, safeReturnUrl, now + TicketLifetime);
        }

        return $"{EndpointPath}?ticket={Uri.EscapeDataString(ticket)}";
    }

    private void PurgeExpiredNoLock(DateTimeOffset now)
    {
        while (_order.Count > 0 && _order.Peek() is var head && (!_tickets.TryGetValue(head, out (string Secret, string ReturnUrl, DateTimeOffset ExpiresAt) entry) || entry.ExpiresAt < now))
        {
            _ = _order.Dequeue();
            _ = _tickets.Remove(head);
        }
    }

    public (string Secret, string ReturnUrl)? TryConsume(string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            return null;

        DateTimeOffset now = clock.GetUtcNow();

        lock (_lock)
        {
            PurgeExpiredNoLock(now);

            return _tickets.Remove(ticket, out (string Secret, string ReturnUrl, DateTimeOffset ExpiresAt) entry) && entry.ExpiresAt >= now
                ? (entry.Secret, entry.ReturnUrl)
                : null;
        }
    }
}

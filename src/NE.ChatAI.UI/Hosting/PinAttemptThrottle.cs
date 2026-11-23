using System;
using System.Threading;

namespace NE.ChatAI.Hosting;

/// <summary>Rate-limits failed PIN guesses across connections to prevent LAN brute-forcing.</summary>
internal sealed class PinAttemptThrottle(TimeProvider clock)
{
    public const int MaxAttemptsBeforeCooldown = 6;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(20);

    private readonly Lock _lock = new();
    private int _failures;
    private DateTimeOffset _lockedUntil;

    public bool IsLockedOut(out int remainingSeconds)
    {
        DateTimeOffset now = clock.GetUtcNow();

        lock (_lock)
        {
            if (now >= _lockedUntil)
            {
                remainingSeconds = 0;
                return false;
            }

            remainingSeconds = Math.Max(1, (int)Math.Ceiling((_lockedUntil - now).TotalSeconds));
            return true;
        }
    }

    public bool RecordFailure(out int remainingSeconds)
    {
        DateTimeOffset now = clock.GetUtcNow();

        lock (_lock)
        {
            if (now < _lockedUntil)
            {
                remainingSeconds = Math.Max(1, (int)Math.Ceiling((_lockedUntil - now).TotalSeconds));
                return true;
            }

            _failures++;

            if (_failures >= MaxAttemptsBeforeCooldown)
            {
                _failures = 0;
                _lockedUntil = now + Cooldown;
                remainingSeconds = (int)Cooldown.TotalSeconds;
                return true;
            }

            remainingSeconds = 0;
            return false;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _failures = 0;
            _lockedUntil = default;
        }
    }
}

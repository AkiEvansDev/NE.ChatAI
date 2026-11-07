using System;
using System.Security.Cryptography;

namespace NE.ChatAI.Samplers;

/// <summary>The seed a generation is made with, and the chat's seed after it.</summary>
public readonly record struct SeedChoice(long Seed, long? ChatSeed);

/// <summary>
/// Picks a generation's seed by the preset's rule. It always names one — never koboldcpp's <c>-1</c>, whose pick nobody learns — so
/// the seed is kept with the reply.
/// </summary>
public static class Seeds
{
    /// <param name="rule">The preset's rule.</param>
    /// <param name="chatSeed">The chat's seed so far, <c>null</c> for a chat that has none yet.</param>
    /// <param name="regenerate">Whether this is a new variant of a reply already there.</param>
    public static SeedChoice Pick(SeedRule rule, long? chatSeed, bool regenerate)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return rule.Mode switch
        {
            SeedMode.Fixed => new SeedChoice(rule.Value >= 0 ? rule.Value : New(), chatSeed),
            // A regenerate changes this message alone: the chat's seed stays for the next one.
            SeedMode.PerChat when regenerate => new SeedChoice(New(), chatSeed ?? New()),
            SeedMode.PerChat => chatSeed is long kept ? new SeedChoice(kept, kept) : Started(),
            _ => new SeedChoice(New(), chatSeed)
        };
    }

    private static SeedChoice Started()
    {
        var seed = New();

        return new SeedChoice(seed, seed);
    }

    /// <summary>A seed koboldcpp takes: a non-negative 32-bit number.</summary>
    public static long New()
        => RandomNumberGenerator.GetInt32(int.MaxValue);
}

using System;
using System.Security.Cryptography;

namespace NE.ChatAI.Samplers;

/// <summary>The seed a generation is made with, and the chat's seed after it.</summary>
public readonly record struct SeedChoice(long Seed, long? ChatSeed);

/// <summary>Picks a concrete non-negative seed for each generation so the seed used is always known and persisted.</summary>
public static class Seeds
{
    /// <summary>Resolves the generation seed and updated chat seed according to <paramref name="rule"/>.</summary>
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

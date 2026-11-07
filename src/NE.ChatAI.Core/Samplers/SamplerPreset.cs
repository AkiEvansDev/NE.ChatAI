using System;
using System.Collections.Generic;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Samplers;

/// <summary>Where a generation's seed comes from.</summary>
public enum SeedMode
{
    /// <summary>A new seed every generation.</summary>
    Random,

    /// <summary>The chat keeps one seed; a regenerate draws a new one for that message only.</summary>
    PerChat,

    /// <summary>Always <see cref="SeedRule.Value"/>.</summary>
    Fixed
}

public sealed record SeedRule
{
    public SeedMode Mode { get; init; } = SeedMode.PerChat;

    public long Value { get; init; } = -1;
}

/// <summary>DRY: penalizes repeating a sequence already in the context once it runs past an allowed length.</summary>
public sealed record DrySampler
{
    public double Multiplier { get; init; }

    public double Base { get; init; } = 1.75;

    public int AllowedLength { get; init; } = 2;

    public int PenaltyRange { get; init; }

    public IReadOnlyList<string> SequenceBreakers { get; init; } = ["\n", ":", "\"", "*"];
}

/// <summary>XTC: with a probability, removes the likeliest tokens above a threshold, so the reply leaves the obvious path.</summary>
public sealed record XtcSampler
{
    public double Threshold { get; init; } = 0.1;

    public double Probability { get; init; }
}

/// <summary>A temperature that moves with the distribution's entropy between two bounds.</summary>
public sealed record DynamicTemperature
{
    public double Min { get; init; }

    public double Max { get; init; } = 2;

    public double Exponent { get; init; } = 1;
}

public sealed record MirostatSampler
{
    /// <summary>1 or 2; 0 is off.</summary>
    public int Mode { get; init; }

    public double Tau { get; init; } = 5;

    public double Eta { get; init; } = 0.1;
}

/// <summary>The samplers a generation is made with, the reply's length, and the seed rule. A field left out takes the backend's default.</summary>
public sealed record SamplerPreset : NamedDocument
{
    /// <summary>The longest reply, in tokens.</summary>
    public int ReplyLength { get; init; } = 512;

    public double Temperature { get; init; } = 0.8;

    public double TopP { get; init; } = 0.95;

    public int TopK { get; init; }

    public double MinP { get; init; } = 0.05;

    public double TypicalP { get; init; } = 1;

    public double Tfs { get; init; } = 1;

    public double TopA { get; init; }

    public double RepetitionPenalty { get; init; } = 1.05;

    public int RepetitionPenaltyRange { get; init; } = 1024;

    public double RepetitionPenaltySlope { get; init; } = 1;

    public double PresencePenalty { get; init; }

    public double FrequencyPenalty { get; init; }

    public DrySampler Dry { get; init; } = new();

    public XtcSampler Xtc { get; init; } = new();

    public DynamicTemperature? DynamicTemperature { get; init; }

    public MirostatSampler? Mirostat { get; init; }

    public double SmoothingFactor { get; init; }

    public double NSigma { get; init; }

    /// <summary>The order koboldcpp applies its samplers in, by its own numbers.</summary>
    public IReadOnlyList<int> SamplerOrder { get; init; } = [6, 0, 1, 3, 4, 2, 5];

    public SeedRule Seed { get; init; } = new();
}

/// <summary>The samplers a generation was made with, kept with its record: the core ones, and any other that was turned on.</summary>
public sealed record GenerationSamplers
{
    public int ReplyLength { get; init; }

    public double Temperature { get; init; }

    public double TopP { get; init; }

    public int TopK { get; init; }

    public double MinP { get; init; }

    public double RepetitionPenalty { get; init; }

    public long Seed { get; init; }

    public bool? Thinking { get; init; }

    public int? RepetitionPenaltyRange { get; init; }

    public double? RepetitionPenaltySlope { get; init; }

    public double? TypicalP { get; init; }

    public double? Tfs { get; init; }

    public double? TopA { get; init; }

    public double? PresencePenalty { get; init; }

    public double? FrequencyPenalty { get; init; }

    public double? SmoothingFactor { get; init; }

    public double? NSigma { get; init; }

    public DrySampler? Dry { get; init; }

    public XtcSampler? Xtc { get; init; }

    public DynamicTemperature? DynamicTemperature { get; init; }

    public MirostatSampler? Mirostat { get; init; }

    /// <summary>Snapshots the samplers <paramref name="samplers"/> actually puts to work under <paramref name="seed"/>.</summary>
    public static GenerationSamplers From(SamplerPreset samplers, long seed, bool? thinking = null)
    {
        ArgumentNullException.ThrowIfNull(samplers);

        var penalizesRepetition = samplers.RepetitionPenalty != 1;

        return new GenerationSamplers
        {
            ReplyLength = samplers.ReplyLength,
            Temperature = samplers.Temperature,
            TopP = samplers.TopP,
            TopK = samplers.TopK,
            MinP = samplers.MinP,
            RepetitionPenalty = samplers.RepetitionPenalty,
            Seed = seed,
            Thinking = thinking,
            RepetitionPenaltyRange = penalizesRepetition ? samplers.RepetitionPenaltyRange : null,
            RepetitionPenaltySlope = penalizesRepetition && samplers.RepetitionPenaltySlope != 1 ? samplers.RepetitionPenaltySlope : null,
            TypicalP = samplers.TypicalP != 1 ? samplers.TypicalP : null,
            Tfs = samplers.Tfs != 1 ? samplers.Tfs : null,
            TopA = samplers.TopA != 0 ? samplers.TopA : null,
            PresencePenalty = samplers.PresencePenalty != 0 ? samplers.PresencePenalty : null,
            FrequencyPenalty = samplers.FrequencyPenalty != 0 ? samplers.FrequencyPenalty : null,
            SmoothingFactor = samplers.SmoothingFactor != 0 ? samplers.SmoothingFactor : null,
            NSigma = samplers.NSigma != 0 ? samplers.NSigma : null,
            Dry = samplers.Dry.Multiplier != 0 ? samplers.Dry : null,
            Xtc = samplers.Xtc.Probability != 0 ? samplers.Xtc : null,
            DynamicTemperature = samplers.DynamicTemperature,
            Mirostat = samplers.Mirostat is { Mode: > 0 } mirostat ? mirostat : null
        };
    }
}

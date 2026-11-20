using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using NE.ChatAI.Samplers;

namespace NE.ChatAI.Backends.KoboldCpp;

/// <summary>The body of koboldcpp's generate call: the prompt, the samplers in koboldcpp's own names, and the key that aborts it.</summary>
public sealed record KoboldGenerateRequest
{
    public required string Prompt { get; init; }

    /// <summary>The longest reply, in tokens.</summary>
    [JsonPropertyName("max_length")]
    public int MaxLength { get; init; } = 512;

    /// <summary>The context the prompt is fitted into, in tokens.</summary>
    [JsonPropertyName("max_context_length")]
    public int MaxContextLength { get; init; } = 8192;

    public double Temperature { get; init; } = 0.8;

    [JsonPropertyName("top_p")]
    public double TopP { get; init; } = 0.95;

    [JsonPropertyName("top_k")]
    public int TopK { get; init; }

    [JsonPropertyName("min_p")]
    public double MinP { get; init; } = 0.05;

    [JsonPropertyName("typical")]
    public double Typical { get; init; } = 1;

    public double Tfs { get; init; } = 1;

    [JsonPropertyName("top_a")]
    public double TopA { get; init; }

    [JsonPropertyName("rep_pen")]
    public double RepetitionPenalty { get; init; } = 1.05;

    [JsonPropertyName("rep_pen_range")]
    public int RepetitionPenaltyRange { get; init; } = 1024;

    [JsonPropertyName("rep_pen_slope")]
    public double RepetitionPenaltySlope { get; init; } = 1;

    [JsonPropertyName("presence_penalty")]
    public double PresencePenalty { get; init; }

    [JsonPropertyName("frequency_penalty")]
    public double FrequencyPenalty { get; init; }

    [JsonPropertyName("dry_multiplier")]
    public double DryMultiplier { get; init; }

    [JsonPropertyName("dry_base")]
    public double DryBase { get; init; } = 1.75;

    [JsonPropertyName("dry_allowed_length")]
    public int DryAllowedLength { get; init; } = 2;

    [JsonPropertyName("dry_penalty_last_n")]
    public int DryPenaltyRange { get; init; }

    [JsonPropertyName("dry_sequence_breakers")]
    public IReadOnlyList<string> DrySequenceBreakers { get; init; } = [];

    [JsonPropertyName("xtc_threshold")]
    public double XtcThreshold { get; init; } = 0.1;

    [JsonPropertyName("xtc_probability")]
    public double XtcProbability { get; init; }

    /// <summary>How far the temperature may move either side of <see cref="Temperature"/>; 0 is a fixed temperature.</summary>
    [JsonPropertyName("dynatemp_range")]
    public double DynamicTemperatureRange { get; init; }

    [JsonPropertyName("dynatemp_exponent")]
    public double DynamicTemperatureExponent { get; init; } = 1;

    [JsonPropertyName("smoothing_factor")]
    public double SmoothingFactor { get; init; }

    public int Mirostat { get; init; }

    [JsonPropertyName("mirostat_tau")]
    public double MirostatTau { get; init; } = 5;

    [JsonPropertyName("mirostat_eta")]
    public double MirostatEta { get; init; } = 0.1;

    public double NSigma { get; init; }

    [JsonPropertyName("sampler_order")]
    public IReadOnlyList<int>? SamplerOrder { get; init; }

    /// <summary>The sampler's seed; the application always names one, so the reply's seed is known and kept.</summary>
    [JsonPropertyName("sampler_seed")]
    public long Seed { get; init; } = -1;

    [JsonPropertyName("stop_sequence")]
    public IReadOnlyList<string> StopSequences { get; init; } = [];

    /// <summary>What <see cref="KoboldCppClient.AbortAsync"/> names this generation by.</summary>
    [JsonPropertyName("genkey")]
    public string? GenKey { get; init; }

    /// <summary>The request for <paramref name="prompt"/> with a preset's samplers, in a context, under a seed.</summary>
    public static KoboldGenerateRequest From(string prompt, SamplerPreset samplers, int contextLength, long seed, IReadOnlyList<string> stopSequences, string genKey)
    {
        ArgumentNullException.ThrowIfNull(samplers);

        // koboldcpp moves the temperature around its middle; the preset says the range's ends.
        DynamicTemperature? dynamic = samplers.DynamicTemperature;
        var temperature = dynamic is null ? samplers.Temperature : (dynamic.Min + dynamic.Max) / 2;

        return new KoboldGenerateRequest
        {
            Prompt = prompt,
            MaxLength = samplers.ReplyLength,
            MaxContextLength = contextLength,
            Temperature = temperature,
            TopP = samplers.TopP,
            TopK = samplers.TopK,
            MinP = samplers.MinP,
            Typical = samplers.TypicalP,
            Tfs = samplers.Tfs,
            TopA = samplers.TopA,
            RepetitionPenalty = samplers.RepetitionPenalty,
            RepetitionPenaltyRange = samplers.RepetitionPenaltyRange,
            RepetitionPenaltySlope = samplers.RepetitionPenaltySlope,
            PresencePenalty = samplers.PresencePenalty,
            FrequencyPenalty = samplers.FrequencyPenalty,
            DryMultiplier = samplers.Dry.Multiplier,
            DryBase = samplers.Dry.Base,
            DryAllowedLength = samplers.Dry.AllowedLength,
            DryPenaltyRange = samplers.Dry.PenaltyRange,
            DrySequenceBreakers = samplers.Dry.SequenceBreakers,
            XtcThreshold = samplers.Xtc.Threshold,
            XtcProbability = samplers.Xtc.Probability,
            DynamicTemperatureRange = dynamic is null ? 0 : Math.Max(0, (dynamic.Max - dynamic.Min) / 2),
            DynamicTemperatureExponent = dynamic?.Exponent ?? 1,
            SmoothingFactor = samplers.SmoothingFactor,
            Mirostat = samplers.Mirostat?.Mode ?? 0,
            MirostatTau = samplers.Mirostat?.Tau ?? 5,
            MirostatEta = samplers.Mirostat?.Eta ?? 0.1,
            NSigma = samplers.NSigma,
            SamplerOrder = samplers.SamplerOrder.Count == 0 ? null : samplers.SamplerOrder,
            Seed = seed,
            StopSequences = stopSequences,
            GenKey = genKey
        };
    }
}

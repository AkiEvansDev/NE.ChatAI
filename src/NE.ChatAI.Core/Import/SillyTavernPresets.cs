using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using NE.ChatAI.Prompts;
using NE.ChatAI.Samplers;

namespace NE.ChatAI.Import;

/// <summary>What one SillyTavern file held, as this application's documents; a part the file did not hold is <c>null</c>.</summary>
public sealed record SillyTavernPresets
{
    /// <summary>From an instruct template, with the reasoning template's markers when the file held both.</summary>
    public ModelFormat? ModelFormat { get; init; }

    /// <summary>From a reasoning template alone, for a model format of the reader's choice.</summary>
    public ThoughtFormat? Thought { get; init; }

    /// <summary>From a context template.</summary>
    public PromptPreset? Prompt { get; init; }

    /// <summary>From a system prompt template, whether or not the file held a context template with it.</summary>
    public SystemPromptPreset? SystemPrompt { get; init; }

    /// <summary>From a text-completion preset.</summary>
    public SamplerPreset? Samplers { get; init; }

    public bool IsEmpty => ModelFormat is null && Thought is null && Prompt is null && SystemPrompt is null && Samplers is null;
}

/// <summary>Imports SillyTavern instruct, context, system-prompt, reasoning, sampler and master-export JSON files.</summary>
public static class SillyTavernImport
{
    private const string KeptKey = "sillyTavern";

    private static readonly string[] SystemPromptRead = ["name", "content"];

    private static readonly string[] InstructRead =
    [
        "name", "input_sequence", "input_suffix", "output_sequence", "output_suffix", "system_sequence", "system_suffix",
        "first_output_sequence", "last_output_sequence", "system_same_as_user", "wrap", "names_behavior", "names", "stop_sequence",
        "sequences_as_stop_strings"
    ];

    private static readonly string[] ContextRead = ["name", "story_string", "example_separator", "chat_start"];

    private static readonly string[] SamplersRead =
    [
        "name", "genamt", "temp", "top_p", "top_k", "min_p", "typical_p", "tfs", "top_a", "rep_pen", "rep_pen_range", "rep_pen_slope",
        "presence_pen", "freq_pen", "dry_multiplier", "dry_base", "dry_allowed_length", "dry_penalty_last_n", "dry_sequence_breakers",
        "xtc_threshold", "xtc_probability", "dynatemp", "min_temp", "max_temp", "dynatemp_exponent", "mirostat_mode", "mirostat_tau",
        "mirostat_eta", "smoothing_factor", "nsigma", "sampler_order"
    ];

    /// <summary>Reads a SillyTavern preset file; the parts it held, or <see cref="SillyTavernPresets.IsEmpty"/> when it held none.</summary>
    public static SillyTavernPresets ReadPresets(JsonElement root, string fallbackName)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return new SillyTavernPresets();

        JsonElement? instruct = Nested(root, "instruct") ?? (root.TryGetProperty("input_sequence", out _) ? root : null);
        JsonElement? context = Nested(root, "context") ?? (root.TryGetProperty("story_string", out _) ? root : null);
        JsonElement? systemPrompt = Nested(root, "sysprompt") ?? (IsSystemPrompt(root) ? root : null);
        JsonElement? reasoning = Nested(root, "reasoning") ?? (IsReasoning(root) ? root : null);
        JsonElement? samplers = Nested(root, "preset") ?? (IsSamplerPreset(root) ? root : null);

        ThoughtFormat? thought = reasoning is JsonElement reasoningElement ? ReadThought(reasoningElement) : null;
        SystemPromptPreset? system = systemPrompt is JsonElement systemElement ? ReadSystemPrompt(systemElement, fallbackName) : null;

        return new SillyTavernPresets
        {
            ModelFormat = instruct is JsonElement instructElement ? ReadModelFormat(instructElement, thought, fallbackName) : null,
            Thought = instruct is null ? thought : null,
            Prompt = context is JsonElement contextElement ? ReadPrompt(contextElement, fallbackName) : null,
            SystemPrompt = system,
            Samplers = samplers is JsonElement samplerElement ? ReadSamplers(samplerElement, fallbackName) : null
        };
    }

    private static JsonElement? Nested(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement nested) && nested.ValueKind == JsonValueKind.Object ? nested : null;

    private static bool IsSystemPrompt(JsonElement root)
        => root.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String && !root.TryGetProperty("story_string", out _);

    private static bool IsReasoning(JsonElement root)
        => root.TryGetProperty("prefix", out _) && root.TryGetProperty("suffix", out _) && !root.TryGetProperty("input_sequence", out _);

    private static bool IsSamplerPreset(JsonElement root)
        => root.TryGetProperty("temp", out _) || root.TryGetProperty("rep_pen", out _);

    private static ThoughtFormat ReadThought(JsonElement reasoning)
        => new() { Prefix = JsonValues.String(reasoning, "prefix") ?? string.Empty, Suffix = JsonValues.String(reasoning, "suffix") ?? string.Empty };

    private static SystemPromptPreset ReadSystemPrompt(JsonElement systemPrompt, string fallbackName)
        => new()
        {
            Name = JsonValues.String(systemPrompt, "name") ?? fallbackName,
            Content = (JsonValues.String(systemPrompt, "content") ?? string.Empty).TrimEnd('\r', '\n'),
            Extra = Kept(systemPrompt, SystemPromptRead)
        };

    private static Dictionary<string, JsonElement>? Kept(JsonElement element, string[] read)
    {
        Dictionary<string, JsonElement> kept = [with(StringComparer.Ordinal)];

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (Array.IndexOf(read, property.Name) < 0)
                kept[property.Name] = property.Value.Clone();
        }

        return kept.Count == 0 ? null : new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [KeptKey] = JsonSerializer.SerializeToElement(kept) };
    }

    private static ModelFormat ReadModelFormat(JsonElement instruct, ThoughtFormat? thought, string fallbackName)
    {
        var stop = JsonValues.String(instruct, "stop_sequence");

        return new ModelFormat
        {
            Name = JsonValues.String(instruct, "name") ?? fallbackName,
            UserPrefix = JsonValues.String(instruct, "input_sequence") ?? string.Empty,
            UserSuffix = JsonValues.String(instruct, "input_suffix") ?? string.Empty,
            ModelPrefix = JsonValues.String(instruct, "output_sequence") ?? string.Empty,
            ModelSuffix = JsonValues.String(instruct, "output_suffix") ?? string.Empty,
            SystemPrefix = JsonValues.String(instruct, "system_sequence") ?? string.Empty,
            SystemSuffix = JsonValues.String(instruct, "system_suffix") ?? string.Empty,
            FirstModelPrefix = JsonValues.NullIfEmpty(JsonValues.String(instruct, "first_output_sequence")),
            LastModelPrefix = JsonValues.NullIfEmpty(JsonValues.String(instruct, "last_output_sequence")),
            SystemSameAsUser = Bool(instruct, "system_same_as_user") ?? false,
            Wrap = Bool(instruct, "wrap") ?? false,
            NamesBehavior = Names(instruct),
            StopSequences = string.IsNullOrEmpty(stop) ? [] : [stop],
            SequencesAsStop = Bool(instruct, "sequences_as_stop_strings") ?? true,
            Thought = thought ?? new ThoughtFormat(),
            Extra = Kept(instruct, InstructRead)
        };
    }

    private static bool? Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static NamesBehavior Names(JsonElement instruct)
        => JsonValues.String(instruct, "names_behavior") switch
        {
            "force" => NamesBehavior.Force,
            "always" => NamesBehavior.Always,
            "none" => NamesBehavior.None,
            _ => Bool(instruct, "names") == true ? NamesBehavior.Always : NamesBehavior.None
        };

    private static PromptPreset ReadPrompt(JsonElement context, string fallbackName)
        => new()
        {
            Name = JsonValues.String(context, "name") ?? fallbackName,
            StoryString = JsonValues.String(context, "story_string") ?? PromptPreset.DefaultStoryString,
            ExampleSeparator = JsonValues.String(context, "example_separator") ?? string.Empty,
            ChatStart = JsonValues.String(context, "chat_start") ?? string.Empty,
            Extra = Kept(context, ContextRead)
        };

    private static SamplerPreset ReadSamplers(JsonElement preset, string fallbackName)
    {
        SamplerPreset defaults = new();
        var mirostat = (int)(Number(preset, "mirostat_mode") ?? 0);

        return new SamplerPreset
        {
            Name = JsonValues.String(preset, "name") ?? fallbackName,
            ReplyLength = (int)(Number(preset, "genamt") ?? defaults.ReplyLength),
            Temperature = Number(preset, "temp") ?? defaults.Temperature,
            TopP = Number(preset, "top_p") ?? defaults.TopP,
            TopK = (int)(Number(preset, "top_k") ?? defaults.TopK),
            MinP = Number(preset, "min_p") ?? defaults.MinP,
            TypicalP = Number(preset, "typical_p") ?? defaults.TypicalP,
            Tfs = Number(preset, "tfs") ?? defaults.Tfs,
            TopA = Number(preset, "top_a") ?? defaults.TopA,
            RepetitionPenalty = Number(preset, "rep_pen") ?? defaults.RepetitionPenalty,
            RepetitionPenaltyRange = (int)(Number(preset, "rep_pen_range") ?? defaults.RepetitionPenaltyRange),
            RepetitionPenaltySlope = Number(preset, "rep_pen_slope") ?? defaults.RepetitionPenaltySlope,
            PresencePenalty = Number(preset, "presence_pen") ?? defaults.PresencePenalty,
            FrequencyPenalty = Number(preset, "freq_pen") ?? defaults.FrequencyPenalty,
            Dry = new DrySampler
            {
                Multiplier = Number(preset, "dry_multiplier") ?? defaults.Dry.Multiplier,
                Base = Number(preset, "dry_base") ?? defaults.Dry.Base,
                AllowedLength = (int)(Number(preset, "dry_allowed_length") ?? defaults.Dry.AllowedLength),
                PenaltyRange = (int)(Number(preset, "dry_penalty_last_n") ?? defaults.Dry.PenaltyRange),
                SequenceBreakers = SequenceBreakers(preset) ?? defaults.Dry.SequenceBreakers
            },
            Xtc = new XtcSampler
            {
                Threshold = Number(preset, "xtc_threshold") ?? defaults.Xtc.Threshold,
                Probability = Number(preset, "xtc_probability") ?? defaults.Xtc.Probability
            },
            DynamicTemperature = Bool(preset, "dynatemp") == true
                ? new DynamicTemperature { Min = Number(preset, "min_temp") ?? 0, Max = Number(preset, "max_temp") ?? 2, Exponent = Number(preset, "dynatemp_exponent") ?? 1 }
                : null,
            Mirostat = mirostat > 0 ? new MirostatSampler { Mode = mirostat, Tau = Number(preset, "mirostat_tau") ?? 5, Eta = Number(preset, "mirostat_eta") ?? 0.1 } : null,
            SmoothingFactor = Number(preset, "smoothing_factor") ?? defaults.SmoothingFactor,
            NSigma = Number(preset, "nsigma") ?? defaults.NSigma,
            SamplerOrder = Integers(preset, "sampler_order") ?? defaults.SamplerOrder,
            Extra = Kept(preset, SamplersRead)
        };
    }

    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
            return null;

        if (value.ValueKind == JsonValueKind.Number)
            return value.GetDouble();

        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static string[]? SequenceBreakers(JsonElement preset)
    {
        if (!preset.TryGetProperty("dry_sequence_breakers", out JsonElement breakers))
            return null;

        if (breakers.ValueKind == JsonValueKind.Array)
            return [.. breakers.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String).Select(static item => item.GetString()!)];

        // SillyTavern serializes DRY sequence breakers as a JSON array inside a JSON string.
        if (breakers.ValueKind != JsonValueKind.String)
            return null;

        try
        {
            return JsonSerializer.Deserialize<string[]>(breakers.GetString()!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int[]? Integers(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.Number).Select(static item => item.GetInt32())]
            : null;
}

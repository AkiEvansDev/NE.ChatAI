using System;
using System.Linq;
using NE.ChatAI.Connections;
using NE.ChatAI.Prompts;
using NE.ChatAI.Samplers;

namespace NE.ChatAI.Controllers;

/// <summary>A connection as its editor shows it.</summary>
internal sealed partial class ConnectionDraft : RecursiveObservable, IDocumentDraft<ConnectionProfile>
{
    [RecursiveMember]
    public partial string Name { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string Backend { get; set; } = nameof(BackendKind.KoboldCpp);

    [RecursiveMember]
    public partial string Address { get; set; } = string.Empty;

    [RecursiveMember]
    public partial bool ContextAuto { get; set; } = true;

    [RecursiveMember]
    public partial decimal ContextValue { get; set; } = 8192;

    [RecursiveMember]
    public partial string? Instruct { get; set; }

    [RecursiveMember]
    public partial string? SystemPrompt { get; set; }

    [RecursiveMember]
    public partial string? Prompt { get; set; }

    [RecursiveMember]
    public partial string? Samplers { get; set; }

    public void Show(ConnectionProfile connection)
    {
        Name = connection.Name;
        Backend = connection.Backend.ToString();
        Address = connection.Address?.ToString() ?? string.Empty;
        ContextAuto = connection.ContextLength.Auto;
        ContextValue = connection.ContextLength.Value;
        Instruct = connection.Instruct;
        SystemPrompt = connection.SystemPrompt;
        Prompt = connection.Prompt;
        Samplers = connection.Samplers;
    }

    /// <summary>The draft over <paramref name="kept"/>: what the editor does not show — fields it does not know — stays as it was.</summary>
    public ConnectionProfile Apply(ConnectionProfile kept)
        => kept with
        {
            Name = Name.Trim(),
            Backend = Enum.TryParse(Backend, out BackendKind backend) ? backend : kept.Backend,
            Address = Uri.TryCreate(Address.Trim(), UriKind.Absolute, out Uri? address) ? address : kept.Address,
            ContextLength = new ContextLength { Auto = ContextAuto, Value = (int)ContextValue },
            Instruct = Instruct,
            SystemPrompt = SystemPrompt,
            Prompt = Prompt,
            Samplers = Samplers
        };
}

/// <summary>A model format as its editor shows it; the stop sequences one to a line.</summary>
internal sealed partial class FormatDraft : RecursiveObservable, IDocumentDraft<ModelFormat>
{
    [RecursiveMember]
    public partial string Name { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string SystemPrefix { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string SystemSuffix { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string UserPrefix { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string UserSuffix { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string ModelPrefix { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string ModelSuffix { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string FirstModelPrefix { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string LastModelPrefix { get; set; } = string.Empty;

    [RecursiveMember]
    public partial bool SystemSameAsUser { get; set; }

    [RecursiveMember]
    public partial bool Wrap { get; set; }

    [RecursiveMember]
    public partial string NamesBehavior { get; set; } = nameof(Prompts.NamesBehavior.None);

    [RecursiveMember]
    public partial string StopSequences { get; set; } = string.Empty;

    [RecursiveMember]
    public partial bool SequencesAsStop { get; set; } = true;

    [RecursiveMember]
    public partial bool ThoughtEnabled { get; set; } = true;

    [RecursiveMember]
    public partial string ThoughtPrefix { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string ThoughtSuffix { get; set; } = string.Empty;

    [RecursiveMember]
    public partial bool ThoughtOpensReply { get; set; }

    [RecursiveMember]
    public partial string ThoughtSystemMark { get; set; } = string.Empty;

    public void Show(ModelFormat format)
    {
        Name = format.Name;
        SystemPrefix = format.SystemPrefix;
        SystemSuffix = format.SystemSuffix;
        UserPrefix = format.UserPrefix;
        UserSuffix = format.UserSuffix;
        ModelPrefix = format.ModelPrefix;
        ModelSuffix = format.ModelSuffix;
        FirstModelPrefix = format.FirstModelPrefix ?? string.Empty;
        LastModelPrefix = format.LastModelPrefix ?? string.Empty;
        SystemSameAsUser = format.SystemSameAsUser;
        Wrap = format.Wrap;
        NamesBehavior = format.NamesBehavior.ToString();
        StopSequences = string.Join('\n', format.StopSequences);
        SequencesAsStop = format.SequencesAsStop;
        ThoughtEnabled = format.Thought.Enabled;
        ThoughtPrefix = format.Thought.Prefix;
        ThoughtSuffix = format.Thought.Suffix;
        ThoughtOpensReply = format.Thought.OpensReply;
        ThoughtSystemMark = format.Thought.SystemMark;
    }

    public ModelFormat Apply(ModelFormat kept)
        => kept with
        {
            Name = Name.Trim(),
            SystemPrefix = SystemPrefix,
            SystemSuffix = SystemSuffix,
            UserPrefix = UserPrefix,
            UserSuffix = UserSuffix,
            ModelPrefix = ModelPrefix,
            ModelSuffix = ModelSuffix,
            FirstModelPrefix = FirstModelPrefix.Length == 0 ? null : FirstModelPrefix,
            LastModelPrefix = LastModelPrefix.Length == 0 ? null : LastModelPrefix,
            SystemSameAsUser = SystemSameAsUser,
            Wrap = Wrap,
            NamesBehavior = Enum.TryParse(NamesBehavior, out NamesBehavior names) ? names : kept.NamesBehavior,
            // One to a line, so a sequence cannot hold a line break of its own: the turn markers are the ones that do.
            StopSequences = [.. StopSequences.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0)],
            SequencesAsStop = SequencesAsStop,
            Thought = new ThoughtFormat { Enabled = ThoughtEnabled, Prefix = ThoughtPrefix, Suffix = ThoughtSuffix, OpensReply = ThoughtOpensReply, SystemMark = ThoughtSystemMark }
        };
}

/// <summary>A system prompt as its editor shows it.</summary>
internal sealed partial class SystemPromptDraft : RecursiveObservable, IDocumentDraft<SystemPromptPreset>
{
    [RecursiveMember]
    public partial string Name { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string Content { get; set; } = string.Empty;

    public void Show(SystemPromptPreset system)
    {
        Name = system.Name;
        Content = system.Content.TrimEnd('\r', '\n');
    }

    public SystemPromptPreset Apply(SystemPromptPreset kept)
        => kept with { Name = Name.Trim(), Content = Content.TrimEnd('\r', '\n') };
}

/// <summary>A prompt preset as its editor shows it.</summary>
internal sealed partial class PromptDraft : RecursiveObservable, IDocumentDraft<PromptPreset>
{
    [RecursiveMember]
    public partial string Name { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string StoryString { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string ExampleSeparator { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string ChatStart { get; set; } = string.Empty;

    [RecursiveMember]
    public partial bool IncludeNames { get; set; }

    [RecursiveMember]
    public partial string ThoughtShow { get; set; } = nameof(ThoughtDisplay.Folded);

    [RecursiveMember]
    public partial bool KeepThoughts { get; set; }

    public void Show(PromptPreset prompt)
    {
        Name = prompt.Name;
        StoryString = prompt.StoryString;
        ExampleSeparator = prompt.ExampleSeparator;
        ChatStart = prompt.ChatStart;
        IncludeNames = prompt.IncludeNames;
        ThoughtShow = prompt.Thoughts.Show.ToString();
        KeepThoughts = prompt.Thoughts.KeepInHistory;
    }

    public PromptPreset Apply(PromptPreset kept)
        => kept with
        {
            Name = Name.Trim(),
            StoryString = StoryString,
            ExampleSeparator = ExampleSeparator,
            ChatStart = ChatStart,
            IncludeNames = IncludeNames,
            Thoughts = new ThoughtHandling
            {
                Show = Enum.TryParse(ThoughtShow, out ThoughtDisplay show) ? show : kept.Thoughts.Show,
                KeepInHistory = KeepThoughts
            }
        };
}

/// <summary>A sampler preset as its editor shows it: every sampler, where the panel shows the few changed while chatting.</summary>
internal sealed partial class SamplerDraft : RecursiveObservable, IDocumentDraft<SamplerPreset>
{
    [RecursiveMember]
    public partial string Name { get; set; } = string.Empty;

    [RecursiveMember]
    public partial decimal ReplyLength { get; set; }

    [RecursiveMember]
    public partial decimal Temperature { get; set; }

    [RecursiveMember]
    public partial decimal TopP { get; set; }

    [RecursiveMember]
    public partial decimal TopK { get; set; }

    [RecursiveMember]
    public partial decimal MinP { get; set; }

    [RecursiveMember]
    public partial decimal TypicalP { get; set; }

    [RecursiveMember]
    public partial decimal Tfs { get; set; }

    [RecursiveMember]
    public partial decimal TopA { get; set; }

    [RecursiveMember]
    public partial decimal RepetitionPenalty { get; set; }

    [RecursiveMember]
    public partial decimal RepetitionPenaltyRange { get; set; }

    [RecursiveMember]
    public partial decimal RepetitionPenaltySlope { get; set; }

    [RecursiveMember]
    public partial decimal PresencePenalty { get; set; }

    [RecursiveMember]
    public partial decimal FrequencyPenalty { get; set; }

    [RecursiveMember]
    public partial decimal DryMultiplier { get; set; }

    [RecursiveMember]
    public partial decimal DryBase { get; set; }

    [RecursiveMember]
    public partial decimal DryAllowedLength { get; set; }

    [RecursiveMember]
    public partial decimal XtcThreshold { get; set; }

    [RecursiveMember]
    public partial decimal XtcProbability { get; set; }

    [RecursiveMember]
    public partial bool DynamicTemperature { get; set; }

    [RecursiveMember]
    public partial decimal DynamicMin { get; set; }

    [RecursiveMember]
    public partial decimal DynamicMax { get; set; }

    [RecursiveMember]
    public partial decimal SmoothingFactor { get; set; }

    [RecursiveMember]
    public partial decimal NSigma { get; set; }

    [RecursiveMember]
    public partial string SeedMode { get; set; } = nameof(Samplers.SeedMode.PerChat);

    [RecursiveMember]
    public partial decimal SeedValue { get; set; }

    public void Show(SamplerPreset samplers)
    {
        Name = samplers.Name;
        ReplyLength = samplers.ReplyLength;
        Temperature = (decimal)samplers.Temperature;
        TopP = (decimal)samplers.TopP;
        TopK = samplers.TopK;
        MinP = (decimal)samplers.MinP;
        TypicalP = (decimal)samplers.TypicalP;
        Tfs = (decimal)samplers.Tfs;
        TopA = (decimal)samplers.TopA;
        RepetitionPenalty = (decimal)samplers.RepetitionPenalty;
        RepetitionPenaltyRange = samplers.RepetitionPenaltyRange;
        RepetitionPenaltySlope = (decimal)samplers.RepetitionPenaltySlope;
        PresencePenalty = (decimal)samplers.PresencePenalty;
        FrequencyPenalty = (decimal)samplers.FrequencyPenalty;
        DryMultiplier = (decimal)samplers.Dry.Multiplier;
        DryBase = (decimal)samplers.Dry.Base;
        DryAllowedLength = samplers.Dry.AllowedLength;
        XtcThreshold = (decimal)samplers.Xtc.Threshold;
        XtcProbability = (decimal)samplers.Xtc.Probability;
        DynamicTemperature = samplers.DynamicTemperature is not null;
        DynamicMin = (decimal)(samplers.DynamicTemperature?.Min ?? 0.5);
        DynamicMax = (decimal)(samplers.DynamicTemperature?.Max ?? 1.5);
        SmoothingFactor = (decimal)samplers.SmoothingFactor;
        NSigma = (decimal)samplers.NSigma;
        SeedMode = samplers.Seed.Mode.ToString();
        SeedValue = samplers.Seed.Value < 0 ? 0 : samplers.Seed.Value;
    }

    public SamplerPreset Apply(SamplerPreset kept)
        => kept with
        {
            Name = Name.Trim(),
            ReplyLength = (int)ReplyLength,
            Temperature = (double)Temperature,
            TopP = (double)TopP,
            TopK = (int)TopK,
            MinP = (double)MinP,
            TypicalP = (double)TypicalP,
            Tfs = (double)Tfs,
            TopA = (double)TopA,
            RepetitionPenalty = (double)RepetitionPenalty,
            RepetitionPenaltyRange = (int)RepetitionPenaltyRange,
            RepetitionPenaltySlope = (double)RepetitionPenaltySlope,
            PresencePenalty = (double)PresencePenalty,
            FrequencyPenalty = (double)FrequencyPenalty,
            Dry = kept.Dry with { Multiplier = (double)DryMultiplier, Base = (double)DryBase, AllowedLength = (int)DryAllowedLength },
            Xtc = new XtcSampler { Threshold = (double)XtcThreshold, Probability = (double)XtcProbability },
            DynamicTemperature = DynamicTemperature ? (kept.DynamicTemperature ?? new DynamicTemperature()) with { Min = (double)DynamicMin, Max = (double)DynamicMax } : null,
            SmoothingFactor = (double)SmoothingFactor,
            NSigma = (double)NSigma,
            Seed = new SeedRule { Mode = Enum.TryParse(SeedMode, out SeedMode mode) ? mode : kept.Seed.Mode, Value = (long)SeedValue }
        };
}

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NE.ChatAI.Connections;
using NE.ChatAI.Hosting;
using NE.ChatAI.Prompts;
using NE.ChatAI.Samplers;
using NE.ChatAI.Settings;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Controllers;

/// <summary>
/// The generation panel: the active connection and the presets it names, and what of them is changed while chatting — the
/// samplers, thinking, the context. Every change is written to its file as it lands, so the next reply is made with it.
/// </summary>
internal abstract partial class ShellController
{
    public const string CustomOptionGroup = "custom";

    /// <summary>How long the panel waits for the server to say which model it runs; a server away is said, not waited for.</summary>
    private static readonly TimeSpan ModelInfoTimeout = TimeSpan.FromSeconds(3);

    private ActiveProfile? _profile;

    [RecursiveMember(false)]
    public RecursiveCollection<OptionItem> ConnectionOptions { get; } = [];

    [RecursiveMember(false)]
    public RecursiveCollection<OptionItem> FormatOptions { get; } = [];

    [RecursiveMember(false)]
    public RecursiveCollection<OptionItem> SystemPromptOptions { get; } = [];

    [RecursiveMember(false)]
    public RecursiveCollection<OptionItem> PromptOptions { get; } = [];

    [RecursiveMember(false)]
    public RecursiveCollection<OptionItem> SamplerOptions { get; } = [];

    [RecursiveMember]
    public partial string? ConnectionId { get; set; }

    [RecursiveMember]
    public partial string? FormatId { get; set; }

    [RecursiveMember]
    public partial string? SystemPromptId { get; set; }

    [RecursiveMember]
    public partial string? PromptId { get; set; }

    [RecursiveMember]
    public partial string? SamplersId { get; set; }

    /// <summary>What the server runs and the context it was started with, or that it does not answer.</summary>
    [RecursiveMember]
    public partial UIPhrase? ModelLine { get; set; }

    [RecursiveMember]
    public partial bool Thinking { get; set; }

    /// <summary>Whether the format marks thoughts at all; a format that does not has nothing to turn on.</summary>
    [RecursiveMember]
    public partial bool ThinkingAvailable { get; set; }

    [RecursiveMember]
    public partial bool IncludeNames { get; set; }

    [RecursiveMember]
    public partial bool ContextAuto { get; set; } = true;

    [RecursiveMember]
    public partial decimal ContextValue { get; set; } = 8192;

    [RecursiveMember]
    public partial decimal Temperature { get; set; }

    [RecursiveMember]
    public partial decimal MinP { get; set; }

    [RecursiveMember]
    public partial decimal TopP { get; set; }

    [RecursiveMember]
    public partial decimal RepetitionPenalty { get; set; }

    [RecursiveMember]
    public partial decimal ReplyLength { get; set; }

    [RecursiveMember]
    public partial string SeedMode { get; set; } = nameof(Samplers.SeedMode.PerChat);

    /// <summary>The preset's fixed seed, which Fixed generates with.</summary>
    [RecursiveMember]
    public partial decimal SeedValue { get; set; }

    protected DataFolder Data => Context.Services.GetRequiredService<DataFolder>();

    /// <summary>Reads the settings, the active connection and every preset into the panel, then asks the server about its model.</summary>
    protected async Task LoadGenerationAsync(CancellationToken cancellationToken)
    {
        await LoadPresetsAsync(cancellationToken).ConfigureAwait(false);

        if (_profile?.Connection.Address is Uri address)
            Context.Runtime.Start(lifetime => DescribeModelAsync(address, lifetime));
    }

    /// <summary>The panel's part that needs no server: the settings, the active connection and every preset.</summary>
    protected async Task LoadPresetsAsync(CancellationToken cancellationToken)
    {
        Fill(ConnectionOptions, await Data.Connections.ListAsync(cancellationToken).ConfigureAwait(false));
        Fill(FormatOptions, await Data.ModelFormats.ListAsync(cancellationToken).ConfigureAwait(false));
        Fill(SystemPromptOptions, await Data.SystemPrompts.ListAsync(cancellationToken).ConfigureAwait(false));
        Fill(PromptOptions, await Data.Prompts.ListAsync(cancellationToken).ConfigureAwait(false));
        Fill(SamplerOptions, await Data.Samplers.ListAsync(cancellationToken).ConfigureAwait(false));

        _profile = await Profiles.LoadActiveAsync(Data, cancellationToken).ConfigureAwait(false);
        Show(_profile);
        OnProfileLoaded(_profile);
    }

    /// <summary>The active profile read afresh — at the start, or after the panel changed it; the page follows what it shows by it.</summary>
    protected virtual void OnProfileLoaded(ActiveProfile? profile)
    {
    }

    protected static void Fill<T>(RecursiveCollection<OptionItem> options, DocumentListing<T> listing)
        where T : NamedDocument
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(listing);

        List<(string Id, string Title, string? Group)> ordered = [with(listing.Items.Count)];
        HashSet<string> kept = [with(StringComparer.Ordinal)];
        var hasBuiltIn = false;

        foreach (StoredDocument<T> stored in listing.Items)
        {
            if (!DefaultDocuments.IsBuiltIn(stored.Id) || !kept.Add(stored.Id))
                continue;

            hasBuiltIn = true;
            ordered.Add((stored.Id, stored.Document.Name.Length > 0 ? stored.Document.Name : stored.Id, null));
        }

        var customGroup = hasBuiltIn ? CustomOptionGroup : null;

        foreach (StoredDocument<T> stored in listing.Items)
        {
            if (DefaultDocuments.IsBuiltIn(stored.Id) || !kept.Add(stored.Id))
                continue;

            ordered.Add((stored.Id, stored.Document.Name.Length > 0 ? stored.Document.Name : stored.Id, customGroup));
        }

        for (var i = options.Count - 1; i >= 0; i--)
        {
            if (!kept.Contains(options[i].Id))
                _ = options.Remove(options[i].Id);
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            (var id, var title, var group) = ordered[i];

            if (options.TryGet(id, out OptionItem? existing))
            {
                existing.Title = title;
                existing.Group = group;

                if (!string.Equals(options[i].Id, id, StringComparison.Ordinal))
                    _ = options.Move(id, i);
            }
            else
            {
                options.Insert(i, new OptionItem { Id = id, Title = title, Group = group, IsContent = true });
            }
        }
    }

    private void Show(ActiveProfile? profile)
    {
        ConnectionId = profile?.ConnectionId;
        FormatId = profile?.Connection.Instruct;
        SystemPromptId = profile?.Connection.SystemPrompt;
        PromptId = profile?.Connection.Prompt;
        SamplersId = profile?.Connection.Samplers;
        ModelLine = profile is null ? new UIPhrase("chatai.connection.none") : null;

        if (profile is null)
            return;

        Thinking = profile.Format.Thought.Enabled;
        ThinkingAvailable = profile.Format.Thought.IsDefined;
        IncludeNames = profile.Prompt.IncludeNames;
        ContextAuto = profile.Connection.ContextLength.Auto;
        ContextValue = profile.Connection.ContextLength.Value;

        SamplerPreset samplers = profile.Samplers;

        Temperature = (decimal)samplers.Temperature;
        MinP = (decimal)samplers.MinP;
        TopP = (decimal)samplers.TopP;
        RepetitionPenalty = (decimal)samplers.RepetitionPenalty;
        ReplyLength = samplers.ReplyLength;
        SeedMode = samplers.Seed.Mode.ToString();
        SeedValue = Math.Max(0, samplers.Seed.Value);
    }

    /// <summary>A job of the runtime's, off the page's turn: a server that is slow or away must not hold the panel.</summary>
    private async Task DescribeModelAsync(Uri address, CancellationToken lifetime)
    {
        (UIPhrase line, _, _) = await QueryModelAsync(address, lifetime).ConfigureAwait(false);

        await Context.Runtime.InvokeAsync(() => ModelLine = line, lifetime).ConfigureAwait(false);
    }

    private async Task<(UIPhrase Line, UIPhrase Toast, UIColorStyle Style)> QueryModelAsync(Uri address, CancellationToken cancellationToken)
    {
        try
        {
            KoboldCppClients clients = Context.Services.GetRequiredService<KoboldCppClients>();
            // Wait for any active reply or scenario call on the same HTTP/1.0 server before starting the probe clock.
            using IDisposable serverSlot = await clients.AcquireSlotAsync(address, cancellationToken).ConfigureAwait(false);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ModelInfoTimeout);
            Backends.KoboldCpp.KoboldCppClient kobold = clients.For(address);
            var rawModel = await kobold.GetModelAsync(timeout.Token).ConfigureAwait(false);
            clients.CacheFor(address).UseModel(rawModel);
            var model = ShortModelName(rawModel);
            var context = await kobold.GetContextLengthAsync(timeout.Token).ConfigureAwait(false);

            return (
                UIPhrase.Of("chatai.model.line", ("model", model), ("context", context)),
                UIPhrase.Of("chatai.connection.connected", ("model", model), ("context", context)),
                UIColorStyle.Success
            );
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            UIPhrase unreachable = new("chatai.backend.unreachable");

            return (unreachable, unreachable, UIColorStyle.Danger);
        }
    }

    /// <summary>koboldcpp names the model <c>koboldcpp/&lt;file&gt;</c>; the file is the name.</summary>
    protected static string ShortModelName(string model)
        => model.StartsWith("koboldcpp/", StringComparison.Ordinal) ? model["koboldcpp/".Length..] : model;

    /// <summary>Makes another connection the active one; its presets come with it.</summary>
    [UICommand]
    public async Task SelectConnectionAsync(CancellationToken cancellationToken)
    {
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

        await Data.WriteSettingsAsync(settings with { Connection = ConnectionId }, cancellationToken).ConfigureAwait(false);
        await LoadGenerationAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks the active connection's server for its model and context now, updating the panel's line and showing a notification.</summary>
    [UICommand]
    public Task<UICommandResult> CheckConnectionAsync(CancellationToken cancellationToken)
        => _profile?.Connection.Address is Uri address
            ? ProbeConnectionAsync(address, updateModelLine: true, cancellationToken)
            : Task.FromResult(Notify("chatai.connection.none", UIColorStyle.Warning));

    protected async Task<UICommandResult> ProbeConnectionAsync(Uri address, bool updateModelLine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        (UIPhrase line, UIPhrase toast, UIColorStyle style) = await QueryModelAsync(address, cancellationToken).ConfigureAwait(false);

        if (updateModelLine)
            ModelLine = line;

        return UICommandResult.Ok([new ShowNotificationEffect(toast, style)]);
    }

    /// <summary>Points the active connection at the model format, system prompt, prompt and sampler presets chosen.</summary>
    [UICommand]
    public async Task SelectPresetsAsync(CancellationToken cancellationToken)
    {
        if (_profile is null)
            return;

        await Data.Connections.WriteAsync(_profile.ConnectionId, _profile.Connection with { Instruct = FormatId, SystemPrompt = SystemPromptId, Prompt = PromptId, Samplers = SamplersId }, cancellationToken).ConfigureAwait(false);
        await LoadGenerationAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the panel's samplers into the active sampler preset.</summary>
    [UICommand]
    public async Task SaveSamplersAsync(CancellationToken cancellationToken)
    {
        if (_profile?.Connection.Samplers is not string id)
            return;

        SamplerPreset samplers = _profile.Samplers with
        {
            Temperature = (double)Temperature,
            MinP = (double)MinP,
            TopP = (double)TopP,
            RepetitionPenalty = (double)RepetitionPenalty,
            ReplyLength = (int)ReplyLength,
            Seed = _profile.Samplers.Seed with { Mode = Enum.TryParse(SeedMode, out SeedMode mode) ? mode : _profile.Samplers.Seed.Mode, Value = (long)SeedValue }
        };

        await Data.Samplers.WriteAsync(id, samplers, cancellationToken).ConfigureAwait(false);
        _profile = _profile with { Samplers = samplers };
    }

    /// <summary>Turns the active model format's thinking on or off.</summary>
    [UICommand]
    public async Task SaveThinkingAsync(CancellationToken cancellationToken)
    {
        if (_profile?.Connection.Instruct is not string id)
            return;

        ModelFormat format = _profile.Format with { Thought = _profile.Format.Thought with { Enabled = Thinking } };

        await Data.ModelFormats.WriteAsync(id, format, cancellationToken).ConfigureAwait(false);
        _profile = _profile with { Format = format };
    }

    /// <summary>Turns prepending speaker names on messages in the active prompt preset on or off.</summary>
    [UICommand]
    public async Task SaveIncludeNamesAsync(CancellationToken cancellationToken)
    {
        if (_profile?.Connection.Prompt is not string id)
            return;

        PromptPreset prompt = _profile.Prompt with { IncludeNames = IncludeNames };

        await Data.Prompts.WriteAsync(id, prompt, cancellationToken).ConfigureAwait(false);
        _profile = _profile with { Prompt = prompt };
    }

    /// <summary>Writes the active connection's context: the server's own, or the number given.</summary>
    [UICommand]
    public async Task SaveContextAsync(CancellationToken cancellationToken)
    {
        if (_profile is null)
            return;

        ConnectionProfile connection = _profile.Connection with { ContextLength = new ContextLength { Auto = ContextAuto, Value = (int)ContextValue } };

        await Data.Connections.WriteAsync(_profile.ConnectionId, connection, cancellationToken).ConfigureAwait(false);
        _profile = _profile with { Connection = connection };
    }

    /// <summary>The seed rules in the order the panel offers them.</summary>
    public static IReadOnlyList<OptionItem> SeedModes { get; } =
    [
        new OptionItem { Id = nameof(Samplers.SeedMode.PerChat), Title = "chatai.seed.per-chat" },
        new OptionItem { Id = nameof(Samplers.SeedMode.Random), Title = "chatai.seed.random" },
        new OptionItem { Id = nameof(Samplers.SeedMode.Fixed), Title = "chatai.seed.fixed" }
    ];
}

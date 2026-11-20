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

/// <summary>Generation side panel: active connection, presets, samplers, thinking toggle, and context length.</summary>
internal abstract partial class ShellController
{
    public const string CustomOptionGroup = "custom";

    private static readonly TimeSpan ModelInfoTimeout = TimeSpan.FromSeconds(4);

    public static IReadOnlyList<OptionItem> SeedModes { get; } =
    [
        new OptionItem { Id = nameof(Samplers.SeedMode.PerChat), Title = "chatai.seed.per-chat" },
        new OptionItem { Id = nameof(Samplers.SeedMode.Random), Title = "chatai.seed.random" },
        new OptionItem { Id = nameof(Samplers.SeedMode.Fixed), Title = "chatai.seed.fixed" }
    ];

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

    [RecursiveMember]
    public partial UIPhrase? ModelLine { get; set; }

    [RecursiveMember]
    public partial bool Thinking { get; set; }

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

    [RecursiveMember]
    public partial decimal SeedValue { get; set; }

    protected DataFolder Data => Context.Services.GetRequiredService<DataFolder>();

    protected async Task LoadGenerationAsync(CancellationToken cancellationToken)
    {
        await LoadPresetsAsync(runtimeActive: true, cancellationToken).ConfigureAwait(false);

        if (_profile?.Connection.Address is Uri address)
            Context.Runtime.Start(lifetime => DescribeModelAsync(address, lifetime));
    }

    protected async Task LoadPresetsAsync(bool runtimeActive, CancellationToken cancellationToken)
    {
        Fill(ConnectionOptions, await Data.Connections.ListAsync(cancellationToken).ConfigureAwait(false));
        Fill(FormatOptions, await Data.ModelFormats.ListAsync(cancellationToken).ConfigureAwait(false));
        Fill(SystemPromptOptions, await Data.SystemPrompts.ListAsync(cancellationToken).ConfigureAwait(false));
        Fill(PromptOptions, await Data.Prompts.ListAsync(cancellationToken).ConfigureAwait(false));
        Fill(SamplerOptions, await Data.Samplers.ListAsync(cancellationToken).ConfigureAwait(false));

        _profile = await Profiles.LoadActiveAsync(Data, cancellationToken).ConfigureAwait(false);
        Show(_profile);
        await OnProfileLoadedAsync(_profile, runtimeActive, cancellationToken).ConfigureAwait(false);
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

    protected virtual Task OnProfileLoadedAsync(ActiveProfile? profile, bool runtimeActive, CancellationToken cancellationToken)
        => Task.CompletedTask;

    private async Task DescribeModelAsync(Uri address, CancellationToken lifetime)
    {
        (UIPhrase line, _, _) = await QueryModelAsync(address, lifetime).ConfigureAwait(false);

        await Context.Runtime.InvokeAsync(() =>
        {
            if (_profile?.Connection.Address == address)
                ModelLine = line;
        }, lifetime).ConfigureAwait(false);
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

    protected static string ShortModelName(string model)
        => model.StartsWith("koboldcpp/", StringComparison.Ordinal) ? model["koboldcpp/".Length..] : model;

    [UICommand]
    public async Task SelectConnectionAsync(CancellationToken cancellationToken)
    {
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

        await Data.WriteSettingsAsync(settings with { Connection = ConnectionId }, cancellationToken).ConfigureAwait(false);
        await LoadGenerationAsync(cancellationToken).ConfigureAwait(false);
    }

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

    [UICommand]
    public async Task SelectPresetsAsync(CancellationToken cancellationToken)
    {
        if (_profile is null)
            return;

        await Data.Connections.WriteAsync(_profile.ConnectionId, _profile.Connection with { Instruct = FormatId, SystemPrompt = SystemPromptId, Prompt = PromptId, Samplers = SamplersId }, cancellationToken).ConfigureAwait(false);
        await LoadGenerationAsync(cancellationToken).ConfigureAwait(false);
    }

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

    [UICommand]
    public async Task SaveThinkingAsync(CancellationToken cancellationToken)
    {
        if (_profile?.Connection.Instruct is not string id)
            return;

        ModelFormat format = _profile.Format with { Thought = _profile.Format.Thought with { Enabled = Thinking } };

        await Data.ModelFormats.WriteAsync(id, format, cancellationToken).ConfigureAwait(false);
        _profile = _profile with { Format = format };
    }

    [UICommand]
    public async Task SaveIncludeNamesAsync(CancellationToken cancellationToken)
    {
        if (_profile?.Connection.Prompt is not string id)
            return;

        PromptPreset prompt = _profile.Prompt with { IncludeNames = IncludeNames };

        await Data.Prompts.WriteAsync(id, prompt, cancellationToken).ConfigureAwait(false);
        _profile = _profile with { Prompt = prompt };
    }

    [UICommand]
    public async Task SaveContextAsync(CancellationToken cancellationToken)
    {
        if (_profile is null)
            return;

        ConnectionProfile connection = _profile.Connection with { ContextLength = new ContextLength { Auto = ContextAuto, Value = (int)ContextValue } };

        await Data.Connections.WriteAsync(_profile.ConnectionId, connection, cancellationToken).ConfigureAwait(false);
        _profile = _profile with { Connection = connection };
    }
}

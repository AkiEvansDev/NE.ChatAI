using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Characters;
using NE.ChatAI.Chats;
using NE.ChatAI.Settings;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Controllers;

/// <summary>Displays how personas, characters and worlds are bound on a layered graph and opens any card's editor dialog on click.</summary>
internal sealed partial class WorldsController : ShellController
{
    public const string PageId = "chatai-worlds-page";
    public const string CanvasId = "chatai-worlds-graph";
    public const string CharacterNodePrefix = "character:";
    public const string PersonaNodePrefix = "persona:";
    public const string WorldNodePrefix = "world:";

    private const string CharacterColor = "#006E64";
    private const string PersonaColor = "#503CB4";
    private const string WorldColor = "#2850A0";

    private string _topology = string.Empty;

    [RecursiveMember(false)]
    public RecursiveCollection<UIGraphNode> Nodes { get; } = [];

    [RecursiveMember]
    public partial UIGraphDocument GraphDocument { get; set; } = UIGraphDocument.Empty;

    [RecursiveMember]
    public partial UIVisibility GraphVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember]
    public partial UIVisibility EmptyVisibility { get; set; } = UIVisibility.Visible;

    protected override async Task OnAttachedAsync(UINavigationRequest navigation, CancellationToken cancellationToken)
    {
        await ResolveLastChatActorsAsync(cancellationToken).ConfigureAwait(false);
        await base.OnAttachedAsync(navigation, cancellationToken).ConfigureAwait(false);
    }

    private async Task ResolveLastChatActorsAsync(CancellationToken cancellationToken)
    {
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        Chat? chat = settings.LastChat is string lastId ? await Data.Chats.ReadAsync(lastId, cancellationToken).ConfigureAwait(false) : null;

        if (chat is null)
        {
            OpenCharacterId = null;
            ActivePersonaId = null;
            ActiveWorldId = null;
            return;
        }

        OpenCharacterId = chat.Character;
        Character? character = chat.Character is string charId ? await Data.Characters.ReadAsync(charId, cancellationToken).ConfigureAwait(false) : null;
        ActivePersonaId = ChatLibrary.ResolvePersonaId(Data, chat, character, settings.Persona);
        Persona? persona = ActivePersonaId is string personaId ? await Data.Personas.ReadAsync(personaId, cancellationToken).ConfigureAwait(false) : null;
        ActiveWorldId = character?.World ?? persona?.World;
    }

    protected override async Task OnLanguageChangedAsync(string language, CancellationToken cancellationToken)
    {
        await base.OnLanguageChangedAsync(language, cancellationToken).ConfigureAwait(false);
        RebuildGraph();
    }

    protected override void OnCardsLoaded()
        => RebuildGraph();

    protected override async Task OnCardChangedAsync(CancellationToken cancellationToken)
    {
        await ResolveLastChatActorsAsync(cancellationToken).ConfigureAwait(false);
        RebuildGraph();
    }

    private void RebuildGraph()
    {
        HashSet<string> worldIds = [with(StringComparer.Ordinal), .. KnownWorlds.Select(static stored => stored.Id)];
        HashSet<string> personaIds = [with(StringComparer.Ordinal), .. KnownPersonas.Select(static stored => stored.Id)];
        var defaultPersonaId = DefaultPersonaId is string fallback && personaIds.Contains(fallback) ? fallback : null;

        StoredDocument<World>[] worlds = [.. KnownWorlds.OrderBy(static item => item.Document.Name, StringComparer.CurrentCultureIgnoreCase)];
        StoredDocument<Persona>[] personas = [.. KnownPersonas.OrderBy(static item => item.Document.Name, StringComparer.CurrentCultureIgnoreCase)];
        StoredDocument<Character>[] characters = [.. KnownCharacters.OrderBy(static item => item.Document.Name, StringComparer.CurrentCultureIgnoreCase)];

        Dictionary<string, List<UIGraphLink>> worldLinks = [with(StringComparer.Ordinal)];
        Dictionary<string, List<UIGraphLink>> personaLinks = [with(StringComparer.Ordinal)];

        foreach (StoredDocument<Persona> stored in personas)
        {
            if (stored.Document.World is string worldId && worldIds.Contains(worldId))
                AddLink(worldLinks, worldId, WorldNodePrefix + worldId, PersonaNodePrefix + stored.Id);
        }

        foreach (StoredDocument<Character> stored in characters)
        {
            var characterNodeId = CharacterNodePrefix + stored.Id;

            if (stored.Document.World is string worldId && worldIds.Contains(worldId))
                AddLink(worldLinks, worldId, WorldNodePrefix + worldId, characterNodeId);

            var effectivePersonaId = stored.Document.Persona is string ownPersona && personaIds.Contains(ownPersona)
                ? ownPersona
                : defaultPersonaId;

            if (effectivePersonaId is not null)
                AddLink(personaLinks, effectivePersonaId, PersonaNodePrefix + effectivePersonaId, characterNodeId);
        }

        var characterLabel = Context.Translate("chatai.worlds.kind.character") ?? "Character";
        var personaLabel = Context.Translate("chatai.worlds.kind.persona") ?? "Persona";
        var activeBadge = Context.Translate("chatai.chats.active") ?? "active";
        var defaultBadge = Context.Translate("chatai.chats.default") ?? "default";
        List<UIGraphNode> desired = [with(worlds.Length + personas.Length + characters.Length)];

        foreach (StoredDocument<World> stored in worlds)
        {
            var nodeId = WorldNodePrefix + stored.Id;
            var enabledArticles = stored.Document.Articles.Count(static article => article.Enabled);
            var worldKind = Context.Translate(UIPhrase.Of("chatai.worlds.kind.world", ("count", enabledArticles))) ?? $"World · {enabledArticles}";

            desired.Add(new UIGraphNode(nodeId)
            {
                Title = stored.Document.Name,
                Description = JoinKindAndTags(worldKind, stored.Document.Tags),
                Image = Pictures.WorldAvatarOf(stored.Id),
                Icon = AppIcons.Outline(AppIcons.Worlds),
                Color = WorldColor,
                BadgeTitle = string.Equals(stored.Id, ActiveWorldId, StringComparison.Ordinal) ? activeBadge : null,
                Links = worldLinks.TryGetValue(stored.Id, out List<UIGraphLink>? links) ? [.. links] : []
            });
        }

        foreach (StoredDocument<Persona> stored in personas)
        {
            var nodeId = PersonaNodePrefix + stored.Id;
            var badge = string.Equals(stored.Id, ActivePersonaId, StringComparison.Ordinal)
                ? activeBadge
                : string.Equals(stored.Id, DefaultPersonaId, StringComparison.Ordinal)
                    ? defaultBadge
                    : null;

            desired.Add(new UIGraphNode(nodeId)
            {
                Title = stored.Document.Name,
                Description = JoinKindAndTags(personaLabel, stored.Document.Tags),
                Image = Pictures.PersonaAvatarOf(stored.Id),
                Icon = AppIcons.Outline(AppIcons.Persona),
                Color = PersonaColor,
                BadgeTitle = badge,
                Links = personaLinks.TryGetValue(stored.Id, out List<UIGraphLink>? links) ? [.. links] : []
            });
        }

        foreach (StoredDocument<Character> stored in characters)
        {
            var nodeId = CharacterNodePrefix + stored.Id;

            desired.Add(new UIGraphNode(nodeId)
            {
                Title = stored.Document.Name,
                Description = JoinKindAndTags(characterLabel, stored.Document.Tags),
                Image = Pictures.CharacterAvatarOf(stored.Id),
                Icon = AppIcons.Outline(AppIcons.Chats),
                Color = CharacterColor,
                BadgeTitle = string.Equals(stored.Id, OpenCharacterId, StringComparison.Ordinal) ? activeBadge : null,
                Links = []
            });
        }

        SyncNodes(desired);
        var topology = string.Join('|', desired.Select(static node => node.Links.Count == 0 ? node.Id : $"{node.Id}->{string.Join(',', node.Links.Select(static link => link.To))}"));

        if (!string.Equals(_topology, topology, StringComparison.Ordinal))
        {
            _topology = topology;
            GraphDocument = UIGraphDocument.Empty.WithKey(topology);
        }

        GraphVisibility = desired.Count > 0 ? UIVisibility.Visible : UIVisibility.Collapsed;
        EmptyVisibility = desired.Count > 0 ? UIVisibility.Collapsed : UIVisibility.Visible;
    }

    private static void AddLink(Dictionary<string, List<UIGraphLink>> map, string ownerId, string fromNodeId, string toNodeId)
    {
        if (!map.TryGetValue(ownerId, out List<UIGraphLink>? list))
        {
            list = [];
            map[ownerId] = list;
        }

        list.Add(new UIGraphLink($"{fromNodeId}->{toNodeId}", toNodeId, null));
    }

    private static string JoinKindAndTags(string kind, IReadOnlyList<string> tags)
        => tags.Count == 0 ? kind : $"{kind} · {string.Join(", ", tags)}";

    private void SyncNodes(List<UIGraphNode> desired)
    {
        HashSet<string> kept = [with(StringComparer.Ordinal), .. desired.Select(static node => node.Id)];

        for (var i = Nodes.Count - 1; i >= 0; i--)
        {
            if (!kept.Contains(Nodes[i].Id))
                _ = Nodes.Remove(Nodes[i].Id);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            UIGraphNode next = desired[i];

            if (Nodes.TryGet(next.Id, out _))
            {
                if (!string.Equals(Nodes[i].Id, next.Id, StringComparison.Ordinal))
                    _ = Nodes.Move(next.Id, i);

                Nodes[i] = next;
            }
            else
            {
                Nodes.Insert(i, next);
            }
        }
    }

    [UICommand]
    public async Task<UICommandResult> OpenNodeAsync(string nodeId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return UICommandResult.Ok([]);

        (TagScope scope, var id) = nodeId switch
        {
            _ when nodeId.StartsWith(CharacterNodePrefix, StringComparison.Ordinal) => (TagScope.Character, nodeId[CharacterNodePrefix.Length..]),
            _ when nodeId.StartsWith(PersonaNodePrefix, StringComparison.Ordinal) => (TagScope.Persona, nodeId[PersonaNodePrefix.Length..]),
            _ when nodeId.StartsWith(WorldNodePrefix, StringComparison.Ordinal) => (TagScope.World, nodeId[WorldNodePrefix.Length..]),
            _ => default
        };

        if (string.IsNullOrEmpty(id))
            return UICommandResult.Ok([]);

        return await OpenCardEditorAsync(scope, id, cancellationToken).ConfigureAwait(false) is string dialogKey
            ? UICommandResult.Ok([new OpenDialogEffect(dialogKey)])
            : UICommandResult.Ok([]);
    }

    [UICommand]
    public UICommandResult CreateFromGraph(string kind)
        => AskCreateCardOf(kind);
}

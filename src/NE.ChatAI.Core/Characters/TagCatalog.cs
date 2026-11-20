using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Settings;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Characters;

/// <summary>Which kind of card a set of tags belongs to; each kind keeps its own tags apart from the others.</summary>
public enum TagScope
{
    Character,
    Persona,
    World
}

/// <summary>A tag known to the application: its name and its colour, written as the UI writes one; none, and it is drawn plain.</summary>
public sealed record KnownTag(string Name, string? Color);

/// <summary>Lists, adds, recolours, renames and deletes card tags per <see cref="TagScope"/>, matching names case-insensitively.</summary>
public static class TagCatalog
{
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    /// <summary>Every tag known for <paramref name="scope"/>, by name, each with its colour.</summary>
    public static async Task<IReadOnlyList<KnownTag>> ListAsync(DataFolder data, TagScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> carried = await CarriedAsync(data, scope, cancellationToken).ConfigureAwait(false);
        Dictionary<string, KnownTag> known = [with(Names)];

        foreach (TagStyle style in StylesOf(settings, scope))
            _ = known.TryAdd(style.Name, new KnownTag(style.Name, style.Color));

        foreach (var tag in carried)
            _ = known.TryAdd(tag, new KnownTag(tag, null));

        return [.. known.Values.OrderBy(static tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static Task<IReadOnlyList<string>> CarriedAsync(DataFolder data, TagScope scope, CancellationToken cancellationToken)
        => scope switch
        {
            TagScope.Persona => TagsOfAsync(data.Personas, cancellationToken),
            TagScope.World => TagsOfAsync(data.Worlds, cancellationToken),
            _ => TagsOfAsync(data.Characters, cancellationToken)
        };

    private static async Task<IReadOnlyList<string>> TagsOfAsync<T>(DocumentStore<T> store, CancellationToken cancellationToken) where T : Card
    {
        DocumentListing<T> cards = await store.ListAsync(cancellationToken).ConfigureAwait(false);

        return [.. cards.Items.SelectMany(static stored => stored.Document.Tags)];
    }

    private static IReadOnlyList<TagStyle> StylesOf(AppSettings settings, TagScope scope)
        => scope switch
        {
            TagScope.Persona => settings.PersonaTags,
            TagScope.World => settings.WorldTags,
            _ => settings.Tags
        };

    /// <summary>Makes a tag no card of <paramref name="scope"/> carries yet; one already known is left as it is.</summary>
    public static async Task<bool> AddAsync(DataFolder data, TagScope scope, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        name = name.Trim();

        if (name.Length == 0 || (await ListAsync(data, scope, cancellationToken).ConfigureAwait(false)).Any(tag => Names.Equals(tag.Name, name)))
            return false;

        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TagStyle> styles = StylesOf(settings, scope);

        await data.WriteSettingsAsync(WithStyles(settings, scope, [.. styles, new TagStyle(name, null)]), cancellationToken).ConfigureAwait(false);

        return true;
    }

    private static AppSettings WithStyles(AppSettings settings, TagScope scope, IReadOnlyList<TagStyle> styles)
        => scope switch
        {
            TagScope.Persona => settings with { PersonaTags = styles },
            TagScope.World => settings with { WorldTags = styles },
            _ => settings with { Tags = styles }
        };

    /// <summary>Keeps <paramref name="color"/> as the tag's colour in <paramref name="scope"/>; none, and it is drawn plain.</summary>
    public static async Task SetColorAsync(DataFolder data, TagScope scope, string name, string? color, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        name = name.Trim();

        if (name.Length == 0)
            return;

        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TagStyle> styles = StylesOf(settings, scope);

        await data.WriteSettingsAsync(WithStyles(settings, scope, [.. styles.Where(style => !Names.Equals(style.Name, name)), new TagStyle(name, color)]), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Renames <paramref name="from"/> to <paramref name="to"/> across all cards and styles in <paramref name="scope"/>, merging when <paramref name="to"/> already exists.</summary>
    public static async Task<bool> RenameAsync(DataFolder data, TagScope scope, string from, string to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        from = from.Trim();
        to = to.Trim();

        if (from.Length == 0 || to.Length == 0 || string.Equals(from, to, StringComparison.Ordinal))
            return false;

        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TagStyle> styles = StylesOf(settings, scope);
        TagStyle? source = styles.FirstOrDefault(style => Names.Equals(style.Name, from));

        if (source is null && !(await CarriedAsync(data, scope, cancellationToken).ConfigureAwait(false)).Contains(from, Names))
            return false;

        TagStyle? target = styles.FirstOrDefault(style => Names.Equals(style.Name, to) && !Names.Equals(style.Name, from));
        TagStyle renamed = new(to, target?.Color ?? source?.Color);

        await data.WriteSettingsAsync(WithStyles(settings, scope, [.. styles.Where(style => !Names.Equals(style.Name, from) && !Names.Equals(style.Name, to)), renamed]), cancellationToken).ConfigureAwait(false);
        await RetagScopeAsync(data, scope, from, to, cancellationToken).ConfigureAwait(false);

        return true;
    }

    private static Task RetagScopeAsync(DataFolder data, TagScope scope, string from, string? to, CancellationToken cancellationToken)
        => scope switch
        {
            TagScope.Persona => RetagAsync(data.Personas, from, to, cancellationToken),
            TagScope.World => RetagAsync(data.Worlds, from, to, cancellationToken),
            _ => RetagAsync(data.Characters, from, to, cancellationToken)
        };

    private static async Task RetagAsync<T>(DocumentStore<T> store, string from, string? to, CancellationToken cancellationToken) where T : Card
    {
        DocumentListing<T> cards = await store.ListAsync(cancellationToken).ConfigureAwait(false);

        foreach (StoredDocument<T> stored in cards.Items)
        {
            if (!stored.Document.Tags.Contains(from, Names))
                continue;

            IEnumerable<string> tags = stored.Document.Tags
                .Select(tag => Names.Equals(tag, from) || (to is not null && Names.Equals(tag, to)) ? to : tag)
                .OfType<string>()
                .Distinct(Names);

            await store.WriteAsync(stored.Id, stored.Document with { Tags = [.. tags] }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Takes a tag off every card of <paramref name="scope"/> that carries it and forgets its colour.</summary>
    public static async Task DeleteAsync(DataFolder data, TagScope scope, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        name = name.Trim();

        if (name.Length == 0)
            return;

        AppSettings settings = await data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TagStyle> styles = StylesOf(settings, scope);

        await data.WriteSettingsAsync(WithStyles(settings, scope, [.. styles.Where(style => !Names.Equals(style.Name, name))]), cancellationToken).ConfigureAwait(false);
        await RetagScopeAsync(data, scope, name, null, cancellationToken).ConfigureAwait(false);
    }
}

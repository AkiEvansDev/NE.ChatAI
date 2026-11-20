using System;
using System.Collections.Generic;
using NE.ChatAI.Characters;
using NE.ChatAI.Settings;

namespace NE.ChatAI.Controllers;

/// <summary>One tag badge rendered on a card row.</summary>
internal sealed partial class TagBadge : RecursiveObservable, IBindableItem, IContentItem
{
    [RecursiveMember(false)]
    public required string Id { get; init; }

    public bool IsContent => true;

    [RecursiveMember]
    public partial string Text { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIThemeColor? Color { get; set; }
}

/// <summary>Maps persisted tag styles into <see cref="UIThemeColor"/> values and syncs badge collections in place.</summary>
internal static class TagBadges
{
    public static Dictionary<string, UIThemeColor> ColorsOf(AppSettings settings, TagScope scope)
    {
        ArgumentNullException.ThrowIfNull(settings);

        IReadOnlyList<TagStyle> styles = scope switch
        {
            TagScope.Persona => settings.PersonaTags,
            TagScope.World => settings.WorldTags,
            _ => settings.Tags
        };
        Dictionary<string, UIThemeColor> colors = [with(StringComparer.OrdinalIgnoreCase)];

        for (var i = 0; i < styles.Count; i++)
            AddColor(colors, styles[i].Name, styles[i].Color);

        return colors;
    }

    public static Dictionary<string, UIThemeColor> ColorsOf(IReadOnlyList<KnownTag> known)
    {
        ArgumentNullException.ThrowIfNull(known);

        Dictionary<string, UIThemeColor> colors = [with(StringComparer.OrdinalIgnoreCase)];

        for (var i = 0; i < known.Count; i++)
            AddColor(colors, known[i].Name, known[i].Color);

        return colors;
    }

    private static void AddColor(Dictionary<string, UIThemeColor> colors, string name, string? rawColor)
    {
        if (UIThemeColor.TryParse(rawColor, out UIThemeColor color) && HasColor(color))
            colors[name] = color;
    }

    public static bool HasColor(UIThemeColor? color)
        => color is { } value
            && value != UIThemeColor.Transparent
            && value.Light is not { Opacity: 0 }
            && value.Dark is not { Opacity: 0 };

    public static OptionItem OptionOf(string tag, IReadOnlyDictionary<string, UIThemeColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);

        return new OptionItem { Id = tag, IsContent = true, BadgeTitle = tag, BadgeVariant = UIBadgeVariant.Surface, BadgeTitleType = UITextAppearance.Caption, BadgeColor = colors.TryGetValue(tag, out UIThemeColor color) ? color : null };
    }

    public static void Fill(RecursiveCollection<TagBadge> badges, IEnumerable<string> tags, IReadOnlyDictionary<string, UIThemeColor> colors)
    {
        ArgumentNullException.ThrowIfNull(badges);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(colors);

        List<string> ordered = [];
        HashSet<string> kept = [with(StringComparer.Ordinal)];

        foreach (var tag in tags)
        {
            if (kept.Add(tag))
                ordered.Add(tag);
        }

        for (var i = badges.Count - 1; i >= 0; i--)
        {
            if (!kept.Contains(badges[i].Id))
                _ = badges.Remove(badges[i].Id);
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var tag = ordered[i];
            UIThemeColor? badgeColor = colors.TryGetValue(tag, out UIThemeColor color) ? color : null;

            if (badges.TryGet(tag, out TagBadge? existing))
            {
                existing.Text = tag;
                existing.Color = badgeColor;

                if (!string.Equals(badges[i].Id, tag, StringComparison.Ordinal))
                    _ = badges.Move(tag, i);
            }
            else
            {
                badges.Insert(i, new TagBadge { Id = tag, Text = tag, Color = badgeColor });
            }
        }
    }
}

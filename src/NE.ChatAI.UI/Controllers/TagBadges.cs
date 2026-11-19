using System;
using System.Collections.Generic;
using NE.ChatAI.Characters;
using NE.ChatAI.Settings;

namespace NE.ChatAI.Controllers;

/// <summary>A tag as a badge draws it: its name in its colour.</summary>
internal sealed partial class TagBadge : RecursiveObservable, IBindableItem, IContentItem
{
    [RecursiveMember(false)]
    public required string Id { get; init; }

    /// <summary>A tag is the reader's own word, never a key.</summary>
    public bool IsContent => true;

    [RecursiveMember]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>The tag's colour; none, and the badge is drawn as a quiet surface.</summary>
    [RecursiveMember]
    public partial UIThemeColor? Color { get; set; }
}

/// <summary>The tags' colours as the settings keep them, read into badges.</summary>
internal static class TagBadges
{
    /// <summary>Each tag's colour in <paramref name="scope"/> by its name, case aside; a colour the UI cannot read is left out.</summary>
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

        foreach (TagStyle tag in styles)
        {
            if (UIThemeColor.TryParse(tag.Color, out UIThemeColor color) && HasColor(color))
                colors[tag.Name] = color;
        }

        return colors;
    }

    /// <summary>Whether <paramref name="color"/> has a visible fill; zero opacity clears a tag's colour back to the theme's quiet badge.</summary>
    public static bool HasColor(UIThemeColor? color)
        => color is { } value
            && value != UIThemeColor.Transparent
            && value.Light is not { Opacity: 0 }
            && value.Dark is not { Opacity: 0 };

    /// <summary>A tag as a badge select offers it: the badge itself, in its colour, the reader's own word.</summary>
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

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
        Dictionary<string, UIThemeColor> colors = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>A tag as a checkable menu entry offers it: the badge itself in its colour, with a checkmark at the end when chosen.</summary>
    public static MenuItem MenuItemOf(string tag, IReadOnlyDictionary<string, UIThemeColor> colors, bool isChecked)
    {
        ArgumentNullException.ThrowIfNull(colors);

        return new MenuItem
        {
            Id = tag,
            IsContent = true,
            Kind = UIMenuItemKind.Check,
            Checked = isChecked,
            BadgeTitle = tag,
            BadgeVariant = UIBadgeVariant.Surface,
            BadgeTitleType = UITextAppearance.Caption,
            BadgeColor = colors.TryGetValue(tag, out UIThemeColor color) ? color : null
        };
    }

    public static void Fill(RecursiveCollection<TagBadge> badges, IEnumerable<string> tags, IReadOnlyDictionary<string, UIThemeColor> colors)
    {
        ArgumentNullException.ThrowIfNull(badges);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(colors);

        badges.Clear();

        foreach (var tag in tags)
            badges.Add(new TagBadge { Id = tag, Text = tag, Color = colors.TryGetValue(tag, out UIThemeColor color) ? color : null });
    }
}

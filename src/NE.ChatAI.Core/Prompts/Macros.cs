using System;
using System.Collections.Generic;
using System.Text;

namespace NE.ChatAI.Prompts;

/// <summary>Expands SillyTavern <c>{{name}}</c> values, <c>{{#if name}}…{{else}}…{{/if}}</c> conditionals, and <c>{{newline}}</c>/<c>{{noop}}</c>/<c>{{trim}}</c> directives.</summary>
public static class Macros
{
    private const string Open = "{{";
    private const string Close = "}}";
    private const string TrimMarker = "\u0001trim\u0001";

    /// <summary>Renders <paramref name="template"/> against case-insensitive <paramref name="values"/>, leaving unknown macros literal.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values, Action<string, string>? used = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        if (!template.Contains(Open, StringComparison.Ordinal))
            return template;

        StringBuilder output = new(template.Length);
        Stack<(bool Writing, bool Outer)> conditions = new();
        var writing = true;
        var position = 0;

        while (position < template.Length)
        {
            var open = template.IndexOf(Open, position, StringComparison.Ordinal);
            var close = open < 0 ? -1 : template.IndexOf(Close, open + Open.Length, StringComparison.Ordinal);

            if (open < 0 || close < 0)
            {
                if (writing)
                    _ = output.Append(template, position, template.Length - position);

                break;
            }

            // Advance past any lone '{{' so an unclosed brace does not swallow a later valid '{{macro}}'.
            while (true)
            {
                var nestedOpen = template.IndexOf(Open, open + Open.Length, StringComparison.Ordinal);

                if (nestedOpen < 0 || nestedOpen >= close)
                    break;

                open = nestedOpen;
            }

            if (writing)
                _ = output.Append(template, position, open - position);

            var macro = template[(open + Open.Length)..close].Trim();
            position = close + Close.Length;

            if (IsIfDirective(macro, out var conditionName))
            {
                var holds = Lookup(values, conditionName) is { } value && !string.IsNullOrWhiteSpace(value);

                conditions.Push((writing && holds, writing));
                writing = writing && holds;
                continue;
            }

            if (string.Equals(macro, "else", StringComparison.OrdinalIgnoreCase) && conditions.TryPop(out (bool Writing, bool Outer) branch))
            {
                conditions.Push((branch.Outer && !branch.Writing, branch.Outer));
                writing = branch.Outer && !branch.Writing;
                continue;
            }

            if (string.Equals(macro, "/if", StringComparison.OrdinalIgnoreCase) && conditions.TryPop(out (bool Writing, bool Outer) ended))
            {
                writing = ended.Outer;
                continue;
            }

            if (!writing)
                continue;

            _ = output.Append(Expand(macro, template[open..position], values, used));
        }

        var rendered = output.ToString();

        return rendered.Contains(TrimMarker, StringComparison.Ordinal) ? ApplyTrim(rendered) : rendered;
    }

    private static bool IsIfDirective(string macro, out string name)
    {
        if (macro.Length > 3 && macro.StartsWith("#if", StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(macro[3]))
        {
            name = macro[3..].Trim();
            return name.Length > 0;
        }

        name = string.Empty;
        return false;
    }

    private static string? Lookup(IReadOnlyDictionary<string, string> values, string name)
    {
        if (values.TryGetValue(name, out var value))
            return value;

        foreach (KeyValuePair<string, string> pair in values)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    private static string Expand(string macro, string written, IReadOnlyDictionary<string, string> values, Action<string, string>? used)
    {
        if (string.Equals(macro, "newline", StringComparison.OrdinalIgnoreCase))
            return "\n";

        if (string.Equals(macro, "noop", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        if (string.Equals(macro, "trim", StringComparison.OrdinalIgnoreCase))
            return TrimMarker;

        if (Lookup(values, macro) is not { } value)
            return written;

        used?.Invoke(macro, value);

        return value;
    }

    private static string ApplyTrim(string rendered)
    {
        StringBuilder output = new(rendered.Length);
        var parts = rendered.Split(TrimMarker);

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];

            if (i > 0)
                part = part.TrimStart();

            if (i < parts.Length - 1)
                part = part.TrimEnd();

            _ = output.Append(part);
        }

        return output.ToString();
    }
}

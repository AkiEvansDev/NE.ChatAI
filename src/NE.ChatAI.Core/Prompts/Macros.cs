using System;
using System.Collections.Generic;
using System.Text;

namespace NE.ChatAI.Prompts;

/// <summary>
/// SillyTavern's macros: <c>{{name}}</c> by its value, <c>{{#if name}}…{{else}}…{{/if}}</c> on whether the value has words,
/// <c>{{newline}}</c>, <c>{{noop}}</c> and <c>{{trim}}</c>. Names are matched case aside; a name with no value is left as it was
/// written, so a macro this application does not know still reads in the prompt.
/// </summary>
public static class Macros
{
    private const string Open = "{{";
    private const string Close = "}}";
    private const string TrimMarker = "\u0001trim\u0001";

    /// <summary>
    /// The template with every macro it names put in from <paramref name="values"/> (by name, case aside); <paramref name="used"/>,
    /// when given, hears every value put in — what the prompt's blocks are counted by.
    /// </summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values, Action<string, string>? used = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        if (!template.Contains(Open, StringComparison.Ordinal))
            return template;

        StringBuilder output = new(template.Length);
        // Each open #if: whether its branch is being written, and whether the one around it was.
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

            if (writing)
                _ = output.Append(template, position, open - position);

            var macro = template[(open + Open.Length)..close].Trim();
            position = close + Close.Length;

            if (macro.StartsWith("#if ", StringComparison.OrdinalIgnoreCase))
            {
                var holds = Lookup(values, macro[4..].Trim()) is { } value && !string.IsNullOrWhiteSpace(value);

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

    /// <summary><c>{{trim}}</c> takes away the whitespace on either side of it, line breaks included.</summary>
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

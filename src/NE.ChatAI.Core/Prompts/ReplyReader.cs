using System;
using System.Text;

namespace NE.ChatAI.Prompts;

/// <summary>
/// The reply as it streams in, with a thinking model's thought — marked as its format says, or in <c>&lt;think&gt;</c> tags — kept
/// apart from what it says.
/// </summary>
/// <remarks>
/// koboldcpp does not stream a special marker faithfully: the same prompt opens with <c>&lt;|channel&gt;thought</c>, a bare
/// <c>thought</c>, or <c>&lt;thought</c>, and several tokens may come as one piece. So the thought is read from the whole text: its
/// label (the prefix's word, <c>thought</c>) with nothing before it but characters of the marker it follows, up to the suffix.
/// A fine-tune often thinks in <c>&lt;think&gt;…&lt;/think&gt;</c> whatever its base's format says, so those tags are read too.
/// </remarks>
public sealed class ReplyReader
{
    private const string ThinkOpen = "<think>";
    private const string ThinkClose = "</think>";

    private readonly ThoughtFormat _thought;
    private readonly string _marker;
    private readonly string _label;
    private readonly string _speakerPrefix;
    private readonly StringBuilder _raw = new();
    private bool _complete;

    /// <summary>
    /// The thought's opening the prompt already wrote (<see cref="ThoughtFormat.OpenedByPrompt"/>): the stream continues it, so it is
    /// read as the stream's start.
    /// </summary>
    private readonly string _opened;

    public ReplyReader(ThoughtFormat thought, string? speaker = null)
    {
        ArgumentNullException.ThrowIfNull(thought);

        _thought = thought;
        (_marker, _label) = SplitPrefix(thought.Prefix);
        _speakerPrefix = string.IsNullOrWhiteSpace(speaker) ? string.Empty : $"{speaker.Trim()}:";
        _opened = thought.OpenedByPrompt ? thought.Prefix : string.Empty;
    }

    /// <summary>A prefix as its special marker and its label: <c>&lt;|channel&gt;</c> and <c>thought</c>, or no marker and all of it.</summary>
    private static (string Marker, string Label) SplitPrefix(string prefix)
    {
        var close = prefix.StartsWith('<') ? prefix.IndexOf('>', StringComparison.Ordinal) : prefix.StartsWith('[') ? prefix.IndexOf(']', StringComparison.Ordinal) : -1;

        return close > 0 ? (prefix[..(close + 1)], prefix[(close + 1)..].Trim()) : (string.Empty, prefix.Trim());
    }

    /// <summary>What the character says so far, without the thought.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>What the model thought before it answered; empty for a model that does not.</summary>
    public string Thought { get; private set; } = string.Empty;

    /// <summary>Takes the next piece of the stream and answers whether <see cref="Text"/> or <see cref="Thought"/> changed.</summary>
    public bool Append(string piece)
    {
        ArgumentNullException.ThrowIfNull(piece);

        _ = _raw.Append(piece);

        return Split();
    }

    /// <summary>
    /// The stream has ended: a partial marker that never finished opening becomes text, while an opened thought cut short before its
    /// closing marker stays in <see cref="Thought"/>.
    /// </summary>
    public bool Complete()
    {
        _complete = true;

        return Split();
    }

    private bool Split()
    {
        var text = Text;
        var thought = Thought;
        var raw = _opened + _raw.ToString();

        (var nextText, var nextThought) = (_thought.IsDefined ? ReadFormat(raw) : null) ?? ReadTags(raw);
        nextText = StripSpeaker(nextText);
        Text = _complete ? nextText.TrimEnd('\r', '\n') : nextText;
        Thought = nextThought;

        return !string.Equals(text, Text, StringComparison.Ordinal) || !string.Equals(thought, Thought, StringComparison.Ordinal);
    }

    /// <summary>The thought as the format marks it; none when the reply does not open with it.</summary>
    private (string Text, string Thought)? ReadFormat(string raw)
    {
        var label = LabelEnd(raw);

        if (label == Opening.None)
            return null;

        if (label == Opening.Pending)
            return _complete && _opened.Length == 0 ? (raw, string.Empty) : (string.Empty, string.Empty);

        var close = raw.IndexOf(_thought.Suffix, label, StringComparison.Ordinal);

        if (close >= 0)
            return (raw[(close + _thought.Suffix.Length)..].TrimStart(), raw[label..close].Trim());

        return (string.Empty, raw[label..].Trim());
    }

    /// <summary>Where the thought's label ends in <paramref name="raw"/>; <see cref="Opening.Pending"/> while it may still open one.</summary>
    private int LabelEnd(string raw)
    {
        // A marker with no label (<think>) stands whole: skipping its characters would read "think about it" as one.
        if (_label.Length == 0)
            return raw.StartsWith(_marker, StringComparison.Ordinal) ? _marker.Length : _marker.StartsWith(raw, StringComparison.Ordinal) ? Opening.Pending : Opening.None;

        var start = 0;

        // Whatever of the marker came through stands first: all of it, some of it, or none.
        while (start < raw.Length && _marker.Contains(raw[start], StringComparison.Ordinal))
            start++;

        ReadOnlySpan<char> rest = raw.AsSpan(start);

        if (rest.StartsWith(_label, StringComparison.Ordinal))
        {
            var end = start + _label.Length;

            // "thoughtful" is a word, not the label: the label is followed by a line break, the suffix, or nothing yet.
            if (end == raw.Length)
                return Opening.Pending;

            return raw[end] is '\n' or '\r' or ' ' || raw.AsSpan(end).StartsWith(_thought.Suffix, StringComparison.Ordinal) ? end : Opening.None;
        }

        return _label.AsSpan().StartsWith(rest, StringComparison.Ordinal) ? Opening.Pending : Opening.None;
    }

    /// <summary>The thought in <c>&lt;think&gt;</c> tags opening the reply, after any blank; else the reply is all text.</summary>
    private (string Text, string Thought) ReadTags(string raw)
    {
        ReadOnlySpan<char> rest = raw.AsSpan().TrimStart();
        var start = raw.Length - rest.Length;

        if (!rest.StartsWith(ThinkOpen, StringComparison.Ordinal))
        {
            // "<thi" may yet be the tag: nothing is said until it is or is not.
            var pending = !_complete && !rest.IsEmpty && ThinkOpen.AsSpan().StartsWith(rest, StringComparison.Ordinal);

            return pending ? (string.Empty, string.Empty) : (raw, string.Empty);
        }

        var open = start + ThinkOpen.Length;
        var close = raw.IndexOf(ThinkClose, open, StringComparison.Ordinal);

        if (close >= 0)
            return (raw[(close + ThinkClose.Length)..].TrimStart(), raw[open..close].Trim());

        return (string.Empty, raw[open..].Trim());
    }

    /// <summary>Takes the speaker's <c>Name:</c> and the space after it off the reply's opening, waiting while a partial name may still end in <c>:</c>.</summary>
    private string StripSpeaker(string text)
    {
        if (_speakerPrefix.Length == 0 || text.Length == 0)
            return text;

        ReadOnlySpan<char> trimmed = text.AsSpan().TrimStart();

        if (trimmed.IsEmpty)
            return string.Empty;

        if (trimmed.StartsWith(_speakerPrefix, StringComparison.OrdinalIgnoreCase))
            return trimmed[_speakerPrefix.Length..].TrimStart().ToString();

        if (!_complete && _speakerPrefix.AsSpan().StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return trimmed.Length == text.Length ? text : trimmed.ToString();
    }

    private static class Opening
    {
        public const int None = -1;
        public const int Pending = 0;
    }
}

using System;
using System.Text;

namespace NE.ChatAI.Prompts;

/// <summary>Separates a streaming model reply into spoken <see cref="Text"/> and reasoning <see cref="Thought"/>.</summary>
public sealed class ReplyReader
{
    private const string ThinkOpen = "<think>";
    private const string ThinkClose = "</think>";

    private readonly ThoughtFormat _thought;
    private readonly string _marker;
    private readonly string _label;
    private readonly string _trimmedSuffix;
    private readonly string _speakerPrefix;
    private readonly string _opened;
    private readonly StringBuilder _raw = new();
    private bool _complete;

    public ReplyReader(ThoughtFormat thought, string? speaker = null)
    {
        ArgumentNullException.ThrowIfNull(thought);

        _thought = thought;
        (_marker, _label) = SplitPrefix(thought.Prefix);
        _trimmedSuffix = thought.Suffix.Trim('\r', '\n');
        _speakerPrefix = string.IsNullOrWhiteSpace(speaker) ? string.Empty : $"{speaker.Trim()}:";
        _opened = thought.OpenedByPrompt ? thought.Prefix : string.Empty;
    }

    private static (string Marker, string Label) SplitPrefix(string prefix)
    {
        var close = prefix.StartsWith('<', StringComparison.Ordinal) ? prefix.IndexOf('>', StringComparison.Ordinal) : prefix.StartsWith('[', StringComparison.Ordinal) ? prefix.IndexOf(']', StringComparison.Ordinal) : -1;

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

        // SillyTavern reasoning templates often wrap the closing tag in extra newlines the model itself may omit.
        if (_trimmedSuffix.Length > 0 && !string.Equals(_trimmedSuffix, _thought.Suffix, StringComparison.Ordinal))
        {
            var fallbackClose = raw.IndexOf(_trimmedSuffix, label, StringComparison.Ordinal);

            if (fallbackClose >= 0)
                return (raw[(fallbackClose + _trimmedSuffix.Length)..].TrimStart(), raw[label..fallbackClose].Trim());
        }

        return (string.Empty, raw[label..].Trim());
    }

    private int LabelEnd(string raw)
    {
        // A marker with no label (<think>) stands whole: skipping its characters would read "think about it" as one.
        if (_label.Length == 0)
            return raw.StartsWith(_marker, StringComparison.Ordinal) ? _marker.Length : _marker.StartsWith(raw, StringComparison.Ordinal) ? Opening.Pending : Opening.None;

        var start = 0;

        // koboldcpp may drop part or all of a special token marker (<|channel|>thought) when streaming chunks.
        while (start < raw.Length && _marker.Contains(raw[start], StringComparison.Ordinal))
            start++;

        ReadOnlySpan<char> rest = raw.AsSpan(start);

        if (rest.StartsWith(_label, StringComparison.Ordinal))
        {
            var end = start + _label.Length;

            // "thoughtful" is a word, not the label: the label is followed by a line break, the suffix, or nothing yet.
            if (end == raw.Length)
                return Opening.Pending;

            return raw[end] is '\n' or '\r' or ' ' || raw.AsSpan(end).StartsWith(_thought.Suffix, StringComparison.Ordinal) || (_trimmedSuffix.Length > 0 && raw.AsSpan(end).StartsWith(_trimmedSuffix, StringComparison.Ordinal))
                ? end
                : Opening.None;
        }

        return _label.AsSpan().StartsWith(rest, StringComparison.Ordinal) ? Opening.Pending : Opening.None;
    }

    private (string Text, string Thought) ReadTags(string raw)
    {
        ReadOnlySpan<char> rest = raw.AsSpan().TrimStart();
        var start = raw.Length - rest.Length;

        if (!rest.StartsWith(ThinkOpen, StringComparison.Ordinal))
        {
            var pending = !_complete && !rest.IsEmpty && ThinkOpen.AsSpan().StartsWith(rest, StringComparison.Ordinal);

            return pending ? (string.Empty, string.Empty) : (raw, string.Empty);
        }

        var open = start + ThinkOpen.Length;
        var close = raw.IndexOf(ThinkClose, open, StringComparison.Ordinal);

        if (close >= 0)
            return (raw[(close + ThinkClose.Length)..].TrimStart(), raw[open..close].Trim());

        return (string.Empty, raw[open..].Trim());
    }

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

        return text;
    }

    /// <summary>Finalizes the stream, flushing any unfinished opening marker as text while keeping an unclosed thought in <see cref="Thought"/>.</summary>
    public bool Complete()
    {
        _complete = true;

        return Split();
    }

    private static class Opening
    {
        public const int None = -1;
        public const int Pending = -2;
    }
}

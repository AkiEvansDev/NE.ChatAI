using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Prompts;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Characters;

/// <summary>Which world a chat is played in, and what of it goes into the prompt.</summary>
public static class WorldInfo
{
    private const int MaxCascadePasses = 4;

    /// <summary>The character's world, else the persona's; none when neither names one the data folder holds.</summary>
    public static async Task<World?> OfAsync(DataFolder data, Card? character, Card? persona, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        foreach (var id in (string?[])[character?.World, persona?.World])
        {
            if (id is not null && Slug.IsValidId(id) && await data.Worlds.ReadAsync(id, cancellationToken).ConfigureAwait(false) is World world)
                return world;
        }

        return null;
    }

    /// <summary>What a world puts into the prompt without chat history: its overview after the story, then each constant article that is on.</summary>
    public static IReadOnlyList<WorldArticle> ArticlesOf(World? world)
        => ArticlesOf(world, [], null);

    /// <summary>
    /// What a world puts into the prompt for <paramref name="history"/>: its overview after the story, then each enabled article in list
    /// order — constant ones (no triggers) always, and triggered ones when matched in the last <see cref="World.ScanDepth"/> messages or
    /// cascading from another triggered article.
    /// </summary>
    public static IReadOnlyList<WorldArticle> ArticlesOf(World? world, IReadOnlyList<PromptMessage> history, string? continuation = null)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (world is null)
            return [];

        var count = world.Articles.Count;
        var active = new bool[count];
        List<TriggeredCandidate>? candidates = null;

        for (var i = 0; i < count; i++)
        {
            WorldArticle article = world.Articles[i];

            if (!article.Enabled || string.IsNullOrWhiteSpace(article.Text))
                continue;

            ParsedTriggers triggers = ParseTriggers(article.Triggers);

            if (triggers.IsConstant)
            {
                active[i] = true;
                continue;
            }

            candidates ??= [];
            candidates.Add(new TriggeredCandidate(i, article.Text, triggers.Positive, triggers.Negative));
        }

        if (candidates is not null)
            ActivateTriggered(candidates, active, ScanWindow(history, continuation, world.ScanDepth));

        List<WorldArticle> articles = new(count + 1);

        if (!string.IsNullOrWhiteSpace(world.Description))
            articles.Add(new WorldArticle { Title = world.Name, Text = world.Description });

        for (var i = 0; i < count; i++)
        {
            if (active[i])
                articles.Add(world.Articles[i]);
        }

        return articles;
    }

    private static ParsedTriggers ParseTriggers(IReadOnlyList<string> rawTriggers)
    {
        if (rawTriggers.Count == 0)
            return new ParsedTriggers([], []);

        List<string>? positive = null;
        List<string>? negative = null;

        for (var i = 0; i < rawTriggers.Count; i++)
        {
            ReadOnlySpan<char> rest = rawTriggers[i].AsSpan();

            while (!rest.IsEmpty)
            {
                var comma = rest.IndexOf(',');
                ReadOnlySpan<char> piece = (comma >= 0 ? rest[..comma] : rest).Trim();
                rest = comma >= 0 ? rest[(comma + 1)..] : [];

                if (piece.IsEmpty)
                    continue;

                var excluded = piece[0] == '-';

                if (excluded)
                    piece = piece[1..].Trim();

                if (!ContainsWordChar(piece))
                    continue;

                if (excluded)
                {
                    negative ??= [];
                    negative.Add(piece.ToString());
                }
                else
                {
                    positive ??= [];
                    positive.Add(piece.ToString());
                }
            }
        }

        return new ParsedTriggers(positive is null ? [] : [.. positive], negative is null ? [] : [.. negative]);
    }

    private static bool ContainsWordChar(ReadOnlySpan<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLetterOrDigit(text[i]))
                return true;
        }

        return false;
    }

    private static string[] ScanWindow(IReadOnlyList<PromptMessage> history, string? continuation, int scanDepth)
    {
        var depth = Math.Max(0, scanDepth);
        var start = Math.Max(0, history.Count - depth);
        var hasContinuation = !string.IsNullOrWhiteSpace(continuation);
        var length = history.Count - start + (hasContinuation ? 1 : 0);

        if (length == 0)
            return [];

        var texts = new string[length];
        var index = 0;

        for (var i = start; i < history.Count; i++)
            texts[index++] = history[i].Text;

        if (hasContinuation)
            texts[index] = continuation!;

        return texts;
    }

    private static void ActivateTriggered(List<TriggeredCandidate> candidates, bool[] active, string[] initialTexts)
    {
        var excluded = new bool[candidates.Count];
        List<string> waveTexts = [];

        for (var i = 0; i < candidates.Count; i++)
        {
            TriggeredCandidate candidate = candidates[i];

            if (MatchesAny(candidate.Negative, initialTexts))
            {
                excluded[i] = true;
                continue;
            }

            if (candidate.Positive.Length == 0 || MatchesAny(candidate.Positive, initialTexts))
            {
                active[candidate.Index] = true;

                if (candidate.Positive.Length > 0)
                    waveTexts.Add(candidate.Text);
            }
        }

        for (var pass = 1; pass < MaxCascadePasses && waveTexts.Count > 0; pass++)
        {
            var currentWave = waveTexts.ToArray();
            waveTexts.Clear();

            for (var i = 0; i < candidates.Count; i++)
            {
                TriggeredCandidate candidate = candidates[i];

                if (active[candidate.Index] || excluded[i])
                    continue;

                if (MatchesAny(candidate.Negative, currentWave))
                {
                    excluded[i] = true;
                    continue;
                }

                if (candidate.Positive.Length > 0 && MatchesAny(candidate.Positive, currentWave))
                {
                    active[candidate.Index] = true;
                    waveTexts.Add(candidate.Text);
                }
            }
        }
    }

    private static bool MatchesAny(string[] triggers, string[] texts)
    {
        if (triggers.Length == 0 || texts.Length == 0)
            return false;

        for (var i = 0; i < triggers.Length; i++)
        {
            ReadOnlySpan<char> pattern = triggers[i].AsSpan();

            for (var j = 0; j < texts.Length; j++)
            {
                if (MatchesTrigger(texts[j].AsSpan(), pattern))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Matches a trigger phrase in <paramref name="text"/>: case-insensitive, every word starting at a left word boundary, and ending at
    /// a right word boundary unless the pattern word ends with <c>*</c> (prefix match for inflected forms).
    /// </summary>
    private static bool MatchesTrigger(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        ReadOnlySpan<char> patternRest = pattern;

        if (!ReadNextPatternWord(ref patternRest, out ReadOnlySpan<char> firstStem, out var firstPrefix))
            return false;

        var searchFrom = 0;

        while (searchFrom < text.Length)
        {
            var offset = text[searchFrom..].IndexOf(firstStem, StringComparison.OrdinalIgnoreCase);

            if (offset < 0)
                return false;

            var pos = searchFrom + offset;
            searchFrom = pos + 1;

            if (pos > 0 && char.IsLetterOrDigit(text[pos - 1]))
                continue;

            var cursor = pos + firstStem.Length;

            if (!AdvanceWordEnd(text, ref cursor, firstPrefix))
                continue;

            if (MatchesRemainingWords(text, cursor, patternRest))
                return true;
        }

        return false;
    }

    private static bool ReadNextPatternWord(ref ReadOnlySpan<char> rest, out ReadOnlySpan<char> stem, out bool prefix)
    {
        while (!rest.IsEmpty && !char.IsLetterOrDigit(rest[0]))
            rest = rest[1..];

        if (rest.IsEmpty)
        {
            stem = [];
            prefix = false;
            return false;
        }

        var length = 0;

        while (length < rest.Length && char.IsLetterOrDigit(rest[length]))
            length++;

        stem = rest[..length];
        rest = rest[length..];
        prefix = !rest.IsEmpty && rest[0] == '*';

        while (!rest.IsEmpty && rest[0] == '*')
            rest = rest[1..];

        return true;
    }

    private static bool AdvanceWordEnd(ReadOnlySpan<char> text, ref int cursor, bool prefix)
    {
        if (prefix)
        {
            while (cursor < text.Length && char.IsLetterOrDigit(text[cursor]))
                cursor++;

            return true;
        }

        return cursor >= text.Length || !char.IsLetterOrDigit(text[cursor]);
    }

    private static bool MatchesRemainingWords(ReadOnlySpan<char> text, int cursor, ReadOnlySpan<char> patternRest)
    {
        while (ReadNextPatternWord(ref patternRest, out ReadOnlySpan<char> nextStem, out var nextPrefix))
        {
            var separated = false;

            while (cursor < text.Length && !char.IsLetterOrDigit(text[cursor]))
            {
                if (text[cursor] is '\n' or '\r')
                    return false;

                separated = true;
                cursor++;
            }

            if (!separated || !text[cursor..].StartsWith(nextStem, StringComparison.OrdinalIgnoreCase))
                return false;

            cursor += nextStem.Length;

            if (!AdvanceWordEnd(text, ref cursor, nextPrefix))
                return false;
        }

        return true;
    }

    private readonly record struct ParsedTriggers(string[] Positive, string[] Negative)
    {
        public bool IsConstant => Positive.Length == 0 && Negative.Length == 0;
    }

    private readonly record struct TriggeredCandidate(int Index, string Text, string[] Positive, string[] Negative);
}

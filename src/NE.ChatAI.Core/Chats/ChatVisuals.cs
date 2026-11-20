using System;
using System.Collections.Generic;

namespace NE.ChatAI.Chats;

/// <summary>Resolves and maintains timeline-bound <see cref="Chat.Visuals"/> across scenario commits, variant swipes, edits, rollbacks and branches.</summary>
public static class ChatVisuals
{
    /// <summary>Returns the canonical image key currently active for <paramref name="target"/> along <paramref name="messages"/>, or <c>null</c> when none is set or it was cleared.</summary>
    public static string? ResolveActive(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatVisual> visuals, ChatVisualTarget target)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(visuals);

        if (visuals.Count == 0)
            return null;

        Dictionary<string, int>? indexById = messages.Count > 0 ? ChatEditing.IndexMap(messages) : null;
        ChatVisual? best = null;
        var bestAnchor = int.MinValue;

        for (var i = 0; i < visuals.Count; i++)
        {
            ChatVisual visual = visuals[i];

            if (visual.Target != target || !TryResolveActiveAnchor(visual, indexById, messages, out var anchorIndex))
                continue;

            if (anchorIndex >= bestAnchor)
            {
                best = visual;
                bestAnchor = anchorIndex;
            }
        }

        return best is not null && !string.IsNullOrWhiteSpace(best.Image) ? best.Image : null;
    }

    private static bool TryResolveActiveAnchor(ChatVisual visual, Dictionary<string, int>? indexById, IReadOnlyList<ChatMessage> messages, out int anchorIndex)
    {
        if (visual.After is null)
        {
            anchorIndex = -1;
            return true;
        }

        if (indexById is not null && indexById.TryGetValue(visual.After, out var index) && IsVariantActive(visual, messages[index]))
        {
            anchorIndex = index;
            return true;
        }

        anchorIndex = int.MinValue;
        return false;
    }

    private static bool IsVariantActive(ChatVisual visual, ChatMessage message)
        => visual.Variant is not int variant || ChatEditing.Shown(message) == variant;

    /// <summary>Whether <paramref name="visual"/> stands on the active branch of <paramref name="messages"/>.</summary>
    public static bool IsActive(ChatVisual visual, IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(visual);
        ArgumentNullException.ThrowIfNull(messages);

        if (visual.After is null)
            return true;

        var at = ChatEditing.IndexOf(messages, visual.After);

        return at >= 0 && IsVariantActive(visual, messages[at]);
    }

    /// <summary>Commits <paramref name="written"/> visuals and <paramref name="generations"/> onto <paramref name="chat"/>, replacing any active visual of the same target at the same anchor.</summary>
    public static Chat PutMany(Chat chat, IReadOnlyList<ChatVisual> written, IReadOnlyList<GenerationRecord> generations)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(generations);

        if (written.Count == 0 && generations.Count == 0)
            return chat;

        IReadOnlyList<ChatVisual> allVisuals = written.Count == 0 ? chat.Visuals : MergeVisuals(chat.Messages, chat.Visuals, written);
        IReadOnlyList<GenerationRecord> allGenerations = generations.Count == 0 ? chat.Generations : [.. chat.Generations, .. generations];

        return chat with { Visuals = allVisuals, Generations = allGenerations };
    }

    private static List<ChatVisual> MergeVisuals(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatVisual> existing, IReadOnlyList<ChatVisual> written)
    {
        List<ChatVisual> visuals = [.. existing];
        Dictionary<string, int>? indexById = messages.Count > 0 ? ChatEditing.IndexMap(messages) : null;

        for (var i = 0; i < written.Count; i++)
        {
            ChatVisual incoming = NormalizeVisual(written[i], messages);

            if (TryResolveActiveAnchor(incoming, indexById, messages, out var incomingAnchor))
            {
                for (var j = visuals.Count - 1; j >= 0; j--)
                {
                    if (visuals[j].Target == incoming.Target
                        && TryResolveActiveAnchor(visuals[j], indexById, messages, out var existingAnchor)
                        && existingAnchor == incomingAnchor)
                    {
                        visuals.RemoveAt(j);
                    }
                }
            }

            visuals.Add(incoming);
        }

        return visuals;
    }

    private static ChatVisual NormalizeVisual(ChatVisual visual, IReadOnlyList<ChatMessage> messages)
    {
        var image = string.IsNullOrWhiteSpace(visual.Image) ? null : visual.Image.Trim();
        var variant = visual.Variant;

        if (variant is null && visual.After is string afterId)
        {
            var at = ChatEditing.IndexOf(messages, afterId);

            if (at >= 0)
                variant = ChatEditing.Shown(messages[at]);
        }
        else if (visual.After is null)
        {
            variant = null;
        }

        return string.Equals(visual.Image, image, StringComparison.Ordinal) && visual.Variant == variant
            ? visual
            : visual with { Image = image, Variant = variant };
    }

    internal static IReadOnlyList<ChatVisual> PinVariantVisuals(IReadOnlyList<ChatVisual> visuals, string messageId, int variant)
    {
        if (visuals.Count == 0)
            return visuals;

        List<ChatVisual>? changed = null;

        for (var i = 0; i < visuals.Count; i++)
        {
            ChatVisual visual = visuals[i];

            if (visual.Variant is not null || !string.Equals(visual.After, messageId, StringComparison.Ordinal))
                continue;

            changed ??= [.. visuals];
            changed[i] = visual with { Variant = variant };
        }

        return changed ?? visuals;
    }

    /// <summary>Removes visuals anchored at or after <paramref name="fromAt"/> when a message is refreshed.</summary>
    public static IReadOnlyList<ChatVisual> DropFrom(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatVisual> visuals, int fromAt)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(visuals);

        if (visuals.Count == 0 || fromAt < 0 || fromAt >= messages.Count)
            return visuals;

        Dictionary<string, int> indexById = ChatEditing.IndexMap(messages);
        List<ChatVisual>? kept = null;

        for (var i = 0; i < visuals.Count; i++)
        {
            ChatVisual visual = visuals[i];
            var drop = visual.After is string afterId
                && indexById.TryGetValue(afterId, out var afterIndex)
                && (afterIndex > fromAt || (afterIndex == fromAt && IsVariantActive(visual, messages[fromAt])));

            if (drop)
                kept ??= ChatEditing.CopyPrefix(visuals, i);
            else
                kept?.Add(visual);
        }

        return kept ?? visuals;
    }

    internal static IReadOnlyList<ChatVisual> OnMessageDeleted(IReadOnlyList<ChatMessage> before, IReadOnlyList<ChatVisual> visuals, int deletedAt, IReadOnlyList<ChatMessage> after)
    {
        if (visuals.Count == 0)
            return visuals;

        Dictionary<string, int> indexById = ChatEditing.IndexMap(before);
        HashSet<string> survivingIds = ChatEditing.CollectAllMessageIds(after);
        List<ChatVisual>? changed = null;

        for (var i = 0; i < visuals.Count; i++)
        {
            ChatVisual visual = visuals[i];
            var keep = visual.After is null
                || ((!indexById.TryGetValue(visual.After, out var afterIndex) || afterIndex != deletedAt) && survivingIds.Contains(visual.After));

            if (!keep)
                changed ??= ChatEditing.CopyPrefix(visuals, i);
            else
                changed?.Add(visual);
        }

        return changed ?? visuals;
    }

    internal static IReadOnlyList<ChatVisual> OnRolledBack(IReadOnlyList<ChatMessage> before, IReadOnlyList<ChatVisual> visuals, IReadOnlyList<ChatMessage> after, int? remadeAt = null)
    {
        if (visuals.Count == 0)
            return visuals;

        Dictionary<string, int> indexById = ChatEditing.IndexMap(before);
        HashSet<string> survivingIds = ChatEditing.CollectAllMessageIds(after);
        var cutoff = remadeAt ?? after.Count;
        List<ChatVisual>? changed = null;

        for (var i = 0; i < visuals.Count; i++)
        {
            ChatVisual visual = visuals[i];
            bool keep;

            if (visual.After is null)
            {
                keep = true;
            }
            else if (indexById.TryGetValue(visual.After, out var afterIndex))
            {
                keep = afterIndex < cutoff || (afterIndex == cutoff && remadeAt is int remade && !IsVariantActive(visual, before[remade]));
            }
            else
            {
                keep = survivingIds.Contains(visual.After);
            }

            if (!keep)
                changed ??= ChatEditing.CopyPrefix(visuals, i);
            else
                changed?.Add(visual);
        }

        return changed ?? visuals;
    }

    internal static IReadOnlyList<ChatVisual> OnBranchedUpTo(IReadOnlyList<ChatMessage> before, IReadOnlyList<ChatVisual> visuals, int upToAt, IReadOnlyList<ChatMessage> after)
    {
        if (visuals.Count == 0 || after.Count == 0)
            return [];

        Dictionary<string, int> indexById = ChatEditing.IndexMap(before);
        HashSet<string> survivingIds = ChatEditing.CollectAllMessageIds(after);
        List<ChatVisual> kept = [with(visuals.Count)];

        for (var i = 0; i < visuals.Count; i++)
        {
            ChatVisual visual = visuals[i];

            if (visual.After is null)
                kept.Add(visual);
            else if (indexById.TryGetValue(visual.After, out var afterIndex) && afterIndex <= upToAt)
                kept.Add(visual);
            else if (survivingIds.Contains(visual.After))
                kept.Add(visual);
        }

        return kept;
    }
}

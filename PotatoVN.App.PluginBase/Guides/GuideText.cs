using System;
using System.Collections.Generic;

namespace PotatoVN.App.PluginBase.Guides;

internal sealed class GuideText
{
    internal string Value { get; }
    internal IReadOnlyList<int> ParagraphStarts { get; }

    internal GuideText(string text)
    {
        Value = text;
        var starts = new List<int> { 0 };
        for (int i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] == '\n') starts.Add(i + 1);
        }
        ParagraphStarts = starts;
    }

    internal IReadOnlyList<int> Find(string query)
    {
        var matches = new List<int>();
        if (query.Length == 0) return matches;

        for (int start = 0; start < Value.Length;)
        {
            int match = Value.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
            if (match < 0) break;
            matches.Add(match);
            start = match + query.Length;
        }
        return matches;
    }

    internal GuideBookmark Bookmark(int paragraph, double progress) =>
        new(paragraph < 0 ? null : ParagraphKey(paragraph), paragraph, progress);

    internal int FindParagraph(GuideBookmark bookmark)
    {
        int preferred = bookmark.ParagraphIndex;
        if (bookmark.ParagraphKey is not { Length: > 0 } key) return -1;
        if ((uint)preferred < (uint)ParagraphStarts.Count && ParagraphKey(preferred) == key) return preferred;
        int nearest = -1;
        for (int i = 0; i < ParagraphStarts.Count; i++)
        {
            if (ParagraphKey(i) == key && (nearest < 0 || Math.Abs((long)i - preferred) < Math.Abs((long)nearest - preferred)))
                nearest = i;
        }
        return nearest;
    }

    private string ParagraphKey(int index)
    {
        int start = ParagraphStarts[index];
        int end = index + 1 < ParagraphStarts.Count ? ParagraphStarts[index + 1] - 1 : Value.Length;
        // 以完整段落的哈希定位，避免不同分支使用相同的段落开头。
        var hash = 2166136261u;
        for (int i = start; i < end; i++) hash = unchecked((hash ^ Value[i]) * 16777619u);
        return hash.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }
}

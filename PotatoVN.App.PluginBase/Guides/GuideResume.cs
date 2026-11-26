using System;
using System.Collections.Concurrent;
using System.Linq;

namespace PotatoVN.App.PluginBase.Guides;

public sealed record GuideResume(GuideSource Source, GuideReference? Article = null, Guid? SavedId = null,
    string? SavedUrl = null, CachedGuideText? Cached = null)
{
    internal static GuideResume Online(GuideReference article) => new(article.Source, article);

    internal static GuideResume Saved(SavedGuide guide) => new(GuideSource.SavedLinks, SavedId: guide.Id, SavedUrl: guide.Url);

    internal static GuideResume? FromLegacy(GameGuideHistory? history, LocalGuideMount? local, SavedGuide[] saved)
    {
        if (history?.Get(history.LastSource) is { } reading) return Online(reading.Article);
        if (local?.Bookmark is not null) return new GuideResume(GuideSource.Local);
        SavedGuide[] read = saved.Where(guide => guide.Bookmark is not null).Take(2).ToArray();
        if (read.Length == 1) return Saved(read[0]);
        return local is not null ? new GuideResume(GuideSource.Local) : null;
    }
}

public sealed record CachedGuideText(string Title, string Text, string? OriginalUrl, bool HasVisualContent,
    DateTimeOffset CachedAt)
{
    internal const int MaxArticleLength = 200_000;
    private const int MaxTotalLength = 500_000;
    private const int MaxCachedGames = 48;

    internal bool IsValid => !string.IsNullOrWhiteSpace(Text) && Text.Length <= MaxArticleLength &&
        Title is { Length: <= 320 } && OriginalUrl is null or { Length: <= 8192 };

    internal static CachedGuideText? Create(GuideArticle article, Uri? original)
    {
        if (article.Text.Length > MaxArticleLength) return null;
        string title = article.Title.Length <= 320 ? article.Title : article.Title[..320];
        string? url = original?.AbsoluteUri;
        return new CachedGuideText(title, article.Text, url is { Length: > 8192 } ? null : url,
            article.HasVisualContent, DateTimeOffset.UtcNow);
    }

    internal GuideArticle ToArticle() => new(Title, Text, HasVisualContent);

    internal Uri? GetOriginal() => GuideWebLinks.TryParse(OriginalUrl, out Uri url) ? url : null;

    internal static bool Trim(ConcurrentDictionary<Guid, GuideResume> readings)
    {
        int remaining = MaxTotalLength;
        int count = 0;
        bool changed = false;
        foreach (var (gameId, reading) in readings.Where(pair => pair.Value?.Cached is not null)
                     .OrderByDescending(pair => pair.Value.Cached!.CachedAt))
        {
            var cached = reading.Cached!;
            if (cached.IsValid && count < MaxCachedGames && cached.Text.Length <= remaining)
            {
                count++;
                remaining -= cached.Text.Length;
            }
            else
                changed |= readings.TryUpdate(gameId, reading with { Cached = null }, reading);
        }
        return changed;
    }
}

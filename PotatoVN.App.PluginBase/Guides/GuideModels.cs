using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PotatoVN.App.PluginBase.Guides;

public enum GuideSource { Df2an = 1, Ymgal = 2 }

public readonly record struct GuideReference(GuideSource Source, string Id)
{
    internal bool IsValid => (Source is GuideSource.Df2an or GuideSource.Ymgal) && GuideIds.IsValid(Id);
}

public sealed record GuideBookmark(string? ParagraphKey, int ParagraphIndex, double Progress);

public sealed record GuideReading(GuideReference Article, string? Title, GuideBookmark? Bookmark = null);

public sealed record GameGuideHistory(GuideReading? Df2an, GuideReading? Ymgal, GuideSource LastSource)
{
    internal GuideReading? Get(GuideSource source)
    {
        GuideReading? reading = source == GuideSource.Df2an ? Df2an : Ymgal;
        return reading is not null && reading.Article.IsValid && reading.Article.Source == source ? reading : null;
    }

    internal GameGuideHistory With(GuideReading reading) => reading.Article.Source == GuideSource.Df2an
        ? this with { Df2an = reading, LastSource = GuideSource.Df2an }
        : this with { Ymgal = reading, LastSource = GuideSource.Ymgal };
}

internal static class GuideHistory
{
    internal static void Migrate(ConcurrentDictionary<Guid, GameGuideHistory> readings,
        IEnumerable<KeyValuePair<Guid, string>> legacy)
    {
        foreach (var (gameId, url) in legacy)
        {
            if (GuideIds.FromLegacyTopic(url) is not { } id) continue;
            var reading = new GuideReading(new GuideReference(GuideSource.Df2an, id), null);
            readings.AddOrUpdate(gameId, new GameGuideHistory(reading, null, GuideSource.Df2an),
                (_, existing) => existing is null ? new GameGuideHistory(reading, null, GuideSource.Df2an) :
                    existing.Get(GuideSource.Df2an) is null ? existing with { Df2an = reading } : existing);
        }
    }

    internal static void Remember(ConcurrentDictionary<Guid, GameGuideHistory> readings, Guid gameId,
        GuideReading reading)
    {
        var empty = new GameGuideHistory(null, null, reading.Article.Source);
        readings.AddOrUpdate(gameId, empty.With(reading), (_, existing) => (existing ?? empty).With(reading));
    }

    internal static bool RememberPosition(ConcurrentDictionary<Guid, GameGuideHistory> readings, Guid gameId,
        GuideReference article, GuideBookmark bookmark)
    {
        while (readings.TryGetValue(gameId, out GameGuideHistory? history) &&
               history?.Get(article.Source) is { } prior && prior.Article == article && prior.Bookmark != bookmark)
        {
            var reading = prior with { Bookmark = bookmark };
            var updated = article.Source == GuideSource.Df2an
                ? history with { Df2an = reading }
                : history with { Ymgal = reading };
            if (readings.TryUpdate(gameId, updated, history)) return true;
        }
        return false;
    }
}

internal enum GuideIssue { None, NoResults, MissingGameId, Unavailable, LayoutChanged, ArticleMissing }

internal sealed record GuideResult<T>(T? Value, GuideIssue Issue, Uri? BrowserUrl) where T : class
{
    internal static GuideResult<T> Found(T value, Uri url) => new(value, GuideIssue.None, url);
    internal static GuideResult<T> Failed(GuideIssue issue, Uri? url) => new(null, issue, url);
}

internal sealed record GuideLink(string Id, string Title);

internal sealed record GuideArticle(string Title, string Text);

internal sealed record GuideGame(string? ChineseName, string? OriginalName, string? DisplayName, string? YmgalId)
{
    internal IEnumerable<string> SearchNames => new[] { ChineseName, OriginalName, DisplayName }
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase);
}

internal static class GuideIds
{
    internal static bool IsValid(string? id) =>
        id is { Length: > 0 and <= 20 } && id.All(char.IsAsciiDigit) && id.Any(c => c != '0');

    internal static string? FromPath(string path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string id = path[prefix.Length..].TrimEnd('/');
        return IsValid(id) ? id.TrimStart('0') : null;
    }

    internal static string? FromLegacyTopic(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.StartsWith('/')) return FromPath(url.Split('?', '#')[0], "/topics/");
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? address) && address.Scheme is "http" or "https"
            ? FromPath(address.AbsolutePath, "/topics/")
            : null;
    }
}

internal static class GuideSearch
{
    internal static GuideSource Preferred(GuideSource? setting, GameGuideHistory? history, GuideGame game)
    {
        if (setting is { } explicitSource) return explicitSource;
        if (history?.Get(history.LastSource) is { } reading) return reading.Article.Source;
        return GuideIds.IsValid(game.YmgalId) ? GuideSource.Ymgal : GuideSource.Df2an;
    }

    internal static GuideSource Alternate(GuideSource source) =>
        source == GuideSource.Df2an ? GuideSource.Ymgal : GuideSource.Df2an;

    internal static async Task<(GuideSource Source, GuideResult<IReadOnlyList<GuideLink>> Result,
        GuideResult<IReadOnlyList<GuideLink>>? Other)> FindAsync(
        GuideSource source, bool automatic,
        Func<GuideSource, CancellationToken, Task<GuideResult<IReadOnlyList<GuideLink>>>> search,
        CancellationToken cancellationToken)
    {
        var first = await search(source, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (first.Value is not null || !automatic) return (source, first, null);

        GuideSource alternate = Alternate(source);
        var second = await search(alternate, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (second.Value is not null) return (alternate, second, first);

        // 无编号只是备用来源不可用的原因，保留首选站点更有用的浏览器入口。
        return second.Issue == GuideIssue.MissingGameId
            ? (source, first, second)
            : (alternate, second, first);
    }

    internal static IReadOnlyList<GuideLink> RankTitles(IReadOnlyList<GuideLink> entries, GuideGame game)
    {
        var names = game.SearchNames.Select(NormalizeTitle).Where(name => name.Length > 0).ToArray();
        return entries.OrderBy(entry =>
        {
            string title = NormalizeTitle(entry.Title);
            if (names.Any(name => name.Equals(title, StringComparison.Ordinal))) return 0;
            return names.Any(name => title.Contains(name, StringComparison.Ordinal)) ? 1 : 2;
        }).ToArray();
    }

    internal static bool HasExactTitle(IReadOnlyList<GuideLink> entries, GuideGame game)
    {
        var names = game.SearchNames.Select(NormalizeTitle).Where(name => name.Length > 0).ToHashSet();
        return entries.Any(entry => names.Contains(NormalizeTitle(entry.Title)));
    }

    private static string NormalizeTitle(string title)
    {
        var normalized = new StringBuilder(title.Length);
        foreach (Rune rune in title.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune)) normalized.Append(Rune.ToUpperInvariant(rune));
        }
        return normalized.ToString();
    }
}

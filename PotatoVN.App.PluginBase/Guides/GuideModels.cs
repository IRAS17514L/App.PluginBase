using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PotatoVN.App.PluginBase.Guides;

public enum GuideSource { Df2an = 1, Ymgal = 2, Local = 3, Cngal = 4, Seiya = 5, SavedLinks = 6, Steam = 7, Aggregate = 8 }

public readonly record struct GuideReference(GuideSource Source, string Id)
{
    internal bool IsValid => Source switch
    {
        GuideSource.Df2an or GuideSource.Ymgal or GuideSource.Cngal or GuideSource.Steam => GuideIds.IsValid(Id),
        GuideSource.Seiya => GuideWebLinks.TryParse(Id, out Uri url) && SeiyaGuideSource.IsArticleUrl(url),
        _ => false,
    };
}

public sealed record GuideBookmark(string? ParagraphKey, int ParagraphIndex, double Progress);

public sealed record GuideReading(GuideReference Article, string? Title, GuideBookmark? Bookmark = null);

public sealed record GameGuideHistory(GuideReading? Df2an, GuideReading? Ymgal, GuideSource LastSource,
    GuideReading? Cngal = null, GuideReading? Seiya = null, GuideReading? Steam = null)
{
    internal GuideReading? Get(GuideSource source)
    {
        GuideReading? reading = source switch
        {
            GuideSource.Df2an => Df2an,
            GuideSource.Ymgal => Ymgal,
            GuideSource.Cngal => Cngal,
            GuideSource.Seiya => Seiya,
            GuideSource.Steam => Steam,
            _ => null,
        };
        return reading is not null && reading.Article.IsValid && reading.Article.Source == source ? reading : null;
    }

    internal GameGuideHistory With(GuideReading reading, bool makeLastSource = true)
    {
        GuideSource source = reading.Article.Source;
        GameGuideHistory updated = source switch
        {
            GuideSource.Df2an => this with { Df2an = reading },
            GuideSource.Ymgal => this with { Ymgal = reading },
            GuideSource.Cngal => this with { Cngal = reading },
            GuideSource.Seiya => this with { Seiya = reading },
            GuideSource.Steam => this with { Steam = reading },
            _ => throw new ArgumentOutOfRangeException(nameof(reading)),
        };
        return makeLastSource ? updated with { LastSource = source } : updated;
    }
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
            var updated = history.With(reading, makeLastSource: false);
            if (readings.TryUpdate(gameId, updated, history)) return true;
        }
        return false;
    }
}

internal enum GuideIssue { None, NoResults, MissingGameId, Unavailable, LayoutChanged, ArticleMissing, BrowserOnly }

internal sealed record GuideResult<T>(T? Value, GuideIssue Issue, Uri? BrowserUrl) where T : class
{
    internal static GuideResult<T> Found(T value, Uri url) => new(value, GuideIssue.None, url);
    internal static GuideResult<T> Failed(GuideIssue issue, Uri? url) => new(null, issue, url);
}

internal sealed record GuideLink(string Id, string Title);

internal sealed record GuideArticle(string Title, string Text, bool HasVisualContent = false);

internal sealed record GuideGame(string? ChineseName, string? OriginalName, string? DisplayName, string? YmgalId,
    string? CngalId = null, string? SteamId = null)
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
    internal static GuideSource? InitialSource(string setting, bool hasLocalMount) => setting switch
    {
        "2dfan" => GuideSource.Df2an,
        "ymgal" => GuideSource.Ymgal,
        "cngal" => GuideSource.Cngal,
        "seiya" => GuideSource.Seiya,
        "local-first" when hasLocalMount => GuideSource.Local,
        _ => null,
    };

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

    internal static string NormalizeTitle(string title)
    {
        var normalized = new StringBuilder(title.Length);
        foreach (Rune rune in title.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune)) normalized.Append(Rune.ToUpperInvariant(rune));
        }
        return normalized.ToString();
    }
}

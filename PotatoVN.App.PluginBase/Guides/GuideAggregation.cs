using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PotatoVN.App.PluginBase.Guides;

internal sealed record GuideSiteResults(GuideSource Source, IReadOnlyList<GuideLink> Links, GuideIssue Issue, Uri? BrowserUrl)
{
    internal static GuideSiteResults From(GuideSource source, GuideResult<IReadOnlyList<GuideLink>> result) =>
        new(source, result.Value ?? [], result.Issue, result.BrowserUrl);

    internal static GuideSiteResults From(GuideSource source, WebGuideResult result) =>
        new(source, result.Links, result.Issue, result.BrowseUrl);
}

internal sealed record RankedGuide(GuideSource Source, GuideLink Link, int Score, int SiteRank);

internal static class GuideAggregation
{
    internal static IReadOnlyList<GuideSource> OnlineSources { get; } = Array.AsReadOnly(
        new[] { GuideSource.Df2an, GuideSource.Ymgal, GuideSource.Cngal, GuideSource.Seiya, GuideSource.Steam });

    internal static GuideSource[] NormalizeSources(IEnumerable<GuideSource>? selected) =>
        selected is null ? [..OnlineSources] : OnlineSources.Intersect(selected).ToArray();

    private static readonly string[] GuideTerms =
    [
        "攻略", "全成就", "全结局", "全結局", "全路线", "全路線", "全流程", "选项", "選択肢", "通关指南",
        "好感度", "ルート", "チャート", "実績", "walkthrough", "achievement", "route order", "endings", "ruta", "прохождение",
    ];

    private static readonly string[] OtherTopics =
    [
        "评测", "評測", "杂谈", "雜談", "感想", "测评", "簡評", "简评", "试玩", "下载", "下載", "汉化", "漢化",
        "补丁", "補丁", "汉字化", "日本語化", "patch", "translation", "review", "modding", "steam deck",
    ];

    private static readonly string[] ConsoleVersions =
        ["Switch版", "Switch2版", "Switch 2版", "PS2版", "PS3版", "PS4版", "PS5版", "PSP版", "Vita版",
         "Xbox One版", "Xbox360版", "Xbox Series"];

    internal static Task<GuideSiteResults[]> SearchAsync(IEnumerable<GuideSource>? selected, bool includeSteam,
        Func<GuideSource, CancellationToken, Task<GuideSiteResults>> search, Action<GuideSource, Exception> logError,
        CancellationToken token) =>
        Task.WhenAll(NormalizeSources(selected).Where(source => includeSteam || source != GuideSource.Steam)
            .Select(source => SearchSiteAsync(source, search, logError, token)));

    private static async Task<GuideSiteResults> SearchSiteAsync(GuideSource source,
        Func<GuideSource, CancellationToken, Task<GuideSiteResults>> search, Action<GuideSource, Exception> logError,
        CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            return await search(source, timeout.Token).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return new GuideSiteResults(source, [], GuideIssue.Unavailable, null);
        }
        catch (Exception error)
        {
            token.ThrowIfCancellationRequested();
            logError(source, error);
            return new GuideSiteResults(source, [], GuideIssue.Unavailable, null);
        }
    }

    internal static IReadOnlyList<RankedGuide> Rank(GuideGame game, IEnumerable<GuideSiteResults> sites)
    {
        var names = game.SearchNames.Select(GuideSearch.NormalizeTitle).Where(title => title.Length > 0).ToArray();
        bool hasSteamEdition = GuideWebLinks.SteamGuides(game.SteamId) is not null;
        var ranked = new List<RankedGuide>();
        foreach (var site in sites)
        {
            var candidates = site.Links.DistinctBy(link => link.Id)
                .Select(link => (Link: link, Score: Score(site.Source, link.Title, names, hasSteamEdition)))
                .Where(item => item.Score >= 0)
                .OrderByDescending(item => item.Score);
            ranked.AddRange(candidates.Select((item, index) => new RankedGuide(site.Source, item.Link, item.Score, index)));
        }

        return ranked.OrderByDescending(item => item.Score).ThenBy(item => item.SiteRank).ToArray();
    }

    internal static IReadOnlyList<RankedGuide> BestMatches(IReadOnlyList<RankedGuide> ranked) =>
        ranked.Where(item => item.SiteRank < 3).Take(8).ToArray();

    internal static bool LooksLikeGuide(string title) => HasTerms(title, GuideTerms);

    private static int Score(GuideSource source, string title, string[] names, bool hasSteamEdition)
    {
        bool guide = LooksLikeGuide(title);
        if (!guide && HasTerms(title, OtherTopics)) return -1;
        string normalized = GuideSearch.NormalizeTitle(title);
        bool mentionsGame = names.Any(name => normalized.Contains(name, StringComparison.Ordinal));
        // 目录和词条已经确认游戏归属，标题可以只写路线名。
        bool knownGuide = source is GuideSource.Cngal or GuideSource.Seiya;
        int score = (guide || knownGuide ? 2 : 0) + (mentionsGame ? 1 : 0);
        if (source == GuideSource.Seiya && hasSteamEdition && HasTerms(title, ConsoleVersions) &&
            !title.Contains("Steam", StringComparison.OrdinalIgnoreCase) && !title.Contains("PC", StringComparison.OrdinalIgnoreCase))
            score = Math.Max(0, score - 2);
        return score;
    }

    private static bool HasTerms(string title, string[] terms) =>
        terms.Any(term => title.Contains(term, StringComparison.OrdinalIgnoreCase));
}

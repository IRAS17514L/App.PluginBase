using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;

namespace PotatoVN.App.PluginBase.Guides;

internal sealed class SeiyaGuideSource(GuidePageClient pages)
{
    internal static readonly Uri Main = new("https://seiya-saiga.com/game/kouryaku.html");
    internal static readonly Uri AllAges = new("https://galge.seiya-saiga.com/game/galge.html");
    private static readonly Encoding CatalogEncoding = CodePagesEncodingProvider.Instance.GetEncoding(932)!;
    private static readonly Regex GuideSuffix = new(@"\s*(?:[:：]\s*)?攻略\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex PlatformSuffix = new(
        @"(?:[\s(（、，,/＆&]+(?:Nintendo\s+)?(?:Switch(?:\s*[2２])?|Steam|PS\s*[345P]|(?:PS\s*)?Vita|PC)\s*(?:版(?:対応)?)?\s*[)）]*)+\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal async Task<WebGuideResult> SearchAsync(GuideGame game, CancellationToken token)
    {
        if (!game.SearchNames.Any()) return WebGuideResult.FromLinks([], Main);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var catalogs = await Task.WhenAll(ReadCatalogAsync(Main, game, timeout.Token),
            ReadCatalogAsync(AllAges, game, timeout.Token));
        token.ThrowIfCancellationRequested();

        var matches = catalogs.SelectMany(result => result.Links).DistinctBy(link => link.Id).ToArray();
        if (matches.Length > 0) return WebGuideResult.FromLinks(GuideSearch.RankTitles(matches, game), Main);
        var failure = catalogs.FirstOrDefault(result => result.Issue != GuideIssue.NoResults);
        return failure ?? WebGuideResult.FromLinks([], Main);
    }

    private async Task<WebGuideResult> ReadCatalogAsync(Uri index, GuideGame game, CancellationToken token)
    {
        try
        {
            string html = await pages.GetAsync(index, token, CatalogEncoding);
            return await Task.Run(() => ParseIndex(html, index, game), token);
        }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
        {
            return WebGuideResult.Failure(GuideIssue.Unavailable, index);
        }
    }

    internal static WebGuideResult ParseIndex(string html, Uri index, GuideGame game)
    {
        var names = game.SearchNames.Select(GuideSearch.NormalizeTitle).Where(name => name.Length > 0).ToHashSet();
        var guides = new Dictionary<string, GuideLink>();
        bool foundIndex = false;
        foreach (var link in GuideHtml.Parse(html).QuerySelectorAll("a[href]"))
        {
            if (!Uri.TryCreate(index, link.GetAttribute("href"), out Uri? url) ||
                !GuideWebLinks.TryParse(url.AbsoluteUri, out _) || !url.IsDefaultPort ||
                (url.Host != Main.Host && url.Host != AllAges.Host) ||
                !url.AbsolutePath.StartsWith("/game/", StringComparison.OrdinalIgnoreCase) ||
                !url.AbsolutePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                (url.AbsolutePath == Main.AbsolutePath || url.AbsolutePath == AllAges.AbsolutePath)) continue;
            foundIndex = true;
            string title = GuideSuffix.Replace(link.TextContent.Trim(), "").Trim();
            if (!names.Contains(GuideSearch.NormalizeTitle(title)) &&
                !names.Contains(GuideSearch.NormalizeTitle(PlatformSuffix.Replace(title, "")))) continue;
            var canonical = new UriBuilder(url) { Scheme = "https", Port = -1, Query = "", Fragment = "" }.Uri;
            if (!guides.TryGetValue(canonical.AbsoluteUri, out GuideLink? existing) ||
                names.Contains(GuideSearch.NormalizeTitle(title)) && !names.Contains(GuideSearch.NormalizeTitle(existing.Title)))
                guides[canonical.AbsoluteUri] = new GuideLink(canonical.AbsoluteUri, title);
        }

        return foundIndex
            ? WebGuideResult.FromLinks(guides.Values.ToArray(), index)
            : WebGuideResult.Failure(GuideIssue.LayoutChanged, index);
    }
}

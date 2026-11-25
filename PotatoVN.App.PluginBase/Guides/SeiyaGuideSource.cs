using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace PotatoVN.App.PluginBase.Guides;

internal sealed class SeiyaGuideSource(GuidePageClient pages)
{
    internal static readonly Uri Main = new("https://seiya-saiga.com/game/kouryaku.html");
    internal static readonly Uri AllAgesPc = new("https://galge.seiya-saiga.com/game/kouryaku.html");
    internal static readonly Uri AllAges = new("https://galge.seiya-saiga.com/game/galge.html");
    private static readonly Encoding CatalogEncoding = CodePagesEncodingProvider.Instance.GetEncoding(932)!;
    private static readonly Regex MetaTag = new(@"<meta\b[^>]*>", RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex GuideSuffix = new(@"\s*(?:[:：]\s*)?攻略\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex PlatformSuffix = new(
        @"(?:[\s(、,/&]+(?:(?:for\s+)?Windows\s+PC|(?:Nintendo\s+)?Switch(?:\s*2)?|Steam|PS\s*[2345P]|(?:PS\s*)?Vita|Xbox\s*(?:360|One|Series\s*[XS])|PC)\s*(?:版(?:対応)?)?\s*\)*)+\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex KanaReading = new(
        @"^(?<title>[\x20-\x7E]+)\((?<reading>[\p{IsKatakana}\s]{4,})\)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal async Task<WebGuideResult> SearchAsync(GuideGame game, CancellationToken token)
    {
        if (!game.SearchNames.Any()) return WebGuideResult.FromLinks([], Main);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var catalogs = await Task.WhenAll(ReadCatalogAsync(Main, game, timeout.Token),
            ReadCatalogAsync(AllAgesPc, game, timeout.Token), ReadCatalogAsync(AllAges, game, timeout.Token));
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

    internal static bool IsArticleUrl(Uri url) =>
        url.Scheme is "http" or "https" && url.IsDefaultPort && url.UserInfo.Length == 0 &&
        (url.Host == Main.Host || url.Host == AllAges.Host) &&
        url.AbsolutePath.StartsWith("/game/", StringComparison.OrdinalIgnoreCase) &&
        url.AbsolutePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
        !url.AbsolutePath.Equals(Main.AbsolutePath, StringComparison.OrdinalIgnoreCase) &&
        !url.AbsolutePath.Equals(AllAgesPc.AbsolutePath, StringComparison.OrdinalIgnoreCase) &&
        !url.AbsolutePath.Equals(AllAges.AbsolutePath, StringComparison.OrdinalIgnoreCase);

    internal static Uri CanonicalUrl(Uri url) =>
        new UriBuilder(url) { Scheme = "https", Port = -1, Query = "", Fragment = "" }.Uri;

    internal async Task<GuideResult<GuideArticle>> ReadArticleAsync(string id, string title, CancellationToken token)
    {
        if (!GuideWebLinks.TryParse(id, out Uri address) || !IsArticleUrl(address))
            return GuideResult<GuideArticle>.Failed(GuideIssue.ArticleMissing, null);

        address = CanonicalUrl(address);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var visited = new HashSet<Uri>();
            while (visited.Count < 3 && visited.Add(address))
            {
                string html = await pages.GetAsync(address, timeout.Token, CatalogEncoding);
                if (RefreshTarget(html, address) is not { } target)
                    return await Task.Run(() => ParseArticle(GuideHtml.Parse(html), address, title), timeout.Token);
                address = target;
            }
            return GuideResult<GuideArticle>.Failed(GuideIssue.LayoutChanged, address);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return GuideResult<GuideArticle>.Failed(GuideIssue.ArticleMissing, address);
        }
        catch (Exception error) when (error is IOException or HttpRequestException ||
                                      error is OperationCanceledException && !token.IsCancellationRequested)
        {
            return GuideResult<GuideArticle>.Failed(GuideIssue.Unavailable, address);
        }
    }

    private static Uri? RefreshTarget(string html, Uri address)
    {
        // 有些旧跳转页的 title 已损坏，整页解析会把后面的 meta 当成标题。
        int headerEnd = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        string header = html[..Math.Min(headerEnd >= 0 ? headerEnd : html.Length, 8192)];
        foreach (Match tag in MetaTag.Matches(header))
        {
            if (!tag.Value.Contains("refresh", StringComparison.OrdinalIgnoreCase)) continue;
            var meta = GuideHtml.Parse(tag.Value).QuerySelector("meta");
            if (!string.Equals(meta?.GetAttribute("http-equiv"), "refresh", StringComparison.OrdinalIgnoreCase)) continue;
            string? content = meta?.GetAttribute("content");
            int marker = content?.IndexOf("url=", StringComparison.OrdinalIgnoreCase) ?? -1;
            if (marker < 0 || !Uri.TryCreate(address, content![(marker + 4)..].Trim().Trim('"', '\''), out Uri? target) ||
                !IsArticleUrl(target)) continue;

            // 旧站留下的部分跳转重复拼接了 /game/。
            if (target.AbsolutePath.StartsWith("/game/game/", StringComparison.OrdinalIgnoreCase))
                target = new Uri(target, target.AbsolutePath[5..]);
            return CanonicalUrl(target);
        }
        return null;
    }

    private static GuideResult<GuideArticle> ParseArticle(IHtmlDocument page, Uri address, string fallbackTitle)
    {
        string? title = page.Title?.Trim();
        if (page.Body is not { } body || title is null || !title.Contains("攻略", StringComparison.Ordinal) ||
            body.QuerySelector("table") is null)
            return GuideResult<GuideArticle>.Failed(GuideIssue.LayoutChanged, address);

        FlattenRoutes(page);
        return GuideHtml.ArticleFromBody(body, string.IsNullOrWhiteSpace(fallbackTitle) ? title : fallbackTitle, address);
    }

    private static void FlattenRoutes(IHtmlDocument page)
    {
        foreach (var table in page.QuerySelectorAll<IHtmlTableElement>("table").Reverse())
        {
            var rows = table.Rows.ToArray();
            for (int i = 0; i + 1 < rows.Length; i++)
            {
                var headingRow = rows[i];
                var detailRow = rows[i + 1];
                var headings = headingRow.Cells.ToArray();
                var details = detailRow.Cells.ToArray();
                if (headings.Length < 2 || headings.Length != details.Length ||
                    headings.Any(cell => cell.ColumnSpan != 1 || cell.RowSpan != 1 ||
                        cell.QuerySelector("table, br, input") is not null || cell.TextContent.Trim().Length is 0 or > 120) ||
                    details.Any(cell => cell.ColumnSpan != 1 || cell.RowSpan != 1 || cell.QuerySelector("table") is not null) ||
                    !details.Any(cell => cell.QuerySelector("input[type=checkbox], br") is not null)) continue;

                // 网站按列排角色路线。每个标题后接同列正文，便于在窄窗连续阅读。
                var replacement = page.CreateElement("tr");
                var cellBody = page.CreateElement("td");
                replacement.AppendChild(cellBody);
                for (int column = 0; column < headings.Length; column++)
                {
                    var heading = page.CreateElement("h3");
                    heading.TextContent = GuideHtml.ToText(headings[column]);
                    var text = page.CreateElement("p");
                    text.TextContent = GuideHtml.ToText(details[column]);
                    cellBody.AppendChild(heading);
                    cellBody.AppendChild(text);
                }
                headingRow.Parent!.InsertBefore(replacement, headingRow);
                headingRow.Parent!.RemoveChild(headingRow);
                detailRow.Parent!.RemoveChild(detailRow);
                i++;
            }
        }
    }

    private static IEnumerable<string> TitleKeys(string title)
    {
        yield return GuideSearch.NormalizeTitle(title);
        string withPlatformSpacing = title.Normalize(NormalizationForm.FormKC).Replace(")", ") ");
        string baseTitle = PlatformSuffix.Replace(withPlatformSpacing, "").Trim();
        yield return GuideSearch.NormalizeTitle(baseTitle);

        // 罗马字标题后有时附带片假名读法，保留两种查找方式。
        Match alias = KanaReading.Match(baseTitle);
        if (!alias.Success) yield break;
        yield return GuideSearch.NormalizeTitle(alias.Groups["title"].Value);
        yield return GuideSearch.NormalizeTitle(alias.Groups["reading"].Value);
    }

    internal static WebGuideResult ParseIndex(string html, Uri index, GuideGame game)
    {
        var names = game.SearchNames.SelectMany(TitleKeys).Where(name => name.Length > 0).ToHashSet();
        var guides = new Dictionary<string, GuideLink>();
        bool foundIndex = false;
        foreach (var link in GuideHtml.Parse(html).QuerySelectorAll("a[href]"))
        {
            if (!Uri.TryCreate(index, link.GetAttribute("href"), out Uri? url) || !IsArticleUrl(url)) continue;
            foundIndex = true;
            string title = GuideSuffix.Replace(link.TextContent.Trim(), "").Trim();
            if (!TitleKeys(title).Any(names.Contains)) continue;
            var canonical = CanonicalUrl(url);
            if (!guides.TryGetValue(canonical.AbsoluteUri, out GuideLink? existing) ||
                names.Contains(GuideSearch.NormalizeTitle(title)) && !names.Contains(GuideSearch.NormalizeTitle(existing.Title)))
                guides[canonical.AbsoluteUri] = new GuideLink(canonical.AbsoluteUri, title);
        }

        return foundIndex
            ? WebGuideResult.FromLinks(guides.Values.ToArray(), index)
            : WebGuideResult.Failure(GuideIssue.LayoutChanged, index);
    }
}

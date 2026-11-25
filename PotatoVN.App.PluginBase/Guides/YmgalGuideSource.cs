using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace PotatoVN.App.PluginBase.Guides;

internal sealed class YmgalGuideSource(GuidePageClient pages)
{
    internal static readonly Uri BaseUrl = new("https://www.ymgal.games");

    internal async Task<GuideResult<IReadOnlyList<GuideLink>>> SearchAsync(GuideGame game, CancellationToken token)
    {
        if (GuideIds.IsValid(game.YmgalId))
            return await ReadPageAsync($"/ga{game.YmgalId}", GuideIssue.NoResults, ParseArticles, token);

        var names = game.SearchNames.Select(GuideSearch.NormalizeTitle).Where(name => name.Length > 0).ToHashSet();
        var last = GuideResult<IReadOnlyList<GuideLink>>.Failed(GuideIssue.MissingGameId, BaseUrl);
        foreach (string name in game.SearchNames)
        {
            string searchPath = $"/search?type=ga&keyword={Uri.EscapeDataString(name)}";
            var archives = await ReadPageAsync(searchPath, GuideIssue.NoResults,
                (page, url) => ParseArchives(page, url, names), token);
            if (archives.Value is [{ Id: var id }])
                return await ReadPageAsync($"/ga{id}", GuideIssue.NoResults, ParseArticles, token);
            if (archives.Issue == GuideIssue.Unavailable) return archives;
            if (last.Issue != GuideIssue.LayoutChanged) last = archives;
        }
        return last;
    }

    private static GuideResult<IReadOnlyList<GuideLink>> ParseArchives(
        IHtmlDocument page, Uri url, HashSet<string> names)
    {
        var matches = page.QuerySelectorAll("#yms-search-result a.game-view-card[href]")
            .Where(card => card.QuerySelectorAll("h3, .subtitle")
                .Any(title => names.Contains(GuideSearch.NormalizeTitle(title.TextContent))))
            .Select(card => Uri.TryCreate(url, card.GetAttribute("href"), out Uri? address) &&
                IsSite(address) ? GuideIds.FromPath(address.AbsolutePath, "/ga") : null)
            .OfType<string>().Distinct().Take(2).ToArray();
        if (matches is [var id])
            return GuideResult<IReadOnlyList<GuideLink>>.Found([new GuideLink(id, "")], url);
        return GuideResult<IReadOnlyList<GuideLink>>.Failed(
            page.QuerySelector("#yms-search-result") is not null ? GuideIssue.NoResults : GuideIssue.LayoutChanged, url);
    }

    internal Task<GuideResult<GuideArticle>> ReadArticleAsync(string id, string title, CancellationToken token) =>
        ReadPageAsync($"/co/article/{id}", GuideIssue.ArticleMissing,
            (page, url) => GuideHtml.Article(page, ".article-content", "h1, .article-title", title, url), token);

    private async Task<GuideResult<T>> ReadPageAsync<T>(string path, GuideIssue missing,
        Func<IHtmlDocument, Uri, GuideResult<T>> parse, CancellationToken token) where T : class
    {
        var url = new Uri(BaseUrl, path);
        try
        {
            string html = await pages.GetAsync(url, token);
            return await Task.Run(() => parse(GuideHtml.Parse(html), url), token);
        }
        catch (HttpRequestException error)
        {
            token.ThrowIfCancellationRequested();
            return GuideResult<T>.Failed(error.StatusCode == HttpStatusCode.NotFound ? missing : GuideIssue.Unavailable, url);
        }
        catch (IOException)
        {
            token.ThrowIfCancellationRequested();
            return GuideResult<T>.Failed(GuideIssue.Unavailable, url);
        }
    }

    private static GuideResult<IReadOnlyList<GuideLink>> ParseArticles(IHtmlDocument page, Uri url)
    {
        var links = GuideHtml.Links(page, ".article-item a.article-title[href]", "/co/article/", url, IsSite);
        if (links.Count > 0) return GuideResult<IReadOnlyList<GuideLink>>.Found(links, url);
        bool recognized = GuideHtml.HasEmptyNotice(page) ||
            page.QuerySelector(".game-detail, .game-info, .game-detail-wrap, .game-info-content") is not null &&
            page.QuerySelector(".article-item") is null;
        return GuideResult<IReadOnlyList<GuideLink>>.Failed(
            recognized ? GuideIssue.NoResults : GuideIssue.LayoutChanged, url);
    }

    private static bool IsSite(Uri url) => url.Host is "www.ymgal.games" or "ymgal.games";
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Html.Dom;

namespace PotatoVN.App.PluginBase.Guides;

internal sealed class YmgalGuideSource(GuidePageClient pages)
{
    private static readonly Uri BaseUrl = new("https://www.ymgal.games");

    internal Task<GuideResult<IReadOnlyList<GuideLink>>> SearchAsync(GuideGame game, CancellationToken token)
    {
        if (!GuideIds.IsValid(game.YmgalId))
            return Task.FromResult(GuideResult<IReadOnlyList<GuideLink>>.Failed(GuideIssue.MissingGameId, new Uri(
                BaseUrl, $"/search?keyword={Uri.EscapeDataString(game.SearchNames.FirstOrDefault() ?? "")}")));
        return ReadPageAsync($"/ga{game.YmgalId}", GuideIssue.NoResults, ParseArticles, token);
    }

    internal Task<GuideResult<GuideArticle>> ReadArticleAsync(string id, string title, CancellationToken token) =>
        ReadPageAsync($"/co/article/{id}", GuideIssue.ArticleMissing,
            (page, url) => GuideHtml.Article(page, ".article-content", "h1, .article-title", title, url), token);

    internal void ClearCache() => pages.Clear(IsSite);

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
            page.QuerySelector(".game-detail, .game-info") is not null &&
            page.QuerySelector(".article-item") is null;
        return GuideResult<IReadOnlyList<GuideLink>>.Failed(
            recognized ? GuideIssue.NoResults : GuideIssue.LayoutChanged, url);
    }

    private static bool IsSite(Uri url) => url.Host is "www.ymgal.games" or "ymgal.games";
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Html.Dom;

namespace PotatoVN.App.PluginBase.Guides;

internal sealed class Df2anGuideSource(GuidePageClient pages, Func<string> getDomain, Action<string> setDomain)
{
    internal const string DefaultDomain = "https://2dfan.com";
    internal static IReadOnlyList<string> KnownDomains { get; } =
        [DefaultDomain, "https://2dfdf.de", "https://2dfmax.top", "https://fan2d.top", "https://acgfan.top"];
    private static readonly HashSet<string> KnownHosts = KnownDomains.SelectMany(domain =>
    {
        string host = new Uri(domain).Host;
        return new[] { host, $"www.{host}" };
    }).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal async Task<GuideResult<IReadOnlyList<GuideLink>>> SearchAsync(GuideGame game, CancellationToken token)
    {
        GuideResult<IReadOnlyList<GuideLink>>? layoutError = null;
        GuideResult<IReadOnlyList<GuideLink>>? last = null;
        var candidates = new Dictionary<string, GuideLink>();
        Uri? resultsUrl = null;
        foreach (string title in game.SearchNames)
        {
            string path = $"/subjects/search?keyword={Uri.EscapeDataString(title)}";
            var result = await ReadPageAsync(path, GuideIssue.NoResults, ParseSubjects, token);
            if (result.Value is { } links)
            {
                foreach (GuideLink link in links) candidates.TryAdd(link.Id, link);
                resultsUrl = result.BrowserUrl;
                if (GuideSearch.HasExactTitle(links, game)) break;
            }
            if (result.Issue == GuideIssue.Unavailable)
            {
                last = result;
                break;
            }
            if (result.Issue == GuideIssue.LayoutChanged) layoutError ??= result;
            last = result;
        }
        if (resultsUrl is not null)
            return GuideResult<IReadOnlyList<GuideLink>>.Found(
                GuideSearch.RankTitles(candidates.Values.ToArray(), game), resultsUrl);
        return layoutError ?? last ?? GuideResult<IReadOnlyList<GuideLink>>.Failed(
            GuideIssue.NoResults, OnDomain(getDomain(), "/subjects"));
    }

    internal async Task<GuideSiteResults> SearchArticlesAsync(GuideGame names, CancellationToken token)
    {
        var subjects = await SearchAsync(names, token);
        if (subjects.Value is not { } matches) return GuideSiteResults.From(GuideSource.Df2an, subjects);

        var exact = matches.Where(link => GuideSearch.HasExactTitle([link], names)).Take(3).ToArray();
        if (exact.Length == 0)
            return new GuideSiteResults(GuideSource.Df2an, [], GuideIssue.NoResults, subjects.BrowserUrl);

        var lists = await Task.WhenAll(exact.Select(subject => GetTopicsAsync(subject.Id, token)));
        var links = lists.Where(result => result.Value is not null).SelectMany(result => result.Value!).ToArray();
        if (links.Length > 0)
            return new GuideSiteResults(GuideSource.Df2an, links, GuideIssue.None, lists[0].BrowserUrl);
        var failure = lists.FirstOrDefault(result => result.Issue is GuideIssue.Unavailable or GuideIssue.LayoutChanged) ?? lists[0];
        return GuideSiteResults.From(GuideSource.Df2an, failure);
    }

    internal Task<GuideResult<IReadOnlyList<GuideLink>>> GetTopicsAsync(string subjectId, CancellationToken token) =>
        ReadPageAsync($"/subjects/{subjectId}", GuideIssue.NoResults, ParseTopics, token);

    internal Task<GuideResult<GuideArticle>> ReadArticleAsync(string id, string title, CancellationToken token) =>
        ReadPageAsync($"/topics/{id}", GuideIssue.ArticleMissing,
            (page, url) => GuideHtml.Article(page, ".topic-content", "h1, .topic-title", title, url), token);

    internal async Task<string?> ProbeAsync(CancellationToken token, string? excludedDomain = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        List<Task<string?>> probes = KnownDomains
            .Where(domain => !string.Equals(domain, excludedDomain, StringComparison.OrdinalIgnoreCase))
            .Select(async domain => await pages.ProbeAsync(SubjectsPage(domain), timeout.Token) ? domain : null)
            .ToList();

        while (probes.Count > 0)
        {
            Task<string?> completed = await Task.WhenAny(probes);
            probes.Remove(completed);
            string? domain = await completed;
            token.ThrowIfCancellationRequested();
            if (domain is not null)
            {
                await timeout.CancelAsync();
                return domain;
            }
        }
        token.ThrowIfCancellationRequested();
        return null;
    }

    private async Task<GuideResult<T>> ReadPageAsync<T>(string path, GuideIssue missing,
        Func<IHtmlDocument, Uri, GuideResult<T>> parse, CancellationToken token) where T : class
    {
        string initialDomain = getDomain().TrimEnd('/');
        Uri url = OnDomain(initialDomain, path);
        try
        {
            string html;
            string? mirror = null;
            try
            {
                html = await pages.GetAsync(url, token);
            }
            catch (HttpRequestException error) when (MayUseMirror(error))
            {
                mirror = await ProbeAsync(token, initialDomain);
                if (mirror is null) throw;
                url = OnDomain(mirror, path);
                html = await pages.GetAsync(url, token);
            }

            GuideResult<T> result = await Task.Run(() => parse(GuideHtml.Parse(html), url), token);
            token.ThrowIfCancellationRequested();
            if (mirror is not null && result.Issue != GuideIssue.LayoutChanged &&
                string.Equals(getDomain().TrimEnd('/'), initialDomain, StringComparison.OrdinalIgnoreCase))
                setDomain(mirror);
            return result;
        }
        catch (HttpRequestException error)
        {
            token.ThrowIfCancellationRequested();
            return GuideResult<T>.Failed(error.StatusCode == HttpStatusCode.NotFound ? missing : GuideIssue.Unavailable, url);
        }
        catch (System.IO.IOException)
        {
            token.ThrowIfCancellationRequested();
            return GuideResult<T>.Failed(GuideIssue.Unavailable, url);
        }
    }

    private static GuideResult<IReadOnlyList<GuideLink>> ParseSubjects(IHtmlDocument page, Uri url)
    {
        string selector = page.QuerySelector(".nexus-subjects") is not null
            ? ".subject-card-title a[href]" : "a[href]";
        var links = GuideHtml.Links(page, selector, "/subjects/", url, link => link.Host == url.Host || IsKnownHost(link));
        return Results(links, GuideHtml.HasEmptyNotice(page), url);
    }

    private static GuideResult<IReadOnlyList<GuideLink>> ParseTopics(IHtmlDocument page, Uri url)
    {
        string selector = page.QuerySelector(".nexus-right-sidebar") is not null
            ? ".nexus-right-sidebar a.related-link[href]" : "a[href]";
        var links = GuideHtml.Links(page, selector, "/topics/", url, link => link.Host == url.Host || IsKnownHost(link));
        bool recognized = GuideHtml.HasEmptyNotice(page) ||
            page.QuerySelector(".subject-info, #subject-info, h1.subject-title") is not null;
        return Results(links, recognized, url);
    }

    private static GuideResult<IReadOnlyList<GuideLink>> Results(IReadOnlyList<GuideLink> links, bool empty, Uri url) =>
        links.Count > 0 ? GuideResult<IReadOnlyList<GuideLink>>.Found(links, url) :
            GuideResult<IReadOnlyList<GuideLink>>.Failed(empty ? GuideIssue.NoResults : GuideIssue.LayoutChanged, url);

    private static bool MayUseMirror(HttpRequestException error) =>
        error.StatusCode is null or HttpStatusCode.Forbidden or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)error.StatusCode.Value >= 500;

    internal static bool IsKnownHost(Uri url) => KnownHosts.Contains(url.Host);

    internal static Uri SubjectsPage(string domain) => OnDomain(domain, "/subjects");

    private static Uri OnDomain(string domain, string path) => new(domain.TrimEnd('/') + path);
}

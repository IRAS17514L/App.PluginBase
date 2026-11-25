using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PotatoVN.App.PluginBase.Guides;

internal sealed class CngalGuideSource(GuidePageClient pages)
{
    private static readonly Uri Api = new("https://api.cngal.org/");
    internal static readonly Uri Site = new("https://www.cngal.org/");
    internal static readonly Uri ConnectionCheck = new(Api, "api/home/Search?Types=Game&Text=CLANNAD");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Entry(int Id, string? Name);
    private sealed record Hit(Entry? Entry);
    private sealed record SearchPage(Hit[]? Data);
    private sealed record SearchPayload(SearchPage? PagedResultDto);
    private sealed record Article(int Id, string? Type, string? Name, string? DisplayName);
    private sealed record EntryPayload(string? Type, Article[]? ArticleRelevances);

    internal async Task<WebGuideResult> SearchAsync(GuideGame game, CancellationToken token)
    {
        Uri browse = Site;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            string? id = GuideIds.IsValid(game.CngalId) ? game.CngalId!.TrimStart('0') : null;
            id ??= await FindGameAsync(game, timeout.Token);
            if (id is null) return WebGuideResult.FromLinks([], browse);

            browse = new Uri(Site, $"entries/index/{id}");
            var entry = await ReadAsync<EntryPayload>($"api/entries/GetEntryView/{id}", timeout.Token);
            if (entry?.Type is null) return WebGuideResult.Failure(GuideIssue.LayoutChanged, browse);
            if (entry.Type != "Game") return WebGuideResult.FromLinks([], browse);
            var links = new List<GuideLink>();
            var seen = new HashSet<int>();
            foreach (Article article in entry.ArticleRelevances ?? [])
            {
                string? title = string.IsNullOrWhiteSpace(article.DisplayName) ? article.Name : article.DisplayName;
                if (article.Id <= 0 || string.IsNullOrWhiteSpace(title) ||
                    (article.Type != "Strategy" && !title.Contains("攻略", StringComparison.Ordinal)) ||
                    !seen.Add(article.Id)) continue;
                var url = new Uri(Site, $"articles/index/{article.Id}");
                links.Add(new GuideLink(url.AbsoluteUri, title.Trim()));
            }

            return WebGuideResult.FromLinks(links, browse);
        }
        catch (JsonException) { return WebGuideResult.Failure(GuideIssue.LayoutChanged, browse); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return WebGuideResult.FromLinks([], browse);
        }
        catch (Exception error) when (error is IOException or HttpRequestException ||
                                      error is OperationCanceledException && !token.IsCancellationRequested)
        {
            return WebGuideResult.Failure(GuideIssue.Unavailable, browse);
        }
    }

    private async Task<string?> FindGameAsync(GuideGame game, CancellationToken token)
    {
        foreach (string name in game.SearchNames)
        {
            string normalized = GuideSearch.NormalizeTitle(name);
            if (normalized.Length == 0) continue;
            var response = await ReadAsync<SearchPayload>(
                $"api/home/Search?Types=Game&Text={Uri.EscapeDataString(name)}", token);
            var exact = (response?.PagedResultDto?.Data ?? throw new JsonException())
                .Select(hit => hit.Entry)
                .OfType<Entry>()
                .Where(entry => entry.Id > 0 && entry.Name is not null && GuideSearch.NormalizeTitle(entry.Name) == normalized)
                .Select(entry => entry.Id)
                .Distinct()
                .Take(2)
                .ToArray();
            if (exact.Length == 1) return exact[0].ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return null;
    }

    private async Task<T?> ReadAsync<T>(string path, CancellationToken token) =>
        JsonSerializer.Deserialize<T>(await pages.GetAsync(new Uri(Api, path), token), Json);
}

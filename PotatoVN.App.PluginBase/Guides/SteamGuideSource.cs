using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;

namespace PotatoVN.App.PluginBase.Guides;

internal sealed class SteamGuideSource(GuidePageClient pages)
{
    internal async Task<WebGuideResult> SearchAsync(Uri guidesPage, CancellationToken token)
    {
        try
        {
            string html = await pages.GetAsync(guidesPage, token);
            return await Task.Run(() => Parse(html, guidesPage), token);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return WebGuideResult.FromLinks([], guidesPage);
        }
        catch (Exception error) when (error is IOException or HttpRequestException)
        {
            return WebGuideResult.Failure(GuideIssue.Unavailable, guidesPage);
        }
    }

    internal static WebGuideResult Parse(string html, Uri guidesPage)
    {
        var page = GuideHtml.Parse(html);
        string appId = guidesPage.Segments[2].TrimEnd('/');
        var links = new List<GuideLink>();
        var seen = new HashSet<string>();
        foreach (IElement card in page.QuerySelectorAll(".guides_home_list_ctn a.workshopItemCollection[href]"))
        {
            if (card.GetAttribute("data-appid") != appId ||
                card.QuerySelector(".workshopItemApp.guide") is null ||
                !Uri.TryCreate(guidesPage, card.GetAttribute("href"), out Uri? address) ||
                address.Scheme != "https" || address.Host != guidesPage.Host || !address.IsDefaultPort ||
                address.UserInfo.Length != 0 || address.AbsolutePath != "/sharedfiles/filedetails/") continue;
            string? id = card.GetAttribute("data-publishedfileid");
            string? title = card.QuerySelector(".workshopItemTitle")?.TextContent.Trim();
            if (!GuideIds.IsValid(id) || string.IsNullOrWhiteSpace(title) || !seen.Add(id!)) continue;
            links.Add(new GuideLink(new Uri(guidesPage, $"/sharedfiles/filedetails/?id={id}").AbsoluteUri, title));
        }
        if (links.Count == 0 && page.QuerySelector(".guideInfo, .guide_categories") is null)
            return WebGuideResult.Failure(GuideIssue.LayoutChanged, guidesPage);
        return WebGuideResult.FromLinks(links, guidesPage);
    }
}

using System;
using System.Collections.Generic;

namespace PotatoVN.App.PluginBase.Guides;

public sealed record SavedGuideLink(Guid Id, string Url, string Title, GuideBookmark? Bookmark = null);

internal static class GuideWebLinks
{
    internal static bool TryParse(string? input, out Uri address)
    {
        if (Uri.TryCreate(input?.Trim(), UriKind.Absolute, out Uri? uri) &&
            uri.Scheme is "http" or "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0)
        {
            address = uri;
            return true;
        }

        address = null!;
        return false;
    }

    internal static GuideReference? ReaderReference(Uri address)
    {
        if (!address.IsDefaultPort) return null;
        if (Df2anGuideSource.IsKnownHost(address) &&
            GuideIds.FromPath(address.AbsolutePath, "/topics/") is { } topic)
            return new GuideReference(GuideSource.Df2an, topic);
        if (address.Host is "ymgal.games" or "www.ymgal.games" &&
            GuideIds.FromPath(address.AbsolutePath, "/co/article/") is { } article)
            return new GuideReference(GuideSource.Ymgal, article);
        return null;
    }

    internal static Uri? SteamGuides(string? id) =>
        uint.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint appId) &&
        appId > 0
            ? new Uri($"https://steamcommunity.com/app/{appId}/guides/?l=schinese")
            : null;
}

internal sealed record WebGuideResult(IReadOnlyList<GuideLink> Links, GuideIssue Issue, Uri BrowseUrl)
{
    internal static WebGuideResult FromLinks(IReadOnlyList<GuideLink> links, Uri browseUrl) =>
        new(links, links.Count > 0 ? GuideIssue.None : GuideIssue.NoResults, browseUrl);

    internal static WebGuideResult Failure(GuideIssue issue, Uri browseUrl) => new([], issue, browseUrl);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace PotatoVN.App.PluginBase.Guides;

internal static class GuideHtml
{
    internal static IHtmlDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    internal static IReadOnlyList<GuideLink> Links(IParentNode page, string selector, string prefix,
        Uri pageUrl, Func<Uri, bool> acceptsHost)
    {
        var items = new List<GuideLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (IElement link in page.QuerySelectorAll(selector))
        {
            if (!Uri.TryCreate(pageUrl, link.GetAttribute("href"), out Uri? address) ||
                address.Scheme is not ("http" or "https") || !acceptsHost(address)) continue;
            string? id = GuideIds.FromPath(address.AbsolutePath, prefix);
            string title = link.TextContent.Trim();
            if (id is null || title.Length < 2 || title == "查看完整介绍" || !seen.Add(id)) continue;
            items.Add(new GuideLink(id, title));
        }
        return items;
    }

    internal static bool HasEmptyNotice(IHtmlDocument page)
    {
        string text = (page.QuerySelector("main, #content, .main-content") ?? page.Body)?.TextContent ?? "";
        return new[] { "没有找到", "未找到", "暂无攻略", "暂无文章", "暂无结果", "没有相关", "没有搜索结果", "没有符合", "找不到相关" }
            .Any(notice => text.Contains(notice, StringComparison.Ordinal));
    }

    internal static GuideResult<GuideArticle> Article(IHtmlDocument page, string contentSelector,
        string titleSelector, string fallbackTitle, Uri url)
    {
        IElement? element = page.QuerySelector(contentSelector);
        if (element is null) return GuideResult<GuideArticle>.Failed(GuideIssue.LayoutChanged, url);
        string body = ToText(element);
        if (body.Length == 0) return GuideResult<GuideArticle>.Failed(GuideIssue.ArticleMissing, url);
        string? title = page.QuerySelector(titleSelector)?.TextContent.Trim();
        return GuideResult<GuideArticle>.Found(new GuideArticle(
            string.IsNullOrWhiteSpace(title) ? fallbackTitle : title, body), url);
    }

    internal static GuideResult<GuideArticle> ArticleFromBody(IElement body, string title, Uri url)
    {
        string text = ToText(body);
        bool hasVisualContent = HasVisualContent(body);
        if (text.Length == 0)
            return GuideResult<GuideArticle>.Failed(hasVisualContent ? GuideIssue.BrowserOnly : GuideIssue.ArticleMissing, url);
        return GuideResult<GuideArticle>.Found(new GuideArticle(title, text, hasVisualContent), url);
    }

    internal static bool HasVisualContent(IElement element) =>
        element.QuerySelector("img, video, iframe, picture, table") is not null;

    internal static string ToText(IElement element)
    {
        var output = new StringBuilder();
        var stack = new Stack<(INode Node, bool EndsBlock)>();
        stack.Push((element, false));
        while (stack.TryPop(out var item))
        {
            if (item.EndsBlock)
            {
                EndLine(output);
                continue;
            }
            if (item.Node is IText text)
            {
                output.Append(text.Data);
                continue;
            }
            if (item.Node is not IElement current ||
                current.LocalName is "script" or "style" or "noscript" or "template" ||
                current.HasAttribute("hidden")) continue;
            string tag = current.LocalName;
            if (tag == "br")
            {
                EndLine(output);
                continue;
            }

            bool block = tag is "p" or "div" or "li" or "ul" or "ol" or "section" or "article" or
                "header" or "footer" or "blockquote" or "pre" or "table" or "tr" or "h1" or "h2" or
                "h3" or "h4" or "h5" or "h6";
            if (block)
            {
                EndLine(output);
                stack.Push((current, true));
            }
            if (tag is "td" or "th" && output.Length > 0 && output[^1] != '\n') output.Append("　");
            for (int i = current.ChildNodes.Length - 1; i >= 0; i--)
                stack.Push((current.ChildNodes[i], false));
        }

        return string.Join("\n", output.ToString().Split('\n')
            .Select(NormalizeInlineWhitespace)
            .Where(line => line.Length > 0));
    }

    private static void EndLine(StringBuilder output)
    {
        if (output.Length > 0 && output[^1] != '\n') output.Append('\n');
    }

    private static string NormalizeInlineWhitespace(string line)
    {
        var output = new StringBuilder(line.Length);
        bool pendingSpace = false;
        foreach (char character in line.AsSpan().Trim())
        {
            if (character is ' ' or '\t' or '\r')
            {
                pendingSpace = output.Length > 0;
                continue;
            }
            if (pendingSpace) output.Append(' ');
            output.Append(character);
            pendingSpace = false;
        }
        return output.ToString();
    }
}

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;

namespace PotatoVN.App.PluginBase.Guides;

internal sealed class ExternalGuidePage(HttpClient? client = null)
{
    private const int MaxPageBytes = 2 * 1024 * 1024;
    private const int MaxTextLength = 250_000;
    private const int MaxRedirects = 5;
    private static readonly HttpClient PublicHttp = CreateHttpClient();
    private readonly HttpClient _http = client ?? PublicHttp;

    internal async Task<GuideResult<GuideArticle>> ReadAsync(Uri address, string title, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        Uri url = address;
        try
        {
            for (int redirects = 0; redirects <= MaxRedirects; redirects++)
            {
                if (!IsPublicWebAddress(url))
                    return GuideResult<GuideArticle>.Failed(GuideIssue.BrowserOnly, address);

                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                    HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (response.Headers.Location is not { } location ||
                        !Uri.TryCreate(url, location, out Uri? next) ||
                        url.Scheme == "https" && next.Scheme == "http") break;
                    url = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    return GuideResult<GuideArticle>.Failed(GuideIssue.Unavailable, address);

                string? type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                bool plainText = type is "text/plain" or "text/markdown";
                if (!plainText && type is not (null or "text/html" or "application/xhtml+xml"))
                    return GuideResult<GuideArticle>.Failed(GuideIssue.BrowserOnly, address);

                string content = await GuidePageClient.ReadTextAsync(response.Content, timeout.Token,
                    plainText ? null : Encoding.UTF8, MaxPageBytes).ConfigureAwait(false);
                GuideArticle? article = plainText
                    ? PlainText(content, title)
                    : await Task.Run(() => ExtractArticle(content, title, timeout.Token), timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                return article is null
                    ? GuideResult<GuideArticle>.Failed(GuideIssue.LayoutChanged, address)
                    : GuideResult<GuideArticle>.Found(article, address);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested) { }
        catch (HttpRequestException) { }
        catch (IOException) { }

        token.ThrowIfCancellationRequested();
        return GuideResult<GuideArticle>.Failed(GuideIssue.Unavailable, address);
    }

    private static GuideArticle? PlainText(string input, string title)
    {
        string text = input.ReplaceLineEndings("\n").Trim();
        return text.Length is > 0 and <= MaxTextLength &&
            !text.Any(character => char.IsControl(character) && character is not ('\n' or '\t'))
            ? new GuideArticle(title, text)
            : null;
    }

    private static GuideArticle? ExtractArticle(string html, string title, CancellationToken token)
    {
        var page = GuideHtml.Parse(html);
        if (page.Body is not { } body ||
            page.QuerySelector("#challenge-running, #challenge-form") is not null ||
            page.Title?.Trim() is "Just a moment..." or "Access Denied") return null;

        foreach (IElement noise in body.QuerySelectorAll(
                     "script, style, noscript, template, svg, nav, aside, footer, form, body > header, " +
                     "[hidden], [aria-hidden=true], [role=navigation], [role=banner], [role=contentinfo], " +
                     "#comments, .comments, .comment-list, .sidebar, .related-posts, .share-buttons, .cookie-banner"))
            noise.Remove();

        string? pageTitle = page.Title?.Trim();
        if (!string.IsNullOrEmpty(pageTitle)) title = pageTitle;

        foreach (string selector in new[]
                 {
                     "[itemprop~=articleBody], .entry-content, .post-content, .article-content, #article-content",
                     "article, [role=article]",
                     "main, [role=main], #content, #main",
                 })
        {
            GuideArticle? best = null;
            foreach (IElement candidate in body.QuerySelectorAll(selector).Take(32))
            {
                token.ThrowIfCancellationRequested();
                if (ReadCandidate(candidate, title, structured: true) is { } article &&
                    (best is null || article.Text.Length > best.Text.Length)) best = article;
            }
            if (best is not null) return best;
        }

        token.ThrowIfCancellationRequested();
        return ReadCandidate(body, title, structured: false);
    }

    private static GuideArticle? ReadCandidate(IElement candidate, string title, bool structured)
    {
        string text = GuideHtml.ToText(candidate);
        if (text.Length > MaxTextLength) return null;
        int characters = text.Count(char.IsLetterOrDigit);
        int minimum = structured ? 8 : 16;
        if (characters < minimum ||
            (characters < 40 && !text.Contains('\n') && candidate.QuerySelector("p, li, tr, pre, blockquote") is null))
            return null;
        int linkedCharacters = candidate.QuerySelectorAll("a")
            .Sum(link => link.TextContent.Count(char.IsLetterOrDigit));
        if (linkedCharacters >= characters * 0.5) return null;

        return new GuideArticle(title, text, GuideHtml.HasVisualContent(candidate));
    }

    private static bool IsPublicWebAddress(Uri address) =>
        address.IsAbsoluteUri && address.Scheme is "http" or "https" && address.IsDefaultPort &&
        !address.IsLoopback && address.UserInfo.Length == 0 &&
        (!IPAddress.TryParse(address.IdnHost, out IPAddress? ip) || IsPublicAddress(ip));

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            int first = bytes[0], second = bytes[1], third = bytes[2];
            return first is > 0 and < 224 and not (10 or 127) &&
                   !(first == 100 && second is >= 64 and <= 127) &&
                   !(first == 169 && second == 254) &&
                   !(first == 172 && second is >= 16 and <= 31) &&
                   !(first == 192 && (second == 168 || second == 0 && third is 0 or 2)) &&
                   !(first == 198 && (second is 18 or 19 || second == 51 && third == 100)) &&
                   !(first == 203 && second == 0 && third == 113);
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xE0) == 0x20 &&
               !(bytes[0] == 0x20 && (bytes[1] == 0x02 ||
                   bytes[1] == 0x01 && (bytes[2] < 2 || bytes[2] == 0x0d && bytes[3] == 0xb8)));
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectPublicAsync,
        };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) PotatoVN-Walkthrough/1.0");
        return client;
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        // 用过滤后的地址连接，避免 DNS 重解析或网页重定向访问局域网。
        IPAddress[] addresses = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token)
            .ConfigureAwait(false)).Where(IsPublicAddress).ToArray();
        if (addresses.Length == 0) throw new HttpRequestException("没有可用的公网地址。");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

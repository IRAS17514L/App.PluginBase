using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PotatoVN.App.PluginBase.Guides;

internal enum GuideConnection { Unavailable, Partial, Connected }

internal sealed class GuidePageClient(HttpClient? client = null)
{
    private static readonly HttpClient DefaultHttp = CreateHttpClient();
    private static readonly Regex MetaCharset = new(@"<meta\b[^>]*\bcharset\s*=\s*[""']?\s*(?<name>[\w-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly HttpClient _http = client ?? DefaultHttp;
    private readonly ConcurrentDictionary<Uri, CachedPage> _pages = new();

    private sealed record CachedPage(string Html, DateTimeOffset ExpiresAt);
    private const int MaxCachedPages = 32;
    private const int MaxCachedPageLength = 1024 * 1024;
    private const int MaxResponseLength = 5 * 1024 * 1024;

    internal void Clear() => _pages.Clear();

    internal async Task<string> GetAsync(Uri url, CancellationToken token, Encoding? fallbackEncoding = null)
    {
        token.ThrowIfCancellationRequested();
        if (_pages.TryGetValue(url, out CachedPage? cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Html;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        string html;
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxResponseLength)
                throw new HttpRequestException("响应内容过大");

            await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (true)
            {
                int read = await source.ReadAsync(chunk, timeout.Token);
                if (read == 0) break;
                if (buffer.Length + read > MaxResponseLength) throw new HttpRequestException("响应内容过大");
                buffer.Write(chunk, 0, read);
            }

            timeout.Token.ThrowIfCancellationRequested();
            var payload = buffer.GetBuffer();
            int length = checked((int)buffer.Length);
            Encoding encoding = ReadEncoding(response.Content, payload, length, fallbackEncoding);
            html = encoding.GetString(payload, 0, length);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new HttpRequestException("请求超时", error);
        }

        token.ThrowIfCancellationRequested();
        if (html.Length <= MaxCachedPageLength)
        {
            _pages[url] = new CachedPage(html, DateTimeOffset.UtcNow.AddMinutes(10));
            foreach (Uri expired in _pages.OrderBy(pair => pair.Value.ExpiresAt)
                         .Take(Math.Max(0, _pages.Count - MaxCachedPages)).Select(pair => pair.Key))
                _pages.TryRemove(expired, out _);
        }
        return html;
    }

    internal async Task<GuideConnection> ProbeEndpointsAsync(IReadOnlyList<Uri> endpoints, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        bool[] results = await Task.WhenAll(endpoints.Select(url => ProbeAsync(url, timeout.Token)));
        token.ThrowIfCancellationRequested();
        int reachable = results.Count(result => result);
        if (reachable == 0) return GuideConnection.Unavailable;
        return reachable == endpoints.Count ? GuideConnection.Connected : GuideConnection.Partial;
    }

    internal async Task<bool> ProbeAsync(Uri url, CancellationToken token)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) { return false; }
    }

    private static Encoding ReadEncoding(HttpContent content, byte[] payload, int length, Encoding? fallbackEncoding)
    {
        string? charset = content.Headers.ContentType?.CharSet?.Trim('"');
        if (string.IsNullOrWhiteSpace(charset) && fallbackEncoding is not null)
        {
            string prefix = Encoding.ASCII.GetString(payload, 0, Math.Min(4096, length));
            charset = MetaCharset.Match(prefix).Groups["name"].Value;
        }
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { return CodePagesEncodingProvider.Instance.GetEncoding(charset) ?? Encoding.GetEncoding(charset); }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }
        return fallbackEncoding ?? Encoding.UTF8;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) PotatoVN-Walkthrough/1.0");
        return client;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Contracts.PluginUi;
using GalgameManager.Enums;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Controls.Prefabs;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase;

public partial class Plugin : IGalgamePageRightPanel
{
    private const string YmgalBase = "https://www.ymgal.games";
    private static readonly HttpClient Http = CreateHttpClient();
    private string _currentSource = "2dfan";

    public FrameworkElement CreateSettingUi()
    {
        TextBox domainBox = new()
        {
            Text = Data.Domain,
            MinWidth = 220,
        };
        domainBox.LostFocus += (_, _) =>
        {
            if (Uri.TryCreate(domainBox.Text, UriKind.Absolute, out var uri) &&
                (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                string normalized = uri.ToString().TrimEnd('/');
                if (Data.Domain != normalized) Data.Domain = normalized;
            }
            else
            {
                domainBox.Text = Data.Domain;
            }
        };
        Button clearButton = new() { Content = "清除已保存的关联" };
        clearButton.Click += (_, _) => Data.TopicUrlMap.Clear();

        StdStackPanel panel = new();
        panel.Children.Add(new StdSetting("2DFan 域名",
            "官方域 2dfan.com 在中国大陆无法访问，可切换备用域（2dfdf.de / 2dfmax.top）", domainBox));
        panel.Children.Add(new StdSetting("已保存的攻略关联",
            $"共 {Data.TopicUrlMap.Count} 个游戏已关联攻略页，清除后需重新检索", clearButton));
        return panel;
    }

    public Task<FrameworkElement> CreateRightPanelUiAsync(Galgame game)
    {
        _currentSource = !string.IsNullOrEmpty(game.Ids[(int)RssType.Ymgal]) ? "ymgal" : "2dfan";

        TextBlock title = new() { Text = "攻略", FontSize = 15, FontWeight = FontWeights.SemiBold };
        Button source2dfan = new() { Content = "2DFan" };
        Button sourceYmgal = new() { Content = "月幕" };
        Button refresh = new() { Content = "重新检索" };
        StackPanel header = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(title);
        header.Children.Add(source2dfan);
        header.Children.Add(sourceYmgal);
        header.Children.Add(refresh);

        TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
        StackPanel content = new() { Spacing = 6 };
        ScrollViewer scroll = new()
        {
            Content = content,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        StackPanel root = new() { Spacing = 8, MaxWidth = 380 };
        root.Children.Add(header);
        root.Children.Add(status);
        root.Children.Add(scroll);

        source2dfan.Click += (_, _) => _ = ShowSourceAsync(game, content, status, "2dfan");
        sourceYmgal.Click += (_, _) => _ = ShowSourceAsync(game, content, status, "ymgal");
        refresh.Click += (_, _) => _ = ShowSourceAsync(game, content, status, _currentSource);

        _ = ShowSourceAsync(game, content, status, null);
        return Task.FromResult<FrameworkElement>(root);
    }

    private async Task ShowSourceAsync(Galgame game, StackPanel content, TextBlock status, string? source)
    {
        if (source is not null) _currentSource = source;
        if (_currentSource == "ymgal")
            await YmgalAsync(game, content, status);
        else
            await Df2anAsync(game, content, status);
    }

    #region 2DFan

    private async Task Df2anAsync(Galgame game, StackPanel content, TextBlock status)
    {
        status.Text = "加载中…";
        try
        {
            if (Data.TopicUrlMap.TryGetValue(game.Uuid, out string? cachedUrl) && !string.IsNullOrEmpty(cachedUrl))
            {
                string url = RewriteDomain(cachedUrl);
                if (url != cachedUrl) Data.TopicUrlMap[game.Uuid] = url;
                await ShowTopicAsync(url, content, status);
                return;
            }
            await SearchAndShowAsync(game, content, status);
        }
        catch (Exception e)
        {
            status.Text = $"获取失败：{e.Message}";
        }
    }

    private async Task SearchAndShowAsync(Galgame game, StackPanel content, TextBlock status)
    {
        string query = BuildQuery(game);
        status.Text = $"正在 2DFan 检索：{query}";
        content.Children.Clear();
        try
        {
            List<(string Title, string Url)> subjects = await SearchSubjectsAsync(query);
            if (subjects.Count == 0)
            {
                status.Text = "没有找到结果。";
                AddOpenSiteButton(content, $"{Data.Domain}/subjects/search?keyword={Uri.EscapeDataString(query)}");
                return;
            }
            status.Text = $"找到 {subjects.Count} 个条目，点击后获取其攻略列表";
            foreach ((string title, string url) in subjects)
            {
                Button subject = new() { Content = title, HorizontalAlignment = HorizontalAlignment.Left };
                subject.Click += async (_, _) =>
                {
                    content.Children.Clear();
                    status.Text = $"正在获取「{title}」的攻略…";
                    try
                    {
                        List<(string Title, string Url)> topics = await GetTopicsAsync(url);
                        if (topics.Count == 0)
                        {
                            status.Text = "该条目暂无攻略。";
                            AddOpenSiteButton(content, url);
                            return;
                        }
                        status.Text = $"「{title}」的攻略：";
                        foreach ((string topicTitle, string topicUrl) in topics)
                        {
                            Button topic = new() { Content = topicTitle, HorizontalAlignment = HorizontalAlignment.Left };
                            topic.Click += async (_, _) =>
                            {
                                Data.TopicUrlMap[game.Uuid] = topicUrl;
                                SaveData();
                                await ShowTopicAsync(topicUrl, content, status);
                            };
                            content.Children.Add(topic);
                        }
                    }
                    catch (Exception e)
                    {
                        status.Text = $"获取攻略列表失败：{e.Message}";
                    }
                };
                content.Children.Add(subject);
            }
        }
        catch (Exception e)
        {
            status.Text = $"检索失败：{e.Message}";
        }
    }

    private static async Task ShowTopicAsync(string url, StackPanel content, TextBlock status)
    {
        content.Children.Clear();
        status.Text = "正在加载攻略…";
        try
        {
            string html = await GetAsync(url);
            string text = await Task.Run(() =>
            {
                var doc = new HtmlParser().ParseDocument(html);
                var element = doc.QuerySelector("div.topic-content");
                return element is null ? string.Empty : HtmlToText(element.InnerHtml);
            });
            if (string.IsNullOrWhiteSpace(text))
            {
                status.Text = "攻略内容为空。";
                AddOpenSiteButton(content, url);
                return;
            }
            ShowText(text, content, status, url);
        }
        catch (Exception e)
        {
            status.Text = $"加载失败：{e.Message}";
            AddOpenSiteButton(content, url);
        }
    }

    private async Task<List<(string Title, string Url)>> SearchSubjectsAsync(string query)
    {
        string html = await GetAsync($"{Data.Domain}/subjects/search?keyword={Uri.EscapeDataString(query)}");
        return await Task.Run(() =>
        {
            var doc = new HtmlParser().ParseDocument(html);
            var list = new List<(string, string)>();
            var seen = new HashSet<string>();
            foreach (var anchor in doc.QuerySelectorAll("a[href]"))
            {
                string href = anchor.GetAttribute("href") ?? string.Empty;
                if (!Regex.IsMatch(href, @"^/subjects/\d+$")) continue;
                string anchorTitle = anchor.TextContent.Trim();
                if (anchorTitle.Length < 2) continue;
                string full = $"{Data.Domain}{href}";
                if (seen.Add(full)) list.Add((anchorTitle, full));
            }
            return list;
        });
    }

    private async Task<List<(string Title, string Url)>> GetTopicsAsync(string subjectUrl)
    {
        string html = await GetAsync(subjectUrl);
        return await Task.Run(() =>
        {
            var doc = new HtmlParser().ParseDocument(html);
            var list = new List<(string, string)>();
            var seen = new HashSet<string>();
            foreach (var anchor in doc.QuerySelectorAll("a[href]"))
            {
                string href = anchor.GetAttribute("href") ?? string.Empty;
                if (!Regex.IsMatch(href, @"^/topics/\d+$")) continue;
                string anchorTitle = anchor.TextContent.Trim();
                if (anchorTitle.Length < 2 || anchorTitle == "查看完整介绍") continue;
                string full = $"{Data.Domain}{href}";
                if (seen.Add(full)) list.Add((anchorTitle, full));
            }
            return list;
        });
    }

    #endregion

    #region 月幕 ymgal

    private async Task YmgalAsync(Galgame game, StackPanel content, TextBlock status)
    {
        content.Children.Clear();
        string? gidStr = game.Ids[(int)RssType.Ymgal];
        if (string.IsNullOrEmpty(gidStr) || !int.TryParse(gidStr, out int gid))
        {
            status.Text = "该游戏没有月幕档案编号，可跳转月幕站内搜索。";
            AddOpenSiteButton(content, $"{YmgalBase}/search?keyword={Uri.EscapeDataString(BuildQuery(game))}");
            return;
        }
        status.Text = "正在加载月幕档案…";
        string url = $"{YmgalBase}/ga{gid}";
        try
        {
            string html = await GetAsync(url);
            string toc = await Task.Run(() =>
            {
                var doc = new HtmlParser().ParseDocument(html);
                var element = doc.QuerySelector("div.introduction-content");
                return element is null ? string.Empty : HtmlToText(element.InnerHtml);
            });
            if (string.IsNullOrWhiteSpace(toc))
            {
                status.Text = "月幕档案暂无目录内容，可在浏览器中查看完整档案。";
                AddOpenSiteButton(content, url);
                return;
            }
            status.Text = "月幕档案目录（完整攻略/介绍请打开浏览器查看）：";
            Button open = new() { Content = "在浏览器打开月幕档案", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) };
            open.Click += (_, _) => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url));
            TextBlock body = new() { Text = toc, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20 };
            content.Children.Add(open);
            content.Children.Add(body);
        }
        catch (Exception e)
        {
            status.Text = $"月幕加载失败：{e.Message}";
            AddOpenSiteButton(content, url);
        }
    }

    #endregion

    #region Shared

    private static void ShowText(string text, StackPanel content, TextBlock status, string url)
    {
        status.Text = string.Empty;
        Button open = new() { Content = "在浏览器打开", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) };
        open.Click += (_, _) => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        TextBlock body = new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 20,
        };
        content.Children.Add(open);
        content.Children.Add(body);
    }

    private static string HtmlToText(string html)
    {
        string withBreaks = html
            .Replace("<br>", "\n")
            .Replace("<br/>", "\n")
            .Replace("<br />", "\n")
            .Replace("</p>", "\n")
            .Replace("</div>", "\n")
            .Replace("</li>", "\n")
            .Replace("</h1>", "\n")
            .Replace("</h2>", "\n")
            .Replace("</h3>", "\n")
            .Replace("</tr>", "\n");
        string text = Regex.Replace(withBreaks, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return string.Join("\n", text.Split('\n')
            .Select(line => Regex.Replace(line, @"[ \t]+", " ").Trim())
            .Where(line => line.Length > 0));
    }

    private static async Task<string> GetAsync(string url)
    {
        using var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static string BuildQuery(Galgame game)
    {
        string cn = game.CnName?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(cn)) return cn;
        string name = game.Name.Value?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(name)) return name;
        return game.OriginalName.Value?.Trim() ?? string.Empty;
    }

    private string RewriteDomain(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return $"{Data.Domain}{uri.PathAndQuery}";
        return url;
    }

    private static void AddOpenSiteButton(StackPanel content, string url)
    {
        Button open = new() { Content = "在浏览器打开站点页面", HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        content.Children.Add(open);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) PotatoVN-Walkthrough/1.0");
        return client;
    }

    #endregion
}

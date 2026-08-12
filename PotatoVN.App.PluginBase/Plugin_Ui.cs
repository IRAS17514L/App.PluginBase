using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using GalgameManager.Enums;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Contracts.PluginUi;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using PotatoVN.App.PluginBase.Controls.Prefabs;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase;

public partial class Plugin : IGalgamePageRightPanel
{
    private const string YmgalBase = "https://www.ymgal.games";
    private static readonly HttpClient Http = CreateHttpClient();
    private string _currentSource = "2dfan";
    private ToggleButton? _source2dfan;
    private ToggleButton? _sourceYmgal;
    private ProgressRing? _progressRing;

    public FrameworkElement CreateSettingUi()
    {
        ComboBox sourceBox = new()
        {
            MinWidth = 200,
            SelectedIndex = Data.DefaultSource switch { "2dfan" => 1, "ymgal" => 2, _ => 0 },
        };
        sourceBox.Items.Add("自动（有月幕编号用月幕，否则 2DFan）");
        sourceBox.Items.Add("2DFan");
        sourceBox.Items.Add("月幕");
        sourceBox.SelectionChanged += (_, _) =>
        {
            Data.DefaultSource = sourceBox.SelectedIndex switch { 1 => "2dfan", 2 => "ymgal", _ => "auto" };
        };

        TextBox domainBox = new()
        {
            Text = Data.Domain,
            MinWidth = 200,
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
        panel.Children.Add(new StdSetting("默认攻略来源",
            "自动：游戏有月幕档案编号时用月幕，否则用 2DFan。可在游戏页内手动切换单个游戏的来源。", sourceBox));
        panel.Children.Add(new StdSetting("2DFan 域名",
            "官方域 2dfan.com 在中国大陆无法访问，可切换备用域（2dfdf.de / 2dfmax.top）", domainBox));
        panel.Children.Add(new StdSetting("已保存的攻略关联",
            $"共 {Data.TopicUrlMap.Count} 个游戏已关联攻略页，清除后需重新检索", clearButton));
        return panel;
    }

    public Task<FrameworkElement> CreateRightPanelUiAsync(Galgame game)
    {
        _currentSource = ResolveDefaultSource(game);

        TextBlock title = new() { Text = "攻略", FontSize = 15, FontWeight = FontWeights.SemiBold };

        _source2dfan = new ToggleButton
        {
            Content = "2DFan",
            MinHeight = 28,
            Padding = new Thickness(12, 3, 12, 3),
            IsChecked = _currentSource == "2dfan",
        };
        _sourceYmgal = new ToggleButton
        {
            Content = "月幕",
            MinHeight = 28,
            Padding = new Thickness(12, 3, 12, 3),
            IsChecked = _currentSource == "ymgal",
        };
        Button refresh = new() { Content = "重新检索", MinHeight = 28, Padding = new Thickness(12, 3, 12, 3) };
        _progressRing = new ProgressRing { Width = 16, Height = 16, IsActive = false };

        StackPanel header = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(title);
        header.Children.Add(_source2dfan);
        header.Children.Add(_sourceYmgal);
        header.Children.Add(refresh);
        header.Children.Add(_progressRing);

        TextBlock status = new()
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = GetSecondaryBrush(),
        };
        StackPanel content = new() { Spacing = 4 };
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

        _source2dfan.Click += (_, _) => _ = ShowSourceAsync(game, content, status, "2dfan");
        _sourceYmgal.Click += (_, _) => _ = ShowSourceAsync(game, content, status, "ymgal");
        refresh.Click += (_, _) => _ = ShowSourceAsync(game, content, status, _currentSource);

        _ = ShowSourceAsync(game, content, status, null);
        return Task.FromResult<FrameworkElement>(root);
    }

    private string ResolveDefaultSource(Galgame game) => Data.DefaultSource switch
    {
        "2dfan" => "2dfan",
        "ymgal" => "ymgal",
        _ => !string.IsNullOrEmpty(game.Ids[(int)RssType.Ymgal]) ? "ymgal" : "2dfan",
    };

    private async Task ShowSourceAsync(Galgame game, StackPanel content, TextBlock status, string? source)
    {
        if (source is not null) _currentSource = source;
        UpdateSourceToggleState();
        if (_progressRing is not null) _progressRing.IsActive = true;
        try
        {
            if (_currentSource == "ymgal")
            {
                try
                {
                    await YmgalAsync(game, content, status);
                }
                catch (Exception e)
                {
                    // 月幕不可用时自动回退到 2DFan，避免玩家无攻略可用
                    _currentSource = "2dfan";
                    UpdateSourceToggleState();
                    status.Text = $"月幕加载失败，已自动切换 2DFan：{e.Message}";
                    await Df2anAsync(game, content, status);
                }
            }
            else
            {
                await Df2anAsync(game, content, status);
            }
        }
        finally
        {
            if (_progressRing is not null) _progressRing.IsActive = false;
        }
    }

    private void UpdateSourceToggleState()
    {
        if (_source2dfan is not null) _source2dfan.IsChecked = _currentSource == "2dfan";
        if (_sourceYmgal is not null) _sourceYmgal.IsChecked = _currentSource == "ymgal";
    }

    #region 2DFan

    private async Task Df2anAsync(Galgame game, StackPanel content, TextBlock status)
    {
        status.Text = "加载中…";
        content.Children.Clear();
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
                ShowEmpty(content, status, "没有找到结果。",
                    $"{Data.Domain}/subjects/search?keyword={Uri.EscapeDataString(query)}");
                return;
            }
            status.Text = $"找到 {subjects.Count} 个条目，点击获取攻略列表";
            for (int i = 0; i < subjects.Count; i++)
            {
                (string title, string url) = subjects[i];
                AddResultRow(content, i + 1, title, async () =>
                {
                    content.Children.Clear();
                    status.Text = $"正在获取「{title}」的攻略…";
                    try
                    {
                        List<(string Title, string Url)> topics = await GetTopicsAsync(url);
                        if (topics.Count == 0)
                        {
                            ShowEmpty(content, status, "该条目暂无攻略。", url);
                            return;
                        }
                        status.Text = $"「{title}」的攻略：";
                        for (int j = 0; j < topics.Count; j++)
                        {
                            (string topicTitle, string topicUrl) = topics[j];
                            AddResultRow(content, j + 1, topicTitle, async () =>
                            {
                                Data.TopicUrlMap[game.Uuid] = topicUrl;
                                SaveData();
                                await ShowTopicAsync(topicUrl, content, status);
                            });
                        }
                    }
                    catch (Exception e)
                    {
                        status.Text = $"获取攻略列表失败：{e.Message}";
                    }
                });
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
                ShowEmpty(content, status, "攻略内容为空。", url);
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
            ShowEmpty(content, status, "该游戏没有月幕档案编号，可跳转月幕站内搜索。",
                $"{YmgalBase}/search?keyword={Uri.EscapeDataString(BuildQuery(game))}");
            return;
        }
        status.Text = "正在加载月幕文章列表…";
        List<(string Title, string Url)> articles = await GetYmgalArticlesAsync(gid);
        if (articles.Count == 0)
        {
            ShowEmpty(content, status, "月幕没有找到该游戏的文章/攻略。", $"{YmgalBase}/ga{gid}");
            return;
        }
        status.Text = $"月幕找到 {articles.Count} 篇相关文章（含攻略/感想）：";
        for (int i = 0; i < articles.Count; i++)
        {
            (string title, string url) = articles[i];
            AddResultRow(content, i + 1, title, async () =>
            {
                content.Children.Clear();
                status.Text = $"正在加载「{title}」…";
                string text = await GetYmgalArticleTextAsync(url);
                if (string.IsNullOrWhiteSpace(text))
                {
                    ShowEmpty(content, status, "文章内容为空。", url);
                    return;
                }
                ShowText(text, content, status, url);
            });
        }
    }

    private async Task<List<(string Title, string Url)>> GetYmgalArticlesAsync(int gid)
    {
        string html = await GetAsync($"{YmgalBase}/ga{gid}");
        return await Task.Run(() =>
        {
            var doc = new HtmlParser().ParseDocument(html);
            var list = new List<(string, string)>();
            var seen = new HashSet<string>();
            foreach (var item in doc.QuerySelectorAll("div.article-item"))
            {
                var link = item.QuerySelector("a.article-title");
                if (link is null) continue;
                string href = link.GetAttribute("href") ?? string.Empty;
                if (!Regex.IsMatch(href, @"^/co/article/\d+$")) continue;
                string title = link.TextContent.Trim();
                if (title.Length < 2) continue;
                string full = $"{YmgalBase}{href}";
                if (seen.Add(full)) list.Add((title, full));
            }
            return list;
        });
    }

    private static async Task<string> GetYmgalArticleTextAsync(string url)
    {
        string html = await GetAsync(url);
        return await Task.Run(() =>
        {
            var doc = new HtmlParser().ParseDocument(html);
            var element = doc.QuerySelector("div.article-content");
            return element is null ? string.Empty : HtmlToText(element.InnerHtml);
        });
    }

    #endregion

    #region Shared

    private static void ShowText(string text, StackPanel content, TextBlock status, string url)
    {
        status.Text = string.Empty;
        AddOpenSiteButton(content, url);
        TextBlock body = new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 20,
            IsTextSelectionEnabled = true,
        };
        content.Children.Add(body);
    }

    private static void ShowEmpty(StackPanel content, TextBlock status, string text, string? url = null)
    {
        status.Text = text;
        content.Children.Clear();
        if (url is not null) AddOpenSiteButton(content, url);
    }

    private static void AddResultRow(StackPanel content, int index, string text, Action onClick)
    {
        Button button = new()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 4, 8, 4),
            Content = new TextBlock
            {
                Text = index > 0 ? $"{index}. {text}" : text,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 3,
                FontSize = 13,
            },
        };
        button.Click += (_, _) => onClick();
        content.Children.Add(button);
    }

    private static void AddOpenSiteButton(StackPanel content, string url)
        => AddResultRow(content, 0, "在浏览器打开站点页面", () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url)));

    private static Brush GetSecondaryBrush()
    {
        if (Application.Current?.Resources?.TryGetValue("TextFillColorSecondaryBrush", out var value) == true &&
            value is Brush brush)
            return brush;
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
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
using Microsoft.UI.Windowing;
using PotatoVN.App.PluginBase.Controls.Prefabs;
using PotatoVN.App.PluginBase.Models;
using Windows.Graphics;

namespace PotatoVN.App.PluginBase;

public partial class Plugin : IGalgamePageRightPanel
{
    private const string YmgalBase = "https://www.ymgal.games";
    private static readonly string[] Known2DfanDomains =
    [
        "https://2dfan.com",
        "https://2dfdf.de",
        "https://2dfmax.top",
        "https://fan2d.top",
        "https://acgfan.top",
    ];
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly Dictionary<Guid, Window> FloatingWindows = [];

    private sealed class PanelState
    {
        public string CurrentSource = "2dfan";
        public ToggleButton? Source2dfan;
        public ToggleButton? SourceYmgal;
        public ProgressRing? ProgressRing;
        public StackPanel? Content;
        public TextBlock? Status;
        public Button? BackButton;
        public Stack<(string StatusText, UIElement[] Children)> BackStack = [];
    }

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

        ComboBox domainBox = new() { MinWidth = 200 };
        foreach (string candidate in Known2DfanDomains)
            domainBox.Items.Add(candidate);
        if (!Known2DfanDomains.Contains(Data.Domain))
            domainBox.Items.Add(Data.Domain);
        domainBox.SelectedItem = Data.Domain;
        domainBox.SelectionChanged += (_, _) =>
        {
            if (domainBox.SelectedItem is string selected && !string.IsNullOrEmpty(selected))
                Data.Domain = selected;
        };

        Button detectButton = new() { Content = "自动检测可用域名" };
        detectButton.Click += async (_, _) =>
        {
            detectButton.IsEnabled = false;
            string? found = await ProbeAvailableDomainAsync();
            detectButton.IsEnabled = true;
            if (found is null)
            {
                Plugin.HostApi.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                    "未检测到可用的 2DFan 域名，请检查网络");
                return;
            }
            Data.Domain = found;
            domainBox.SelectedItem = found;
            Plugin.HostApi.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success,
                $"已切换到可用域名：{found}");
        };
        StackPanel domainStack = new() { Spacing = 6 };
        domainStack.Children.Add(domainBox);
        domainStack.Children.Add(detectButton);

        Button clearButton = new() { Content = "清除已保存的关联" };
        clearButton.Click += (_, _) => Data.TopicUrlMap.Clear();

        StdStackPanel panel = new();
        ToggleSwitch autoOpenToggle = new() { IsOn = Data.AutoOpenFloatOnLaunch };
        autoOpenToggle.Toggled += (_, _) => Data.AutoOpenFloatOnLaunch = autoOpenToggle.IsOn;
        panel.Children.Add(new StdSetting("启动游戏时自动打开攻略浮窗", "启动游戏时自动弹出置顶攻略窗口，可拖动到游戏旁", autoOpenToggle));
        ToggleSwitch minimalToggle = new() { IsOn = Data.MinimalMode };
        minimalToggle.Toggled += (_, _) => Data.MinimalMode = minimalToggle.IsOn;
        panel.Children.Add(new StdSetting("极简模式",
            "浮窗默认只显示攻略内容与一个切换按钮，其余按钮隐藏；可在浮窗内随时切换", minimalToggle));
        panel.Children.Add(new StdSetting("默认攻略来源",
            "自动：游戏有月幕档案编号时用月幕，否则用 2DFan。可在游戏页内手动切换单个游戏的来源。", sourceBox));
        panel.Children.Add(new StdSetting("2DFan 域名",
            "官方域 2dfan.com 在中国大陆无法访问，域名失效时可点“自动检测”选择可用备用域", domainStack));
        panel.Children.Add(new StdSetting("已保存的攻略关联",
            $"共 {Data.TopicUrlMap.Count} 个游戏已关联攻略页，清除后需重新检索", clearButton));
        return panel;
    }

    public Task<FrameworkElement> CreateRightPanelUiAsync(Galgame game) => Task.FromResult(BuildGuidePanel(game, true, true, false, out _, out _));

    private FrameworkElement BuildGuidePanel(Galgame game, bool showFloatButton, bool showHeader, bool fillHeight, out FrameworkElement? header, out PanelState state)
    {
        state = new() { CurrentSource = ResolveDefaultSource(game) };
        PanelState panelState = state;

        TextBlock status = new()
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = GetSecondaryBrush(),
        };
        StackPanel content = new() { Spacing = 4 };
        panelState.Content = content;
        panelState.Status = status;

        StackPanel root = new() { Spacing = 8, MaxWidth = 380 };
        header = null;
        if (showHeader)
        {
            TextBlock title = new() { Text = "攻略", FontSize = 15, FontWeight = FontWeights.SemiBold };

            ToggleButton source2dfan = new()
            {
                Content = "2DFan",
                MinHeight = 28,
                Padding = new Thickness(12, 3, 12, 3),
                IsChecked = panelState.CurrentSource == "2dfan",
            };
            ToggleButton sourceYmgal = new()
            {
                Content = "月幕",
                MinHeight = 28,
                Padding = new Thickness(12, 3, 12, 3),
                IsChecked = panelState.CurrentSource == "ymgal",
            };
            panelState.Source2dfan = source2dfan;
            panelState.SourceYmgal = sourceYmgal;
            Button refresh = new() { Content = "重新检索", MinHeight = 28, Padding = new Thickness(12, 3, 12, 3) };
            ProgressRing progressRing = new() { Width = 16, Height = 16, IsActive = false };
            panelState.ProgressRing = progressRing;

            StackPanel headerPanel = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
            headerPanel.Children.Add(title);
            headerPanel.Children.Add(source2dfan);
            headerPanel.Children.Add(sourceYmgal);
            headerPanel.Children.Add(refresh);
            if (showFloatButton)
            {
                Button floatButton = new() { Content = "浮窗", MinHeight = 28, Padding = new Thickness(12, 3, 12, 3) };
                floatButton.Click += (_, _) => OpenFloatingWindow(game);
                headerPanel.Children.Add(floatButton);
            }
            headerPanel.Children.Add(progressRing);
            root.Children.Add(headerPanel);
            header = headerPanel;

            source2dfan.Click += (_, _) => _ = ShowSourceAsync(game, content, status, "2dfan", panelState);
            sourceYmgal.Click += (_, _) => _ = ShowSourceAsync(game, content, status, "ymgal", panelState);
            refresh.Click += (_, _) => _ = ShowSourceAsync(game, content, status, panelState.CurrentSource, panelState);
        }

        ScrollViewer scroll = new()
        {
            Content = content,
            MaxHeight = fillHeight ? double.PositiveInfinity : 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        root.Children.Add(status);
        root.Children.Add(scroll);

        _ = ShowSourceAsync(game, content, status, null, panelState);
        return root;
    }

    private string ResolveDefaultSource(Galgame game) => Data.DefaultSource switch
    {
        "2dfan" => "2dfan",
        "ymgal" => "ymgal",
        _ => !string.IsNullOrEmpty(game.Ids[(int)RssType.Ymgal]) ? "ymgal" : "2dfan",
    };

    private async Task ShowSourceAsync(Galgame game, StackPanel content, TextBlock status, string? source, PanelState state)
    {
        if (source is not null) state.CurrentSource = source;
        UpdateSourceToggleState(state);
        if (state.ProgressRing is not null) state.ProgressRing.IsActive = true;
        try
        {
            if (state.CurrentSource == "ymgal")
            {
                try
                {
                    await YmgalAsync(game, content, status, state);
                }
                catch (Exception e)
                {
                    // 月幕不可用时自动回退到 2DFan，避免玩家无攻略可用
                    state.CurrentSource = "2dfan";
                    UpdateSourceToggleState(state);
                    status.Text = $"月幕加载失败，已自动切换 2DFan：{e.Message}";
                    await Df2anAsync(game, content, status, state);
                }
            }
            else
            {
                await Df2anAsync(game, content, status, state);
            }
        }
        finally
        {
            if (state.ProgressRing is not null) state.ProgressRing.IsActive = false;
        }
    }

    private void UpdateSourceToggleState(PanelState state)
    {
        if (state.Source2dfan is not null) state.Source2dfan.IsChecked = state.CurrentSource == "2dfan";
        if (state.SourceYmgal is not null) state.SourceYmgal.IsChecked = state.CurrentSource == "ymgal";
    }

    private static void PushBack(PanelState state)
    {
        if (state.Content is null || state.Status is null) return;
        state.BackStack.Push((state.Status.Text, state.Content.Children.ToArray()));
        if (state.BackButton is not null) state.BackButton.Visibility = Visibility.Visible;
    }

    private static void PopBack(PanelState state)
    {
        if (state.Content is null || state.Status is null || state.BackButton is null || state.BackStack.Count == 0) return;
        var (statusText, children) = state.BackStack.Pop();
        state.Content.Children.Clear();
        foreach (var child in children) state.Content.Children.Add(child);
        state.Status.Text = statusText;
        state.BackButton.Visibility = state.BackStack.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenFloatingWindow(Galgame game)
    {
        try
        {
            CloseFloatingWindow(game.Uuid);

            Window window = new() { Title = $"攻略 - {game.Name.Value}" };

            OverlappedPresenter? presenter = window.AppWindow.Presenter as OverlappedPresenter;
            if (presenter is not null)
            {
                presenter.IsAlwaysOnTop = Data.PinFloatOnTop;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }

            FrameworkElement panel = BuildGuidePanel(game, false, true, true, out FrameworkElement? header, out PanelState state);
            PanelState panelState = state;

            // 返回按钮（返回上一层，选错攻略可回退）
            Button backButton = new()
            {
                Content = "←",
                MinHeight = 24,
                Padding = new Thickness(8, 2, 8, 2),
                Visibility = Visibility.Collapsed,
            };
            backButton.Click += (_, _) => PopBack(panelState);
            panelState.BackButton = backButton;

            // 极简/展开切换按钮（低透明度，不影响观看）
            Button expandButton = new()
            {
                Content = "展开",
                MinHeight = 24,
                Padding = new Thickness(8, 2, 8, 2),
                Opacity = 0.45,
            };

            // 极简按钮（完整模式回极简）
            Button minimalButton = new() { Content = "极简", MinHeight = 24, Padding = new Thickness(8, 2, 8, 2) };

            // 钉子置顶
            FontIcon pinIcon = new()
            {
                Glyph = Data.PinFloatOnTop ? "\uE718" : "\uE77A",
                FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
            };
            ToggleButton pinButton = new() { MinHeight = 24, IsChecked = Data.PinFloatOnTop };
            pinButton.Content = pinIcon;

            // 关闭（隐藏式，避免最后一个窗口导致应用退出）
            Button closeButton = new() { Content = "关闭", MinHeight = 24, Padding = new Thickness(8, 2, 8, 2) };

            StackPanel bar = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            bar.Children.Add(backButton);
            bar.Children.Add(expandButton);
            bar.Children.Add(minimalButton);
            bar.Children.Add(pinButton);
            bar.Children.Add(closeButton);

            void ApplyMode()
            {
                bool minimal = Data.MinimalMode;
                backButton.Visibility = panelState.BackStack.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                expandButton.Visibility = minimal ? Visibility.Visible : Visibility.Collapsed;
                minimalButton.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
                pinButton.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
                closeButton.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
                if (header is not null) header.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
                if (panelState.Content is not null)
                    foreach (var child in panelState.Content.Children)
                        if (child is FrameworkElement fe && fe.Tag as string == "openSite")
                            fe.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
                window.AppWindow.Resize(minimal
                    ? new Windows.Graphics.SizeInt32(300, 380)   // 小窗模式
                    : new Windows.Graphics.SizeInt32(480, 700)); // 完整模式
                ApplyTitleBar(window, minimal);
            }

            void UpdatePinState()
            {
                pinIcon.Glyph = pinButton.IsChecked == true ? "\uE718" : "\uE77A";
                if (presenter is not null) presenter.IsAlwaysOnTop = pinButton.IsChecked == true;
                Data.PinFloatOnTop = pinButton.IsChecked == true;
            }

            expandButton.Click += (_, _) => { Data.MinimalMode = false; ApplyMode(); };
            minimalButton.Click += (_, _) => { Data.MinimalMode = true; ApplyMode(); };
            pinButton.Checked += (_, _) => UpdatePinState();
            pinButton.Unchecked += (_, _) => UpdatePinState();
            closeButton.Click += (_, _) => DismissFloatWindow(game.Uuid);

            Grid shell = new() { Padding = new Thickness(4) };
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            shell.Children.Add(bar);
            Grid.SetRow(bar, 0);
            shell.Children.Add(panel);
            Grid.SetRow(panel, 1);
            panel.MaxWidth = double.PositiveInfinity; // 填满窗口宽，消除侧边空白
            panel.HorizontalAlignment = HorizontalAlignment.Stretch;
            panel.VerticalAlignment = VerticalAlignment.Stretch;

            try
            {
                window.SystemBackdrop = new MicaBackdrop();
            }
            catch (Exception) { }

            window.Content = shell;

            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(480, 700));
            FloatingWindows[game.Uuid] = window;
            window.Closed += (_, _) => FloatingWindows.Remove(game.Uuid);
            try
            {
                string iconPath = System.IO.Path.Combine(Plugin.HostApi.GetPluginPath(), "Assets", "plugin-icon.ico");
                if (System.IO.File.Exists(iconPath)) window.AppWindow.SetIcon(iconPath);
            }
            catch (Exception)
            {
                // 图标缺失或设置失败不影响窗口
            }
            window.Activate();
            if (Data.PinFloatOnTop) _ = BumpTopmostAfterMagpieAsync(window);
            _ = FloatWatchdogAsync(game);
            ApplyMode(); // 初始按数据应用
        }
        catch (Exception)
        {
            Plugin.HostApi.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning, "攻略浮窗暂不可用（宿主限制）", null, 3000);
        }
    }

    private async Task OpenFloatingWindowDelayedAsync(Galgame game)
    {
        try
        {
            await Task.Delay(2200); // 等宿主 SetWindowMode 执行完（SystemTray 模式下旧进程此时已退出，窗口不会在旧进程出现）
            if (Data.ActiveGameUuid != game.Uuid) return; // 期间切了游戏或已停止则放弃
            HostApi.InvokeOnMainThread(() => OpenFloatingWindow(game));
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    private static void CloseFloatingWindow(Guid uuid) => DismissFloatWindow(uuid);

    private static void DismissFloatWindow(Guid uuid)
    {
        if (FloatingWindows.Remove(uuid, out Window? window))
            window.AppWindow.Hide();
    }

    private static void ApplyTitleBar(Window window, bool minimal)
    {
        try
        {
            var titleBar = window.AppWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            try { titleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed; } catch { } // Win10 忽略
            // 拖拽区：顶部 28px，排除右侧按钮区（极简排除 64px，完整排除 240px）
            int width = window.AppWindow.Size.Width;
            int rightExclude = minimal ? 64 : 240;
            titleBar.SetDragRectangles([new Windows.Graphics.RectInt32(0, 0, Math.Max(0, width - rightExclude), 28)]);
        }
        catch (Exception)
        {
            // 标题栏设置失败不影响功能
        }
    }

    private async Task FloatWatchdogAsync(Galgame game)
    {
        while (true)
        {
            await Task.Delay(2000);
            if (!FloatingWindows.ContainsKey(game.Uuid)) return;
            // 手动打开（非活跃游戏）的窗口不由看门狗关闭
            if (Data.ActiveGameUuid != game.Uuid) return;
            if (await IsGameProcessRunningAsync(game)) continue;
            DismissFloatWindow(game.Uuid);
            return;
        }
    }

    private static Task<bool> IsGameProcessRunningAsync(Galgame game)
    {
        string? installPath = game.LocalPath;
        if (string.IsNullOrEmpty(installPath)) return Task.FromResult(true); // 无法判断 → 假定运行中，交给宿主消息
        return Task.Run(() =>
        {
            foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    string? file = TryGetProcessPath(process.Id);
                    if (file is not null && file.StartsWith(installPath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // 单个进程查询失败不影响整体
                }
            }
            return false;
        });
    }

    private static string? TryGetProcessPath(int processId)
    {
        IntPtr handle = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            if (QueryFullProcessImageName(handle, 0, buffer, ref size)) return buffer.ToString();
        }
        finally
        {
            CloseHandle(handle);
        }
        return null;
    }

    private static async Task BumpTopmostAfterMagpieAsync(Window window)
    {
        try
        {
            await Task.Delay(3500);
            if (!window.AppWindow.IsVisible) return; // 窗口已隐藏
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }
        catch
        {
            // 窗口已关闭，忽略
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, System.Text.StringBuilder exeName, ref uint size);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    #region 2DFan

    private async Task Df2anAsync(Galgame game, StackPanel content, TextBlock status, PanelState state)
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
            await SearchAndShowAsync(game, content, status, state);
        }
        catch (Exception e)
        {
            status.Text = $"获取失败：{e.Message}";
        }
    }

    private async Task SearchAndShowAsync(Galgame game, StackPanel content, TextBlock status, PanelState state)
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
                    PushBack(state);
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
                                PushBack(state);
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

    private async Task YmgalAsync(Galgame game, StackPanel content, TextBlock status, PanelState state)
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
                PushBack(state);
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

    private static void AddResultRow(StackPanel content, int index, string text, Action onClick, string? tag = null)
    {
        Button button = new()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 4, 8, 4),
            Tag = tag,
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
        => AddResultRow(content, 0, "在浏览器打开站点页面", () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url)), "openSite");

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

    private static async Task<string?> ProbeAvailableDomainAsync()
    {
        foreach (string domain in Known2DfanDomains)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var response = await Http.GetAsync($"{domain}/subjects",
                    HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (response.IsSuccessStatusCode) return domain;
            }
            catch
            {
                // 尝试下一个域名
            }
        }
        return null;
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

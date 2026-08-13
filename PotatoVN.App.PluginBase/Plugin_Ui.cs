using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using GalgameManager.Enums;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Contracts.PluginUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using PotatoVN.App.PluginBase.Controls.Prefabs;
using PotatoVN.App.PluginBase.Models;
using IElement = AngleSharp.Dom.IElement;
using INode = AngleSharp.Dom.INode;
using IText = AngleSharp.Dom.IText;

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
    private static readonly ConcurrentDictionary<string, CachedPage> PageCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Guid, Window> FloatingWindows = [];
    private static readonly Dictionary<Guid, PanelState> FloatingPanels = [];
    private static readonly Dictionary<Guid, CancellationTokenSource> FloatingWatchdogs = [];

    private sealed record PanelSnapshot(
        string StatusText,
        Visibility StatusVisibility,
        string? OpenSiteUrl,
        UIElement[] Children);

    private sealed record CachedPage(string Content, DateTimeOffset ExpiresAt);

    private const int MaxCachedPages = 32;
    private const int MaxCachedPageLength = 1024 * 1024;
    private const int MaxResponseLength = 5 * 1024 * 1024;
    private static readonly TimeSpan PageCacheDuration = TimeSpan.FromMinutes(10);

    private sealed class PanelState
    {
        public string CurrentSource = "2dfan";
        public string? OpenSiteUrl;
        public Button? SiteButton;
        public ToggleButton? Source2dfan;
        public ToggleButton? SourceYmgal;
        public ProgressRing? ProgressRing;
        public StackPanel? Content;
        public TextBlock? Status;
        public Button? OverlayBackButton;
        public Button? BarBackButton;
        public CancellationTokenSource? RequestCancellation;
        public Action? RefreshBarWidth;
        public Stack<PanelSnapshot> BackStack = [];
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
            try
            {
                string? found = await ProbeAvailableDomainAsync();
                if (found is null)
                {
                    Plugin.HostApi.Info(InfoBarSeverity.Warning,
                        "未检测到可用的 2DFan 域名，请检查网络");
                    return;
                }
                Data.Domain = found;
                domainBox.SelectedItem = found;
                Plugin.HostApi.Info(InfoBarSeverity.Success,
                    $"已切换到可用域名：{found}");
            }
            catch (Exception e)
            {
                Plugin.HostApi.Info(InfoBarSeverity.Warning, "域名检测失败", e.Message);
            }
            finally
            {
                detectButton.IsEnabled = true;
            }
        };
        StackPanel domainStack = new() { Spacing = 6 };
        domainStack.Children.Add(domainBox);
        domainStack.Children.Add(detectButton);

        Button clearButton = new() { Content = "清除已保存的关联" };
        StdSetting? associationSetting = null;
        clearButton.Click += (_, _) =>
        {
            Data.TopicUrlMap.Clear();
            SaveData();
            if (associationSetting is not null)
                associationSetting.Description = "当前没有已关联的游戏";
            clearButton.IsEnabled = false;
        };

        StdStackPanel panel = new();
        ToggleSwitch autoOpenToggle = new() { IsOn = Data.AutoOpenFloatOnLaunch };
        autoOpenToggle.Toggled += (_, _) =>
        {
            Data.AutoOpenFloatOnLaunch = autoOpenToggle.IsOn;
            if (!autoOpenToggle.IsOn)
            {
                Data.ActiveGameUuid = null;
                Data.ActiveGamePlayedAt = null;
            }
        };
        panel.Children.Add(new StdSetting("启动游戏时自动打开攻略浮窗", "启动游戏时自动弹出置顶攻略窗口，可拖动到游戏旁", autoOpenToggle));
        ToggleSwitch minimalToggle = new() { IsOn = Data.MinimalMode };
        minimalToggle.Toggled += (_, _) => Data.MinimalMode = minimalToggle.IsOn;
        panel.Children.Add(new StdSetting("极简模式",
            "浮窗默认只显示攻略内容与一个切换按钮，其余按钮隐藏；可在浮窗内随时切换", minimalToggle));
        panel.Children.Add(new StdSetting("默认攻略来源",
            "自动：游戏有月幕档案编号时用月幕，否则用 2DFan。可在游戏页内手动切换单个游戏的来源。", sourceBox));
        panel.Children.Add(new StdSetting("2DFan 域名",
            "官方域 2dfan.com 在中国大陆无法访问，域名失效时可点“自动检测”选择可用备用域", domainStack));
        associationSetting = new StdSetting("已保存的攻略关联",
            Data.TopicUrlMap.Count == 0
                ? "当前没有已关联的游戏"
                : $"共 {Data.TopicUrlMap.Count} 个游戏已关联攻略页，清除后需重新检索", clearButton);
        clearButton.IsEnabled = Data.TopicUrlMap.Count > 0;
        panel.Children.Add(associationSetting);
        return panel;
    }

    public Task<FrameworkElement> CreateRightPanelUiAsync(Galgame game)
    {
        Button floatButton = new()
        {
            Content = "打开攻略浮窗",
            MinHeight = 28,
            Padding = new Thickness(12, 3, 12, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        floatButton.Click += (_, _) => OpenFloatingWindow(game);
        return Task.FromResult<FrameworkElement>(floatButton);
    }

    private FrameworkElement BuildGuidePanel(Galgame game, bool fillHeight, out PanelState state)
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

        Grid root = new()
        {
            RowSpacing = 2,
            MaxWidth = 380,
            VerticalAlignment = fillHeight ? VerticalAlignment.Stretch : VerticalAlignment.Top,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // status
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // scroll

        ScrollViewer scroll = new()
        {
            Content = content,
            MaxHeight = fillHeight ? double.PositiveInfinity : 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        };
        Grid.SetRow(status, 0);
        root.Children.Add(status);
        Grid.SetRow(scroll, 1);
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
        CancellationToken token = BeginPanelRequest(state);
        if (source is not null)
        {
            state.CurrentSource = source;
            ResetNavigationState(state);
        }
        UpdateSourceToggleState(state);
        status.Visibility = Visibility.Visible; // 加载/列表状态需显示 status 行
        if (state.ProgressRing is not null)
        {
            state.ProgressRing.Visibility = Visibility.Visible;
            state.ProgressRing.IsActive = true;
            state.RefreshBarWidth?.Invoke();
        }
        try
        {
            if (state.CurrentSource == "ymgal")
            {
                try
                {
                    await YmgalAsync(game, content, status, state, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    token.ThrowIfCancellationRequested();
                    // 月幕不可用时自动回退到 2DFan，避免玩家无攻略可用
                    state.CurrentSource = "2dfan";
                    UpdateSourceToggleState(state);
                    status.Text = $"月幕加载失败，已自动切换 2DFan：{e.Message}";
                    await Df2anAsync(game, content, status, state, token);
                }
            }
            else
            {
                await Df2anAsync(game, content, status, state, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 新操作或窗口关闭已经取代本次加载。
        }
        catch (Exception e)
        {
            status.Text = $"加载失败：{e.Message}";
        }
        finally
        {
            if (IsCurrentPanelRequest(state, token) && state.ProgressRing is not null)
            {
                state.ProgressRing.IsActive = false;
                state.ProgressRing.Visibility = Visibility.Collapsed;
                state.RefreshBarWidth?.Invoke();
            }
        }
    }

    private void UpdateSourceToggleState(PanelState state)
    {
        if (state.Source2dfan is not null) state.Source2dfan.IsChecked = state.CurrentSource == "2dfan";
        if (state.SourceYmgal is not null) state.SourceYmgal.IsChecked = state.CurrentSource == "ymgal";
    }

    private static void UpdateBackButtons(PanelState state, bool visible)
    {
        if (state.OverlayBackButton is not null) state.OverlayBackButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (state.BarBackButton is not null) state.BarBackButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        state.RefreshBarWidth?.Invoke();
    }

    private static CancellationToken BeginPanelRequest(PanelState state)
    {
        state.RequestCancellation?.Cancel();
        state.RequestCancellation?.Dispose();
        state.RequestCancellation = new CancellationTokenSource();
        return state.RequestCancellation.Token;
    }

    private static bool IsCurrentPanelRequest(PanelState state, CancellationToken token) =>
        state.RequestCancellation is { } current && current.Token == token && !token.IsCancellationRequested;

    private static void CancelPanelRequest(PanelState state)
    {
        state.RequestCancellation?.Cancel();
        state.RequestCancellation?.Dispose();
        state.RequestCancellation = null;
    }

    private static void ResetNavigationState(PanelState state)
    {
        state.BackStack.Clear();
        SetOpenSite(state, null);
        UpdateBackButtons(state, false);
    }

    private static void PushBack(PanelState state)
    {
        if (state.Content is null || state.Status is null) return;
        state.BackStack.Push(new PanelSnapshot(
            state.Status.Text,
            state.Status.Visibility,
            state.OpenSiteUrl,
            state.Content.Children.ToArray()));
        UpdateBackButtons(state, true);
    }

    private static void PopBack(PanelState state)
    {
        if (state.Content is null || state.Status is null || state.BackStack.Count == 0) return;
        CancelPanelRequest(state);
        if (state.ProgressRing is not null)
        {
            state.ProgressRing.IsActive = false;
            state.ProgressRing.Visibility = Visibility.Collapsed;
            state.RefreshBarWidth?.Invoke();
        }
        PanelSnapshot snapshot = state.BackStack.Pop();
        state.Content.Children.Clear();
        foreach (UIElement child in snapshot.Children) state.Content.Children.Add(child);
        state.Status.Text = snapshot.StatusText;
        state.Status.Visibility = snapshot.StatusVisibility;
        SetOpenSite(state, snapshot.OpenSiteUrl);
        UpdateBackButtons(state, state.BackStack.Count > 0);
    }

    private static FontIcon CreateGlyphIcon(string glyph)
    {
        return new FontIcon
        {
            Glyph = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
            FontSize = 12,
        };
    }

    private bool OpenFloatingWindow(Galgame game, bool notifyOnFailure = true)
    {
        try
        {
            if (FloatingWindows.TryGetValue(game.Uuid, out Window? existingWindow))
            {
                bool wasHidden = !existingWindow.AppWindow.IsVisible;
                existingWindow.Activate();
                if (wasHidden && FloatingPanels.TryGetValue(game.Uuid, out PanelState? existingPanel) &&
                    existingPanel.Content is not null && existingPanel.Status is not null)
                {
                    _ = ShowSourceAsync(game, existingPanel.Content, existingPanel.Status,
                        existingPanel.CurrentSource, existingPanel);
                }
                StartFloatWatchdog(game);
                return true;
            }

            Window window = new() { Title = $"攻略 - {game.Name.Value}" };

            OverlappedPresenter? presenter = window.AppWindow.Presenter as OverlappedPresenter;
            if (presenter is not null)
            {
                presenter.IsAlwaysOnTop = Data.PinFloatOnTop;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }
            // AppWindow 的尺寸单位是物理像素，XAML 布局使用逻辑像素。
            // 缩放比例必须在 Content 加载后从 XamlRoot 读取，不能在这里提前固定为 1。
            double measuredBarWidth = 420;
            double GetScale()
            {
                double xamlScale =
                    (window.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 0;
                if (xamlScale > 0) return xamlScale;

                // Content 尚未 Loaded 时使用 HWND 的当前显示器 DPI，避免首次显示先窄后宽。
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                uint dpi = GetDpiForWindow(hwnd);
                return dpi > 0 ? dpi / 96.0 : 1.0;
            }

            int ToPhysicalPixels(double logicalPixels) =>
                Math.Max(1, (int)Math.Ceiling(logicalPixels * GetScale()));

            void ApplyMinSize(bool minimal)
            {
                if (presenter is not null)
                    presenter.PreferredMinimumWidth = minimal
                        ? 0
                        : ToPhysicalPixels(measuredBarWidth);
            }

            ApplyMinSize(Data.MinimalMode);

            FrameworkElement panel = BuildGuidePanel(game, true, out PanelState state);
            PanelState panelState = state;

            // 来源切换（2DFan/月幕，与返回/极简同行）
            ToggleButton source2dfan = new()
            {
                Content = "2DFan",
                FontSize = 12,
                MinHeight = 24,
                Padding = new Thickness(8, 2, 8, 2),
                IsChecked = panelState.CurrentSource == "2dfan",
            };
            ToggleButton sourceYmgal = new()
            {
                Content = "月幕",
                FontSize = 12,
                MinHeight = 24,
                Padding = new Thickness(8, 2, 8, 2),
                IsChecked = panelState.CurrentSource == "ymgal",
            };
            panelState.Source2dfan = source2dfan;
            panelState.SourceYmgal = sourceYmgal;
            source2dfan.Click += (_, _) => _ = ShowSourceAsync(game, panelState.Content!, panelState.Status!, "2dfan", panelState);
            sourceYmgal.Click += (_, _) => _ = ShowSourceAsync(game, panelState.Content!, panelState.Status!, "ymgal", panelState);

            // 返回（回退一层；与极简同行，最左侧）
            Button backButton = new()
            {
                Content = CreateGlyphIcon("\uE72B"),
                MinHeight = 24,
                Padding = new Thickness(8, 2, 8, 2),
                Visibility = Visibility.Collapsed,
            };
            ToolTipService.SetToolTip(backButton, "返回");
            backButton.Click += (_, _) => PopBack(panelState);
            panelState.BarBackButton = backButton;

            // 极简按钮（完整模式回极简；BackToWindow 四角内收，与展开 FullScreen 成对）
            Button minimalButton = new() { Content = CreateGlyphIcon("\uE73F"), MinHeight = 24, Padding = new Thickness(8, 2, 8, 2) };
            ToolTipService.SetToolTip(minimalButton, "极简");

            // 钉子置顶
            FontIcon pinIcon = new()
            {
                Glyph = Data.PinFloatOnTop ? "\uE718" : "\uE77A",
                FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
                FontSize = 12,
            };
            ToggleButton pinButton = new() { MinHeight = 24, Padding = new Thickness(8, 2, 8, 2), IsChecked = Data.PinFloatOnTop };
            pinButton.Content = pinIcon;

            // 重新检索（↻，与返回/网站同行，位于返回与网站中间）
            Button refreshButton = new() { Content = "↻", FontSize = 12, MinHeight = 24, Padding = new Thickness(8, 2, 8, 2) };
            ToolTipService.SetToolTip(refreshButton, "重新检索");
            refreshButton.Click += (_, _) =>
            {
                ClearPageCache(panelState.CurrentSource);
                _ = ShowSourceAsync(game, panelState.Content!, panelState.Status!, panelState.CurrentSource,
                    panelState);
            };

            // 浏览器打开站点（符号化，替代内容区文字按钮）
            Button siteButton = new()
            {
                Content = "⇱",
                FontSize = 12,
                MinHeight = 24,
                Padding = new Thickness(8, 2, 8, 2),
                Visibility = Visibility.Collapsed,
            };
            ToolTipService.SetToolTip(siteButton, "在浏览器打开站点页面");
            siteButton.Click += async (_, _) =>
            {
                if (panelState.OpenSiteUrl is not { } siteUrl) return;
                try
                {
                    bool launched = await Windows.System.Launcher.LaunchUriAsync(new Uri(siteUrl));
                    if (!launched)
                        Plugin.HostApi.Info(InfoBarSeverity.Warning, "无法打开浏览器", siteUrl);
                }
                catch (Exception e)
                {
                    Plugin.HostApi.Info(InfoBarSeverity.Warning, "无法打开浏览器", e.Message);
                }
            };
            panelState.SiteButton = siteButton;

            // 关闭（隐藏式，避免最后一个窗口导致应用退出；叉号）
            Button closeButton = new() { Content = CreateGlyphIcon("\uE8BB"), MinHeight = 24, Padding = new Thickness(8, 2, 8, 2) };
            ToolTipService.SetToolTip(closeButton, "关闭");

            // 加载进度圈（原在 header，随来源按钮同移入 bar）
            ProgressRing progressRing = new()
            {
                Width = 16,
                Height = 16,
                IsActive = false,
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
            };
            panelState.ProgressRing = progressRing;

            StackPanel bar = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            bar.Children.Add(backButton); // 返回按钮：bar 最左侧，与极简同行
            bar.Children.Add(refreshButton); // 重新检索：返回与网站中间
            bar.Children.Add(source2dfan);  // 来源：2DFan
            bar.Children.Add(sourceYmgal);  // 来源：月幕
            bar.Children.Add(siteButton); // 网站打开：极简按钮左侧
            bar.Children.Add(minimalButton);
            bar.Children.Add(pinButton);
            bar.Children.Add(closeButton);
            bar.Children.Add(progressRing); // 加载进度圈

            // 叠加层：仅极简模式，悬浮内容右上（hover 显隐）
            StackPanel overlay = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
            };

            void ApplyCurrentTitleBar(bool minimal)
            {
                double clientWidth = (window.Content as FrameworkElement)?.ActualWidth ??
                    window.AppWindow.Size.Width / GetScale();
                double dragWidth;
                if (minimal)
                {
                    dragWidth = Math.Max(0, clientWidth - 72);
                }
                else
                {
                    try
                    {
                        UIElement? root = window.Content as UIElement;
                        dragWidth = root is null
                            ? Math.Max(0, clientWidth - measuredBarWidth)
                            : Math.Max(0, bar.TransformToVisual(root)
                                .TransformPoint(new Windows.Foundation.Point()).X);
                    }
                    catch (Exception)
                    {
                        dragWidth = Math.Max(0, clientWidth - measuredBarWidth);
                    }
                }
                ApplyTitleBar(window, dragWidth, GetScale());
            }

            void ApplyMode()
            {
                bool minimal = Data.MinimalMode;
                bar.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
                overlay.Visibility = Visibility.Collapsed; // 叠加层仅极简+hover 显示
                UpdateBackButtons(panelState, panelState.BackStack.Count > 0);
                ApplyMinSize(minimal); // 模式切换时同步更新最小宽度，避免 Resize 被旧下限拦截
                double logicalWidth = minimal ? 300 : Math.Max(480, measuredBarWidth);
                double logicalHeight = minimal ? 380 : 700;
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    ToPhysicalPixels(logicalWidth), ToPhysicalPixels(logicalHeight)));
                ApplyCurrentTitleBar(minimal);
            }

            void UpdatePinState()
            {
                pinIcon.Glyph = pinButton.IsChecked == true ? "\uE718" : "\uE77A";
                if (presenter is not null) presenter.IsAlwaysOnTop = pinButton.IsChecked == true;
                Data.PinFloatOnTop = pinButton.IsChecked == true;
            }

            minimalButton.Click += (_, _) => { Data.MinimalMode = true; ApplyMode(); };
            pinButton.Checked += (_, _) => UpdatePinState();
            pinButton.Unchecked += (_, _) => UpdatePinState();
            closeButton.Click += (_, _) => DismissFloatWindow(game.Uuid);

            Grid shell = new() { Padding = new Thickness(4) };
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 内容叠加区：panel 铺底 + overlay 悬浮右上
            Grid contentGrid = new();
            contentGrid.Children.Add(panel);
            panel.MaxWidth = double.PositiveInfinity; // 填满窗口宽，消除侧边空白
            panel.HorizontalAlignment = HorizontalAlignment.Stretch;
            panel.VerticalAlignment = VerticalAlignment.Stretch;

            Button overlayBackButton = new() { Content = CreateGlyphIcon("\uE72B"), MinHeight = 24, Padding = new Thickness(8, 2, 8, 2) };
            ToolTipService.SetToolTip(overlayBackButton, "返回");
            overlayBackButton.Click += (_, _) => PopBack(state);
            Button expandButton = new() { Content = CreateGlyphIcon("\uE740"), MinHeight = 24, Padding = new Thickness(8, 2, 8, 2), Opacity = 0.6 };
            ToolTipService.SetToolTip(expandButton, "展开");
            expandButton.Click += (_, _) => { Data.MinimalMode = false; ApplyMode(); };
            overlay.Children.Add(overlayBackButton);
            overlay.Children.Add(expandButton);
            contentGrid.Children.Add(overlay);
            state.OverlayBackButton = overlayBackButton;

            shell.Children.Add(bar);         // Row0：完整模式按钮行
            Grid.SetRow(bar, 0);
            shell.Children.Add(contentGrid); // Row1
            Grid.SetRow(contentGrid, 1);

            shell.PointerEntered += (_, _) => { if (Data.MinimalMode) overlay.Visibility = Visibility.Visible; };
            shell.PointerExited += (_, _) => { if (Data.MinimalMode) overlay.Visibility = Visibility.Collapsed; };

            try
            {
                window.SystemBackdrop = new MicaBackdrop();
            }
            catch (Exception) { }

            window.Content = shell;

            // 按钮会随导航状态动态显隐，因此不能只在首次 Loaded 时测量一次。
            // 每次布局后汇总所有可见按钮的固有宽度；只有结果变化时才更新窗口，避免布局循环。
            void RefreshBarWidth()
            {
                if (bar.Visibility != Visibility.Visible) return;

                FrameworkElement[] visibleChildren = bar.Children
                    .OfType<FrameworkElement>()
                    .Where(child => child.Visibility == Visibility.Visible)
                    .ToArray();
                if (visibleChildren.Length == 0) return;

                double childrenWidth = visibleChildren.Sum(child =>
                    Math.Max(child.ActualWidth, child.DesiredSize.Width));
                double spacingWidth = Math.Max(0, visibleChildren.Length - 1) * bar.Spacing;
                double outerLogicalWidth = window.AppWindow.Size.Width / GetScale();
                double nonClientWidth = Math.Max(0, outerLogicalWidth - shell.ActualWidth);
                // StackPanel 靠右排列；窗口最窄时，剩余空间正好等于 shell 左内边距，
                // 从而与右内边距形成等宽缝隙。PreferredMinimumWidth 限制的是包含
                // resize border 的外部宽度，因此还需补上实测的非客户区宽度。
                double requiredWidth = Math.Ceiling(childrenWidth + spacingWidth + shell.Padding.Left +
                    shell.Padding.Right + nonClientWidth);
                if (requiredWidth <= 0 || Math.Abs(requiredWidth - measuredBarWidth) < 0.5) return;

                measuredBarWidth = requiredWidth;
                ApplyMinSize(minimal: false);

                int requiredPhysicalWidth = ToPhysicalPixels(measuredBarWidth);
                if (!Data.MinimalMode && window.AppWindow.Size.Width < requiredPhysicalWidth)
                {
                    window.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                        requiredPhysicalWidth, window.AppWindow.Size.Height));
                }
                ApplyCurrentTitleBar(minimal: false);
            }

            bool refreshQueued = false;
            void QueueBarWidthRefresh()
            {
                if (refreshQueued) return;
                refreshQueued = true;
                window.DispatcherQueue.TryEnqueue(() =>
                {
                    refreshQueued = false;
                    RefreshBarWidth();
                });
            }

            panelState.RefreshBarWidth = QueueBarWidthRefresh;
            bar.SizeChanged += (_, _) => QueueBarWidthRefresh();
            shell.SizeChanged += (_, _) => ApplyCurrentTitleBar(Data.MinimalMode);
            shell.Loaded += (_, _) =>
            {
                // Loaded 时才能取得正确 DPI；再等一次布局循环获得按钮实际宽度。
                window.DispatcherQueue.TryEnqueue(() =>
                {
                    RefreshBarWidth();
                    ApplyMode();
                });
            };

            FloatingWindows[game.Uuid] = window;
            FloatingPanels[game.Uuid] = panelState;
            window.Closed += (_, _) => RemoveFloatingWindow(game.Uuid);
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
            StartFloatWatchdog(game);
            ApplyMode(); // 初始按数据应用
            return true;
        }
        catch (Exception e)
        {
            if (notifyOnFailure)
            {
                Plugin.HostApi.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                    "攻略浮窗暂不可用", e.Message, 3000);
            }
            return false;
        }
    }

    private async Task<bool> OpenFloatingWindowOnMainThreadAsync(Galgame game, bool notifyOnFailure,
        CancellationToken token)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            HostApi.InvokeOnMainThread(() =>
            {
                if (token.IsCancellationRequested)
                {
                    completion.TrySetCanceled(token);
                    return;
                }
                try
                {
                    completion.TrySetResult(OpenFloatingWindow(game, notifyOnFailure));
                }
                catch (Exception e)
                {
                    completion.TrySetException(e);
                }
            });
        }
        catch (Exception e)
        {
            completion.TrySetException(e);
        }
        return await completion.Task.WaitAsync(token);
    }

    private async Task<bool> OpenFloatingWindowWithRetryAsync(Galgame game, CancellationToken token)
    {
        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (Data.ActiveGameUuid != game.Uuid) return false;
            try
            {
                if (await OpenFloatingWindowOnMainThreadAsync(game, notifyOnFailure: false, token)) return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // 宿主 UI 尚未完全就绪时短暂等待并重试。
            }

            if (attempt < maxAttempts) await Task.Delay(500, token);
        }

        Plugin.HostApi.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
            "攻略浮窗自动打开失败", "可在游戏详情页中手动打开", 5000);
        return false;
    }

    private async Task OpenFloatingWindowDelayedAsync(Galgame game, CancellationToken token)
    {
        try
        {
            await Task.Delay(2200, token); // 等宿主 SetWindowMode 执行完（SystemTray 模式下旧进程此时已退出，窗口不会在旧进程出现）
            if (Data.ActiveGameUuid != game.Uuid)
            {
                // 2.2s 内切了游戏/停止：放弃弹窗（诊断用，非错误）
                Plugin.HostApi.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                    $"自动弹窗已放弃：2.2s 内 ActiveGameUuid 已变更（期望 {game.Uuid}）", null, 3000);
                return;
            }
            await OpenFloatingWindowWithRetryAsync(game, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Plugin.HostApi.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                $"自动弹窗失败：{e.Message}", null, 3000);
        }
    }

    private static void CloseFloatingWindow(Guid uuid) => DismissFloatWindow(uuid);

    private static void CloseAllFloatingWindows()
    {
        Window[] windows = FloatingWindows.Values.ToArray();
        foreach (PanelState state in FloatingPanels.Values) CancelPanelRequest(state);
        foreach (CancellationTokenSource cts in FloatingWatchdogs.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        FloatingWindows.Clear();
        FloatingPanels.Clear();
        FloatingWatchdogs.Clear();
        PageCache.Clear();
        foreach (Window window in windows)
        {
            window.Close();
        }
    }

    private static void DismissFloatWindow(Guid uuid)
    {
        if (FloatingPanels.TryGetValue(uuid, out PanelState? state)) CancelPanelRequest(state);
        StopFloatWatchdog(uuid);
        if (FloatingWindows.TryGetValue(uuid, out Window? window))
            window.AppWindow.Hide();
    }

    private static void RemoveFloatingWindow(Guid uuid)
    {
        if (FloatingPanels.Remove(uuid, out PanelState? state)) CancelPanelRequest(state);
        StopFloatWatchdog(uuid);
        FloatingWindows.Remove(uuid);
    }

    private static void ApplyTitleBar(Window window, double dragWidthLogical, double scale)
    {
        try
        {
            var titleBar = window.AppWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            try { titleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed; } catch { } // Win10 忽略
            // dragWidthLogical 直接来自按钮行的实际左边界，完整覆盖空白且不压住按钮。
            int dragWidth = Math.Max(0, (int)Math.Floor(dragWidthLogical * scale));
            int dragHeight = Math.Max(1, (int)Math.Ceiling(28 * scale));
            titleBar.SetDragRectangles(dragWidth > 0
                ? [new Windows.Graphics.RectInt32(0, 0, dragWidth, dragHeight)]
                : []);
        }
        catch (Exception)
        {
            // 标题栏设置失败不影响功能
        }
    }

    private void StartFloatWatchdog(Galgame game)
    {
        StopFloatWatchdog(game.Uuid);
        var cts = new CancellationTokenSource();
        FloatingWatchdogs[game.Uuid] = cts;
        _ = FloatWatchdogAsync(game, cts.Token);
    }

    private static void StopFloatWatchdog(Guid uuid)
    {
        if (!FloatingWatchdogs.Remove(uuid, out CancellationTokenSource? cts)) return;
        cts.Cancel();
        cts.Dispose();
    }

    private async Task FloatWatchdogAsync(Galgame game, CancellationToken token)
    {
        bool wasRunning = false;
        var startTime = DateTime.UtcNow;
        try
        {
            while (true)
            {
                // 宿主停止消息是主要关闭路径；这里每 5 秒低频兜底。
                await Task.Delay(5000, token);
                if (!FloatingWindows.TryGetValue(game.Uuid, out Window? window) ||
                    !window.AppWindow.IsVisible) return;
                // 手动打开（非活跃游戏）的窗口不由看门狗关闭
                if (Data.ActiveGameUuid != game.Uuid) return;
                bool running = await IsGameProcessRunningAsync(game, token);
                // 曾运行 → 现在消失 = 真退出，兜底关窗
                if (!running && wasRunning)
                {
                    DismissFloatWindow(game.Uuid);
                    return;
                }
                // 从未匹配到进程且已超 60s：视为退出兜底关窗（覆盖 LE/包装器路径不匹配场景）
                if (!running && !wasRunning && DateTime.UtcNow - startTime > TimeSpan.FromSeconds(60))
                {
                    DismissFloatWindow(game.Uuid);
                    return;
                }
                wasRunning = running;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 窗口隐藏或关闭。
        }
        catch (Exception)
        {
            // 宿主停止消息仍会负责关闭窗口；看门狗异常不应影响插件进程。
        }
    }

    private static Task<bool> IsGameProcessRunningAsync(Galgame game, CancellationToken token)
    {
        string? installPath = game.LocalPath;
        if (string.IsNullOrEmpty(installPath)) return Task.FromResult(true); // 无法判断 → 假定运行中，交给宿主消息
        return Task.Run(() =>
        {
            string processName = System.IO.Path.GetFileNameWithoutExtension(game.ProcessName ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(processName))
            {
                foreach (System.Diagnostics.Process namedProcess in
                         System.Diagnostics.Process.GetProcessesByName(processName))
                {
                    using (namedProcess)
                    {
                        if (!namedProcess.HasExited) return true;
                    }
                }
            }

            foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcesses())
            {
                using (process)
                {
                    token.ThrowIfCancellationRequested();
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
            }
            return false;
        }, token);
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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

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

    private async Task Df2anAsync(Galgame game, StackPanel content, TextBlock status, PanelState state,
        CancellationToken token)
    {
        status.Text = "加载中…";
        content.Children.Clear();
        try
        {
            if (Data.TopicUrlMap.TryGetValue(game.Uuid, out string? cachedUrl) && !string.IsNullOrEmpty(cachedUrl))
            {
                string url = RewriteDomain(cachedUrl);
                if (url != cachedUrl)
                {
                    Data.TopicUrlMap[game.Uuid] = url;
                    SaveData();
                }
                // 缓存直达：先压入"重新检索"返回层，返回后仍可更换攻略
                ShowCachedPlaceholder(game, content, status, state);
                PushBack(state);
                await ShowTopicAsync(url, content, status, state, token);
                return;
            }
            await SearchAndShowAsync(game, content, status, state, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 新操作已经取代本次加载。
        }
        catch (Exception e)
        {
            status.Text = $"获取失败：{e.Message}";
        }
    }

    private void ShowCachedPlaceholder(Galgame game, StackPanel content, TextBlock status, PanelState state)
    {
        status.Text = "已关联攻略页（缓存直达），可重新检索更换：";
        status.Visibility = Visibility.Visible;
        content.Children.Clear();
        AddResultRow(content, 0, "重新检索攻略", () =>
        {
            Data.TopicUrlMap.Remove(game.Uuid);
            SaveData();
            CancellationToken token = BeginPanelRequest(state);
            _ = SearchAndShowAsync(game, content, status, state, token);
            return Task.CompletedTask;
        });
    }

    private async Task SearchAndShowAsync(Galgame game, StackPanel content, TextBlock status, PanelState state,
        CancellationToken token)
    {
        string query = BuildQuery(game);
        SetOpenSite(state, null);
        status.Text = $"正在 2DFan 检索：{query}";
        content.Children.Clear();
        try
        {
            List<(string Title, string Url)> subjects = await SearchSubjectsAsync(query, token);
            token.ThrowIfCancellationRequested();
            if (subjects.Count == 0)
            {
                ShowEmpty(content, status, "没有找到结果。",
                    $"{Data.Domain}/subjects/search?keyword={Uri.EscapeDataString(query)}", state);
                return;
            }
            status.Text = $"找到 {subjects.Count} 个条目，点击获取攻略列表";
            for (int i = 0; i < subjects.Count; i++)
            {
                (string title, string url) = subjects[i];
                AddResultRow(content, i + 1, title, async () =>
                {
                    CancellationToken itemToken = BeginPanelRequest(state);
                    PushBack(state);
                    content.Children.Clear();
                    status.Text = $"正在获取「{title}」的攻略…";
                    try
                    {
                        List<(string Title, string Url)> topics = await GetTopicsAsync(url, itemToken);
                        itemToken.ThrowIfCancellationRequested();
                        if (topics.Count == 0)
                        {
                            ShowEmpty(content, status, "该条目暂无攻略。", url, state);
                            return;
                        }
                        status.Text = $"「{title}」的攻略：";
                        for (int j = 0; j < topics.Count; j++)
                        {
                            (string topicTitle, string topicUrl) = topics[j];
                            AddResultRow(content, j + 1, topicTitle, async () =>
                            {
                                CancellationToken topicToken = BeginPanelRequest(state);
                                PushBack(state);
                                Data.TopicUrlMap[game.Uuid] = topicUrl;
                                SaveData();
                                await ShowTopicAsync(topicUrl, content, status, state, topicToken);
                            });
                        }
                    }
                    catch (OperationCanceledException) when (itemToken.IsCancellationRequested)
                    {
                        // 新操作已经取代本次加载。
                    }
                    catch (Exception e)
                    {
                        status.Text = $"获取攻略列表失败：{e.Message}";
                    }
                });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 新操作已经取代本次检索。
        }
        catch (Exception e)
        {
            status.Text = $"检索失败：{e.Message}";
        }
    }

    private async Task ShowTopicAsync(string url, StackPanel content, TextBlock status, PanelState state,
        CancellationToken token)
    {
        SetOpenSite(state, null);
        content.Children.Clear();
        status.Text = "正在加载攻略…";
        try
        {
            string html = await Get2DfanAsync(url, token);
            string text = await Task.Run(() =>
            {
                var doc = new HtmlParser().ParseDocument(html);
                var element = doc.QuerySelector("div.topic-content");
                return element is null ? string.Empty : HtmlToText(element);
            }, token);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowEmpty(content, status, "攻略内容为空。", url, state);
                return;
            }
            ShowText(text, content, status, url, state);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 新操作已经取代本次加载。
        }
        catch (Exception e)
        {
            status.Text = $"加载失败：{e.Message}";
            SetOpenSite(state, url);
        }
    }

    private async Task<List<(string Title, string Url)>> SearchSubjectsAsync(string query, CancellationToken token)
    {
        string html = await Get2DfanAsync($"/subjects/search?keyword={Uri.EscapeDataString(query)}", token);
        return await Task.Run(() =>
        {
            var doc = new HtmlParser().ParseDocument(html);
            var list = new List<(string, string)>();
            var seen = new HashSet<string>();
            foreach (var anchor in doc.QuerySelectorAll("a[href]"))
            {
                string href = anchor.GetAttribute("href") ?? string.Empty;
                if (!IsNumericPath(href, "/subjects/")) continue;
                string anchorTitle = anchor.TextContent.Trim();
                if (anchorTitle.Length < 2) continue;
                string full = $"{Data.Domain}{href}";
                if (seen.Add(full)) list.Add((anchorTitle, full));
            }
            return list;
        }, token);
    }

    private async Task<List<(string Title, string Url)>> GetTopicsAsync(string subjectUrl, CancellationToken token)
    {
        string html = await Get2DfanAsync(subjectUrl, token);
        return await Task.Run(() =>
        {
            var doc = new HtmlParser().ParseDocument(html);
            var list = new List<(string, string)>();
            var seen = new HashSet<string>();
            foreach (var anchor in doc.QuerySelectorAll("a[href]"))
            {
                string href = anchor.GetAttribute("href") ?? string.Empty;
                if (!IsNumericPath(href, "/topics/")) continue;
                string anchorTitle = anchor.TextContent.Trim();
                if (anchorTitle.Length < 2 || anchorTitle == "查看完整介绍") continue;
                string full = $"{Data.Domain}{href}";
                if (seen.Add(full)) list.Add((anchorTitle, full));
            }
            return list;
        }, token);
    }

    #endregion

    #region 月幕 ymgal

    private async Task YmgalAsync(Galgame game, StackPanel content, TextBlock status, PanelState state,
        CancellationToken token)
    {
        SetOpenSite(state, null);
        content.Children.Clear();
        string? gidStr = game.Ids[(int)RssType.Ymgal];
        if (string.IsNullOrEmpty(gidStr) || !int.TryParse(gidStr, out int gid))
        {
            ShowEmpty(content, status, "该游戏没有月幕档案编号，可跳转月幕站内搜索。",
                $"{YmgalBase}/search?keyword={Uri.EscapeDataString(BuildQuery(game))}", state);
            return;
        }
        status.Text = "正在加载月幕文章列表…";
        List<(string Title, string Url)> articles = await GetYmgalArticlesAsync(gid, token);
        token.ThrowIfCancellationRequested();
        if (articles.Count == 0)
        {
            ShowEmpty(content, status, "月幕没有找到该游戏的文章/攻略。", $"{YmgalBase}/ga{gid}", state);
            return;
        }
        status.Text = $"月幕找到 {articles.Count} 篇相关文章（含攻略/感想）：";
        for (int i = 0; i < articles.Count; i++)
        {
            (string title, string url) = articles[i];
            AddResultRow(content, i + 1, title, async () =>
            {
                CancellationToken articleToken = BeginPanelRequest(state);
                PushBack(state);
                content.Children.Clear();
                status.Text = $"正在加载「{title}」…";
                string text;
                try
                {
                    text = await GetYmgalArticleTextAsync(url, articleToken);
                    articleToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (articleToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    status.Text = $"加载失败：{e.Message}";
                    SetOpenSite(state, url);
                    return;
                }
                if (string.IsNullOrWhiteSpace(text))
                {
                    ShowEmpty(content, status, "文章内容为空。", url, state);
                    return;
                }
                ShowText(text, content, status, url, state);
            });
        }
    }

    private async Task<List<(string Title, string Url)>> GetYmgalArticlesAsync(int gid, CancellationToken token)
    {
        string html = await GetAsync($"{YmgalBase}/ga{gid}", token);
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
                if (!IsNumericPath(href, "/co/article/")) continue;
                string title = link.TextContent.Trim();
                if (title.Length < 2) continue;
                string full = $"{YmgalBase}{href}";
                if (seen.Add(full)) list.Add((title, full));
            }
            return list;
        }, token);
    }

    private static async Task<string> GetYmgalArticleTextAsync(string url, CancellationToken token)
    {
        string html = await GetAsync(url, token);
        return await Task.Run(() =>
        {
            var doc = new HtmlParser().ParseDocument(html);
            var element = doc.QuerySelector("div.article-content");
            return element is null ? string.Empty : HtmlToText(element);
        }, token);
    }

    #endregion

    #region Shared

    private static void ShowText(string text, StackPanel content, TextBlock status, string url, PanelState state)
    {
        status.Text = string.Empty;
        status.Visibility = Visibility.Collapsed; // 正文显示时收起状态行，消除空白
        SetOpenSite(state, url);
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

    private static void ShowEmpty(StackPanel content, TextBlock status, string text, string? url, PanelState state)
    {
        status.Text = text;
        status.Visibility = Visibility.Visible;
        content.Children.Clear();
        SetOpenSite(state, url);
    }

    // 站点打开按钮：仅浮窗 bar 上"⇱"
    private static void SetOpenSite(PanelState state, string? url)
    {
        state.OpenSiteUrl = url;
        if (state.SiteButton is not null)
            state.SiteButton.Visibility = url is null ? Visibility.Collapsed : Visibility.Visible;
        state.RefreshBarWidth?.Invoke();
    }

    private static Button AddResultRow(StackPanel content, int index, string text, Func<Task> onClick,
        string? tag = null)
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
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await onClick();
            }
            catch (OperationCanceledException)
            {
                // 新操作已取代当前操作。
            }
            catch (Exception e)
            {
                Plugin.HostApi.Info(InfoBarSeverity.Warning, "攻略操作失败", e.Message);
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        content.Children.Add(button);
        return button;
    }

    private static Brush GetSecondaryBrush()
    {
        if (Application.Current?.Resources?.TryGetValue("TextFillColorSecondaryBrush", out var value) == true &&
            value is Brush brush)
            return brush;
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
    }

    private static string HtmlToText(IElement element)
    {
        var builder = new StringBuilder();
        AppendNode(element, builder);
        return string.Join("\n", builder.ToString().Split('\n')
            .Select(NormalizeInlineWhitespace)
            .Where(line => line.Length > 0));

        static void AppendNode(INode node, StringBuilder output)
        {
            if (node is IText text)
            {
                output.Append(text.Data);
                return;
            }

            if (node is not IElement current) return;
            string tag = current.LocalName;
            if (tag is "script" or "style" or "noscript") return;
            if (tag == "br")
            {
                output.AppendLine();
                return;
            }

            bool block = tag is "p" or "div" or "li" or "ul" or "ol" or "section" or "article" or
                "header" or "footer" or "blockquote" or "pre" or "table" or "tr" or "h1" or "h2" or
                "h3" or "h4" or "h5" or "h6";
            if (block && output.Length > 0 && output[^1] != '\n') output.AppendLine();
            foreach (INode child in current.ChildNodes) AppendNode(child, output);
            if (block && output.Length > 0 && output[^1] != '\n') output.AppendLine();
        }

        static string NormalizeInlineWhitespace(string line)
        {
            ReadOnlySpan<char> input = line.AsSpan().Trim();
            if (input.IsEmpty) return string.Empty;
            var output = new StringBuilder(input.Length);
            bool pendingSpace = false;
            foreach (char character in input)
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

    private static async Task<string> GetAsync(string url, CancellationToken token)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (PageCache.TryGetValue(url, out CachedPage? cached))
        {
            if (cached.ExpiresAt > now) return cached.Content;
            PageCache.TryRemove(url, out _);
        }

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxResponseLength)
            throw new HttpRequestException("响应内容过大");
        string content = await ReadContentAsync(response.Content, token);
        if (content.Length <= MaxCachedPageLength)
        {
            PageCache[url] = new CachedPage(content, now + PageCacheDuration);
            TrimPageCache();
        }
        return content;
    }

    private static async Task<string> ReadContentAsync(HttpContent content, CancellationToken token)
    {
        await using Stream source = await content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(chunk, token);
            if (read == 0) break;
            if (buffer.Length + read > MaxResponseLength)
                throw new HttpRequestException("响应内容过大");
            await buffer.WriteAsync(chunk.AsMemory(0, read), token);
        }

        Encoding encoding = Encoding.UTF8;
        string? charset = content.Headers.ContentType?.CharSet?.Trim('"');
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                // 无效或不受支持的编码声明，按 UTF-8 读取。
            }
            catch (NotSupportedException)
            {
                // 当前运行时未注册对应代码页，按 UTF-8 读取。
            }
        }
        return encoding.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static void TrimPageCache()
    {
        int removeCount = PageCache.Count - MaxCachedPages;
        if (removeCount <= 0) return;
        foreach (string key in PageCache
                     .OrderBy(pair => pair.Value.ExpiresAt)
                     .Take(removeCount)
                     .Select(pair => pair.Key))
            PageCache.TryRemove(key, out _);
    }

    private void ClearPageCache(string source)
    {
        IEnumerable<string> prefixes = source == "ymgal"
            ? [YmgalBase]
            : Known2DfanDomains.Append(Data.Domain);
        string[] normalizedPrefixes = prefixes
            .Select(prefix => prefix.TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (string key in PageCache.Keys)
        {
            if (normalizedPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                PageCache.TryRemove(key, out _);
        }
    }

    private async Task<string> Get2DfanAsync(string urlOrPath, CancellationToken token)
    {
        string path = GetPathAndQuery(urlOrPath);
        string initialDomain = Data.Domain.TrimEnd('/');
        try
        {
            return await GetAsync($"{initialDomain}{path}", token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException firstError) when (ShouldTryAlternateDomain(firstError))
        {
            string? availableDomain = await ProbeAvailableDomainAsync(token, initialDomain);
            if (availableDomain is null) throw;

            Data.Domain = availableDomain;
            return await GetAsync($"{availableDomain}{path}", token);
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            string? availableDomain = await ProbeAvailableDomainAsync(token, initialDomain);
            if (availableDomain is null) throw;

            Data.Domain = availableDomain;
            return await GetAsync($"{availableDomain}{path}", token);
        }
    }

    private static bool ShouldTryAlternateDomain(HttpRequestException error) =>
        error.StatusCode is null or System.Net.HttpStatusCode.Forbidden or
            System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests ||
        (int)error.StatusCode.Value >= 500;

    private static string GetPathAndQuery(string urlOrPath)
    {
        if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out Uri? uri)) return uri.PathAndQuery;
        return urlOrPath.StartsWith('/') ? urlOrPath : $"/{urlOrPath}";
    }

    private static bool IsNumericPath(string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length == prefix.Length) return false;
        for (int i = prefix.Length; i < value.Length; i++)
        {
            if (!char.IsAsciiDigit(value[i])) return false;
        }
        return true;
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

    private static async Task<string?> ProbeAvailableDomainAsync(CancellationToken token = default,
        string? excludedDomain = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
        List<Task<string?>> probes = Known2DfanDomains
            .Where(domain => !string.Equals(domain, excludedDomain, StringComparison.OrdinalIgnoreCase))
            .Select(domain => ProbeDomainAsync(domain, timeoutCts.Token))
            .ToList();

        while (probes.Count > 0)
        {
            Task<string?> completed = await Task.WhenAny(probes);
            probes.Remove(completed);
            string? domain = await completed;
            if (domain is not null)
            {
                await timeoutCts.CancelAsync();
                return domain;
            }
        }
        token.ThrowIfCancellationRequested();
        return null;
    }

    private static async Task<string?> ProbeDomainAsync(string domain, CancellationToken token)
    {
        try
        {
            using var response = await Http.GetAsync($"{domain}/subjects",
                HttpCompletionOption.ResponseHeadersRead, token);
            return response.IsSuccessStatusCode ? domain : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
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

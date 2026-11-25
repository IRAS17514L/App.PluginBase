using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Contracts.PluginUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using PotatoVN.App.PluginBase.Controls.Prefabs;
using PotatoVN.App.PluginBase.Guides;

namespace PotatoVN.App.PluginBase;

public partial class Plugin : IGalgamePageRightPanel
{
    private static readonly Dictionary<Guid, Window> FloatingWindows = [];
    private static readonly Dictionary<Guid, PanelState> FloatingPanels = [];
    private static readonly Dictionary<Guid, CancellationTokenSource> FloatingWatchdogs = [];

    public FrameworkElement CreateSettingUi()
    {
        ComboBox sourceBox = new() { MinWidth = 200 };
        (string Key, string Label)[] sources =
        [
            ("auto", "在线自动"), ("2dfan", "2DFan"), ("ymgal", "月幕"),
            ("local-first", "本地优先"), ("cngal", "CnGal"), ("seiya", "誠也の部屋"),
        ];
        foreach (var (_, label) in sources) sourceBox.Items.Add(label);
        sourceBox.SelectedIndex = Math.Max(0, Array.FindIndex(sources, source => source.Key == Data.DefaultSource));
        sourceBox.SelectionChanged += (_, _) =>
        {
            if (sourceBox.SelectedIndex >= 0) Data.DefaultSource = sources[sourceBox.SelectedIndex].Key;
        };

        ComboBox domainBox = new() { MinWidth = 200 };
        foreach (string candidate in Df2anGuideSource.KnownDomains)
            domainBox.Items.Add(candidate);
        if (!Df2anGuideSource.KnownDomains.Contains(Data.Domain))
            domainBox.Items.Add(Data.Domain);
        domainBox.SelectedItem = Data.Domain;
        domainBox.SelectionChanged += (_, _) =>
        {
            if (domainBox.SelectedItem is string selected && !string.IsNullOrEmpty(selected))
                Data.Domain = selected;
        };

        Button detectButton = new() { Content = "检测并切换 2DFan 域名" };
        detectButton.Click += async (_, _) =>
        {
            detectButton.IsEnabled = false;
            try
            {
                string? found = await _df2an.ProbeAsync(_lifetimeCts.Token);
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
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
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

        Button clearButton = new() { Content = "清除阅读记录" };
        StdSetting? associationSetting = null;
        clearButton.Click += (_, _) =>
        {
            Data.ReadingHistory.Clear();
            foreach (var (gameId, guide) in Data.LocalGuides)
            {
                if (guide is { Bookmark: not null })
                    Data.LocalGuides.TryUpdate(gameId, guide with { Bookmark = null }, guide);
            }
            foreach (var (gameId, links) in Data.SavedLinks)
            {
                if (links.Any(link => link.Bookmark is not null))
                    Data.SavedLinks.TryUpdate(gameId,
                        links.Select(link => link.Bookmark is not null ? link with { Bookmark = null } : link).ToArray(), links);
            }
            SaveData();
            if (associationSetting is not null)
                associationSetting.Description = "当前没有阅读记录";
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
        panel.Children.Add(new StdSetting("默认使用简洁模式",
            "缩小工具栏，保留返回、查找和置顶图标。点展开图标可切换到详细模式。", minimalToggle));
        var settingsHelp = CreateGuideButton("查看说明", "了解独占全屏与浮窗置顶");
        settingsHelp.Flyout = CreateGuideHelp();
        panel.Children.Add(new StdSetting("浮窗显示", "独占全屏可能遮住攻略浮窗。", settingsHelp));
        panel.Children.Add(new StdSetting("默认打开方式",
            "打开浮窗时使用的方式。本地优先会先打开已挂载的攻略。", sourceBox));
        panel.Children.Add(CreateOnlineSourcesSetting(domainBox));
        panel.Children.Add(new StdSetting("2DFan 域名",
            "当前域名连不上时，可检测并切换到可用的备用域名。", domainStack));
        int readingCount = Data.ReadingHistory.Keys
            .Union(Data.LocalGuides.Where(pair => pair.Value?.Bookmark is not null).Select(pair => pair.Key))
            .Union(Data.SavedLinks.Where(pair => pair.Value.Any(link => link.Bookmark is not null))
                .Select(pair => pair.Key)).Count();
        associationSetting = new StdSetting("已保存的阅读记录",
            readingCount == 0
                ? "当前没有阅读记录"
                : $"已记录 {readingCount} 个游戏的在线攻略或阅读位置", clearButton);
        clearButton.IsEnabled = readingCount > 0;
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
        DescribeGuideControl(floatButton, "打开攻略浮窗", FullscreenAdvice);
        return Task.FromResult<FrameworkElement>(floatButton);
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

    private bool OpenFloatingWindow(Galgame game, bool notifyOnFailure = true, bool activate = true)
    {
        try
        {
            if (FloatingWindows.TryGetValue(game.Uuid, out Window? existingWindow))
            {
                bool wasHidden = !existingWindow.AppWindow.IsVisible;
                ShowGuideWindow(existingWindow, activate);
                if (FloatingPanels.TryGetValue(game.Uuid, out PanelState? existingPanel))
                {
                    existingPanel.WindowPin?.SetShown(true);
                    if (wasHidden && existingPanel.Reader is null) _ = ShowSourceAsync(game, existingPanel);
                }
                StartFloatWatchdog(game);
                return true;
            }

            Window window = new() { Title = game.Name.Value ?? string.Empty };

            OverlappedPresenter? presenter = window.AppWindow.Presenter as OverlappedPresenter;
            if (presenter is not null)
            {
                presenter.IsAlwaysOnTop = Data.PinFloatOnTop;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }
            // AppWindow 的尺寸单位是物理像素，XAML 布局使用逻辑像素。
            // 缩放比例必须在 Content 加载后从 XamlRoot 读取，不能在这里提前固定为 1。
            bool compact = Data.MinimalMode;
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

            FrameworkElement panel = BuildGuidePanel(window, game.Uuid, out PanelState state);
            var windowPin = new FloatingWindowPin(window, Data.PinFloatOnTop);
            state.WindowPin = windowPin;

            var sourceButtons = GuideCommandRow(
                CreateSourceToggle(null, "在线自动", "并行搜索在插件设置中勾选的网站"),
                CreateSourceToggle(GuideSource.Df2an, "2DFan", "只在 2DFan 查找攻略"),
                CreateSourceToggle(GuideSource.Ymgal, "月幕", "只在月幕查找攻略"),
                CreateSourceToggle(GuideSource.Cngal, "CnGal", "查找 CnGal 攻略，在浏览器阅读原文"),
                CreateSourceToggle(GuideSource.Seiya, "誠也の部屋", "查找誠也の部屋攻略，在浏览器阅读原文"));
            if (GuideWebLinks.SteamGuides(DescribeGame(game).SteamId) is { } steam)
            {
                var steamButton = CreateGuideButton("Steam", "在浏览器打开此游戏的 Steam 社区指南");
                steamButton.Click += async (_, _) => await OpenGuideInBrowserAsync(steam.AbsoluteUri);
                sourceButtons.Children.Add(steamButton);
            }

            var personalSources = GuideCommandRow(
                CreateSourceToggle(GuideSource.Local, "本地", "打开或挂载本地文本攻略"),
                CreateSourceToggle(GuideSource.SavedLinks, "攻略链接", "管理此游戏保存的攻略链接"));

            ToggleButton CreateSourceToggle(GuideSource? source, string label, string description)
            {
                var button = CreateGuideToggle(label, description);
                state.SourceToggles.Add((source, button));
                button.Click += (_, _) => SelectSource(source);
                return button;
            }

            void SelectSource(GuideSource? source)
            {
                if (state.PreferredSource == source)
                {
                    UpdateSourceToggleState(state);
                    if (source == GuideSource.Local) _ = ShowLocalAsync(game, state);
                    if (source is null or GuideSource.SavedLinks) _ = ShowSourceAsync(game, state);
                    return;
                }
                state.PreferredSource = source;
                _ = ShowSourceAsync(game, state);
            }

            string pinDescription = $"将攻略浮窗保持在其他窗口上方。\n\n{FullscreenAdvice}";
            bool pinned = Data.PinFloatOnTop;
            var pinButton = CreateGuideToggle("置顶", pinDescription);
            var compactPin = CreateGuideToggle("置顶", pinDescription);
            pinButton.IsChecked = pinned;
            compactPin.IsChecked = pinned;
            string pinGlyph = pinned ? "\uE718" : "\uE77A";
            var pinIcon = CreateGlyphIcon(pinGlyph);
            var compactPinIcon = CreateGlyphIcon(pinGlyph);
            pinButton.Content = GuideCommandRow(pinIcon, new TextBlock { Text = "置顶" });
            compactPin.Content = compactPinIcon;
            pinButton.Checked += (_, _) => SetPinned(true);
            pinButton.Unchecked += (_, _) => SetPinned(false);
            compactPin.Checked += (_, _) => SetPinned(true);
            compactPin.Unchecked += (_, _) => SetPinned(false);

            void SetPinned(bool value)
            {
                if (pinned == value) return;
                pinned = value;
                pinButton.IsChecked = pinned;
                compactPin.IsChecked = pinned;
                string glyph = pinned ? "\uE718" : "\uE77A";
                pinIcon.Glyph = glyph;
                compactPinIcon.Glyph = glyph;
                if (presenter is not null) presenter.IsAlwaysOnTop = pinned;
                windowPin.SetPinned(pinned);
                Data.PinFloatOnTop = pinned;
            }

            var backButton = CreateGuideButton("返回", "返回上一步", () => PopBack(state));
            state.BarBackButton = backButton;
            var refreshButton = CreateGuideButton("刷新", "重新加载攻略面板", () =>
            {
                CancelPanelRequest(state);
                _guidePages.Clear();
                if (state.CurrentSource == GuideSource.SavedLinks && state.ActiveSavedLink is { } active)
                {
                    ResetReader(state);
                    var link = GetSavedLinks(game.Uuid).FirstOrDefault(item => item.Id == active);
                    if (link is not null && GuideWebLinks.TryParse(link.Url, out Uri address))
                    {
                        _ = OpenSavedLinkAsync(game, state, link, address);
                        return;
                    }
                }

                _ = ShowSourceAsync(game, state);
            });
            var findButton = CreateGuideButton("查找正文", "在攻略正文查找文字（Ctrl+F）",
                () => state.Reader?.OpenFind());
            state.FindButton = findButton;

            var siteButton = CreateGuideButton("在浏览器打开", "在默认浏览器打开当前攻略或搜索页面");
            siteButton.Click += async (_, _) =>
            {
                if (state.OpenSiteUrl is { } siteUrl) await OpenGuideInBrowserAsync(siteUrl);
            };
            state.SiteButton = siteButton;
            siteButton.IsEnabled = false;
            var localFileButton = CreateGuideButton("本地文件", "更换本地攻略或选择文件编码",
                () => _ = ShowLocalAsync(game, state));
            localFileButton.Visibility = Visibility.Collapsed;
            state.LocalFileButton = localFileButton;

            var progressRing = new ProgressRing
            {
                Width = 16,
                Height = 16,
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
            };
            state.ProgressRing = progressRing;

            var sourceRow = new Grid { ColumnSpacing = 8 };
            sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sourceRow.Children.Add(sourceButtons);
            Grid.SetColumn(pinButton, 1);
            sourceRow.Children.Add(pinButton);

            var readingCommands = GuideCommandRow(backButton, refreshButton, findButton, siteButton, localFileButton, progressRing);
            var readingRow = new Grid { ColumnSpacing = 8 };
            readingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            readingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            readingRow.Children.Add(readingCommands);
            Grid.SetColumn(personalSources, 1);
            readingRow.Children.Add(personalSources);

            var detailedTools = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
            detailedTools.Children.Add(sourceRow);
            detailedTools.Children.Add(readingRow);

            var compactBack = CreateGuideButton("返回", "返回上一步", () => PopBack(state), "\uE72B");
            var compactFind = CreateGuideButton("查找正文", "在攻略正文查找文字（Ctrl+F）",
                () => state.Reader?.OpenFind(), "\uE721");
            SetGuideControlSize(compactBack, compact: true);
            SetGuideControlSize(compactFind, compact: true);
            SetGuideControlSize(compactPin, compact: true);
            state.CompactBackButton = compactBack;
            state.CompactFindButton = compactFind;

            var modeButton = CreateGuideButton("简洁模式", "切换到简洁模式");
            var expandIcon = CreateGlyphIcon("\uE740");
            var closeButton = CreateGuideButton("关闭", "隐藏攻略浮窗", () => DismissFloatWindow(game.Uuid));
            var closeIcon = CreateGlyphIcon("\uE8BB");
            var titleActions = GuideCommandRow(compactBack, compactFind, compactPin, modeButton, closeButton);

            var dragRegion = new Grid { MinWidth = 24, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            var titleText = new TextBlock
            {
                Text = window.Title,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            dragRegion.Children.Add(titleText);
            var titleRow = new Grid { ColumnSpacing = 6 };
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleRow.Children.Add(dragRegion);
            Grid.SetColumn(titleActions, 1);
            titleRow.Children.Add(titleActions);

            var chrome = new StackPanel();
            chrome.Children.Add(titleRow);
            chrome.Children.Add(detailedTools);

            var shell = new Grid();
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            shell.Children.Add(chrome);
            Grid.SetRow(panel, 1);
            shell.Children.Add(panel);

            void UpdateMinimumWidth()
            {
                double minimum = compact ? 300 : 398;
                double header = titleActions.ActualWidth + dragRegion.MinWidth + titleRow.ColumnSpacing;
                double detailed = Math.Max(sourceButtons.ActualWidth + pinButton.ActualWidth + sourceRow.ColumnSpacing,
                    readingCommands.ActualWidth + personalSources.ActualWidth + readingRow.ColumnSpacing);
                double contentWidth = compact ? header : Math.Max(header, detailed);
                int width = ToPhysicalPixels(Math.Max(minimum, contentWidth + shell.Padding.Left + shell.Padding.Right + 12));
                if (presenter is not null && presenter.PreferredMinimumWidth != width)
                    presenter.PreferredMinimumWidth = width;
                if (window.AppWindow.IsVisible && window.AppWindow.Size.Width < width)
                    window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, window.AppWindow.Size.Height));
            }

            void ApplyMode()
            {
                detailedTools.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                compactPin.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
                shell.Padding = compact ? new Thickness(6, 2, 6, 4) : new Thickness(10, 6, 10, 8);
                shell.RowSpacing = compact ? 2 : 6;
                titleActions.Spacing = compact ? 2 : 4;
                SetGuideControlSize(modeButton, compact);
                SetGuideControlSize(closeButton, compact);
                closeButton.Content = compact ? closeIcon : "关闭";
                string targetMode = compact ? "详细模式" : "简洁模式";
                modeButton.Content = compact ? expandIcon : targetMode;
                DescribeGuideControl(modeButton, targetMode, $"切换到{targetMode}");
                state.CompactMode = compact;
                UpdateBackButtons(state);
                UpdateFindButton(state);
                UpdateMinimumWidth();
                var size = new Windows.Graphics.SizeInt32(
                    ToPhysicalPixels(compact ? 320 : 480), ToPhysicalPixels(compact ? 420 : 700));
                window.AppWindow.Resize(size);
            }

            modeButton.Click += (_, _) =>
            {
                compact = !compact;
                Data.MinimalMode = compact;
                ApplyMode();
            };

            titleActions.SizeChanged += (_, _) => UpdateMinimumWidth();
            sourceButtons.SizeChanged += (_, _) => UpdateMinimumWidth();
            pinButton.SizeChanged += (_, _) => UpdateMinimumWidth();
            readingCommands.SizeChanged += (_, _) => UpdateMinimumWidth();
            personalSources.SizeChanged += (_, _) => UpdateMinimumWidth();
            shell.SizeChanged += (_, _) => UpdateMinimumWidth();

            try { window.SystemBackdrop = new MicaBackdrop(); }
            catch (Exception) { }

            window.Content = shell;
            ConfigureGuideTitleBar(window, dragRegion);
            ApplyMode();

            FloatingWindows[game.Uuid] = window;
            FloatingPanels[game.Uuid] = state;
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
            ShowGuideWindow(window, activate);
            windowPin.SetShown(true);
            _ = ShowSourceAsync(game, state);
            StartFloatWatchdog(game);
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

    private static void ShowGuideWindow(Window window, bool activate)
    {
        if (activate) window.Activate();
        else if (!window.AppWindow.IsVisible) window.AppWindow.Show(activateWindow: false);
    }

    private static void ConfigureGuideTitleBar(Window window, UIElement dragRegion)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        try
        {
            window.ExtendsContentIntoTitleBar = true;
            window.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
            window.SetTitleBar(dragRegion);
        }
        catch (Exception)
        {
            window.ExtendsContentIntoTitleBar = false;
        }
    }

    private static StackPanel GuideCommandRow(params UIElement[] commands)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
        };
        foreach (UIElement command in commands) row.Children.Add(command);
        return row;
    }

    private static Button CreateGuideButton(string label, string description, Action? action = null, string? glyph = null)
    {
        var button = new Button
        {
            Content = glyph is null ? label : CreateGlyphIcon(glyph),
            FontSize = 13,
        };
        SetGuideControlSize(button, compact: false);
        DescribeGuideControl(button, label, description);
        if (action is not null) button.Click += (_, _) => action();
        return button;
    }

    private static ToggleButton CreateGuideToggle(string label, string description)
    {
        var button = new ToggleButton
        {
            Content = label,
            FontSize = 13,
        };
        SetGuideControlSize(button, compact: false);
        DescribeGuideControl(button, label, description);
        return button;
    }

    private static void SetGuideControlSize(Control control, bool compact)
    {
        control.MinHeight = compact ? 26 : 30;
        control.MinWidth = compact ? 26 : 0;
        control.Padding = compact ? new Thickness(4, 2, 4, 2) : new Thickness(8, 3, 8, 3);
    }

    private static void DescribeGuideControl(Control control, string name, string description)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(control, description);
        ToolTipService.SetToolTip(control, new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 280,
        });
    }

    private const string FullscreenAdvice =
        "游戏使用独占全屏时，攻略浮窗可能被遮住。请在游戏的显示设置中改用无边框全屏或窗口化，再确认浮窗已开启置顶。";

    private static Flyout CreateGuideHelp()
    {
        var content = new StackPanel { Width = 224, Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "浮窗显示说明",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock { Text = FullscreenAdvice, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock
        {
            Text = "详细模式使用「置顶」按钮，简洁模式使用图钉图标。开启后，可以点回游戏继续操作。",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        });
        return new Flyout { Content = content, Placement = FlyoutPlacementMode.Bottom };
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
                if (!Data.AutoOpenFloatOnLaunch || Data.ActiveGameUuid != game.Uuid)
                {
                    completion.TrySetResult(false);
                    return;
                }
                try
                {
                    completion.TrySetResult(OpenFloatingWindow(game, notifyOnFailure, activate: false));
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
            if (!Data.AutoOpenFloatOnLaunch || Data.ActiveGameUuid != game.Uuid) return false;
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

        if (!token.IsCancellationRequested && Data.AutoOpenFloatOnLaunch && Data.ActiveGameUuid == game.Uuid)
            Plugin.HostApi.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                "攻略浮窗自动打开失败", "可在游戏详情页中手动打开", 5000);
        return false;
    }

    private async Task OpenFloatingWindowDelayedAsync(Galgame game, CancellationToken token)
    {
        try
        {
            await Task.Delay(2200, token); // 等宿主 SetWindowMode 执行完（SystemTray 模式下旧进程此时已退出，窗口不会在旧进程出现）
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
        foreach (PanelState state in FloatingPanels.Values)
        {
            state.WindowPin?.Dispose();
            CancelPanelRequest(state);
        }
        foreach (CancellationTokenSource cts in FloatingWatchdogs.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        FloatingWindows.Clear();
        FloatingPanels.Clear();
        FloatingWatchdogs.Clear();
        foreach (Window window in windows)
        {
            window.Close();
        }
    }

    private static void DismissFloatWindow(Guid uuid)
    {
        if (FloatingPanels.TryGetValue(uuid, out PanelState? state))
        {
            state.WindowPin?.SetShown(false);
            state.Reader?.SavePosition();
            CancelPanelRequest(state);
        }
        StopFloatWatchdog(uuid);
        if (FloatingWindows.TryGetValue(uuid, out Window? window))
            window.AppWindow.Hide();
    }

    private static void RemoveFloatingWindow(Guid uuid)
    {
        if (FloatingPanels.Remove(uuid, out PanelState? state))
        {
            state.WindowPin?.Dispose();
            CancelPanelRequest(state);
        }
        StopFloatWatchdog(uuid);
        FloatingWindows.Remove(uuid);
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
                token.ThrowIfCancellationRequested();
                if (Data.ActiveGameUuid != game.Uuid) return;
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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, System.Text.StringBuilder exeName, ref uint size);

    private static void SetOpenSite(PanelState state, string? url)
    {
        state.OpenSiteUrl = url;
        bool local = state.CurrentSource == GuideSource.Local;
        if (state.SiteButton is not null)
        {
            state.SiteButton.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
            state.SiteButton.IsEnabled = url is not null;
        }
        if (state.LocalFileButton is not null)
            state.LocalFileButton.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Button AddResultRow(Panel content, int index, string text, Func<Task> onClick)
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
}

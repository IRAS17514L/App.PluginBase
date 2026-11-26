using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Contracts.PluginUi;
using Microsoft.UI.Dispatching;
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

        StackPanel panel = new() { Spacing = 13 };
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
        panel.Children.Add(new StdSetting("启动游戏时自动打开攻略浮窗", "启动游戏时自动弹出置顶攻略窗口，可拖动到游戏旁", autoOpenToggle).View);
        ToggleSwitch minimalToggle = new() { IsOn = Data.MinimalMode };
        minimalToggle.Toggled += (_, _) => Data.MinimalMode = minimalToggle.IsOn;
        panel.Children.Add(new StdSetting("默认使用简洁模式",
            "精简工具栏，在标题栏或窗口菜单中切换显示模式。", minimalToggle).View);
        panel.Children.Add(CreateWindowPlacementSetting());
        var settingsHelp = CreateGuideButton("查看说明", "查看攻略浮窗操作说明");
        settingsHelp.Flyout = CreateGuideHelp();
        panel.Children.Add(new StdSetting("浮窗操作说明", "查看攻略来源、置顶、贴边收起和显示设置的用法。", settingsHelp).View);
        panel.Children.Add(new StdSetting("默认打开方式",
            "没有上次攻略时使用。本地优先会打开已挂载的文件。", sourceBox).View);
        panel.Children.Add(CreateOnlineSourcesSetting(domainBox));
        panel.Children.Add(new StdSetting("2DFan 域名",
            "访问失败时，搜索会尝试备用域名。也可在「检测各站连接」后手动切换。", domainBox).View);
        panel.Children.Add(CreateGuideCacheSetting(completedOnly: false));
        panel.Children.Add(CreateGuideCacheSetting(completedOnly: true));
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

    private static FontIcon CreateGlyphIcon(string glyph, string fontFamily = "Segoe Fluent Icons,Segoe MDL2 Assets")
    {
        return new FontIcon
        {
            Glyph = glyph,
            FontFamily = new FontFamily(fontFamily),
            FontSize = 12,
        };
    }

    private bool OpenFloatingWindow(Galgame game, bool notifyOnFailure = true, bool activate = true)
    {
        try
        {
            if (FloatingWindows.TryGetValue(game.Uuid, out Window? existingWindow))
            {
                FloatingPanels.TryGetValue(game.Uuid, out PanelState? existingPanel);
                bool wasDismissed = !existingWindow.AppWindow.IsVisible && existingPanel?.WindowDock?.IsCollapsed != true;
                if (wasDismissed) FloatingWindowDisplay.FitToDisplay(existingWindow.AppWindow);
                ShowGuideWindow(existingWindow, activate);
                if (existingPanel is not null)
                {
                    existingPanel.WindowDock?.WindowShown();
                    existingPanel.WindowPin?.SetShown(true);
                    if (wasDismissed) _ = OpenInitialGuideAsync(game, existingPanel);
                    if (activate) existingPanel.ShowFirstUseHelp?.Invoke();
                }
                StartFloatWatchdog(game);
                return true;
            }

            Window window = new() { Title = game.Name.Value ?? string.Empty };
            AppWindow app = window.AppWindow;
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

            OverlappedPresenter? presenter = app.Presenter as OverlappedPresenter;
            if (presenter is not null)
            {
                presenter.IsAlwaysOnTop = Data.PinFloatOnTop;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }
            // AppWindow 的尺寸单位是物理像素，XAML 布局使用逻辑像素。
            // 缩放比例必须在 Content 加载后从 XamlRoot 读取，不能在这里提前固定为 1。
            bool compact = Data.MinimalMode;
            GuideWindowSize? compactSize = Data.CompactFloatingSize;
            GuideWindowSize? detailedSize = Data.DetailedFloatingSize;
            double GetScale()
            {
                double xamlScale =
                    (window.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 0;
                if (xamlScale > 0) return xamlScale;

                // Content 尚未 Loaded 时使用 HWND 的当前显示器 DPI，避免首次显示先窄后宽。
                return FloatingWindowDisplay.ScaleForWindow(hwnd);
            }

            FrameworkElement panel = BuildGuidePanel(window, game, out PanelState state);
            var windowPin = new FloatingWindowPin(window, Data.PinFloatOnTop);
            state.WindowPin = windowPin;

            var sourceMenu = new MenuFlyoutSubItem { Text = "攻略来源" };
            var sourceMenuItems = new List<(GuideSource? Source, ToggleMenuFlyoutItem Item)>();
            var sourceButtons = GuideCommandRow(
                CreateSourceToggle(null, "在线自动", "并行搜索在插件设置中勾选的网站"),
                CreateSourceToggle(GuideSource.Df2an, "2DFan", "只在 2DFan 查找攻略"),
                CreateSourceToggle(GuideSource.Ymgal, "月幕", "只在月幕查找攻略"),
                CreateSourceToggle(GuideSource.Cngal, "CnGal", "查找并在浮窗阅读 CnGal 攻略"),
                CreateSourceToggle(GuideSource.Seiya, "誠也の部屋", "查找并在浮窗阅读誠也の部屋攻略"));
            if (GuideWebLinks.SteamGuides(DescribeGame(game).SteamId) is not null)
                sourceButtons.Children.Add(CreateSourceToggle(GuideSource.Steam, "Steam", "查找并在浮窗阅读 Steam 社区指南"));

            var personalSources = GuideCommandRow(
                CreateSourceToggle(GuideSource.Local, "本地", "打开或挂载本地文本攻略"),
                CreateSourceToggle(GuideSource.SavedLinks, "自定义攻略", "管理此游戏保存的攻略链接和粘贴文本"));

            ToggleButton CreateSourceToggle(GuideSource? source, string label, string description)
            {
                var button = CreateGuideToggle(label, description);
                state.SourceToggles.Add((source, button));
                button.Click += (_, _) => SelectSource(source);
                var item = new ToggleMenuFlyoutItem { Text = label };
                item.Click += (_, _) => SelectSource(source);
                sourceMenuItems.Add((source, item));
                sourceMenu.Items.Add(item);
                return button;
            }

            void SelectSource(GuideSource? source)
            {
                if (state.PreferredSource == source && source is not (null or GuideSource.Local or GuideSource.SavedLinks) &&
                    state.Reader is null && !state.ResumedOnOpen)
                {
                    UpdateSourceToggleState(state);
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
                state.WindowDock?.SetPinned(pinned);
                Data.PinFloatOnTop = pinned;
            }

            var backButton = CreateGuideButton("返回", "返回上一步", () => _ = GoBackAsync(game, state));
            state.BarBackButton = backButton;
            var refreshButton = CreateGuideButton("刷新", "重新加载攻略面板", RefreshGuide);

            void RefreshGuide()
            {
                CancelPanelRequest(state);
                _guidePages.Clear(game.Uuid);
                if (state.ActiveReading is { } reading && CanOpenReading(game.Uuid, reading))
                    _ = OpenReadingAsync(game, state, reading, useCache: false);
                else
                    _ = ShowSourceAsync(game, state);
            }

            var findButton = CreateGuideButton("查找正文", "在攻略正文查找文字",
                () => state.Reader?.OpenFind());
            state.FindButton = findButton;

            var siteButton = CreateGuideButton("在浏览器打开", "在默认浏览器打开当前攻略或搜索页面",
                OpenCurrentSite);
            async void OpenCurrentSite()
            {
                if (state.OpenSiteUrl is { } siteUrl) await OpenGuideInBrowserAsync(siteUrl);
            }

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

            var compactBack = CreateGuideButton("返回", "返回上一步", () => _ = GoBackAsync(game, state), "\uE72B");
            var compactFind = CreateGuideButton("查找正文", "在攻略正文查找文字",
                () => state.Reader?.OpenFind(), "\uE721");
            SetGuideControlSize(compactBack, compact: true);
            SetGuideControlSize(compactFind, compact: true);
            SetGuideControlSize(compactPin, compact: true);
            state.CompactBackButton = compactBack;
            state.CompactFindButton = compactFind;

            string TargetModeName() => compact ? "详细模式" : "简洁模式";
            const string modeIconFont = "Segoe MDL2 Assets";
            var detailedModeIcon = CreateGlyphIcon("\uE740", modeIconFont);
            var compactModeIcon = CreateGlyphIcon("\uE73F", modeIconFont);
            var modeButton = CreateGuideButton(TargetModeName(), $"切换到{TargetModeName()}");
            modeButton.Content = compact ? detailedModeIcon : compactModeIcon;
            var closeButton = CreateGuideButton("关闭", "隐藏攻略浮窗", () => DismissFloatWindow(game.Uuid));
            var closeIcon = CreateGlyphIcon("\uE8BB");
            var menuButton = CreateGuideButton("窗口菜单", "攻略操作和显示设置", glyph: "\uE712");
            SetGuideControlSize(menuButton, compact: true);
            var titleActions = GuideCommandRow(compactBack, compactFind, compactPin, modeButton, menuButton, closeButton);

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
            ToolTipService.SetToolTip(titleText, window.Title);
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

            void UpdateMinimumWidth(double? scale = null)
            {
                int minimumWidth = (int)Math.Ceiling(GuideAppearance.MinimumWindowWidth * (scale ?? GetScale()));
                if (presenter is not null && presenter.PreferredMinimumWidth != minimumWidth)
                    presenter.PreferredMinimumWidth = minimumWidth;
            }

            void ApplyLayout(double width)
            {
                bool showDetailedTools = !compact && width >= GuideAppearance.DetailedToolsWidth;
                bool showHeaderActions = width >= GuideAppearance.HeaderActionsWidth;
                detailedTools.Visibility = showDetailedTools ? Visibility.Visible : Visibility.Collapsed;
                compactPin.Visibility = !showDetailedTools && showHeaderActions ? Visibility.Visible : Visibility.Collapsed;
                modeButton.Visibility = showHeaderActions ? Visibility.Visible : Visibility.Collapsed;
                shell.Padding = showDetailedTools ? new Thickness(10, 6, 10, 8) : new Thickness(6, 2, 6, 4);
                shell.RowSpacing = showDetailedTools ? 6 : 2;
                titleActions.Spacing = showDetailedTools ? 4 : 2;
                SetGuideControlSize(modeButton, compact: !showDetailedTools);
                SetGuideControlSize(closeButton, compact: !showDetailedTools);
                closeButton.Content = showDetailedTools ? "关闭" : closeIcon;
                modeButton.Content = showDetailedTools ? TargetModeName() : (compact ? detailedModeIcon : compactModeIcon);
                state.ShowHeaderReadingActions = !showDetailedTools && showHeaderActions;
                UpdateBackButtons(state);
                UpdateFindButton(state);
            }

            void ToggleMode()
            {
                RememberWindowPlacement();
                var bounds = FloatingWindowDisplay.WindowBounds(app);
                double scale = FloatingWindowDisplay.ScaleForWindow(hwnd);
                var currentSize = new GuideWindowSize(bounds.Width / scale, bounds.Height / scale);

                compact = !compact;
                Data.MinimalMode = compact;
                string targetMode = TargetModeName();
                DescribeGuideControl(modeButton, targetMode, $"切换到{targetMode}");
                var savedSize = compact ? compactSize : detailedSize;
                var (width, height) = GuideAppearance.SizeForModeSwitch(compact, savedSize, currentSize).ToPixels(scale);
                var target = bounds with { Width = width, Height = height };
                if (FloatingWindowDisplay.ForBounds(bounds) is { } currentDisplay)
                    target = GuideWindowPlacement.Fit(currentDisplay.WorkArea, target);

                if (target != bounds) FloatingWindowDisplay.MoveWindow(app, target);
                RememberWindowPlacement();
                ApplyLayout(app.Size.Width / FloatingWindowDisplay.ScaleForWindow(hwnd));
            }

            var guideLayer = new ContentControl
            {
                Content = shell,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                IsTabStop = false,
            };
            var help = CreateGuideHelpView(() => state.HideGuideHelp?.Invoke());
            help.View.Padding = new Thickness(10, 6, 10, 8);
            help.View.Visibility = Visibility.Collapsed;
            help.View.ActualThemeChanged += (_, _) => UpdateHelpBackground();
            var windowLayout = new Grid();
            windowLayout.Children.Add(guideLayer);
            windowLayout.Children.Add(help.View);

            void UpdateHelpBackground()
            {
                help.View.Background = Application.Current?.Resources.TryGetValue(
                    "ApplicationPageBackgroundThemeBrush", out var background) == true && background is Brush brush
                    ? brush
                    : new SolidColorBrush(help.View.ActualTheme == ElementTheme.Dark
                        ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 32)
                        : Microsoft.UI.Colors.White);
            }

            void ShowHelp()
            {
                if (state.IsGuideHelpOpen) return;
                state.IsGuideHelpOpen = true;
                guideLayer.IsEnabled = false;
                guideLayer.IsHitTestVisible = false;
                guideLayer.Opacity = 0;
                UpdateHelpBackground();
                help.View.Visibility = Visibility.Visible;
                ConfigureGuideTitleBar(window, help.DragRegion);
                help.Close.Focus(FocusState.Programmatic);
                Data.HasSeenGuideHelp = true;
            }

            void HideHelp()
            {
                if (!state.IsGuideHelpOpen) return;
                state.IsGuideHelpOpen = false;
                help.View.Visibility = Visibility.Collapsed;
                guideLayer.IsEnabled = true;
                guideLayer.IsHitTestVisible = true;
                guideLayer.Opacity = 1;
                ConfigureGuideTitleBar(window, dragRegion);
                menuButton.Focus(FocusState.Programmatic);
            }

            state.HideGuideHelp = HideHelp;
            var appearance = CreateAppearanceFlyout(state);
            var clearCache = CreateGameCacheConfirmation(game);
            var windowMenu = new MenuFlyout
            {
                Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
                ShouldConstrainToRootBounds = false,
            };
            MenuFlyoutItem AddMenuItem(string label, Action action)
            {
                var item = new MenuFlyoutItem { Text = label };
                item.Click += (_, _) => action();
                windowMenu.Items.Add(item);
                return item;
            }

            windowMenu.Items.Add(sourceMenu);
            var menuBack = AddMenuItem("返回", () => _ = GoBackAsync(game, state));
            AddMenuItem("刷新", RefreshGuide);
            var menuFind = AddMenuItem("查找正文", () => state.Reader?.OpenFind());
            var menuSite = AddMenuItem("在浏览器打开", OpenCurrentSite);
            AddMenuItem("本地文件", () => _ = ShowLocalAsync(game, state));
            windowMenu.Items.Add(new MenuFlyoutSeparator());
            var menuPin = AddMenuItem("置顶", () => SetPinned(!pinned));
            var menuMode = AddMenuItem("切换模式", ToggleMode);
            var menuDock = new ToggleMenuFlyoutItem { Text = "贴边自动收起" };
            menuDock.Click += (_, _) =>
            {
                Data.AutoHideAtScreenEdge = menuDock.IsChecked;
                foreach (var floatingPanel in FloatingPanels.Values)
                    floatingPanel.WindowDock?.SetEnabled(Data.AutoHideAtScreenEdge);
            };
            windowMenu.Items.Add(menuDock);

            bool windowActive = false;
            bool windowMenuRequested = false;
            menuButton.Click += (_, _) =>
            {
                if (windowMenuRequested || windowMenu.IsOpen) return;
                windowMenuRequested = true;
                if (!windowActive) window.Activate();
                if (!menuButton.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ShowRequestedWindowMenu))
                    windowMenuRequested = false;
            };

            void ShowRequestedWindowMenu()
            {
                windowMenuRequested = false;
                if (!windowActive || !menuButton.IsLoaded || !window.AppWindow.IsVisible ||
                    state.IsGuideHelpOpen || windowMenu.IsOpen) return;

                // 等点击激活完成再打开菜单，避免置顶复核在菜单弹出时改变层级。
                windowPin.CancelScheduledRechecks();
                windowMenu.ShowAt(menuButton);
            }

            Action? pendingMenuAction = null;
            AddMenuItem("显示设置", () => AfterWindowMenu(() => appearance.ShowAt(menuButton)));
            AddMenuItem("操作说明", () => AfterWindowMenu(ShowHelp));
            AddMenuItem("清除此游戏的攻略缓存", () => AfterWindowMenu(() => clearCache.ShowAt(menuButton)));
            windowMenu.Closed += (_, _) => menuButton.DispatcherQueue.TryEnqueue(RunPendingMenuAction);

            void AfterWindowMenu(Action action)
            {
                pendingMenuAction = action;
                menuButton.DispatcherQueue.TryEnqueue(RunPendingMenuAction);
            }

            void RunPendingMenuAction()
            {
                if (windowMenu.IsOpen || pendingMenuAction is not { } selected) return;
                pendingMenuAction = null;
                if (menuButton.IsLoaded && window.AppWindow.IsVisible) selected();
            }

            windowMenu.Opening += (_, _) =>
            {
                foreach (var (source, item) in sourceMenuItems) item.IsChecked = source == state.PreferredSource;
                menuBack.IsEnabled = state.ResumedOnOpen || state.BackStack.Count > 0;
                menuFind.IsEnabled = state.Reader is not null;
                menuSite.IsEnabled = state.OpenSiteUrl is not null;
                menuPin.Text = pinned ? "取消置顶" : "置顶";
                menuMode.Text = $"切换到{TargetModeName()}";
                menuDock.IsChecked = Data.AutoHideAtScreenEdge;
            };

            bool firstUseHelpPending = false;
            state.ShowFirstUseHelp = () =>
            {
                if (Data.HasSeenGuideHelp || firstUseHelpPending) return;
                firstUseHelpPending = true;
                if (menuButton.IsLoaded) QueueFirstUseHelp();
            };
            menuButton.Loaded += (_, _) => QueueFirstUseHelp();
            window.Activated += (_, args) =>
            {
                windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
                if (windowActive) state.ShowFirstUseHelp?.Invoke();
            };

            void QueueFirstUseHelp()
            {
                if (!firstUseHelpPending) return;
                if (!menuButton.DispatcherQueue.TryEnqueue(() =>
                    {
                        firstUseHelpPending = false;
                        if (!Data.HasSeenGuideHelp && windowActive && menuButton.IsLoaded && window.AppWindow.IsVisible &&
                            !state.IsGuideHelpOpen && !windowMenuRequested && !windowMenu.IsOpen &&
                            !appearance.IsOpen && !clearCache.IsOpen)
                            ShowHelp();
                    }))
                    firstUseHelpPending = false;
            }

            modeButton.Click += (_, _) => ToggleMode();
            shell.SizeChanged += (_, args) =>
            {
                UpdateMinimumWidth();
                ApplyLayout(args.NewSize.Width);
            };

            try { window.SystemBackdrop = new MicaBackdrop(); }
            catch (Exception) { }

            window.Content = windowLayout;
            ConfigureGuideTitleBar(window, dragRegion);
            var savedPlacement = Data.FloatingPlacement is { IsValid: true } placement ? placement : null;
            var display = (savedPlacement is not null ? FloatingWindowDisplay.Find(savedPlacement.DisplayName) : null)
                ?? FloatingWindowDisplay.Default();
            double initialScale = display?.Scale ?? GetScale();
            var initialSize = (compact ? compactSize : detailedSize) ?? GuideAppearance.InitialSize(compact);
            var (initialWidth, initialHeight) = initialSize.ToPixels(initialScale);
            UpdateMinimumWidth(initialScale);
            if (display is not null)
            {
                var bounds = savedPlacement?.Restore(display.WorkArea, initialScale, initialSize) ??
                    GuideWindowPlacement.Center(display.WorkArea, initialWidth, initialHeight);
                FloatingWindowDisplay.MoveWindow(app, bounds);
            }
            else
                app.Resize(new Windows.Graphics.SizeInt32(initialWidth, initialHeight));

            ApplyLayout(app.Size.Width / FloatingWindowDisplay.ScaleForWindow(hwnd));

            void RememberWindowPlacement()
            {
                if (!app.IsVisible && state.WindowDock?.IsCollapsed != true ||
                    presenter is { State: not OverlappedPresenterState.Restored }) return;

                var bounds = FloatingWindowDisplay.WindowBounds(app);
                if (bounds.Width <= 0 || bounds.Height <= 0 ||
                    FloatingWindowDisplay.ForBounds(bounds) is not { } currentDisplay) return;
                var capturedPlacement = GuideWindowPlacement.Capture(currentDisplay.Name, currentDisplay.WorkArea,
                    bounds, FloatingWindowDisplay.ScaleForWindow(hwnd));
                if (compact) compactSize = capturedPlacement.Size;
                else detailedSize = capturedPlacement.Size;
                Data.SetFloatingSize(compact, capturedPlacement.Size);
                Data.FloatingPlacement = capturedPlacement;
            }

            state.RememberWindowPlacement = RememberWindowPlacement;
            app.Changed += (_, args) =>
            {
                if (args.DidPositionChange || args.DidSizeChange) RememberWindowPlacement();
            };
            app.Closing += (_, _) => RememberWindowPlacement();

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
            state.WindowOpacity.TrySetTransparency(Data.WindowTransparency);
            windowPin.SetShown(true);
            state.WindowDock = new FloatingWindowDock(window, windowPin, Data.AutoHideAtScreenEdge, pinned,
                () => windowMenuRequested || windowMenu.IsOpen || appearance.IsOpen || clearCache.IsOpen ||
                    state.IsGuideHelpOpen || state.IsPickingLocalFile);
            state.WindowDock.WindowShown(trackRestoredPosition: savedPlacement is not null);
            if (activate) state.ShowFirstUseHelp?.Invoke();
            _ = OpenInitialGuideAsync(game, state);
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
            state.WindowDock?.Dispose();
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
            state.RememberWindowPlacement?.Invoke();
            state.WindowDock?.WindowDismissed();
            state.HideGuideHelp?.Invoke();
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
            state.WindowDock?.Dispose();
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
                    (!window.AppWindow.IsVisible &&
                     (!FloatingPanels.TryGetValue(game.Uuid, out PanelState? panel) || panel.WindowDock?.IsCollapsed != true)))
                    return;
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

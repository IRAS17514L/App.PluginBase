using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PotatoVN.App.PluginBase.Controls.Prefabs;
using PotatoVN.App.PluginBase.Guides;

namespace PotatoVN.App.PluginBase;

public partial class Plugin
{
    private FrameworkElement CreateWindowPlacementSetting()
    {
        const string description = "浮窗找不到时，可将它移到 PotatoVN 窗口所在屏幕的中央。";
        var reset = CreateGuideButton("重置浮窗位置", description);
        var setting = new StdSetting("浮窗位置", description, reset);
        reset.Click += async (_, _) =>
        {
            reset.IsEnabled = false;
            try
            {
                ResetFloatingWindowPlacement();
                await SaveDataNowAsync();
                setting.Description = "浮窗位置已重置。";
            }
            catch (Exception error)
            {
                setting.Description = "未能完成重置，请稍后重试。";
                _hostApi.DeveloperEvent(msg: "重置攻略浮窗位置失败", e: error);
            }
            finally
            {
                reset.IsEnabled = true;
            }
        };
        return setting.View;
    }

    private void ResetFloatingWindowPlacement()
    {
        var mainWindow = _hostApi.GetMainWindow();
        var display = (mainWindow is not null
                ? FloatingWindowDisplay.ForWindow(WinRT.Interop.WindowNative.GetWindowHandle(mainWindow))
                : null)
            ?? FloatingWindowDisplay.Default()
            ?? throw new InvalidOperationException("找不到可用的显示器。");
        var savedSize = Data.GetFloatingSize(Data.MinimalMode) ?? GuideAppearance.InitialSize(Data.MinimalMode);
        var centered = new GuideWindowPlacement(display.Name, .5, .5, savedSize.Width, savedSize.Height);
        double targetScale = display.Scale;

        foreach (var (gameId, window) in FloatingWindows)
        {
            var app = window.AppWindow;
            if (app.IsVisible && app.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored } presenter)
                presenter.Restore(activateWindow: false);

            double currentScale = FloatingWindowDisplay.ScaleForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
            var size = app.Size;
            var windowPlacement = centered with { Width = size.Width / currentScale, Height = size.Height / currentScale };
            FloatingWindowDisplay.MoveWindow(app, windowPlacement.Restore(display.WorkArea, targetScale));

            if (FloatingPanels.TryGetValue(gameId, out var panel)) panel.WindowDock?.Expand();
        }

        Data.FloatingPlacement = centered;
    }

    private FrameworkElement CreateOnlineSourcesSetting(ComboBox domainBox)
    {
        var controls = new StackPanel { Spacing = 4 };
        var statusBySource = new Dictionary<GuideSource, TextBlock>();
        var switchDomain = CreateGuideButton("切换到备用域名", "使用本次检测到的 2DFan 备用域名");
        switchDomain.Visibility = Visibility.Collapsed;
        (string Current, string Backup)? offeredDomain = null;
        foreach (GuideSource source in GuideAggregation.OnlineSources)
        {
            var toggle = new CheckBox
            {
                Content = source == GuideSource.Steam ? "Steam 社区指南" : SourceName(source),
                IsChecked = Data.AutoSearchSources.Contains(source),
                MinHeight = 30,
            };
            void SaveSelection(bool enabled)
            {
                var selected = Data.AutoSearchSources.Where(existing => existing != source);
                Data.AutoSearchSources = GuideAggregation.NormalizeSources(enabled ? selected.Append(source) : selected);
            }
            toggle.Checked += (_, _) => SaveSelection(true);
            toggle.Unchecked += (_, _) => SaveSelection(false);

            var status = new TextBlock
            {
                Foreground = GetSecondaryBrush(),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(toggle);
            Grid.SetColumn(status, 1);
            row.Children.Add(status);
            controls.Children.Add(row);
            statusBySource.Add(source, status);
            if (source == GuideSource.Df2an)
                controls.Children.Add(switchDomain);
        }

        void ClearDomainOffer()
        {
            offeredDomain = null;
            switchDomain.Visibility = Visibility.Collapsed;
        }

        domainBox.SelectionChanged += (_, _) =>
        {
            ClearDomainOffer();
            statusBySource[GuideSource.Df2an].Text = "";
        };
        switchDomain.Click += (_, _) =>
        {
            if (offeredDomain is { } offer && offer.Current == Data.Domain)
            {
                domainBox.SelectedItem = offer.Backup;
                statusBySource[GuideSource.Df2an].Text = "已切换";
            }
            ClearDomainOffer();
        };
        var detect = CreateGuideButton("检测各站连接", "通过当前网络检查每个站点的连接");
        controls.Children.Add(detect);
        detect.Click += async (_, _) =>
        {
            detect.IsEnabled = false;
            string domain = Data.Domain;
            domainBox.SelectedItem = domain;
            ClearDomainOffer();
            var token = _lifetimeCts.Token;
            try
            {
                await Task.WhenAll(statusBySource.Select(async entry =>
                {
                    var (source, status) = entry;
                    status.Text = "检测中…";
                    if (source == GuideSource.Df2an)
                    {
                        var (currentReachable, backup) = await _df2an.CheckConfiguredDomainAsync(domain, token);
                        if (domain != Data.Domain)
                        {
                            domainBox.SelectedItem = Data.Domain;
                            status.Text = "域名已变更";
                            return;
                        }

                        status.Text = currentReachable ? "可连接" : backup is null ? "连接失败" : "当前域名不可用";
                        if (backup is not null)
                        {
                            offeredDomain = (domain, backup);
                            string label = $"切换到 {new Uri(backup).Host}";
                            switchDomain.Content = label;
                            DescribeGuideControl(switchDomain, label, $"使用本次检测到的备用域名 {backup}");
                            switchDomain.Visibility = Visibility.Visible;
                        }
                        return;
                    }

                    var result = await _guidePages.ProbeEndpointsAsync(ConnectionEndpoints(source), token);
                    status.Text = result switch
                    {
                        GuideConnection.Connected => "可连接",
                        GuideConnection.Partial => "部分可连接",
                        _ => "连接失败",
                    };
                }));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                _hostApi.DeveloperEvent(msg: "检测攻略站连接失败", e: error);
                foreach (TextBlock status in statusBySource.Values.Where(status => status.Text == "检测中…"))
                    status.Text = "检测失败";
            }
            finally
            {
                detect.IsEnabled = true;
            }
        };
        return new StdSetting("在线自动搜索来源",
            "可多选。只在已勾选的网站搜寻攻略。Steam 需要游戏已有 App ID。", controls).View;
    }

    private FrameworkElement CreateGuideCacheSetting(bool completedOnly)
    {
        string title = completedOnly ? "已通关游戏的攻略缓存" : "全部攻略缓存";
        string description = completedOnly ? "清理标记为「玩过」的游戏缓存及续读记录。"
            : "清理所有游戏的缓存及续读记录。";
        string label = completedOnly ? "清除已通关缓存" : "清除全部缓存";
        var button = CreateGuideButton(label, "查看将清理的游戏数量");
        var setting = new StdSetting(title, description, button);
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Width = 250, Spacing = 8 };
        content.Children.Add(message);
        var flyout = new Flyout { Content = content, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var confirm = CreateGuideButton("清除", "确认清除攻略缓存和阅读记录");
        var cancel = CreateGuideButton("取消", "取消清理", flyout.Hide);
        content.Children.Add(GuideCommandRow(confirm, cancel));
        button.Flyout = flyout;

        Guid[] ReadTargets() => completedOnly ? GetCompletedGameCacheIds() : GetGameCacheIds().ToArray();

        Guid[] previewedGames = [];
        flyout.Opening += (_, _) =>
        {
            previewedGames = [];
            confirm.IsEnabled = false;
            try
            {
                previewedGames = ReadTargets();
                confirm.IsEnabled = previewedGames.Length > 0;
                string scope = completedOnly ? "个「玩过」游戏" : "个游戏";
                message.Text = confirm.IsEnabled
                    ? $"将清除 {previewedGames.Length} {scope}的攻略缓存和阅读记录。挂载文件、收藏链接和粘贴文本仍可使用。"
                    : "没有可清除的攻略缓存或阅读记录。";
            }
            catch (Exception error)
            {
                message.Text = "暂时无法读取可清理的游戏，请稍后重试。";
                _hostApi.DeveloperEvent(msg: "读取攻略缓存清理范围失败", e: error);
            }
        };

        confirm.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            confirm.IsEnabled = false;
            flyout.Hide();
            bool cleared = false;
            try
            {
                Guid[] targets = previewedGames.Intersect(ReadTargets()).ToArray();
                if (targets.Length == 0)
                {
                    setting.Description = "没有可清除的攻略缓存或阅读记录。";
                    return;
                }

                foreach (Guid gameId in targets) ClearGameGuideCache(gameId);
                cleared = true;
                await SaveDataNowAsync();
                setting.Description = $"已清除 {targets.Length} 个游戏的攻略缓存和阅读记录。";
            }
            catch (Exception error)
            {
                setting.Description = cleared ? "缓存和记录已清除，正在重试保存。" : "清理未完成，请稍后重试。";
                _hostApi.DeveloperEvent(msg: "清理攻略缓存失败", e: error);
                if (cleared) SaveData();
            }
            finally
            {
                button.IsEnabled = true;
            }
        };

        return setting.View;
    }

    private static Uri[] ConnectionEndpoints(GuideSource source) => source switch
    {
        GuideSource.Ymgal => [YmgalGuideSource.BaseUrl],
        GuideSource.Cngal => [CngalGuideSource.ConnectionCheck, CngalGuideSource.Site],
        GuideSource.Seiya => [SeiyaGuideSource.Main, SeiyaGuideSource.AllAgesPc, SeiyaGuideSource.AllAges],
        GuideSource.Steam => [GuideWebLinks.SteamGuides("324160")!],
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };
}

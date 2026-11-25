using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Controls.Prefabs;
using PotatoVN.App.PluginBase.Guides;

namespace PotatoVN.App.PluginBase;

public partial class Plugin
{
    private StdSetting CreateOnlineSourcesSetting(ComboBox domainBox)
    {
        var controls = new StackPanel { Spacing = 4 };
        var statusBySource = new Dictionary<GuideSource, TextBlock>();
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
        }

        domainBox.SelectionChanged += (_, _) => statusBySource[GuideSource.Df2an].Text = "";
        var detect = CreateGuideButton("检测各站连接", "通过当前网络检查每个站点的连接");
        controls.Children.Add(detect);
        detect.Click += async (_, _) =>
        {
            detect.IsEnabled = false;
            string domain = Data.Domain;
            var token = _lifetimeCts.Token;
            try
            {
                await Task.WhenAll(statusBySource.Select(async entry =>
                {
                    var (source, status) = entry;
                    status.Text = "检测中…";
                    var result = await _guidePages.ProbeEndpointsAsync(ConnectionEndpoints(source, domain), token);
                    if (source == GuideSource.Df2an && domain != Data.Domain) return;
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
            "可多选。只在已勾选的网站搜寻攻略。Steam 需要游戏已有 App ID。", controls);
    }

    private static Uri[] ConnectionEndpoints(GuideSource source, string domain) => source switch
    {
        GuideSource.Df2an => [Df2anGuideSource.SubjectsPage(domain)],
        GuideSource.Ymgal => [YmgalGuideSource.BaseUrl],
        GuideSource.Cngal => [CngalGuideSource.ConnectionCheck, CngalGuideSource.Site],
        GuideSource.Seiya => [SeiyaGuideSource.Main, SeiyaGuideSource.AllAges],
        GuideSource.Steam => [GuideWebLinks.SteamGuides("324160")!],
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };
}

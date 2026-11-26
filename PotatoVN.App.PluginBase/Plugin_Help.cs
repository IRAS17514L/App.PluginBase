using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace PotatoVN.App.PluginBase;

public partial class Plugin
{
    private Flyout CreateGuideHelp()
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var (view, _, _, scroll) = CreateGuideHelpView(flyout.Hide);
        view.Width = 288;
        scroll.MaxHeight = 300;
        flyout.Content = view;
        flyout.Opened += (_, _) => Data.HasSeenGuideHelp = true;
        return flyout;
    }

    private static (Grid View, Grid DragRegion, Button Close, ScrollViewer Scroll) CreateGuideHelpView(Action onClose)
    {
        var view = new Grid { RowSpacing = 8 };
        view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        view.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dragRegion = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        var title = new TextBlock
        {
            Text = "操作说明",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        dragRegion.Children.Add(title);
        header.Children.Add(dragRegion);

        var close = CreateGuideButton("关闭说明", "关闭操作说明（Esc）", onClose, "\uE8BB");
        SetGuideControlSize(close, compact: true);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        view.Children.Add(header);

        var sections = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 8, 0) };
        AddSection("找攻略",
            "选择「在线自动」搜索多个网站，也可从「⋯ → 攻略来源」指定网站。用「本地」挂载文本文件，或在「自定义攻略」添加链接、粘贴正文。");
        AddSection("阅读与显示",
            "按 Ctrl+F 查找正文。打开「⋯ → 显示设置」可调字号和透明度，也可在「⋯」菜单切换详细或简洁模式。");
        AddSection("贴边收起",
            "把浮窗拖到屏幕边缘，鼠标离开后会收为窄条，悬停即可展开。可在「⋯」菜单关闭自动收起。");
        AddSection("边玩边看",
            $"用图钉或「置顶」按钮保持浮窗在前。{FullscreenAdvice}");
        sections.Children.Add(new TextBlock
        {
            Text = "可随时在「⋯ → 操作说明」重看。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = GetSecondaryBrush(),
        });

        var scroll = new ScrollViewer
        {
            Content = sections,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroll, 1);
        view.Children.Add(scroll);
        view.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Escape) return;
            args.Handled = true;
            onClose();
        };
        return (view, dragRegion, close, scroll);

        void AddSection(string heading, string text)
        {
            var headingBlock = new TextBlock
            {
                Text = heading,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            AutomationProperties.SetHeadingLevel(headingBlock, AutomationHeadingLevel.Level2);
            var section = new StackPanel { Spacing = 2 };
            section.Children.Add(headingBlock);
            section.Children.Add(new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap });
            sections.Children.Add(section);
        }
    }
}

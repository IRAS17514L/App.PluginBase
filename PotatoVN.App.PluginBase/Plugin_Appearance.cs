using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PotatoVN.App.PluginBase.Guides;

namespace PotatoVN.App.PluginBase;

public partial class Plugin
{
    private Flyout CreateAppearanceFlyout(PanelState state)
    {
        var fontLabel = new TextBlock();
        var transparencyLabel = new TextBlock();
        var fontSize = new Slider
        {
            Minimum = GuideAppearance.MinimumFontSize,
            Maximum = GuideAppearance.MaximumFontSize,
            StepFrequency = 1,
            TickFrequency = 1,
            IsThumbToolTipEnabled = false,
        };
        var transparency = new Slider
        {
            Minimum = 0,
            Maximum = GuideAppearance.MaximumTransparency,
            StepFrequency = 1,
            TickFrequency = 1,
            IsThumbToolTipEnabled = false,
        };
        AutomationProperties.SetName(fontSize, "正文字号");
        AutomationProperties.SetName(transparency, "浮窗透明度");

        var message = new TextBlock
        {
            Text = "当前窗口无法调整透明度。",
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var content = new StackPanel { Width = 188, Spacing = 4 };
        content.Children.Add(fontLabel);
        content.Children.Add(fontSize);
        content.Children.Add(transparencyLabel);
        content.Children.Add(transparency);
        content.Children.Add(message);

        var flyout = new Flyout
        {
            Content = content,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = false,
        };

        bool syncing = false;
        void UpdateLabels()
        {
            fontLabel.Text = $"正文字号 {Data.ReaderFontSize}";
            transparencyLabel.Text = $"浮窗透明度 {Data.WindowTransparency}%";
        }

        flyout.Opening += (_, _) =>
        {
            syncing = true;
            fontSize.Value = Data.ReaderFontSize;
            transparency.Value = Data.WindowTransparency;
            syncing = false;
            UpdateLabels();
            message.Visibility = Visibility.Collapsed;
        };
        fontSize.ValueChanged += (_, args) =>
        {
            if (syncing) return;
            Data.ReaderFontSize = GuideAppearance.ClampFontSize((int)Math.Round(args.NewValue));
            foreach (var panel in FloatingPanels.Values) panel.Reader?.SetFontSize(Data.ReaderFontSize);
            UpdateLabels();
        };
        transparency.ValueChanged += (_, args) =>
        {
            if (syncing) return;
            int value = GuideAppearance.ClampTransparency((int)Math.Round(args.NewValue));
            if (!state.WindowOpacity.TrySetTransparency(value))
            {
                syncing = true;
                transparency.Value = Data.WindowTransparency;
                syncing = false;
                message.Visibility = Visibility.Visible;
                return;
            }

            Data.WindowTransparency = value;
            foreach (var panel in FloatingPanels.Values)
            {
                if (panel != state) panel.WindowOpacity.TrySetTransparency(value);
            }
            message.Visibility = Visibility.Collapsed;
            UpdateLabels();
        };
        return flyout;
    }
}

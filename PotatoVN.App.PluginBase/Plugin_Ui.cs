using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Controls;
using PotatoVN.App.PluginBase.Controls.Prefabs;
using PotatoVN.App.PluginBase.Helper;

namespace PotatoVN.App.PluginBase;

public partial class Plugin
{
    public FrameworkElement CreateSettingUi()
    {
        StdStackPanel panel = new();
        panel.Children.Add(new UserControl1().WarpWithPanel());
        panel.Children.Add(new Setting("设置标题", "这是一个设置", new ToggleSwitch()).WarpWithPanel());
        return panel;
    }
}
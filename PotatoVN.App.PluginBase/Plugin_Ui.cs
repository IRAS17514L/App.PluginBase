using Microsoft.UI.Xaml;

namespace PotatoVN.App.PluginBase;

public partial class Plugin
{
    public FrameworkElement CreateSettingUi()
    {
        return new Controls.UserControl1();
    }
}
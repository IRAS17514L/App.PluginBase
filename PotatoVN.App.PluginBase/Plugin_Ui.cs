using System.Threading.Tasks;
using GalgameManager.WinApp.Base.Models.Plugin;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using PotatoVN.App.PluginBase.Controls;
using PotatoVN.App.PluginBase.Controls.Prefabs;
using PotatoVN.App.PluginBase.Helper;

namespace PotatoVN.App.PluginBase;

public partial class Plugin
{
    private bool _uiInit;
    
    private void InitUi()
    {
        if (_uiInit) return;
        _hostApi.RegisterSidebarButton(new SidebarButtonInfo
        {
           Id = "sidebarButton1",
           Text = "插件按钮",
           Placement = SidebarButtonPlacement.Menu, 
           FluentGlyph = "&#xE709;",
        }, () =>
        {
            _hostApi.NavigateTo(typeof(ExamplePage), "Example Page");
            return Task.CompletedTask;
        });
        _uiInit = true;
    }
    
    
    public FrameworkElement CreateSettingUi()
    {
        StdStackPanel panel = new();
        panel.Children.Add(new UserControl1().WarpWithPanel());
        panel.Children.Add(new StdSetting("SettingTitle".GetLoc(), "SettingDescription".GetLoc(),
            AddToggleSwitch(_data, nameof(_data.TestBool))).WarpWithPanel());
        StdAccountPanel accountPanel = new StdAccountPanel("title", "userName", "Description",
            new Button(){Content = "Login".GetLoc()}.WarpWithPanel());
        panel.Children.Add(accountPanel);
        return panel;
    }

    private ToggleSwitch AddToggleSwitch(object source, string propName)
    {
        ToggleSwitch toggle = new();
        Binding binding = new()
        {
            Source = source,
            Path = new PropertyPath(propName),
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        };
        toggle.SetBinding(ToggleSwitch.IsOnProperty, binding);
        return toggle;
    }
}
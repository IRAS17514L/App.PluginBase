using System.Threading.Tasks;
using GalgameManager.WinApp.Base.Models.Plugin;
using Microsoft.UI.Xaml;
using PotatoVN.App.PluginBase.Controls;

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

    public FrameworkElement CreateSettingUi() => new UserControl1(_data);
}
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Helper;

namespace PotatoVN.App.PluginBase.Controls
{
    public sealed partial class ExamplePage : Page
    {
        public ExamplePage()
        {
            XamlResourceLocatorFactory.PluginControlInit(ref _contentLoaded, this);
        }

        private void ButtonBase_OnClick(object sender, RoutedEventArgs e)
        {
            ResourceLoader.Initialize();
        }
    }
}

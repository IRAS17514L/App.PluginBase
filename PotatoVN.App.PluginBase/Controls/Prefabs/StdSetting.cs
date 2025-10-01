using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PotatoVN.App.PluginBase.Controls.Prefabs
{
    public sealed class Setting : UserControl
    {
        public Setting(string title, string description, FrameworkElement rightContent)
        {
            rightContent.HorizontalAlignment = HorizontalAlignment.Right;

            var titleText = new TextBlock { Text = title };
            var descriptionText = new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap
            };
            TryApplyStyle(titleText, "BodyTextBlockStyle");
            TryApplyStyle(descriptionText, "DescriptionTextStyle");

            var leftStack = new StackPanel { Orientation = Orientation.Vertical };
            leftStack.Children.Add(titleText);
            leftStack.Children.Add(descriptionText);

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            
            //左侧
            root.Children.Add(leftStack);
            Grid.SetColumn(leftStack, 0);
            //中间空隙
            var spacer = new Grid();
            root.Children.Add(spacer);
            Grid.SetColumn(spacer, 1);
            //右侧
            root.Children.Add(rightContent);
            Grid.SetColumn(rightContent, 2);

            Content = root;
        }
        
        private static void TryApplyStyle(FrameworkElement fe, string key)
        {
            if (Application.Current?.Resources != null &&
                Application.Current.Resources.TryGetValue(key, out var v) &&
                v is Style s)
            {
                fe.Style = s;
            }
        }
    }
}

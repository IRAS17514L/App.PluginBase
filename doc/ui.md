# 插件UI开发文档

PotatoVN为插件预留了丰富的插件UI注入接口。

然而，由于 WinUI 3 框架的技术限制，我们无法在一个 XAML 文件中直接嵌套另一个来自不同程序集的 XAML 文件，即插件的XAML文件是不能嵌套另一个XAML的，只能使用WinUI3提供的标准控件。

因此，我们 强烈推荐 使用 C# 代码来描述和构建你的插件 UI，而不是使用 XAML。这样做可以绕过框架的限制，让自定义的UI能够相互嵌套，并能保证最佳的兼容性和稳定性。

以下示例代码将生成一个包含设置项和账户面板的 UI：
```csharp
public FrameworkElement CreateSettingUi()
{
    StdStackPanel panel = new();
    panel.Children.Add(new UserControl1().WarpWithPanel());
    panel.Children.Add(new StdSetting("设置标题", "这是一个设置",
        AddToggleSwitch(_data, nameof(_data.TestBool))).WarpWithPanel());
    StdAccountPanel accountPanel = new StdAccountPanel("title", "userName", "Description",
        new Button().WarpWithPanel());
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
```

`UserControl1`：
```yaml
<?xml version="1.0" encoding="utf-8"?>
<UserControl
    x:Class="PotatoVN.App.PluginBase.Controls.UserControl1"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d">

    <StackPanel Spacing="10" Padding="20" HorizontalAlignment="Center" VerticalAlignment="Center">
        <TextBlock FontSize="24" Text="大家好，我是插件！" />
        <TextBlock Text="这是一个来自外部DLL的用户控件。" />
        <Button x:Name="PluginButton" Content="点击我" Click="PluginButton_Click"/>
        <TextBlock x:Name="StatusText" />
    </StackPanel>
</UserControl>

```

在`Controls/Prefabs`目录下，我们提供了一些预设的UI控件（如`StdSetting`、`StdAccountPanel`等），你可以直接使用它们来快速构建你的插件UI。

## UI注入软件
PotatoVN提供了多种UI注入接口，允许插件将自定义UI注入到应用的不同位置。你可以在应用公开库的`Contracts/PluginUi`目录下找到这些接口的定义。如果你需要的UI注入位置没有接口，你可以阅读软件本体的代码，并使用`harmony`库来创建新的UI注入点。
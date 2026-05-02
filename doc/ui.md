# 插件UI开发文档

PotatoVN为插件预留了丰富的插件UI注入接口。

PotatoVN 支持插件使用XAML定义UI（就像常规的WinUI控件那样），也支持使用c#文件直接描述UI。

## XAML描述UI
如果你计划使用XAML来编写UI，请确保以下几点：

1. 插件中的 `Page`、`UserControl`、自定义控件不要直接调用默认生成的 `InitializeComponent()`；请继续使用模板里提供的 `XamlResourceLocatorFactory.PluginControlInit()`（请参考下面的案例）。
2. 插件项目保持 WinUI 类库配置，并启用 `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>`。 (默认模板已启用)
3. 打包插件时要保留生成出来的 `.pri` 文件，以及 `程序集名/...` 这一整套编译后的 XAML 资源目录 （这也是模板默认启用的）。

以下为XAML描述UI的案例：
```xaml
<?xml version="1.0" encoding="utf-8"?>
<UserControl
    x:Class="PotatoVN.App.PluginBase.Controls.TestControl"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d">
    <Grid>
        <Button Content="Hello World!"/>
    </Grid>
</UserControl>
```

```csharp
using Microsoft.UI.Xaml.Controls;

namespace PotatoVN.App.PluginBase.Controls
{
    public sealed partial class TestControl : UserControl
    {
        //_contentLoaded为UserControl使用XAML描述时自动生成的字段，不需要你自己定义
        public TestControl() => XamlResourceLocatorFactory.PluginControlInit(ref _contentLoaded, this);
    }
}
```

## C#描述UI

如果你计划采用c#描述UI，可以参考以下例子。

以下示例代码将生成一个包含嵌套插件控件、设置项和账户面板的 UI：

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
```

在`Controls/Prefabs`目录下，我们提供了一些预设的UI控件（如`StdSetting`、`StdAccountPanel`等），你可以直接使用它们来快速构建你的插件UI。

## UI注入软件
PotatoVN提供了多种UI注入接口，允许插件将自定义UI注入到应用的不同位置。你可以在应用公开库的`Contracts/PluginUi`目录下找到这些接口的定义。如果你需要的UI注入位置没有接口，你可以阅读软件本体的代码，并使用`harmony`库来创建新的UI注入点。

## 与宿主保持相同的风格
为了让插件UI与宿主应用保持一致的风格，建议使用PotatoVN提供的预设Style：Controls/Styles目录下有以下预设style，请务必考虑使用它们：
* FontSize：定义了应用中使用的字体大小。
* TextBlock： 定义了常见的TextBlock样式。
* Thickness：定义了各种常用的Margin和Padding值。

使用示例：
```xaml
<TextBlock Style="{ThemeResource DescriptionTextStyle}" Text="这是一段描述文本"/>
```
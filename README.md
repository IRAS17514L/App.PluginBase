# PotatoVN 攻略面板

一个为 [PotatoVN](https://github.com/GoldenPotato137/PotatoVN) 提供游戏攻略检索与独立浮窗阅读的客户端插件。攻略正文不会嵌入游戏详情页。

## 功能

- 在游戏详情页提供“打开攻略浮窗”入口，攻略正文显示在独立浮窗中。
- 支持 **2DFan** 和 **月幕** 两个内容来源。
- 自动选择攻略来源：游戏有月幕档案编号时优先使用月幕，否则使用 2DFan；也可手动切换。
- 将站点文章提取为可选中的纯文本，方便在游戏旁阅读。
- 攻略浮窗支持置顶、极简模式、返回导航以及使用浏览器打开原文。
- 可在启动游戏时自动打开浮窗，并在 PotatoVN 进入游玩模式后恢复窗口。
- 缓存游戏与 2DFan 攻略页的关联，下次可以直接打开。
- 支持多个 2DFan 备用域名及自动可用性检测。

## 使用

1. 在 PotatoVN 的插件管理界面安装并启用插件。
2. 打开任意游戏详情页，点击右侧的“打开攻略浮窗”。
3. 可在插件设置中调整默认来源、2DFan 域名、自动开窗和极简模式。

攻略内容来自第三方网站，需要正常的网络连接；站点不可达或页面结构变化时，内容检索可能失败。

## 构建

需要 Windows、.NET 8 SDK 以及可用的 WinUI 3 / Windows App SDK 构建环境。

```powershell
git submodule update --init --remote
dotnet build PotatoVN.App.Plugin.sln --configuration Release
```

Release 构建完成后，可安装的包位于：

```text
PotatoVN.App.PluginBase/artifacts/plugin.pvnplugin.zip
```

Debug 构建可使用：

```powershell
dotnet build PotatoVN.App.Plugin.sln --configuration Debug
```

## 项目结构

- `PotatoVN.App.PluginBase/Plugin.cs`：插件生命周期、配置持久化及游戏启停事件。
- `PotatoVN.App.PluginBase/Plugin_Ui.cs`：设置界面、攻略浮窗、数据获取及页面解析。
- `PotatoVN/GalgameManager.WinApp.Base`：通过 Git submodule 引入的 PotatoVN 插件公开 API。
- `doc/`：PotatoVN 插件开发参考文档。

## 开发说明

插件基于 .NET 8 和 WinUI 3，通过 `IPlugin`、`IPluginSetting` 与 `IGalgamePageRightPanel` 接入 PotatoVN。请不要直接修改 `PotatoVN/GalgameManager.WinApp.Base` 中的公开 API 项目。

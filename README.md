# PotatoVN 攻略面板

一个为 [PotatoVN](https://github.com/GoldenPotato137/PotatoVN) 提供游戏攻略检索与独立浮窗阅读的客户端插件。攻略正文不会嵌入游戏详情页。

## 功能

- 在游戏详情页提供“打开攻略浮窗”入口，攻略正文显示在独立浮窗中。
- 支持 **2DFan** 和 **月幕** 两个内容来源。
- 可在详细模式选择「本地」挂载 TXT、Markdown、NFO 等纯文本攻略。优先列出 PotatoVN 通过「打开文本」记住的文件及游戏目录候选，也可手动浏览。游戏目录内的挂载随游戏目录迁移，支持续读、查找和手动切换编码。
- 「在线自动」续读在线攻略，或在月幕和 2DFan 之间查找。首次查找时，有月幕档案编号的游戏优先查月幕。需要开窗时优先打开已挂载的文件，可在插件设置中将默认来源设为「本地优先」。
- 2DFan 按中文名、原名和显示名查找，优先列出标题匹配的条目。多个候选由玩家确认。
- 将站点文章提取为可选中的纯文本，方便在游戏旁阅读。
- 攻略浮窗提供详细模式和简洁模式。详细模式使用带文字的按钮，简洁模式在同一行显示游戏标题和操作图标。悬停在图标上可查看按钮说明。
- 可在启动游戏时自动打开浮窗，并在 PotatoVN 进入游玩模式后恢复窗口。
- 两个来源分别记住每款游戏最近成功打开的攻略和阅读位置。重新打开浮窗可以续读，重启后优先恢复到上次读到的段落。
- 点击放大镜或按 `Ctrl+F` 查找正文。可在匹配项之间跳转。
- 支持多个 2DFan 备用域名及自动可用性检测。

## 使用

1. 在 PotatoVN 的插件管理界面安装并启用插件。
2. 打开任意游戏详情页，点击右侧的“打开攻略浮窗”。
3. 可在插件设置中调整默认来源、2DFan 域名、自动开窗和默认显示模式。
4. 全屏与置顶说明可在插件设置的「浮窗显示」中查看，也会在鼠标悬停于「打开攻略浮窗」或「置顶」按钮时显示。

攻略内容来自第三方网站，需要正常的网络连接；站点不可达或页面结构变化时，内容检索可能失败。

## 已知问题

- 开启置顶后，切回窗口化或无边框全屏的游戏时，插件会重新尝试置顶，键盘焦点留在游戏中。
- 独占全屏游戏可能绕过桌面合成，攻略浮窗可能无法覆盖。遇到这种情况，请将游戏切换为无边框全屏或窗口化模式。

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
- `PotatoVN.App.PluginBase/Plugin_Ui.cs`：插件设置和浮窗生命周期。
- `PotatoVN.App.PluginBase/Plugin_Reader.cs`：攻略来源选择、面板导航和续读。
- `PotatoVN.App.PluginBase/Guides`：站点请求、页面解析、阅读记录和正文索引。
- `PotatoVN/GalgameManager.WinApp.Base`：通过 Git submodule 引入的 PotatoVN 插件公开 API。
- `doc/`：PotatoVN 插件开发参考文档。

## 开发说明

插件基于 .NET 8 和 WinUI 3，通过 `IPlugin`、`IPluginSetting` 与 `IGalgamePageRightPanel` 接入 PotatoVN。请不要直接修改 `PotatoVN/GalgameManager.WinApp.Base` 中的公开 API 项目。

运行离线回归测试（首次构建需有本地 NuGet 依赖或可访问 NuGet）：

```powershell
dotnet run --project tests/GuideTests/GuideTests.csproj
```

测试使用 HTML 样例和模拟 HTTP 响应，覆盖来源回退、链接迁移、段落定位和网络超时。

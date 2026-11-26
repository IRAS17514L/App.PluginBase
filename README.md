# PotatoVN 攻略面板

一个为 [PotatoVN](https://github.com/GoldenPotato137/PotatoVN) 提供游戏攻略检索与独立浮窗阅读的客户端插件。攻略正文不会嵌入游戏详情页。

## 功能

- 在游戏详情页提供“打开攻略浮窗”入口，攻略正文显示在独立浮窗中。
- 详细模式的在线来源栏并列显示 **2DFan**、**月幕**、**CnGal** 和 **誠也の部屋**。点击匹配的攻略即可在浮窗阅读。誠也の部屋同时查询主站、全年龄 PC 和主机攻略索引，可按游戏原名匹配。
- 可在详细模式选择「本地」挂载 TXT、Markdown、NFO 等纯文本攻略。优先列出 PotatoVN 通过「打开文本」记住的文件及游戏目录候选，也可手动浏览。游戏目录内的挂载随游戏目录迁移，支持续读、查找和手动切换编码。
- 「在线自动」并行搜索在插件设置中勾选的网站，勾选 Steam 时，有 Steam App ID 的游戏也会列入该游戏的公开 Steam 指南。结果标明来源，默认推荐前 8 条，可展开查看其余候选。没有上次攻略时，可用「本地优先」打开已挂载的文件。
- 2DFan 按中文名、原名和显示名查找，优先列出标题匹配的条目。多个候选由玩家确认。
- 各站的攻略正文可以在浮窗阅读。誠也の部屋的多列路线按标题依次展开，图片、视频和完整排版可在浏览器查看原文。
- 攻略浮窗提供详细模式和简洁模式。详细模式使用带文字的按钮，简洁模式在同一行显示游戏标题和操作图标。悬停在图标上可查看按钮说明。
- 浮窗会记住上次的位置和大小。找不到浮窗时，可在插件设置中点击「重置浮窗位置」，将它移到 PotatoVN 窗口所在屏幕的中央。原显示器断开时，重新打开会使用仍可见的显示器。
- 浮窗可在主屏或副屏贴边收起。窄条会避开任务栏，悬停可展开。浮窗菜单中可关闭「贴边自动收起」。
- 可在启动游戏时自动打开浮窗，并在 PotatoVN 进入游玩模式后恢复窗口。
- 打开浮窗时会恢复该游戏上次成功打开的攻略和阅读位置。网页正文缓存可跨重启使用，超出缓存容量时会尝试重新读取原文。点「返回」才按默认来源搜索。
- 插件设置可清除全部攻略缓存，也可只清除 PotatoVN 标记为「玩过」的游戏缓存。浮窗菜单可清理当前游戏。清理时一并删除网页正文缓存和续读记录，保留挂载文件、收藏链接和粘贴文本。
- 点击放大镜或按 `Ctrl+F` 查找正文。可在匹配项之间跳转。
- 在「自定义攻略」可为每款游戏保存网页链接和粘贴的纯文本。链接优先使用已适配网站的解析方式，也会尝试提取其他公开网页的正文。遇到登录、动态加载或图片为主的页面，可在浏览器打开原文。
- 粘贴的攻略可命名、编辑或删除，无需另存为文件。网页正文和粘贴文本都支持浮窗查找和续读。
- 有 Steam App ID 时，在线来源栏会显示「Steam」按钮，点击可查看并阅读该游戏的公开社区指南。
- 设置页的「检测各站连接」会检查当前网络下的各站连接。2DFan 当前域名不可用时会检测备用域名，并提供一键切换。搜索 2DFan 遇到访问失败时也会尝试备用域名。

## 使用

1. 在 PotatoVN 的插件管理界面安装并启用插件。
2. 打开任意游戏详情页，点击右侧的“打开攻略浮窗”。
3. 可在插件设置中调整默认来源、2DFan 域名、自动开窗和默认显示模式。
4. 首次主动打开或进入攻略浮窗时，操作说明会覆盖当前浮窗。点右上角关闭或按 Esc 即可回到攻略。之后可在浮窗的「⋯ → 操作说明」或插件设置的「浮窗操作说明」中重看。

攻略内容来自第三方网站，需要正常的网络连接；站点不可达或页面结构变化时，内容检索可能失败。

## 已知问题

- 开启置顶后，切回窗口化或无边框全屏的游戏时，插件会重新尝试置顶，键盘焦点留在游戏中。
- 独占全屏游戏可能绕过桌面合成，攻略浮窗可能无法覆盖。遇到这种情况，请将游戏切换为无边框全屏或窗口化模式。

## 版本规则

- 修复和样式微调只增加补丁版本（第三位）。
- 新增功能才增加次版本（第二位）。
- 大版本（第一位）须经项目维护者明确同意后才可增加。

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
- `PotatoVN.App.PluginBase/Plugin_Web.cs`：补充来源、攻略链接和浏览器入口。
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

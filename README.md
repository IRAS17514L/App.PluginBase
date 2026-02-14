# PotatoVN插件脚手架

这个项目是一个基于PotatoVN的插件脚手架，旨在帮助开发者快速创建和开发PotatoVN插件。请阅读[插件开发文档](https://potatovn.net/development/client-plugin/quick-start.html)以获取更多关于插件开发的信息。

## 脚手架内容
本插件脚手架内包含了以下快捷功能，请自行选择是否保留：
* `Controls/Prefabs`: 包含了一系列预制的UI控件，方便开发者快速构建插件界面，建议使用它们来保持界面的一致性。
* `Controls/UserControl1.xaml`: 一个示例控件，显示在插件的设置界面中。
* `Helper/PluginLocalization.cs/LocalizationExtensions.cs`: 一个帮助类，用于处理插件的本地化字符串，方便开发者进行多语言支持。如果你的插件不需要支持多国语言，可以选择删除这个类。
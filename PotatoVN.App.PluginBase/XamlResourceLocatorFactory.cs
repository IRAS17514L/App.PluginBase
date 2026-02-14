using System;
using System.Runtime.CompilerServices;

namespace PotatoVN.App.PluginBase
{
    /// <summary>
    /// 这是一个XAML资源定位器工厂类，用于生成插件中XAML资源的URI。
    /// 我们建议开发者不要使用XAML来定义UI，而是使用纯c#代码来定义UI，WinUI3的XAML定位有很多bug
    /// </summary>
    internal static class XamlResourceLocatorFactory
    {
        private static readonly string PackageName;
        public static string PackagePath = string.Empty;

        static XamlResourceLocatorFactory()
        {
            PackageName = typeof(XamlResourceLocatorFactory).Assembly.GetName().Name ?? throw new InvalidOperationException();
        }

        internal static Uri Create([CallerFilePath] string callerFilePath = "")
        {
            // This is not a foolproof solution, but it works well enough to get started
            var i = callerFilePath.LastIndexOf(PackageName, StringComparison.Ordinal);
            var componentPath = callerFilePath[i..^3];
            return new Uri($"ms-appx:///{PackagePath}\\{componentPath}");
        }
    }
}

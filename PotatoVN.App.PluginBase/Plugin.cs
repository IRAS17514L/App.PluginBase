using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using GalgameManager.WinApp.Base.Contracts;
using GalgameManager.WinApp.Base.Contracts.PluginUi;
using GalgameManager.WinApp.Base.Models;
using PotatoVN.App.PluginBase.Helper;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase
{
    public partial class Plugin : IPlugin, IPluginSetting
    {
        public static IPotatoVnApi HostApi { get; private set; } = null!;
        private IPotatoVnApi _hostApi = null!;
        internal WalkthroughData Data { get; private set; } = new();

        public PluginInfo Info { get; } = new()
        {
            Id = new Guid("c9a68427-b773-4a98-bb66-2c6f4a4ebe37"),
            Name = "攻略面板",
            Description = "在游戏详情页显示攻略，支持 2DFan 与月幕（中文）双来源，自动检索并支持手动关联直达。",
        };

        public async Task InitializeAsync(IPotatoVnApi hostApi)
        {
            _hostApi = hostApi;
            HostApi = hostApi;
            XamlResourceLocatorFactory.PackagePath = _hostApi.GetPluginPath();
            var dataJson = await _hostApi.GetDataAsync();
            if (!string.IsNullOrWhiteSpace(dataJson))
            {
                try
                {
                    Data = System.Text.Json.JsonSerializer.Deserialize<WalkthroughData>(dataJson) ?? new WalkthroughData();
                }
                catch
                {
                    Data = new WalkthroughData();
                }
            }
            Data.PropertyChanged += (_, _) => SaveData();
        }

        public Task OnUninstallAsync(bool deleteData, Action<TimeSpan> extendWaitHandler, CancellationToken cts)
        {
            if (cts.IsCancellationRequested) return Task.FromCanceled(cts);
            return Task.CompletedTask;
        }

        internal void SaveData() => _ = _hostApi.SaveDataAsync(System.Text.Json.JsonSerializer.Serialize(Data));
    }
}

namespace PotatoVN.App.PluginBase.Models
{
    /// <summary>
    /// 插件数据：2DFan 域名覆盖 + 游戏到攻略页的关联缓存。
    /// 数据按版本号保存，读取时做兼容检查。
    /// </summary>
    public partial class WalkthroughData : ObservableObject
    {
        public int Version { get; set; } = 1;

        /// <summary>2DFan 域名（官方域会被墙，备用域 2dfdf.de / 2dfmax.top）</summary>
        [ObservableProperty] private string _domain = "https://2dfan.com";

        /// <summary>默认攻略来源："auto"（自动）| "2dfan" | "ymgal"</summary>
        [ObservableProperty] private string _defaultSource = "auto";

        /// <summary>游戏 Uuid -> 2DFan 攻略 topic 页 URL</summary>
        public Dictionary<Guid, string> TopicUrlMap { get; set; } = [];
    }
}

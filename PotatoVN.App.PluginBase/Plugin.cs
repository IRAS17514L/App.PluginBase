using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using GalgameManager.WinApp.Base.Contracts;
using GalgameManager.WinApp.Base.Contracts.PluginUi;
using GalgameManager.WinApp.Base.Models;
using GalgameManager.WinApp.Base.Models.Msgs;
using GalgameManager.Models;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
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
            _hostApi.Messenger.Register<GalgamePlayedMessage>(this, OnGamePlayed);
            _hostApi.Messenger.Register<GalgameStoppedMessage>(this, OnGameStopped);
            TryRestoreFloatWindowAfterRestart();
        }

        private void OnGamePlayed(object recipient, GalgamePlayedMessage message)
        {
            if (!Data.AutoOpenFloatOnLaunch) return;
            Data.ActiveGameUuid = message.Value.Uuid;
            Data.ActiveGamePlayedAt = DateTime.Now;
            _ = OpenFloatingWindowDelayedAsync(message.Value);
        }

        private void OnGameStopped(object recipient, GalgameStoppedMessage message)
        {
            Data.ActiveGameUuid = null;
            Data.ActiveGamePlayedAt = null;
            HostApi.InvokeOnMainThread(() => CloseFloatingWindow(message.Value.Uuid));
        }

        private async void TryRestoreFloatWindowAfterRestart()
        {
            try
            {
                if (!Data.AutoOpenFloatOnLaunch) return;
                if (Data.ActiveGameUuid is not { } uuid || Data.ActiveGamePlayedAt is not { } playedAt) return;
                // 仅宿主重启（SystemTray 游玩模式的 /r 重启）后恢复；正常启动一律不弹
                if (IsHostRestart() == false)
                {
                    Data.ActiveGameUuid = null;
                    Data.ActiveGamePlayedAt = null;
                    return;
                }
                // 超 15 分钟视为过期
                if (DateTime.Now - playedAt > TimeSpan.FromMinutes(15)) return;
                Galgame? game = _hostApi.GetAllGames().FirstOrDefault(g => g.Uuid == uuid);
                if (game is null) return;
                // 游戏进程必须真的在跑，否则是残留状态，清掉
                if (!IsGameProcessRunning(game))
                {
                    Data.ActiveGameUuid = null;
                    Data.ActiveGamePlayedAt = null;
                    return;
                }
                await Task.Delay(600); // 等宿主界面稳定（新进程 UI 就绪即可，窗口独立于宿主页面）
                HostApi.InvokeOnMainThread(() => OpenFloatingWindow(game));
            }
            catch (Exception)
            {
                // 浮窗恢复失败不阻塞插件启动
            }
        }

        private static bool IsHostRestart()
        {
            try
            {
                var args = AppInstance.GetCurrent().GetActivatedEventArgs();
                if (args.Kind == ExtendedActivationKind.Launch && args.Data is ILaunchActivatedEventArgs launchArgs)
                {
                    string[] argStrings = launchArgs.Arguments.Split();
                    if (argStrings.Length > 1)
                        argStrings = argStrings.Skip(1).ToArray();
                    if (argStrings.Contains("/r")) return true;
                }
            }
            catch (Exception)
            {
                // 激活参数不可用时退回命令行判定
            }
            return Environment.GetCommandLineArgs().Any(a => a == "/r");
        }

        public Task OnUninstallAsync(bool deleteData, Action<TimeSpan> extendWaitHandler, CancellationToken cts)
        {
            if (cts.IsCancellationRequested) return Task.FromCanceled(cts);
            _hostApi.Messenger.Unregister<GalgamePlayedMessage>(this);
            _hostApi.Messenger.Unregister<GalgameStoppedMessage>(this);
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

        /// <summary>启动游戏时自动打开攻略浮窗</summary>
        [ObservableProperty] private bool _autoOpenFloatOnLaunch = true;

        /// <summary>当前游玩中的游戏 Uuid（宿主重启后用于恢复浮窗）</summary>
        [ObservableProperty] private Guid? _activeGameUuid;

        /// <summary>最后一次启动游戏的时间（防止重启后误开很久以前的开局）</summary>
        [ObservableProperty] private DateTime? _activeGamePlayedAt;

        /// <summary>浮窗是否默认置顶</summary>
        [ObservableProperty] private bool _pinFloatOnTop = true;
    }
}

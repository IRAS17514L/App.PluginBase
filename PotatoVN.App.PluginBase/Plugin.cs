using System;
using System.Collections.Generic;
using System.ComponentModel;
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
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase
{
    public partial class Plugin : IPlugin, IPluginSetting
    {
        public static IPotatoVnApi HostApi { get; private set; } = null!;
        private IPotatoVnApi _hostApi = null!;
        private readonly SemaphoreSlim _saveGate = new(1, 1);
        private readonly object _saveScheduleLock = new();
        private readonly CancellationTokenSource _lifetimeCts = new();
        private CancellationTokenSource? _scheduledSaveCts;
        internal WalkthroughData Data { get; private set; } = new();

        public PluginInfo Info { get; } = new()
        {
            Id = new Guid("c9a68427-b773-4a98-bb66-2c6f4a4ebe37"),
            Name = "攻略面板",
            Description = "提供攻略检索与独立浮窗阅读，支持 2DFan 与月幕（中文）双来源、自动检索和手动关联直达。",
        };

        public async Task InitializeAsync(IPotatoVnApi hostApi)
        {
            _hostApi = hostApi;
            HostApi = hostApi;
            var dataJson = await _hostApi.GetDataAsync();
            if (!string.IsNullOrWhiteSpace(dataJson))
            {
                try
                {
                    Data = System.Text.Json.JsonSerializer.Deserialize<WalkthroughData>(dataJson) ?? new WalkthroughData();
                }
                catch (Exception e)
                {
                    Data = new WalkthroughData();
                    _hostApi.DeveloperEvent(msg: "攻略面板配置损坏，已恢复默认值", e: e);
                }
            }
            bool dataNormalized = NormalizeData();
            Data.PropertyChanged += OnDataPropertyChanged;
            if (dataNormalized) ScheduleSave();
            _hostApi.Messenger.Register<GalgamePlayedMessage>(this, OnGamePlayed);
            _hostApi.Messenger.Register<GalgameStoppedMessage>(this, OnGameStopped);
            _ = TryRestoreFloatWindowAfterRestartAsync(_lifetimeCts.Token);
        }

        private void OnGamePlayed(object recipient, GalgamePlayedMessage message)
        {
            if (!Data.AutoOpenFloatOnLaunch) return;
            Data.ActiveGameUuid = message.Value.Uuid;
            Data.ActiveGamePlayedAt = DateTime.Now;
            _ = SaveAndOpenDelayedAsync(message.Value, _lifetimeCts.Token);
        }

        private async Task SaveAndOpenDelayedAsync(Galgame game, CancellationToken token)
        {
            try
            {
                // 宿主 SystemTray 游玩模式约 1s 后 Restart("/r") 杀旧进程；必须等数据落盘，
                // 否则新进程 TryRestore 读不到 ActiveGameUuid，浮窗无法恢复
                // 等待一次经过串行化的完整快照落盘，确保宿主 /r 重启后能读到两个活动字段。
                await SaveDataNowAsync();
            }
            catch (Exception e)
            {
                // 保存失败不阻塞后续弹窗尝试
                _hostApi.DeveloperEvent(msg: "保存攻略浮窗启动状态失败", e: e);
            }
            try
            {
                await OpenFloatingWindowDelayedAsync(game, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 插件正在卸载。
            }
        }

        private void OnGameStopped(object recipient, GalgameStoppedMessage message)
        {
            Data.ActiveGameUuid = null;
            Data.ActiveGamePlayedAt = null;
            HostApi.InvokeOnMainThread(() => CloseFloatingWindow(message.Value.Uuid));
        }

        private async Task TryRestoreFloatWindowAfterRestartAsync(CancellationToken token)
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
                if (DateTime.Now - playedAt > TimeSpan.FromMinutes(15))
                {
                    Data.ActiveGameUuid = null;
                    Data.ActiveGamePlayedAt = null;
                    return;
                }
                // 插件初始化通常发生在游戏库加载之后，但较慢设备上仍可能出现短暂空库。
                // 给宿主最多 30 秒完成恢复，避免原先 3 秒窗口造成偶发漏开。
                Galgame? game = null;
                for (int i = 0; i < 60 && game is null; i++)
                {
                    game = _hostApi.GetAllGames().FirstOrDefault(g => g.Uuid == uuid);
                    if (game is null) await Task.Delay(500, token);
                }
                if (game is null) return;
                await Task.Delay(600, token); // 等宿主界面稳定（新进程 UI 就绪即可，窗口独立于宿主页面）
                await OpenFloatingWindowWithRetryAsync(game, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 插件正在卸载。
            }
            catch (Exception e)
            {
                // 浮窗恢复失败不阻塞插件启动
                _hostApi.DeveloperEvent(msg: "恢复攻略浮窗失败", e: e);
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

        public async Task OnUninstallAsync(bool deleteData, Action<TimeSpan> extendWaitHandler, CancellationToken cts)
        {
            cts.ThrowIfCancellationRequested();
            await _lifetimeCts.CancelAsync();
            _hostApi.Messenger.Unregister<GalgamePlayedMessage>(this);
            _hostApi.Messenger.Unregister<GalgameStoppedMessage>(this);
            Data.PropertyChanged -= OnDataPropertyChanged;
            CancelScheduledSave();
            if (!deleteData)
            {
                await PersistDataAsync();
            }

            var windowsClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = cts.Register(() => windowsClosed.TrySetCanceled(cts));
            try
            {
                _hostApi.InvokeOnMainThread(() =>
                {
                    try
                    {
                        CloseAllFloatingWindows();
                        windowsClosed.TrySetResult();
                    }
                    catch (Exception e)
                    {
                        windowsClosed.TrySetException(e);
                    }
                });
            }
            catch (Exception e)
            {
                windowsClosed.TrySetException(e);
            }
            await windowsClosed.Task;
        }

        private async Task PersistDataAsync()
        {
            await _saveGate.WaitAsync();
            try
            {
                // 获取锁之后再序列化：即使此前排队了多个 PropertyChanged 保存，
                // 每次写入也都是当前完整状态，不会让旧快照最后完成并覆盖新快照。
                string json = System.Text.Json.JsonSerializer.Serialize(Data);
                await _hostApi.SaveDataAsync(json);
            }
            finally
            {
                _saveGate.Release();
            }
        }

        private async Task SaveDataNowAsync()
        {
            CancelScheduledSave();
            await PersistDataAsync();
        }

        private void OnDataPropertyChanged(object? sender, PropertyChangedEventArgs e) => ScheduleSave();

        private bool NormalizeData()
        {
            bool changed = false;
            if (Data.TopicUrlMap is null)
            {
                Data.TopicUrlMap = [];
                changed = true;
            }
            if (Data.DefaultSource is not ("auto" or "2dfan" or "ymgal"))
            {
                Data.DefaultSource = "auto";
                changed = true;
            }
            if (!Uri.TryCreate(Data.Domain, UriKind.Absolute, out Uri? domain) ||
                domain.Scheme is not ("http" or "https"))
            {
                Data.Domain = "https://2dfan.com";
                changed = true;
            }
            if (Data.Version != 1)
            {
                Data.Version = 1;
                changed = true;
            }
            return changed;
        }

        private void ScheduleSave()
        {
            CancellationTokenSource cts;
            lock (_saveScheduleLock)
            {
                _scheduledSaveCts?.Cancel();
                _scheduledSaveCts = cts = new CancellationTokenSource();
            }
            _ = SaveAfterDelayAsync(cts);
        }

        private async Task SaveAfterDelayAsync(CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(250, cts.Token);
                await PersistDataAsync();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // 后续变更会保存更新后的完整快照。
            }
            catch (Exception e)
            {
                // 普通设置变更采用后台保存；关键的游戏启动状态会显式等待。
                _hostApi.DeveloperEvent(msg: "保存攻略面板设置失败", e: e);
            }
            finally
            {
                lock (_saveScheduleLock)
                {
                    if (ReferenceEquals(_scheduledSaveCts, cts))
                        _scheduledSaveCts = null;
                }
                cts.Dispose();
            }
        }

        private void CancelScheduledSave()
        {
            lock (_saveScheduleLock)
            {
                _scheduledSaveCts?.Cancel();
                _scheduledSaveCts = null;
            }
        }

        internal void SaveData() => ScheduleSave();
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

        /// <summary>极简模式：浮窗只显示攻略内容与一个切换按钮</summary>
        [ObservableProperty] private bool _minimalMode = false;
    }
}

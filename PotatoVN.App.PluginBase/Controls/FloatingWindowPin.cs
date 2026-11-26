using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace PotatoVN.App.PluginBase;

internal sealed class FloatingWindowPin : IDisposable
{
    private static readonly TimeSpan[] RecheckIntervals =
    [
        TimeSpan.FromMilliseconds(80),
        TimeSpan.FromMilliseconds(320),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2.2),
    ];

    private readonly IntPtr _window;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly WinEventProc _foregroundChanged;
    private IntPtr _hook;
    private GCHandle _callbackHandle;
    private int _nextRecheck;
    private bool _pinned;
    private bool _shown;
    private bool _disposed;

    internal FloatingWindowPin(Window window, bool pinned)
        : this(WinRT.Interop.WindowNative.GetWindowHandle(window), window.DispatcherQueue, pinned) { }

    internal FloatingWindowPin(IntPtr window, DispatcherQueue dispatcher, bool pinned)
    {
        _window = window;
        _dispatcher = dispatcher;
        _timer = _dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += Recheck;
        _foregroundChanged = OnForegroundChanged;
        _pinned = pinned;
    }

    internal void SetShown(bool shown)
    {
        _shown = shown;
        Update();
    }

    internal void SetPinned(bool pinned)
    {
        _pinned = pinned;
        Update();
    }

    internal void CancelScheduledRechecks() => _timer.Stop();

    private bool IsActive => !_disposed && _shown && _pinned;

    private void Update()
    {
        if (!IsActive)
        {
            _timer.Stop();
            if (_hook != IntPtr.Zero && UnhookWinEvent(_hook))
            {
                _hook = IntPtr.Zero;
                _callbackHandle.Free();
            }
            return;
        }

        if (_hook == IntPtr.Zero)
        {
            _callbackHandle = GCHandle.Alloc(_foregroundChanged);
            _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero,
                _foregroundChanged, 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
            if (_hook == IntPtr.Zero) _callbackHandle.Free();
        }

        RaiseWithoutActivation();
        ScheduleRechecks();
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr window, int objectId,
        int childId, uint thread, uint time)
    {
        // WinEvent 回调中只排队，等游戏完成这次窗口切换再调整层级。
        if (window != IntPtr.Zero && IsActive)
            _dispatcher.TryEnqueue(ScheduleRechecks);
    }

    private void ScheduleRechecks()
    {
        if (!IsActive) return;
        _timer.Stop();
        _nextRecheck = 0;
        StartNextRecheck();
    }

    private void Recheck(DispatcherQueueTimer sender, object args)
    {
        if (!IsActive) return;
        RaiseWithoutActivation();
        StartNextRecheck();
    }

    private void StartNextRecheck()
    {
        // 部分游戏在获得焦点后还会异步调整一次层级，只在本次切换后短暂复核。
        if (_nextRecheck >= RecheckIntervals.Length) return;
        _timer.Interval = RecheckIntervals[_nextRecheck++];
        _timer.Start();
    }

    private void RaiseWithoutActivation()
    {
        if (IsWindowVisible(_window))
            SetWindowPos(_window, HwndTopmost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    public void Dispose()
    {
        _disposed = true;
        Update();
        _timer.Tick -= Recheck;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId,
        int childId, uint thread, uint time);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width,
        int height, uint flags);

    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
}

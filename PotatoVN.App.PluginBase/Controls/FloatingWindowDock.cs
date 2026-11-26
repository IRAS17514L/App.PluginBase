using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PotatoVN.App.PluginBase.Guides;
using Windows.Graphics;

namespace PotatoVN.App.PluginBase;

internal sealed class FloatingWindowDock : IDisposable
{
    private const int HideDelay = 600;
    private const int TabThickness = 8;
    private const int TabLength = 81;
    private const uint TabWindowStyles = 0x08000000 | 0x00080000 | 0x00000080; // NOACTIVATE、LAYERED、TOOLWINDOW
    private const uint Popup = 0x80000000;
    private const uint Notify = 0x0100;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint KeepTabBounds = SwpNoSize | SwpNoMove | SwpNoActivate;
    private const uint LeftButtonUp = 0x0202;
    private const uint PointerUp = 0x0247;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private const uint RootWindow = 2;
    private const uint ExtendedFrameBounds = 9;
    private readonly Window _window;
    private readonly AppWindow _app;
    private readonly IntPtr _handle;
    private readonly FloatingWindowPin _pin;
    private readonly Func<bool> _hasOpenControls;
    private readonly DispatcherQueueTimer _timer;
    private readonly TabSubclassProc _tabProc;
    private IntPtr _tabHandle;
    private FloatingWindowPin? _tabPin;
    private GuideWindowBounds _collapsedWorkArea;
    private bool _enabled;
    private bool _pinned;
    private bool _open;
    private bool _moved;
    private bool _disposed;
    private long _outsideSince;
    private long _waitUntil;

    internal bool IsCollapsed { get; private set; }

    internal FloatingWindowDock(Window window, FloatingWindowPin pin, bool enabled, bool pinned, Func<bool> hasOpenControls)
    {
        _window = window;
        _app = window.AppWindow;
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _pin = pin;
        _enabled = enabled;
        _pinned = pinned;
        _hasOpenControls = hasOpenControls;
        _tabProc = OnTabMessage;
        _timer = window.DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += OnTick;
        _app.Changed += OnWindowChanged;
    }

    internal void WindowShown(bool trackRestoredPosition = false)
    {
        _open = true;
        _moved |= trackRestoredPosition;
        IsCollapsed = false;
        HideTab();
        _waitUntil = Environment.TickCount64 + 1400;
        UpdateTracking();
    }

    internal void WindowDismissed()
    {
        _open = false;
        IsCollapsed = false;
        _timer.Stop();
        HideTab();
    }

    internal void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled && IsCollapsed) Expand();
        UpdateTracking();
    }

    internal void SetPinned(bool pinned)
    {
        _pinned = pinned;
        if (_tabHandle != IntPtr.Zero)
            SetWindowPos(_tabHandle, pinned ? HwndTopmost : HwndNotTopmost, 0, 0, 0, 0, KeepTabBounds);
        _tabPin?.SetPinned(pinned);
    }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_open || IsCollapsed) return;
        if (args.DidPositionChange) _moved = true;
        if (args.DidPositionChange || args.DidSizeChange || args.DidVisibilityChange) UpdateTracking();
    }

    private void UpdateTracking()
    {
        _outsideSince = 0;
        if (_open && _enabled && (IsCollapsed || _moved))
        {
            if (!_timer.IsRunning) _timer.Start();
        }
        else
            _timer.Stop();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (IsCollapsed)
        {
            if (!IsWindowVisible(_tabHandle) ||
                FloatingWindowDisplay.ForWindow(_tabHandle)?.WorkArea != _collapsedWorkArea)
            {
                FloatingWindowDisplay.FitToDisplay(_app);
                Expand();
                return;
            }
            if (GetCursorPos(out PointInt32 cursor) && GetAncestor(WindowFromPoint(cursor), RootWindow) == _tabHandle)
                Expand();
            return;
        }

        if (FindLayout(out GuideWindowBounds frame, out GuideWindowBounds work) is not { } layout)
        {
            _timer.Stop();
            return;
        }

        long now = Environment.TickCount64;
        if (now < _waitUntil ||
            !GetCursorPos(out PointInt32 point) || frame.Contains(point.X, point.Y) ||
            GetAsyncKeyState(0x01) < 0 || GetAsyncKeyState(0x02) < 0 ||
            _hasOpenControls() ||
            (GetForegroundWindow() == _handle && _window.Content.XamlRoot is { } root &&
             FocusManager.GetFocusedElement(root) is TextBox))
        {
            _outsideSince = 0;
            return;
        }
        if (_outsideSince == 0) _outsideSince = now;
        if (now - _outsideSince >= HideDelay) Collapse(layout, frame, work);
    }

    private GuideDockLayout? FindLayout(out GuideWindowBounds frame, out GuideWindowBounds work)
    {
        AppWindow app = _app;
        frame = default;
        work = default;
        if (!app.IsVisible || app.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored })
            return null;

        frame = FrameBounds(app);
        if (frame.Width <= 0 || frame.Height <= 0 || FloatingWindowDisplay.ForBounds(frame) is not { } display)
            return null;

        work = display.WorkArea;
        double scale = FloatingWindowDisplay.ScaleForWindow(_handle);
        return GuideDockLayout.Find(frame, work, (int)Math.Ceiling(12 * scale),
            (int)Math.Ceiling(TabThickness * scale), (int)Math.Ceiling(TabLength * scale), display.Bounds);
    }

    private GuideWindowBounds FrameBounds(AppWindow app)
    {
        if (DwmGetWindowAttribute(_handle, ExtendedFrameBounds, out NativeRect rect, 16) == 0 &&
            rect.Right > rect.Left && rect.Bottom > rect.Top)
            return new GuideWindowBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return FloatingWindowDisplay.WindowBounds(app);
    }

    private void Collapse(GuideDockLayout layout, GuideWindowBounds frame, GuideWindowBounds work)
    {
        try
        {
            if (_tabHandle == IntPtr.Zero) CreateTab();

            if (!FloatingDockTabRenderer.TryDraw(_tabHandle, layout) ||
                !SetWindowPos(_tabHandle, _pinned ? HwndTopmost : HwndNotTopmost, 0, 0, 0, 0,
                    KeepTabBounds | SwpShowWindow) || !IsWindowVisible(_tabHandle))
                throw new InvalidOperationException("无法显示攻略浮窗贴边标签。");
            _tabPin?.SetShown(true);

            // 用可见边框对齐工作区，保留主窗口的尺寸和阅读状态。
            _collapsedWorkArea = work;
            IsCollapsed = true;
            AppWindow app = _app;
            PointInt32 position = app.Position;
            app.Move(new PointInt32(position.X + layout.Expanded.X - frame.X,
                position.Y + layout.Expanded.Y - frame.Y));
            app.Hide();
            _pin.SetShown(false);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or NotSupportedException)
        {
            if (IsCollapsed) Expand();
            else HideTab();
            _moved = false;
            _timer.Stop();
        }
    }

    internal void Expand()
    {
        if (!_open || !IsCollapsed) return;
        IsCollapsed = false;
        if (!_app.IsVisible) _app.Show(activateWindow: false);
        HideTab();
        _pin.SetShown(true);
        _waitUntil = Environment.TickCount64 + HideDelay;
        UpdateTracking();
    }

    private void CreateTab()
    {
        // 使用无标题栏的原生弹出窗口，让半透明像素完全决定窄条外形。
        IntPtr tab = CreateWindowEx(TabWindowStyles, "STATIC", _window.Title, Popup | Notify,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (tab == IntPtr.Zero) throw new InvalidOperationException("无法创建攻略浮窗贴边标签。");
        if (!SetWindowSubclass(tab, _tabProc, 1, 0))
        {
            DestroyWindow(tab);
            throw new InvalidOperationException("无法设置攻略浮窗贴边标签交互。");
        }
        _tabHandle = tab;
        _tabPin = new FloatingWindowPin(tab, _window.DispatcherQueue, _pinned);
    }

    private IntPtr OnTabMessage(IntPtr window, uint message, nuint wParam, nint lParam, nuint subclassId, nuint data)
    {
        if (message is LeftButtonUp or PointerUp)
        {
            _window.DispatcherQueue.TryEnqueue(Expand);
            return IntPtr.Zero;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void HideTab()
    {
        _tabPin?.SetShown(false);
        if (IsWindowVisible(_tabHandle)) ShowWindow(_tabHandle, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WindowDismissed();
        _app.Changed -= OnWindowChanged;
        _timer.Tick -= OnTick;
        _tabPin?.Dispose();
        if (_tabHandle == IntPtr.Zero) return;
        RemoveWindowSubclass(_tabHandle, _tabProc, 1);
        DestroyWindow(_tabHandle);
        _tabHandle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out NativeRect value, uint size);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr TabSubclassProc(IntPtr window, uint message, nuint wParam, nint lParam,
        nuint subclassId, nuint data);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, TabSubclassProc proc, nuint subclassId, nuint data);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, TabSubclassProc proc, nuint subclassId);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointInt32 point);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr WindowFromPoint(PointInt32 point);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width,
        int height, uint flags);
}

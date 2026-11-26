using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using PotatoVN.App.PluginBase.Guides;

namespace PotatoVN.App.PluginBase;

internal sealed record FloatingWindowDisplay(IntPtr Handle, string Name, GuideWindowBounds Bounds, GuideWindowBounds WorkArea)
{
    private const uint DefaultToNearest = 2;
    private const uint DefaultToPrimary = 1;

    internal double Scale => GetDpiForMonitor(Handle, 0, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;

    internal static double ScaleForWindow(IntPtr window)
    {
        uint dpi = GetDpiForWindow(window);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    internal static GuideWindowBounds WindowBounds(AppWindow app) =>
        new(app.Position.X, app.Position.Y, app.Size.Width, app.Size.Height);

    internal static void MoveWindow(AppWindow app, GuideWindowBounds bounds)
    {
        var rect = new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        app.MoveAndResize(rect);
        // 跨缩放比例移动时，Windows 可能先应用 WM_DPICHANGED 建议的尺寸。
        if (WindowBounds(app) != bounds) app.MoveAndResize(rect);
    }

    internal static void FitToDisplay(AppWindow app)
    {
        var bounds = WindowBounds(app);
        if (ForBounds(bounds) is not { } display) return;
        var fitted = GuideWindowPlacement.Fit(display.WorkArea, bounds);
        if (fitted != bounds) MoveWindow(app, fitted);
    }

    internal static FloatingWindowDisplay? ForWindow(IntPtr window) => Read(MonitorFromWindow(window, DefaultToNearest));

    internal static FloatingWindowDisplay? ForBounds(GuideWindowBounds bounds)
    {
        var rect = new NativeRect { Left = bounds.X, Top = bounds.Y, Right = bounds.Right, Bottom = bounds.Bottom };
        return Read(MonitorFromRect(in rect, DefaultToNearest));
    }

    internal static FloatingWindowDisplay? Default() =>
        ForWindow(GetForegroundWindow()) ?? Read(MonitorFromWindow(IntPtr.Zero, DefaultToPrimary));

    internal static FloatingWindowDisplay? Find(string name)
    {
        FloatingWindowDisplay? result = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            if (Read(monitor) is not { } display || !string.Equals(display.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
            result = display;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    private static FloatingWindowDisplay? Read(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return null;
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
        if (!GetMonitorInfo(monitor, ref info)) return null;
        var work = info.Work.ToBounds();
        if (work.Width <= 0 || work.Height <= 0) return null;
        return new FloatingWindowDisplay(monitor, info.Device, info.Screen.ToBounds(), work);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;

        internal readonly GuideWindowBounds ToBounds() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Screen, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromRect(in NativeRect rect, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("shcore.dll", ExactSpelling = true)]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}

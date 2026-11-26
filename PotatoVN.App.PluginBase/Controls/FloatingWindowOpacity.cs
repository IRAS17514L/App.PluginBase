using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using PotatoVN.App.PluginBase.Guides;

namespace PotatoVN.App.PluginBase;

internal sealed class FloatingWindowOpacity(Window window)
{
    private const int ExtendedStyle = -20;
    private const int Layered = 0x00080000;
    private const uint UseAlpha = 2;
    private bool _addedLayeredStyle;
    private int _transparency;

    internal bool TrySetTransparency(int transparency)
    {
        transparency = GuideAppearance.ClampTransparency(transparency);
        if (transparency == _transparency) return true;

        IntPtr handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        int style = GetWindowLong(handle, ExtendedStyle);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0) return false;
        bool needsLayeredStyle = (style & Layered) == 0;

        if (needsLayeredStyle && !TrySetStyle(handle, style | Layered)) return false;
        if (!SetLayeredWindowAttributes(handle, 0, GuideAppearance.Alpha(transparency), UseAlpha))
        {
            if (needsLayeredStyle) TrySetStyle(handle, style);
            return false;
        }

        _addedLayeredStyle |= needsLayeredStyle;
        if (transparency == 0 && _addedLayeredStyle && TrySetStyle(handle, style & ~Layered))
            _addedLayeredStyle = false;

        _transparency = transparency;
        return true;
    }

    private static bool TrySetStyle(IntPtr handle, int style) =>
        SetWindowLong(handle, ExtendedStyle, style) != 0 || Marshal.GetLastPInvokeError() == 0;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr handle, uint key, byte alpha, uint flags);
}

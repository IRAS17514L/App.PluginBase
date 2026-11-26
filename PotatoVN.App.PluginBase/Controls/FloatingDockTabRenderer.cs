using System;
using System.Runtime.InteropServices;
using PotatoVN.App.PluginBase.Guides;
using Windows.Graphics;

namespace PotatoVN.App.PluginBase;

internal static class FloatingDockTabRenderer
{
    private const byte SourceAlpha = 1;
    private const uint UseAlpha = 2;

    internal static bool TryDraw(IntPtr window, GuideDockLayout layout)
    {
        GuideWindowBounds bounds = layout.Tab;
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        try
        {
            BitmapInfoHeader info = new()
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = bounds.Width,
                Height = -bounds.Height,
                Planes = 1,
                BitCount = 32,
            };
            bitmap = CreateDIBSection(dc, ref info, 0, out IntPtr pixels, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || pixels == IntPtr.Zero) return false;

            previous = SelectObject(dc, bitmap);
            if (previous == IntPtr.Zero) return false;
            int[] image = HalfRoundedPixels(layout);
            Marshal.Copy(image, 0, pixels, image.Length);
            var position = new PointInt32(bounds.X, bounds.Y);
            var size = new SizeInt32(bounds.Width, bounds.Height);
            PointInt32 origin = default;
            BlendFunction blend = new() { SourceConstantAlpha = 128, AlphaFormat = SourceAlpha };
            return UpdateLayeredWindow(window, IntPtr.Zero, in position, in size, dc, in origin, 0, in blend, UseAlpha);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(dc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    private static int[] HalfRoundedPixels(GuideDockLayout layout)
    {
        int width = layout.Tab.Width;
        int height = layout.Tab.Height;
        var pixels = new int[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int alpha = layout.TabAlphaAt(x, y);
            int gray = (alpha + 1) / 2;
            // UpdateLayeredWindow 要求颜色通道使用预乘透明度。
            pixels[y * width + x] = (alpha << 24) | (gray << 16) | (gray << 8) | gray;
        }
        return pixels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPixelsPerMeter, YPixelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOperation, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info, uint usage,
        out IntPtr pixels, IntPtr section, uint offset);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, in PointInt32 position,
        in SizeInt32 size, IntPtr sourceDc, in PointInt32 origin, uint colorKey, in BlendFunction blend, uint flags);
}

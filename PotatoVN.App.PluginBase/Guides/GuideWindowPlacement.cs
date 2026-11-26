using System;

namespace PotatoVN.App.PluginBase.Guides;

public readonly record struct GuideWindowSize(double Width, double Height)
{
    internal bool IsValid => double.IsFinite(Width) && Width > 0 && double.IsFinite(Height) && Height > 0;

    internal (int Width, int Height) ToPixels(double scale) =>
        (RoundPixels(Width * scale), RoundPixels(Height * scale));

    private static int RoundPixels(double size) => (int)Math.Clamp(Math.Round(size), 1, int.MaxValue);
}

public sealed record GuideWindowPlacement(string DisplayName, double Horizontal, double Vertical, double Width, double Height)
{
    internal bool IsValid => !string.IsNullOrWhiteSpace(DisplayName) &&
        double.IsFinite(Horizontal) && Horizontal is >= 0 and <= 1 &&
        double.IsFinite(Vertical) && Vertical is >= 0 and <= 1 &&
        Size.IsValid;

    internal GuideWindowSize Size => new(Width, Height);

    internal static GuideWindowPlacement Capture(string displayName, GuideWindowBounds work, GuideWindowBounds window,
        double scale) => new(displayName,
            Fraction(window.X - work.X, work.Width - window.Width),
            Fraction(window.Y - work.Y, work.Height - window.Height),
            window.Width / scale, window.Height / scale);

    internal GuideWindowBounds Restore(GuideWindowBounds work, double scale, GuideWindowSize? size = null)
    {
        var (width, height) = (size ?? Size).ToPixels(scale);
        return Place(work, width, height, Horizontal, Vertical);
    }

    internal static GuideWindowBounds Center(GuideWindowBounds work, int width, int height) =>
        Place(work, width, height, .5, .5);

    internal static GuideWindowBounds Fit(GuideWindowBounds work, GuideWindowBounds window)
    {
        int width = Math.Min(window.Width, work.Width);
        int height = Math.Min(window.Height, work.Height);
        return new GuideWindowBounds(Math.Clamp(window.X, work.X, work.Right - width),
            Math.Clamp(window.Y, work.Y, work.Bottom - height), width, height);
    }

    private static GuideWindowBounds Place(GuideWindowBounds work, int width, int height, double horizontal, double vertical)
    {
        width = Math.Clamp(width, 1, work.Width);
        height = Math.Clamp(height, 1, work.Height);

        // 用剩余移动空间的比例保存位置，分辨率变化后也能保留贴边方向。
        int x = work.X + (int)Math.Round((work.Width - width) * horizontal);
        int y = work.Y + (int)Math.Round((work.Height - height) * vertical);
        return new GuideWindowBounds(x, y, width, height);
    }

    private static double Fraction(int offset, int space) =>
        space > 0 ? Math.Clamp(offset / (double)space, 0, 1) : .5;
}

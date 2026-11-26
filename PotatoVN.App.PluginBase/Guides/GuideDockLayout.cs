using System;

namespace PotatoVN.App.PluginBase.Guides;

internal enum GuideDockEdge { Left, Right, Top, Bottom }

internal readonly record struct GuideWindowBounds(int X, int Y, int Width, int Height)
{
    internal int Right => X + Width;
    internal int Bottom => Y + Height;

    internal bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

internal readonly record struct GuideDockLayout(GuideDockEdge Edge, GuideWindowBounds Expanded, GuideWindowBounds Tab)
{
    internal static GuideDockLayout? Find(GuideWindowBounds frame, GuideWindowBounds work,
        int threshold, int thickness, int length, GuideWindowBounds? displayBounds = null)
    {
        GuideDockEdge? nearest = null;
        int minimum = threshold + 1;
        bool verticalOverlap = frame.Y < work.Bottom && frame.Bottom > work.Y;
        bool horizontalOverlap = frame.X < work.Right && frame.Right > work.X;
        Check(GuideDockEdge.Left, frame.X - work.X, verticalOverlap);
        Check(GuideDockEdge.Right, frame.Right - work.Right, verticalOverlap);
        Check(GuideDockEdge.Top, frame.Y - work.Y, horizontalOverlap);
        Check(GuideDockEdge.Bottom, frame.Bottom - work.Bottom, horizontalOverlap);
        if (displayBounds is { } display && display != work)
        {
            // 有任务栏时，贴屏幕边界也能收起，窄条仍落在任务栏内侧。
            Check(GuideDockEdge.Left, frame.X - display.X, verticalOverlap);
            Check(GuideDockEdge.Right, frame.Right - display.Right, verticalOverlap);
            Check(GuideDockEdge.Top, frame.Y - display.Y, horizontalOverlap);
            Check(GuideDockEdge.Bottom, frame.Bottom - display.Bottom, horizontalOverlap);
        }
        if (nearest is not { } edge) return null;

        bool verticalTab = edge is GuideDockEdge.Left or GuideDockEdge.Right;
        int x = edge switch
        {
            GuideDockEdge.Left => work.X,
            GuideDockEdge.Right => Math.Max(work.X, work.Right - frame.Width),
            _ => Fit(frame.X, work.X, work.Right, frame.Width),
        };
        int y = edge switch
        {
            GuideDockEdge.Top => work.Y,
            GuideDockEdge.Bottom => Math.Max(work.Y, work.Bottom - frame.Height),
            _ => Fit(frame.Y, work.Y, work.Bottom, frame.Height),
        };
        int tabWidth = Math.Min(Math.Min(work.Width, frame.Width), verticalTab ? thickness : length);
        int tabHeight = Math.Min(Math.Min(work.Height, frame.Height), verticalTab ? length : thickness);
        int tabX = edge switch
        {
            GuideDockEdge.Left => work.X,
            GuideDockEdge.Right => work.Right - tabWidth,
            _ => Fit(x + (frame.Width - tabWidth) / 2, work.X, work.Right, tabWidth),
        };
        int tabY = edge switch
        {
            GuideDockEdge.Top => work.Y,
            GuideDockEdge.Bottom => work.Bottom - tabHeight,
            _ => Fit(y + (frame.Height - tabHeight) / 2, work.Y, work.Bottom, tabHeight),
        };
        return new GuideDockLayout(edge, frame with { X = x, Y = y },
            new GuideWindowBounds(tabX, tabY, tabWidth, tabHeight));

        void Check(GuideDockEdge candidate, int distance, bool overlaps)
        {
            int gap = Math.Abs(distance);
            if (!overlaps || gap >= minimum) return;
            minimum = gap;
            nearest = candidate;
        }
    }

    internal int TabAlphaAt(int x, int y)
    {
        bool vertical = Edge is GuideDockEdge.Left or GuideDockEdge.Right;
        int length = vertical ? Tab.Height : Tab.Width;
        int thickness = vertical ? Tab.Width : Tab.Height;
        int alongEdge = vertical ? y : x;
        int fromEdge = Edge switch
        {
            GuideDockEdge.Left => x,
            GuideDockEdge.Right => Tab.Width - 1 - x,
            GuideDockEdge.Top => y,
            _ => Tab.Height - 1 - y,
        };

        // 将圆角矩形沿屏幕边缘裁半，在同一可见厚度内保留朝内的圆角。
        double radius = Math.Min(length / 2.0, thickness);
        double dx = Math.Max(0, Math.Abs(alongEdge + 0.5 - length / 2.0) - (length / 2.0 - radius));
        double dy = Math.Max(0, fromEdge + 0.5 - (thickness - radius));
        double coverage = Math.Clamp(radius + 0.5 - Math.Sqrt(dx * dx + dy * dy), 0, 1);
        return (int)Math.Round(coverage * 255);
    }

    private static int Fit(int position, int start, int end, int size) =>
        Math.Clamp(position, start, Math.Max(start, end - size));
}

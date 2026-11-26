using System;

namespace PotatoVN.App.PluginBase.Guides;

internal static class GuideAppearance
{
    internal const int DefaultFontSize = 13;
    internal const int MinimumFontSize = 10;
    internal const int MaximumFontSize = 36;
    internal const int MaximumTransparency = 80;
    internal const int MinimumWindowWidth = 160;
    internal const int HeaderActionsWidth = 220;
    internal const int DetailedToolsWidth = 520;
    internal const int InitialDetailedWidth = 550;
    internal const int SingleLineFindWidth = 280;

    internal static GuideWindowSize InitialSize(bool compact) =>
        compact ? new(320, 420) : new(InitialDetailedWidth, 700);

    internal static GuideWindowSize SizeForModeSwitch(bool compact, GuideWindowSize? saved, GuideWindowSize current)
    {
        if (saved is { } size) return size;
        if (compact) return InitialSize(compact: true);
        return current.Width < DetailedToolsWidth ? current with { Width = InitialDetailedWidth } : current;
    }

    internal static int ClampFontSize(int size) => Math.Clamp(size, MinimumFontSize, MaximumFontSize);

    internal static int ClampTransparency(int transparency) => Math.Clamp(transparency, 0, MaximumTransparency);

    internal static byte Alpha(int transparency) =>
        (byte)Math.Round(255 * (100 - ClampTransparency(transparency)) / 100d);
}

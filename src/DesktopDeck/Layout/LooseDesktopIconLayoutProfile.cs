namespace DesktopDeck.Layout;

/// <summary>
/// Canonical logical spacing profile for DesktopDeck-managed loose desktop icons.
/// The loose desktop host and linked in-group spacing both consume these values so
/// the setting never needs a second persisted H/V spacing copy.
/// </summary>
public static class LooseDesktopIconLayoutProfile
{
    public const double ItemWidthDip = 88d;
    public const double ItemHeightDip = 104d;
    public const double HorizontalSpacingDip = 8d;
    public const double VerticalSpacingDip = 6d;
    public const double EdgeInsetDip = 12d;

    // A detached Recycle Bin is an independent, Explorer-hosted WPF child HWND.
    // Its original 96 x 114 DIP rectangle left very little room for the stock
    // icon artwork, two-line caption, selection border and shadow at mixed DPI.
    // These dimensions are the *whole HWND*, not the 54 DIP bitmap itself.
    public const double RecycleBinWidthDip = 128d;
    public const double RecycleBinHeightDip = 156d;
    public const double RecycleBinVisualDip = 54d;

    public static IconSpacing Spacing => new(HorizontalSpacingDip, VerticalSpacingDip);
    public static double CellWidthDip => ItemWidthDip + HorizontalSpacingDip;
    public static double CellHeightDip => ItemHeightDip + VerticalSpacingDip;

    // Automatic loose-icon placement must reserve the largest possible item
    // HWND. Do not change CellWidth/HeightDip: they are also used by the
    // optional linked *in-group* icon spacing setting.
    public static double LooseAutoCellWidthDip => Math.Max(ItemWidthDip, RecycleBinWidthDip) + HorizontalSpacingDip;
    public static double LooseAutoCellHeightDip => Math.Max(ItemHeightDip, RecycleBinHeightDip) + VerticalSpacingDip;
}

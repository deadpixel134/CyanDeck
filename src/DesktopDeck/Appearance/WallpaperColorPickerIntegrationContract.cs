namespace DesktopDeck.Appearance;

internal static class WallpaperColorPickerIntegrationContract
{
    internal const string ManagedCapability = "Appearance.Edit";

    internal const string UserGestureRule =
        "Start only from an explicit Pick color from wallpaper command. Use a temporary transparent click-capture surface with a crosshair cursor; Escape cancels. Do not install a global mouse hook or poll cursor position.";

    internal const string CoordinateRule =
        "Pass physical screen-pixel coordinates to WallpaperColorPickerController. The capture surface must convert WPF DIPs through its current per-monitor DPI transform before calling the controller.";

    internal const string SamplingRule =
        "Do not sample the composed screen with GetPixel, BitBlt, CopyFromScreen, or DesktopDeck screenshot code. Resolve the real wallpaper through Windows IDesktopWallpaper and sample the wallpaper bitmap itself so windows/icons/groups cannot contaminate the chosen color.";

    internal const string AppearanceRule =
        "Apply sampled RGB through the existing per-group background appearance command while preserving the group's current alpha/transparency. Then refresh v7.26 H/S/B/T editor state from the host.";

    internal const string ChameleonRule =
        "Sample and edit the saved base background color only. v7.22 Chameleon blended/full-color presentation remains transient and is not a sampling source.";

    internal const string PersistenceRule =
        "The picker adds no new durable state. Existing ARGB appearance persistence, copy/paste, templates, snapshots, and the debounced state writer retain ownership.";

    internal const string SafetyRule =
        "Reject non-file and UNC wallpaper paths to avoid unexpected network access. Do not log wallpaper paths or sampled coordinates. Cap decoded wallpaper images at 100 million pixels.";
}

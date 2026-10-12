using System;

namespace DesktopDeck.Appearance;

public enum ShortcutBadgeKind
{
    None = 0,
    FileShortcut,
    InternetShortcut,
}

public enum ShortcutKindHint
{
    Unknown = 0,
    NotShortcut,
    FileShortcut,
    InternetShortcut,
}

public sealed record ShortcutBadgeOptions
{
    public bool Enabled { get; init; } = true;
    public bool HideFileShortcutBadges { get; init; }
    public bool HideInternetShortcutBadges { get; init; }
    public bool IncludeFolderPortalItems { get; init; }
    public double RelativeBadgeScale { get; init; } = 0.34;
    public double MinimumBadgeSizeDip { get; init; } = 8.0;
    public double MaximumBadgeSizeDip { get; init; } = 22.0;

    public ShortcutBadgeOptions Normalize()
    {
        static double FiniteOr(double value, double fallback) => double.IsFinite(value) ? value : fallback;

        var scale = Math.Clamp(FiniteOr(RelativeBadgeScale, 0.34), 0.20, 0.50);
        var min = Math.Clamp(FiniteOr(MinimumBadgeSizeDip, 8.0), 6.0, 20.0);
        var max = Math.Clamp(FiniteOr(MaximumBadgeSizeDip, 22.0), 10.0, 40.0);
        if (max < min)
        {
            (min, max) = (max, min);
        }

        return this with
        {
            RelativeBadgeScale = scale,
            MinimumBadgeSizeDip = min,
            MaximumBadgeSizeDip = max,
        };
    }
}

public sealed record ShortcutBadgeContext
{
    public string? ItemPath { get; init; }
    public ShortcutKindHint KindHint { get; init; } = ShortcutKindHint.Unknown;
    public bool IsFolderPortalItem { get; init; }
    public bool IsDesktopBackedItem { get; init; } = true;
    public bool IsItemInteractive { get; init; } = true;
}

public sealed record ShortcutBadgeDecision
{
    public ShortcutBadgeKind Kind { get; init; }
    public bool IsShortcut => Kind != ShortcutBadgeKind.None;
    public bool ShowBadge { get; init; }
    public bool SuppressHostShortcutOverlay { get; init; }
    public string Reason { get; init; } = string.Empty;
}

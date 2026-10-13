using System;

namespace CyanDeck.Appearance;

public static class ShortcutBadgePolicy
{
    public static ShortcutBadgeDecision Evaluate(
        ShortcutBadgeContext context,
        ShortcutBadgeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var normalized = (options ?? new ShortcutBadgeOptions()).Normalize();
        var kind = ResolveKind(context);

        if (kind == ShortcutBadgeKind.None)
        {
            return Decision(kind, show: false, suppressHost: false, "not-shortcut");
        }

        if (!normalized.Enabled)
        {
            return Decision(kind, show: false, suppressHost: false, "feature-disabled");
        }

        if (!context.IsItemInteractive)
        {
            return Decision(kind, show: false, suppressHost: true, "item-noninteractive");
        }

        if (context.IsFolderPortalItem && !normalized.IncludeFolderPortalItems)
        {
            return Decision(kind, show: false, suppressHost: false, "portal-preserves-host-view");
        }

        if (!context.IsDesktopBackedItem && !context.IsFolderPortalItem)
        {
            return Decision(kind, show: false, suppressHost: false, "non-desktop-item");
        }

        var hidden = kind switch
        {
            ShortcutBadgeKind.FileShortcut => normalized.HideFileShortcutBadges,
            ShortcutBadgeKind.InternetShortcut => normalized.HideInternetShortcutBadges,
            _ => false,
        };

        if (hidden)
        {
            return Decision(kind, show: false, suppressHost: true, "hidden-by-user-setting");
        }

        // CyanDeck owns the visible badge for supported desktop-backed shortcuts.
        // The host icon extractor should therefore supply a clean base icon without
        // a Shell shortcut overlay so the marker cannot be duplicated.
        return Decision(kind, show: true, suppressHost: true, "cyandeck-badge");
    }

    public static ShortcutBadgeKind ResolveKind(ShortcutBadgeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.KindHint switch
        {
            ShortcutKindHint.NotShortcut => ShortcutBadgeKind.None,
            ShortcutKindHint.FileShortcut => ShortcutBadgeKind.FileShortcut,
            ShortcutKindHint.InternetShortcut => ShortcutBadgeKind.InternetShortcut,
            _ => ResolveKindFromName(context.ItemPath),
        };
    }

    public static ShortcutBadgeKind ResolveKindFromName(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName))
        {
            return ShortcutBadgeKind.None;
        }

        var value = pathOrName.Trim();
        if (value.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return ShortcutBadgeKind.FileShortcut;
        }

        if (value.EndsWith(".url", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".website", StringComparison.OrdinalIgnoreCase))
        {
            return ShortcutBadgeKind.InternetShortcut;
        }

        return ShortcutBadgeKind.None;
    }

    private static ShortcutBadgeDecision Decision(
        ShortcutBadgeKind kind,
        bool show,
        bool suppressHost,
        string reason) => new()
        {
            Kind = kind,
            ShowBadge = show,
            SuppressHostShortcutOverlay = suppressHost,
            Reason = reason,
        };
}

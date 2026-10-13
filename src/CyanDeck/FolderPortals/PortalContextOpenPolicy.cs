using System;

namespace CyanDeck.FolderPortals;

/// <summary>
/// Routes only the native context-menu canonical verb "open" for one folder item in a Folder Portal.
/// Double-click, Enter and other open surfaces remain owned by the existing Portal navigation policy.
/// </summary>
public static class PortalContextOpenPolicy
{
    public const string CanonicalOpenVerb = "open";

    public static PortalContextOpenDecision Decide(PortalContextOpenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.IsContextMenuInvocation ||
            !context.IsFolderPortal ||
            !context.IsSingleSelection ||
            !context.SelectedItemIsDirectory ||
            context.IsSafeMode ||
            !context.MasterEnabled ||
            !context.CanOpenItems)
        {
            return PortalContextOpenDecision.DelegateToExistingShellPipeline;
        }

        // Do not match localized visible captions. The integration point supplies the
        // language-independent Shell canonical verb from GetCommandString(GCS_VERBW).
        var canonicalVerb = context.CanonicalVerb?.Trim();
        if (!string.Equals(canonicalVerb, CanonicalOpenVerb, StringComparison.OrdinalIgnoreCase))
        {
            return PortalContextOpenDecision.DelegateToExistingShellPipeline;
        }

        return PortalContextOpenDecision.OpenFolderInExplorer;
    }
}

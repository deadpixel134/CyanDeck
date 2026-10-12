using System;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace DesktopDeck.FolderPortals;

public static class AltDragFolderPortalPolicy
{
    public const int MaxCandidatePathChars = 32767;

    public static AltDragFolderPortalDecision EvaluatePreview(
        in AltDragFolderPortalProbe probe,
        bool requireExactAltModifier = true)
    {
        if (!probe.Enabled)
            return No(AltDragFolderPortalDecisionCode.Disabled);
        if (!probe.MasterEnabled)
            return No(AltDragFolderPortalDecisionCode.MasterDisabled);
        if (probe.SafeMode)
            return No(AltDragFolderPortalDecisionCode.SafeMode);
        if (!probe.CapabilityAllowed)
            return No(AltDragFolderPortalDecisionCode.CapabilityDenied);
        if (!probe.BlankDesktopTarget)
            return No(AltDragFolderPortalDecisionCode.NonBlankDesktop);

        var altHeld = (probe.Modifiers & ModifierKeys.Alt) != 0;
        if (!altHeld)
            return No(AltDragFolderPortalDecisionCode.AltNotHeld);

        if (requireExactAltModifier && probe.Modifiers != ModifierKeys.Alt)
            return No(AltDragFolderPortalDecisionCode.ConflictingModifier);

        if (!probe.HasFileDrop)
            return No(AltDragFolderPortalDecisionCode.MissingFileDrop);
        if (probe.ItemCount != 1)
            return No(AltDragFolderPortalDecisionCode.WrongItemCount);
        if (!TryNormalizeCandidatePath(probe.CandidatePath, out _))
            return No(AltDragFolderPortalDecisionCode.InvalidPath);
        if ((probe.AllowedEffects & DragDropEffects.Link) == 0)
            return No(AltDragFolderPortalDecisionCode.LinkEffectNotAllowed);

        return new AltDragFolderPortalDecision(true, AltDragFolderPortalDecisionCode.Ready);
    }

    public static bool TryNormalizeCandidatePath(string? candidate, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxCandidatePathChars)
            return false;
        if (candidate.IndexOf('\0') >= 0)
            return false;
        if (candidate.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!Path.IsPathFullyQualified(candidate))
            return false;

        try
        {
            normalized = Path.GetFullPath(candidate);
            return Path.IsPathFullyQualified(normalized);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    private static AltDragFolderPortalDecision No(AltDragFolderPortalDecisionCode code) =>
        new(false, code);
}

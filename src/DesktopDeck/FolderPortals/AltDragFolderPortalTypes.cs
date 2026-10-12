using System;
using System.Windows;
using System.Windows.Input;

namespace DesktopDeck.FolderPortals;

public sealed record AltDragFolderPortalOptions
{
    public bool Enabled { get; init; } = true;
    public bool RequireExactAltModifier { get; init; } = true;
}

public enum AltDragFolderPortalDecisionCode
{
    Ready,
    Disabled,
    MasterDisabled,
    SafeMode,
    CapabilityDenied,
    NonBlankDesktop,
    AltNotHeld,
    ConflictingModifier,
    MissingFileDrop,
    WrongItemCount,
    InvalidPath,
    LinkEffectNotAllowed,
    NotDirectory,
    CreationFailed
}

public readonly record struct AltDragFolderPortalProbe(
    bool Enabled,
    bool MasterEnabled,
    bool SafeMode,
    bool CapabilityAllowed,
    bool BlankDesktopTarget,
    ModifierKeys Modifiers,
    bool HasFileDrop,
    int ItemCount,
    string? CandidatePath,
    DragDropEffects AllowedEffects);

public readonly record struct AltDragFolderPortalDecision(
    bool Candidate,
    AltDragFolderPortalDecisionCode Code);

public readonly record struct AltDragFolderPortalCreateRequest(
    string FolderPath,
    Point ScreenPoint);

public enum AltDragFolderPortalCreateResult
{
    Created,
    ReusedExisting,
    Rejected,
    Failed
}

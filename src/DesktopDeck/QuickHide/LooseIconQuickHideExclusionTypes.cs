using System;
using System.Collections.Generic;

namespace DesktopDeck.QuickHide;

public enum QuickHideLooseIconSurfaceKind
{
    DesktopDeckManagedLooseIcon = 0,
    NativeExplorerDesktopIcon = 1,
    GroupItem = 2,
    FolderPortalItem = 3,
    Unknown = 4
}

public enum QuickHideVisibilityMode
{
    HideAllDesktopIconsAndGroups = 0,
    HideIconsNotInGroups = 1,
    HideGroupsOnly = 2
}

public enum LooseIconQuickHideVisibilityDecision
{
    PreserveNormalVisibility = 0,
    KeepVisibleExcluded = 1,
    HideLooseIcon = 2,
    DeferToGroupPolicy = 3,
    DeferToNativeDesktopLayer = 4,
    IgnoreUnsupportedSurface = 5
}

public sealed record LooseDesktopItemQuickHideSnapshot(
    string ItemId,
    QuickHideLooseIconSurfaceKind SurfaceKind,
    bool IsLooseDesktopItem,
    bool IsGroupMember,
    bool ExcludeFromQuickHide)
{
    public bool HasStableId => !string.IsNullOrWhiteSpace(ItemId);

    public bool IsEditableManagedLooseIcon =>
        HasStableId &&
        SurfaceKind == QuickHideLooseIconSurfaceKind.DesktopDeckManagedLooseIcon &&
        IsLooseDesktopItem &&
        !IsGroupMember;
}

public sealed record LooseIconQuickHideVisibilityRequest(
    bool QuickHideFeatureEnabled,
    bool IsQuickHideActive,
    QuickHideVisibilityMode Mode,
    LooseDesktopItemQuickHideSnapshot Item);

public enum LooseIconExclusionEditStatus
{
    Ready = 0,
    UnsupportedSurface = 1,
    MissingStableId = 2,
    CapabilityBlocked = 3,
    NoEligibleItems = 4,
    PersistFailed = 5,
    NoChange = 6
}

public sealed record LooseIconQuickHideMutation(
    string ItemId,
    bool ExcludeFromQuickHide);

public sealed record LooseIconExclusionEditResult(
    LooseIconExclusionEditStatus Status,
    int ChangedItemCount,
    bool VisibilityRefreshRequested);

public enum LooseIconBatchCheckState
{
    Hidden = 0,
    Unchecked = 1,
    Checked = 2,
    Indeterminate = 3
}

public sealed record LooseIconBatchMenuState(
    LooseIconBatchCheckState CheckState,
    int EligibleCount,
    bool NextExcludeValue,
    bool IsEnabled);

/// <summary>
/// Host contract for one atomic persistence transaction. The implementation should update the
/// existing DesktopDeck item-reference model by stable item ID and write state once. If this
/// method returns false, it must leave the model unchanged or restore the old values before return.
/// </summary>
public delegate System.Threading.Tasks.Task<bool> PersistLooseIconQuickHideMutationsAsync(
    IReadOnlyList<LooseIconQuickHideMutation> mutations,
    System.Threading.CancellationToken cancellationToken);

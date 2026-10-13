using System;

namespace CyanDeck.Input;

public enum TabHeaderDropTargetKind
{
    Collection = 0,
    FolderPortal = 1,
}

[Flags]
public enum TabHeaderDropOperation
{
    None = 0,
    Copy = 1,
    Move = 2,
    Link = 4,
}

[Flags]
public enum TabHeaderDropModifiers
{
    None = 0,
    LeftMouseButton = 1,
    RightMouseButton = 2,
    MiddleMouseButton = 4,
    Shift = 8,
    Control = 16,
    Alt = 32,
}

public readonly record struct TabHeaderDropTarget(
    string GroupStableId,
    string TabStableId,
    TabHeaderDropTargetKind Kind)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(GroupStableId) &&
        !string.IsNullOrWhiteSpace(TabStableId) &&
        Kind is TabHeaderDropTargetKind.Collection or TabHeaderDropTargetKind.FolderPortal;
}

public readonly record struct TabHeaderDropContext(
    TabHeaderDropOperation AllowedOperations,
    TabHeaderDropModifiers Modifiers)
{
    public bool Allows(TabHeaderDropOperation operation) =>
        IsSingleOperation(operation) && (AllowedOperations & operation) == operation;

    public static bool IsSingleOperation(TabHeaderDropOperation operation) =>
        operation is TabHeaderDropOperation.Copy or
            TabHeaderDropOperation.Move or
            TabHeaderDropOperation.Link;
}

public readonly record struct TabHeaderDropEvaluation(
    bool Accepted,
    TabHeaderDropOperation PreferredOperation,
    bool ActivateTabAfterCommit)
{
    public static TabHeaderDropEvaluation Reject =>
        new(false, TabHeaderDropOperation.None, false);

    public bool IsValidFor(TabHeaderDropContext context) =>
        Accepted && context.Allows(PreferredOperation);
}

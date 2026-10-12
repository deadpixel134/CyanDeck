using System;

namespace DesktopDeck.Peek;

public sealed record PeekTaskbarButtonOptions
{
    public bool Enabled { get; init; }
    public string? CustomIconPath { get; init; }
    public int ActivationDebounceMilliseconds { get; init; } = 280;

    public PeekTaskbarButtonOptions Normalize() => this with
    {
        CustomIconPath = string.IsNullOrWhiteSpace(CustomIconPath) ? null : CustomIconPath.Trim(),
        ActivationDebounceMilliseconds = Math.Clamp(ActivationDebounceMilliseconds, 150, 1000),
    };
}

public sealed record PeekTaskbarButtonIcon(
    string RelaunchIconResource,
    string? CustomIconPath,
    bool UsesCustomIcon);

public enum PeekTaskbarActivationKind
{
    Activated,
    RestoreCommand,
}

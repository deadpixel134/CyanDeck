using System;

namespace DesktopDeck.Peek;

public enum PeekHotCornerAction
{
    DoNothing = 0,
    ShowFences = 1,
    ShowFullDesktop = 2,
}

public enum TaskbarEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
}

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public bool IsValid => Width > 0 && Height > 0;
}

public sealed class PeekHotCornerOptions
{
    public bool Enabled { get; init; }
    public PeekHotCornerAction LeftClickAction { get; init; } = PeekHotCornerAction.ShowFences;
    public PeekHotCornerAction RightClickAction { get; init; } = PeekHotCornerAction.ShowFullDesktop;
    public int HitThicknessPx { get; init; } = 10;

    public int GetNormalizedHitThicknessPx()
        => Math.Clamp(HitThicknessPx, 4, 32);

    public bool HasAnyAction()
        => LeftClickAction != PeekHotCornerAction.DoNothing ||
           RightClickAction != PeekHotCornerAction.DoNothing;
}

public readonly record struct PrimaryTaskbarMetric(
    TaskbarEdge Edge,
    PixelRect BoundsPx,
    bool IsAutoHide)
{
    public bool IsValid => BoundsPx.IsValid;
}

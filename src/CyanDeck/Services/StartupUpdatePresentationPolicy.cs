namespace CyanDeck.Services;

/// <summary>
/// Pure startup status presentation thresholds. Fast successful update checks
/// stay entirely headless; slower checks show a deliberate, non-flickering
/// status surface before any Fence/Calendar desktop HWND exists.
/// </summary>
public static class StartupUpdatePresentationPolicy
{
    public const int CheckingGraceMilliseconds = 750;
    public const int MinimumVisibleMilliseconds = 950;

    public static bool ShouldShowChecking(bool checkCompleted, long elapsedMilliseconds)
        => !checkCompleted && elapsedMilliseconds >= CheckingGraceMilliseconds;

    public static int RemainingVisibleMilliseconds(long visibleMilliseconds, bool userRequestedSkip)
    {
        if (userRequestedSkip) return 0;
        var elapsed = System.Math.Max(0L, visibleMilliseconds);
        return (int)System.Math.Max(0L, MinimumVisibleMilliseconds - elapsed);
    }
}

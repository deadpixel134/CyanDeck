using System;

namespace CyanDeck.Snapshots;

internal enum SnapshotScreenPreviewCaptureReason
{
    ManualSnapshot = 0,
    AutomaticDailyBackup = 1,
    UndoSnapshot = 2,
    RecoveryCheckpoint = 3,
    ImportedSnapshot = 4,
}

internal enum SnapshotScreenPreviewStatus
{
    Captured = 0,
    SkippedDisabled = 1,
    SkippedReason = 2,
    SkippedSafeMode = 3,
    SkippedInvalidId = 4,
    CaptureUnavailable = 5,
    CaptureFailed = 6,
    PersistFailed = 7,
}

internal sealed record SnapshotScreenPreviewOptions
{
    // Privacy-first defaults: actual screen contents may include other visible applications.
    internal bool Enabled { get; init; } = false;
    internal bool CaptureManualSnapshots { get; init; } = true;
    internal bool CaptureAutomaticDailyBackups { get; init; } = false;
    internal int MaxThumbnailWidthPx { get; init; } = 1600;
    internal int MaxThumbnailHeightPx { get; init; } = 900;
    internal long MaxEncodedBytes { get; init; } = 24L * 1024L * 1024L;

    internal SnapshotScreenPreviewOptions Normalize() => this with
    {
        MaxThumbnailWidthPx = Math.Clamp(MaxThumbnailWidthPx, 320, 3840),
        MaxThumbnailHeightPx = Math.Clamp(MaxThumbnailHeightPx, 180, 2160),
        MaxEncodedBytes = Math.Clamp(MaxEncodedBytes, 1L * 1024L * 1024L, 64L * 1024L * 1024L),
    };

    internal bool ShouldCapture(SnapshotScreenPreviewCaptureReason reason) => reason switch
    {
        SnapshotScreenPreviewCaptureReason.ManualSnapshot => CaptureManualSnapshots,
        SnapshotScreenPreviewCaptureReason.AutomaticDailyBackup => CaptureAutomaticDailyBackups,
        _ => false,
    };
}

internal readonly record struct VirtualDesktopPixelRect(int Left, int Top, int Width, int Height)
{
    internal bool IsValid =>
        Width > 0 &&
        Height > 0 &&
        Width <= 131072 &&
        Height <= 131072;
}

internal readonly record struct SnapshotScreenPreviewCapture(
    byte[] PngBytes,
    VirtualDesktopPixelRect SourceBounds,
    int ThumbnailWidthPx,
    int ThumbnailHeightPx)
{
    internal bool IsValid =>
        PngBytes is { Length: > 8 } &&
        SourceBounds.IsValid &&
        ThumbnailWidthPx > 0 &&
        ThumbnailHeightPx > 0;
}

internal readonly record struct SnapshotScreenPreviewResult(
    SnapshotScreenPreviewStatus Status,
    int ThumbnailWidthPx = 0,
    int ThumbnailHeightPx = 0,
    string? ErrorKind = null)
{
    internal bool Succeeded => Status == SnapshotScreenPreviewStatus.Captured;
}

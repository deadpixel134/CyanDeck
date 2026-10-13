using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace CyanDeck.Snapshots;

/// <summary>
/// Best-effort coordinator. Preview capture failure must never fail layout snapshot creation.
/// </summary>
internal sealed class SnapshotScreenPreviewCoordinator
{
    private readonly SnapshotScreenPreviewStore _store;
    private SnapshotScreenPreviewOptions _options;

    internal SnapshotScreenPreviewCoordinator(string appDataRoot, SnapshotScreenPreviewOptions? options = null)
    {
        _store = new SnapshotScreenPreviewStore(appDataRoot);
        _options = (options ?? new SnapshotScreenPreviewOptions()).Normalize();
    }

    internal SnapshotScreenPreviewOptions Options
    {
        get => _options;
        set => _options = (value ?? new SnapshotScreenPreviewOptions()).Normalize();
    }

    internal async Task<SnapshotScreenPreviewResult> CaptureAsync(
        string snapshotId,
        SnapshotScreenPreviewCaptureReason reason,
        bool isSafeMode,
        CancellationToken cancellationToken = default)
    {
        SnapshotScreenPreviewOptions options = _options.Normalize();
        if (!options.Enabled)
        {
            return new SnapshotScreenPreviewResult(SnapshotScreenPreviewStatus.SkippedDisabled);
        }

        if (!options.ShouldCapture(reason))
        {
            return new SnapshotScreenPreviewResult(SnapshotScreenPreviewStatus.SkippedReason);
        }

        if (isSafeMode)
        {
            return new SnapshotScreenPreviewResult(SnapshotScreenPreviewStatus.SkippedSafeMode);
        }

        if (!_store.TryGetPath(snapshotId, out _))
        {
            return new SnapshotScreenPreviewResult(SnapshotScreenPreviewStatus.SkippedInvalidId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GdiVirtualDesktopThumbnailCapture.TryCapture(options, out SnapshotScreenPreviewCapture capture, out string? errorKind))
            {
                return new SnapshotScreenPreviewResult(
                    errorKind == "unsupported-platform"
                        ? SnapshotScreenPreviewStatus.CaptureUnavailable
                        : SnapshotScreenPreviewStatus.CaptureFailed,
                    ErrorKind: errorKind);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!_store.TryWriteAtomic(snapshotId, capture.PngBytes, options.MaxEncodedBytes))
            {
                return new SnapshotScreenPreviewResult(
                    SnapshotScreenPreviewStatus.PersistFailed,
                    capture.ThumbnailWidthPx,
                    capture.ThumbnailHeightPx,
                    "write-sidecar");
            }

            return new SnapshotScreenPreviewResult(
                SnapshotScreenPreviewStatus.Captured,
                capture.ThumbnailWidthPx,
                capture.ThumbnailHeightPx);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal BitmapSource? TryLoad(string snapshotId) =>
        _store.TryLoad(snapshotId, _options.Normalize().MaxEncodedBytes);

    internal void Delete(string snapshotId) => _store.Delete(snapshotId);

    internal int PruneToSnapshotIds(IEnumerable<string> liveSnapshotIds) =>
        _store.PruneToSnapshotIds(liveSnapshotIds);

    internal string PreviewDirectory => _store.RootDirectory;
}

using System;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopDeck.QuickHide;

/// <summary>
/// Event-driven, one-shot startup visibility normalizer.
/// The host calls TryApplyAsync from existing startup/display-recovery completion paths.
/// This class creates no timer and performs no polling.
/// </summary>
public sealed class QuickHideStartupVisibilityCoordinator : IDisposable
{
    private readonly Func<QuickHideFeatureOptions> _readOptions;
    private readonly Func<QuickHideRuntimeSnapshot> _readState;
    private readonly Func<QuickHideShowReason, CancellationToken, Task<bool>> _ensureVisibleAsync;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _completed;
    private bool _disposed;

    public QuickHideStartupVisibilityCoordinator(
        Func<QuickHideFeatureOptions> readOptions,
        Func<QuickHideRuntimeSnapshot> readState,
        Func<QuickHideShowReason, CancellationToken, Task<bool>> ensureVisibleAsync)
    {
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _readState = readState ?? throw new ArgumentNullException(nameof(readState));
        _ensureVisibleAsync = ensureVisibleAsync ?? throw new ArgumentNullException(nameof(ensureVisibleAsync));
    }

    public async Task<QuickHideStartupApplyResult> TryApplyAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_completed)
            {
                return new QuickHideStartupApplyResult(
                    QuickHideStartupApplyStatus.AlreadyCompleted,
                    VisibilityWasNormalized: false,
                    ShowReason: null);
            }

            var state = _readState();

            if (state.SafeMode)
            {
                _completed = true;
                return new QuickHideStartupApplyResult(
                    QuickHideStartupApplyStatus.SkippedSafeMode,
                    VisibilityWasNormalized: false,
                    ShowReason: null);
            }

            if (state.MasterDisabled)
            {
                _completed = true;
                return new QuickHideStartupApplyResult(
                    QuickHideStartupApplyStatus.SkippedMasterDisabled,
                    VisibilityWasNormalized: false,
                    ShowReason: null);
            }

            if (!state.DesktopShellReady)
            {
                return new QuickHideStartupApplyResult(
                    QuickHideStartupApplyStatus.DeferredDesktopNotReady,
                    VisibilityWasNormalized: false,
                    ShowReason: null);
            }

            if (state.DisplayTransitionActive)
            {
                return new QuickHideStartupApplyResult(
                    QuickHideStartupApplyStatus.DeferredDisplayTransition,
                    VisibilityWasNormalized: false,
                    ShowReason: null);
            }

            var options = _readOptions() ?? new QuickHideFeatureOptions();
            var reason = QuickHideFeaturePolicy.GetStartupShowReason(options, state);
            if (reason is null)
            {
                _completed = true;
                return new QuickHideStartupApplyResult(
                    QuickHideStartupApplyStatus.NoActionNeeded,
                    VisibilityWasNormalized: false,
                    ShowReason: null);
            }

            try
            {
                var visible = await _ensureVisibleAsync(reason.Value, cancellationToken).ConfigureAwait(true);
                if (!visible)
                {
                    // Leave the coordinator pending. The host may invoke it again from an
                    // existing attachment/display-repair completion event; no retry loop is created here.
                    return new QuickHideStartupApplyResult(
                        QuickHideStartupApplyStatus.FailedRetryable,
                        VisibilityWasNormalized: false,
                        ShowReason: reason);
                }

                _completed = true;
                return new QuickHideStartupApplyResult(
                    QuickHideStartupApplyStatus.Applied,
                    VisibilityWasNormalized: true,
                    ShowReason: reason);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return new QuickHideStartupApplyResult(
                    QuickHideStartupApplyStatus.FailedRetryable,
                    VisibilityWasNormalized: false,
                    ShowReason: reason);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _gate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(QuickHideStartupVisibilityCoordinator));
    }
}

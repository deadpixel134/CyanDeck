using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace CyanDeck.Input;

/// <summary>
/// WPF adapter for optional single-touch icon flicks. It observes ordinary touch input first and
/// captures only after the pure recognizer reports a high-speed flick candidate. Slow taps/drags
/// therefore remain on the host path. The host owns source/target identity and the actual move.
/// </summary>
internal sealed class WpfDesktopIconFlickBridge : IDisposable
{
    private readonly UIElement _surface;
    private readonly DesktopIconFlickRecognizer _recognizer = new();
    private readonly Func<DesktopIconFlickOptions> _getOptions;
    private readonly Func<object?, DesktopIconFlickSource?> _tryResolveSource;
    private readonly Func<object?, bool> _isBlockedSource;
    private readonly Func<DesktopIconFlickSource, IReadOnlyList<DesktopIconFlickTarget>> _getTargets;
    private readonly Func<DesktopIconFlickSource, DesktopIconFlickTarget, bool> _canMove;
    private readonly Action<DesktopIconFlickSource> _cancelOrdinaryDrag;
    private readonly Action<DesktopIconFlickTarget?> _previewTarget;
    private readonly Action<DesktopIconFlickSource, DesktopIconFlickTarget> _commitMove;
    private readonly Func<int>? _getGlobalActiveTouchCount;
    private readonly EventHandler<TouchEventArgs> _previewTouchDownHandler;
    private readonly EventHandler<TouchEventArgs> _previewTouchMoveHandler;
    private readonly EventHandler<TouchEventArgs> _previewTouchUpHandler;
    private readonly EventHandler<TouchEventArgs> _touchLeaveHandler;
    private readonly RoutedEventHandler _unloadedHandler;

    private readonly HashSet<int> _activeTouchIds = new();
    private DesktopIconFlickSource? _source;
    private double _startX;
    private double _startY;
    private TouchDevice? _capturedDevice;
    private DesktopIconFlickTarget? _currentTarget;
    private bool _disposed;
    private bool _releasingCapture;

    internal WpfDesktopIconFlickBridge(
        UIElement surface,
        Func<DesktopIconFlickOptions> getOptions,
        Func<object?, DesktopIconFlickSource?> tryResolveSource,
        Func<object?, bool> isBlockedSource,
        Func<DesktopIconFlickSource, IReadOnlyList<DesktopIconFlickTarget>> getTargets,
        Func<DesktopIconFlickSource, DesktopIconFlickTarget, bool> canMove,
        Action<DesktopIconFlickSource> cancelOrdinaryDrag,
        Action<DesktopIconFlickTarget?> previewTarget,
        Action<DesktopIconFlickSource, DesktopIconFlickTarget> commitMove,
        Func<int>? getGlobalActiveTouchCount = null)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _getOptions = getOptions ?? throw new ArgumentNullException(nameof(getOptions));
        _tryResolveSource = tryResolveSource ?? throw new ArgumentNullException(nameof(tryResolveSource));
        _isBlockedSource = isBlockedSource ?? throw new ArgumentNullException(nameof(isBlockedSource));
        _getTargets = getTargets ?? throw new ArgumentNullException(nameof(getTargets));
        _canMove = canMove ?? throw new ArgumentNullException(nameof(canMove));
        _cancelOrdinaryDrag = cancelOrdinaryDrag ?? throw new ArgumentNullException(nameof(cancelOrdinaryDrag));
        _previewTarget = previewTarget ?? throw new ArgumentNullException(nameof(previewTarget));
        _commitMove = commitMove ?? throw new ArgumentNullException(nameof(commitMove));
        _getGlobalActiveTouchCount = getGlobalActiveTouchCount;
        _previewTouchDownHandler = OnPreviewTouchDown;
        _previewTouchMoveHandler = OnPreviewTouchMove;
        _previewTouchUpHandler = OnPreviewTouchUp;
        _touchLeaveHandler = OnTouchLeave;
        _unloadedHandler = OnSurfaceUnloaded;

        _surface.AddHandler(UIElement.PreviewTouchDownEvent, _previewTouchDownHandler, true);
        _surface.AddHandler(UIElement.PreviewTouchMoveEvent, _previewTouchMoveHandler, true);
        _surface.AddHandler(UIElement.PreviewTouchUpEvent, _previewTouchUpHandler, true);
        _surface.TouchLeave += _touchLeaveHandler;
        _surface.LostTouchCapture += OnLostTouchCapture;
        _surface.AddHandler(FrameworkElement.UnloadedEvent, _unloadedHandler);
    }

    private void OnPreviewTouchDown(object? sender, TouchEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _activeTouchIds.Add(e.TouchDevice.Id);
        if (_activeTouchIds.Count > 1 || (_getGlobalActiveTouchCount?.Invoke() ?? _activeTouchIds.Count) > 1)
        {
            CancelCurrent(DesktopIconFlickCancelReason.MultiTouch);
            return;
        }

        DesktopIconFlickOptions options = _getOptions().Normalize();
        if (!options.Enabled || _isBlockedSource(e.OriginalSource))
        {
            return;
        }

        DesktopIconFlickSource? source = _tryResolveSource(e.OriginalSource);
        if (source is not DesktopIconFlickSource resolved || !resolved.IsValid)
        {
            return;
        }

        TouchPoint point = e.GetTouchPoint(_surface);
        DesktopIconFlickSample sample = ToSample(e.TouchDevice.Id, point.Position, Environment.TickCount64);
        DesktopIconFlickUpdate update = _recognizer.Begin(sample, CurrentSurface(), options);
        if (update.Kind != DesktopIconFlickUpdateKind.Tracking)
        {
            return;
        }

        _source = resolved;
        _startX = sample.X;
        _startY = sample.Y;
        _currentTarget = null;
        // Intentionally do not capture or mark handled here: normal tap/drag remains available.
    }

    private void OnPreviewTouchMove(object? sender, TouchEventArgs e)
    {
        if (_disposed || _source is not DesktopIconFlickSource source ||
            _recognizer.ActiveTouchId != e.TouchDevice.Id)
        {
            return;
        }

        if (_activeTouchIds.Count != 1 || (_getGlobalActiveTouchCount?.Invoke() ?? _activeTouchIds.Count) != 1)
        {
            CancelCurrent(DesktopIconFlickCancelReason.MultiTouch);
            return;
        }

        DesktopIconFlickOptions options = _getOptions().Normalize();
        TouchPoint point = e.GetTouchPoint(_surface);
        DesktopIconFlickSample sample = ToSample(e.TouchDevice.Id, point.Position, Environment.TickCount64);
        DesktopIconFlickUpdate update = _recognizer.Move(sample, options);

        if (update.Kind == DesktopIconFlickUpdateKind.Cancelled)
        {
            CancelCurrent(update.CancelReason);
            return;
        }

        if (update.Kind == DesktopIconFlickUpdateKind.Armed && _capturedDevice is null)
        {
            if (!e.TouchDevice.Capture(_surface, CaptureMode.Element))
            {
                CancelCurrent(DesktopIconFlickCancelReason.LostCapture);
                return;
            }

            _capturedDevice = e.TouchDevice;
            _cancelOrdinaryDrag(source);
        }

        if (_capturedDevice is not null && ReferenceEquals(_capturedDevice, e.TouchDevice))
        {
            e.Handled = true;
            UpdateTargetPreview(source, sample.X, sample.Y, update.VelocityDipPerSecond, options);
        }
    }

    private void OnPreviewTouchUp(object? sender, TouchEventArgs e)
    {
        _activeTouchIds.Remove(e.TouchDevice.Id);

        if (_disposed || _source is not DesktopIconFlickSource source ||
            _recognizer.ActiveTouchId != e.TouchDevice.Id)
        {
            return;
        }

        DesktopIconFlickOptions options = _getOptions().Normalize();
        if ((_getGlobalActiveTouchCount?.Invoke() ?? 0) > 0)
        {
            CancelCurrent(DesktopIconFlickCancelReason.MultiTouch);
            return;
        }
        TouchPoint point = e.GetTouchPoint(_surface);
        DesktopIconFlickSample sample = ToSample(e.TouchDevice.Id, point.Position, Environment.TickCount64);
        bool wasCaptured = ReferenceEquals(_capturedDevice, e.TouchDevice);
        DesktopIconFlickUpdate update = _recognizer.End(sample, options);

        if (wasCaptured)
        {
            e.Handled = true;
        }

        if (wasCaptured && update.Kind == DesktopIconFlickUpdateKind.Committed)
        {
            UpdateTargetPreview(source, sample.X, sample.Y, update.VelocityDipPerSecond, options);
            if (_currentTarget is DesktopIconFlickTarget target && _canMove(source, target))
            {
                try
                {
                    _commitMove(source, target);
                }
                finally
                {
                    ClearGesture();
                }
                return;
            }
        }

        ClearGesture();
    }

    private void UpdateTargetPreview(
        DesktopIconFlickSource source,
        double releaseX,
        double releaseY,
        DesktopIconFlickVector velocity,
        DesktopIconFlickOptions options)
    {
        IReadOnlyList<DesktopIconFlickTarget> targets = _getTargets(source) ?? Array.Empty<DesktopIconFlickTarget>();
        if (DesktopIconFlickTargetPlanner.TrySelectTarget(
            _startX,
            _startY,
            releaseX,
            releaseY,
            velocity,
            targets,
            options,
            out DesktopIconFlickTargetHit hit))
        {
            if (_currentTarget is not DesktopIconFlickTarget current || current.TargetStableId != hit.Target.TargetStableId)
            {
                _currentTarget = hit.Target;
                _previewTarget(hit.Target);
            }
            return;
        }

        if (_currentTarget is not null)
        {
            _currentTarget = null;
            _previewTarget(null);
        }
    }


    private void OnTouchLeave(object? sender, TouchEventArgs e)
    {
        if (_disposed || _capturedDevice is not null)
        {
            return;
        }

        _activeTouchIds.Remove(e.TouchDevice.Id);
        if (_recognizer.ActiveTouchId == e.TouchDevice.Id)
        {
            CancelCurrent(DesktopIconFlickCancelReason.LeftSurface);
        }
    }

    private void OnLostTouchCapture(object? sender, TouchEventArgs e)
    {
        if (_releasingCapture || _capturedDevice is null || e.TouchDevice.Id != _capturedDevice.Id)
        {
            return;
        }

        CancelCurrent(DesktopIconFlickCancelReason.LostCapture);
    }

    private void OnSurfaceUnloaded(object sender, RoutedEventArgs e) => Dispose();

    private void CancelCurrent(DesktopIconFlickCancelReason reason)
    {
        _recognizer.Cancel(reason);
        ClearGesture();
    }

    private void ClearGesture()
    {
        _previewTarget(null);
        ReleaseCapturedTouch();
        _recognizer.Reset();
        _source = null;
        _currentTarget = null;
        _startX = 0;
        _startY = 0;
    }

    private void ReleaseCapturedTouch()
    {
        TouchDevice? device = _capturedDevice;
        _capturedDevice = null;
        if (device is null || !ReferenceEquals(device.Captured, _surface))
        {
            return;
        }

        _releasingCapture = true;
        try
        {
            _surface.ReleaseTouchCapture(device);
        }
        finally
        {
            _releasingCapture = false;
        }
    }

    private DesktopIconFlickSurface CurrentSurface() =>
        new(_surface.RenderSize.Width, _surface.RenderSize.Height);

    private static DesktopIconFlickSample ToSample(int touchId, Point point, long timestamp) =>
        new(touchId, point.X, point.Y, timestamp);

    internal void CancelExternal(DesktopIconFlickCancelReason reason = DesktopIconFlickCancelReason.BlockedSource)
    {
        if (_disposed) return;
        CancelCurrent(reason);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _surface.RemoveHandler(UIElement.PreviewTouchDownEvent, _previewTouchDownHandler);
        _surface.RemoveHandler(UIElement.PreviewTouchMoveEvent, _previewTouchMoveHandler);
        _surface.RemoveHandler(UIElement.PreviewTouchUpEvent, _previewTouchUpHandler);
        _surface.TouchLeave -= _touchLeaveHandler;
        _surface.LostTouchCapture -= OnLostTouchCapture;
        _surface.RemoveHandler(FrameworkElement.UnloadedEvent, _unloadedHandler);
        _activeTouchIds.Clear();
        _recognizer.Cancel(DesktopIconFlickCancelReason.Disposed);
        ClearGesture();
    }
}

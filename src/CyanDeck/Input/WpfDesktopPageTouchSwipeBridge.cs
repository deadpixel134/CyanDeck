using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace CyanDeck.Input;

/// <summary>
/// WPF adapter for Desktop Pages touch gestures.
/// Two-finger candidates are not captured on the first touch. Capture begins only after
/// a second unblocked touch joins within the configured window, minimizing interference
/// with ordinary single-touch selection and scrolling. The v6.8 one-finger edge path is
/// retained as an optional compatibility gesture.
/// </summary>
internal sealed class WpfDesktopPageTouchSwipeBridge : IDisposable
{
    private readonly UIElement _surface;
    private readonly DesktopPageTouchSwipeRecognizer _recognizer;
    private readonly Func<DesktopPageTouchSwipeOptions> _getOptions;
    private readonly Func<object?, bool> _isBlockedSource;
    private readonly Func<DesktopPageTouchSwipeDirection, bool> _canNavigate;
    private readonly Action<DesktopPageTouchSwipeDirection, double> _preview;
    private readonly Action _cancelPreview;
    private readonly Action<DesktopPageTouchSwipeDirection> _commit;

    private PendingTouch? _pendingFirst;
    private readonly HashSet<int> _activeSurfaceTouchIds = new();
    private TouchDevice? _primaryDevice;
    private TouchDevice? _secondaryDevice;
    private long _primaryDownTimestamp;
    private long _secondaryDownTimestamp;
    private readonly RoutedEventHandler _unloadedHandler;
    private bool _disposed;
    private bool _releasingCapture;

    internal WpfDesktopPageTouchSwipeBridge(
        UIElement surface,
        Func<DesktopPageTouchSwipeOptions> getOptions,
        Func<object?, bool> isBlockedSource,
        Func<DesktopPageTouchSwipeDirection, bool> canNavigate,
        Action<DesktopPageTouchSwipeDirection, double> preview,
        Action cancelPreview,
        Action<DesktopPageTouchSwipeDirection> commit)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _getOptions = getOptions ?? throw new ArgumentNullException(nameof(getOptions));
        _isBlockedSource = isBlockedSource ?? throw new ArgumentNullException(nameof(isBlockedSource));
        _canNavigate = canNavigate ?? throw new ArgumentNullException(nameof(canNavigate));
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        _cancelPreview = cancelPreview ?? throw new ArgumentNullException(nameof(cancelPreview));
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        _recognizer = new DesktopPageTouchSwipeRecognizer();
        _unloadedHandler = OnSurfaceUnloaded;

        _surface.PreviewTouchDown += OnPreviewTouchDown;
        _surface.PreviewTouchMove += OnPreviewTouchMove;
        _surface.PreviewTouchUp += OnPreviewTouchUp;
        _surface.LostTouchCapture += OnLostTouchCapture;
        _surface.AddHandler(FrameworkElement.UnloadedEvent, _unloadedHandler);
    }

    private void OnPreviewTouchDown(object? sender, TouchEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        DesktopPageTouchSwipeOptions options = _getOptions().Normalize();
        if (!options.Enabled)
        {
            ClearPendingFirst();
            return;
        }

        long now = Environment.TickCount64;
        _activeSurfaceTouchIds.Add(e.TouchDevice.Id);

        if (_recognizer.Mode == DesktopPageTouchSwipeMode.TwoFinger)
        {
            if (!_recognizer.ContainsTouch(e.TouchDevice.Id))
            {
                e.Handled = true;
                ApplyUpdate(_recognizer.Cancel(DesktopPageTouchSwipeCancelReason.MultiTouch));
                ReleaseCapturedTouches();
            }
            return;
        }

        if (_recognizer.Mode == DesktopPageTouchSwipeMode.OneFingerEdge)
        {
            TryPromoteOneFingerToTwoFinger(e, options, now);
            return;
        }

        if (_isBlockedSource(e.OriginalSource))
        {
            ClearPendingFirst();
            return;
        }

        if (_pendingFirst is PendingTouch pending)
        {
            if (e.TouchDevice.Id == pending.Device.Id)
            {
                return;
            }

            long joinDelay = AbsoluteDifference(pending.DownTimestampMilliseconds, now);
            if (options.TwoFingerEnabled && _activeSurfaceTouchIds.Count == 2 && joinDelay <= options.TwoFingerJoinWindowMilliseconds)
            {
                if (TryStartTwoFinger(pending.Device, pending.DownTimestampMilliseconds, e.TouchDevice, now, options))
                {
                    ClearPendingFirst();
                    e.Handled = true;
                    return;
                }
            }

            ClearPendingFirst();
            if (_activeSurfaceTouchIds.Count > 1)
            {
                return;
            }
        }

        if (_activeSurfaceTouchIds.Count > 1)
        {
            return;
        }

        TouchPoint point = e.GetTouchPoint(_surface);
        DesktopPageTouchSwipeUpdate oneFinger = _recognizer.Begin(
            ToSample(e.TouchDevice.Id, point.Position, now),
            CurrentSurface(),
            options);

        if (oneFinger.Kind == DesktopPageTouchSwipeUpdateKind.Started)
        {
            if (!CapturePrimary(e.TouchDevice, now))
            {
                ApplyUpdate(_recognizer.Cancel(DesktopPageTouchSwipeCancelReason.LostCapture));
                return;
            }

            e.Handled = true;
            ApplyUpdate(oneFinger);
            return;
        }

        if (options.TwoFingerEnabled)
        {
            _pendingFirst = new PendingTouch(e.TouchDevice, now);
        }
    }

    private void TryPromoteOneFingerToTwoFinger(
        TouchEventArgs e,
        DesktopPageTouchSwipeOptions options,
        long secondDownTimestamp)
    {
        if (!options.TwoFingerEnabled || _primaryDevice is null || e.TouchDevice.Id == _primaryDevice.Id)
        {
            return;
        }

        if (_isBlockedSource(e.OriginalSource))
        {
            e.Handled = true;
            ApplyUpdate(_recognizer.Cancel(DesktopPageTouchSwipeCancelReason.BlockedSource));
            ReleaseCapturedTouches();
            return;
        }

        long joinDelay = AbsoluteDifference(_primaryDownTimestamp, secondDownTimestamp);
        if (joinDelay > options.TwoFingerJoinWindowMilliseconds)
        {
            e.Handled = true;
            ApplyUpdate(_recognizer.Cancel(DesktopPageTouchSwipeCancelReason.PairJoinTimeout));
            ReleaseCapturedTouches();
            return;
        }

        _cancelPreview();
        if (TryStartTwoFinger(_primaryDevice, _primaryDownTimestamp, e.TouchDevice, secondDownTimestamp, options, primaryAlreadyCaptured: true))
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        ApplyUpdate(_recognizer.Cancel(DesktopPageTouchSwipeCancelReason.LostCapture));
        ReleaseCapturedTouches();
    }

    private bool TryStartTwoFinger(
        TouchDevice first,
        long firstDownTimestamp,
        TouchDevice second,
        long secondDownTimestamp,
        DesktopPageTouchSwipeOptions options,
        bool primaryAlreadyCaptured = false)
    {
        TouchPoint firstPoint;
        TouchPoint secondPoint;
        try
        {
            firstPoint = first.GetTouchPoint(_surface);
            secondPoint = second.GetTouchPoint(_surface);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        DesktopPageTouchSwipeUpdate update = _recognizer.BeginTwoFinger(
            ToSample(first.Id, firstPoint.Position, secondDownTimestamp),
            firstDownTimestamp,
            ToSample(second.Id, secondPoint.Position, secondDownTimestamp),
            secondDownTimestamp,
            CurrentSurface(),
            options);

        if (update.Kind != DesktopPageTouchSwipeUpdateKind.Started)
        {
            ApplyUpdate(update);
            return false;
        }

        if (!primaryAlreadyCaptured && !first.Capture(_surface, CaptureMode.Element))
        {
            _recognizer.Cancel(DesktopPageTouchSwipeCancelReason.LostCapture);
            return false;
        }

        if (!second.Capture(_surface, CaptureMode.Element))
        {
            if (!primaryAlreadyCaptured && ReferenceEquals(first.Captured, _surface))
            {
                _surface.ReleaseTouchCapture(first);
            }
            _recognizer.Cancel(DesktopPageTouchSwipeCancelReason.LostCapture);
            return false;
        }

        _primaryDevice = first;
        _secondaryDevice = second;
        _primaryDownTimestamp = firstDownTimestamp;
        _secondaryDownTimestamp = secondDownTimestamp;
        ApplyUpdate(update);
        return true;
    }

    private bool CapturePrimary(TouchDevice device, long downTimestamp)
    {
        if (!device.Capture(_surface, CaptureMode.Element))
        {
            return false;
        }

        _primaryDevice = device;
        _secondaryDevice = null;
        _primaryDownTimestamp = downTimestamp;
        _secondaryDownTimestamp = 0;
        return true;
    }

    private void OnPreviewTouchMove(object? sender, TouchEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_recognizer.Mode == DesktopPageTouchSwipeMode.TwoFinger)
        {
            if (!_recognizer.ContainsTouch(e.TouchDevice.Id) || !TryGetPairSamples(out var first, out var second))
            {
                return;
            }

            DesktopPageTouchSwipeUpdate update = _recognizer.MoveTwoFinger(first, second, _getOptions());
            if (update.Kind != DesktopPageTouchSwipeUpdateKind.None)
            {
                e.Handled = true;
                ApplyUpdate(update);
            }

            if (update.Kind == DesktopPageTouchSwipeUpdateKind.Cancelled)
            {
                ReleaseCapturedTouches();
            }
            return;
        }

        if (_recognizer.Mode == DesktopPageTouchSwipeMode.OneFingerEdge && _primaryDevice?.Id == e.TouchDevice.Id)
        {
            TouchPoint point = e.GetTouchPoint(_surface);
            DesktopPageTouchSwipeUpdate update = _recognizer.Move(
                ToSample(e.TouchDevice.Id, point.Position, Environment.TickCount64),
                _getOptions());

            if (update.Kind != DesktopPageTouchSwipeUpdateKind.None)
            {
                e.Handled = true;
                ApplyUpdate(update);
            }

            if (update.Kind == DesktopPageTouchSwipeUpdateKind.Cancelled)
            {
                ReleaseCapturedTouches();
            }
        }
    }

    private void OnPreviewTouchUp(object? sender, TouchEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _activeSurfaceTouchIds.Remove(e.TouchDevice.Id);

        if (_pendingFirst is PendingTouch pending && pending.Device.Id == e.TouchDevice.Id)
        {
            ClearPendingFirst();
        }

        if (_recognizer.Mode == DesktopPageTouchSwipeMode.TwoFinger && _recognizer.ContainsTouch(e.TouchDevice.Id))
        {
            if (!TryGetPairSamplesForUp(e, out var first, out var second))
            {
                e.Handled = true;
                ApplyUpdate(_recognizer.Cancel(DesktopPageTouchSwipeCancelReason.LostCapture));
                ReleaseCapturedTouches();
                return;
            }

            DesktopPageTouchSwipeUpdate update = _recognizer.EndTwoFinger(first, second, _getOptions(), _canNavigate);
            e.Handled = true;
            ReleaseCapturedTouches();
            ApplyUpdate(update);
            return;
        }

        if (_recognizer.Mode == DesktopPageTouchSwipeMode.OneFingerEdge && _primaryDevice?.Id == e.TouchDevice.Id)
        {
            TouchPoint point = e.GetTouchPoint(_surface);
            DesktopPageTouchSwipeUpdate update = _recognizer.End(
                ToSample(e.TouchDevice.Id, point.Position, Environment.TickCount64),
                _getOptions(),
                _canNavigate);

            e.Handled = true;
            ReleaseCapturedTouches();
            ApplyUpdate(update);
        }
    }

    private bool TryGetPairSamples(out DesktopPageTouchSwipeSample first, out DesktopPageTouchSwipeSample second)
    {
        first = default;
        second = default;
        if (_primaryDevice is null || _secondaryDevice is null)
        {
            return false;
        }

        try
        {
            long now = Environment.TickCount64;
            TouchPoint firstPoint = _primaryDevice.GetTouchPoint(_surface);
            TouchPoint secondPoint = _secondaryDevice.GetTouchPoint(_surface);
            first = ToSample(_primaryDevice.Id, firstPoint.Position, now);
            second = ToSample(_secondaryDevice.Id, secondPoint.Position, now);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryGetPairSamplesForUp(
        TouchEventArgs upEvent,
        out DesktopPageTouchSwipeSample first,
        out DesktopPageTouchSwipeSample second)
    {
        first = default;
        second = default;
        if (_primaryDevice is null || _secondaryDevice is null)
        {
            return false;
        }

        long now = Environment.TickCount64;
        try
        {
            TouchPoint upPoint = upEvent.GetTouchPoint(_surface);
            TouchDevice other = upEvent.TouchDevice.Id == _primaryDevice.Id ? _secondaryDevice : _primaryDevice;
            TouchPoint otherPoint = other.GetTouchPoint(_surface);

            DesktopPageTouchSwipeSample upSample = ToSample(upEvent.TouchDevice.Id, upPoint.Position, now);
            DesktopPageTouchSwipeSample otherSample = ToSample(other.Id, otherPoint.Position, now);

            if (upEvent.TouchDevice.Id == _primaryDevice.Id)
            {
                first = upSample;
                second = otherSample;
            }
            else
            {
                first = otherSample;
                second = upSample;
            }
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void OnLostTouchCapture(object? sender, TouchEventArgs e)
    {
        if (_disposed || _releasingCapture || !_recognizer.ContainsTouch(e.TouchDevice.Id))
        {
            return;
        }

        ApplyUpdate(_recognizer.Cancel(DesktopPageTouchSwipeCancelReason.LostCapture));
        ReleaseCapturedTouches();
    }

    private void OnSurfaceUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        ClearPendingFirst();
        _activeSurfaceTouchIds.Clear();
        ApplyUpdate(_recognizer.Cancel(DesktopPageTouchSwipeCancelReason.Disposed));
        ReleaseCapturedTouches();
    }

    private DesktopPageTouchSurface CurrentSurface() =>
        new(_surface.RenderSize.Width, _surface.RenderSize.Height);

    private static DesktopPageTouchSwipeSample ToSample(int touchId, Point position, long timestamp) =>
        new(touchId, position.X, position.Y, timestamp);

    private void ApplyUpdate(DesktopPageTouchSwipeUpdate update)
    {
        switch (update.Kind)
        {
            case DesktopPageTouchSwipeUpdateKind.Started:
                if (update.Direction is DesktopPageTouchSwipeDirection startedDirection)
                {
                    _preview(startedDirection, 0);
                }
                break;

            case DesktopPageTouchSwipeUpdateKind.Preview:
                if (update.Direction is DesktopPageTouchSwipeDirection previewDirection)
                {
                    _preview(previewDirection, update.Progress);
                }
                break;

            case DesktopPageTouchSwipeUpdateKind.Committed:
                if (update.Direction is DesktopPageTouchSwipeDirection commitDirection)
                {
                    _commit(commitDirection);
                }
                break;

            case DesktopPageTouchSwipeUpdateKind.Cancelled:
                _cancelPreview();
                break;
        }
    }

    private void ReleaseCapturedTouches()
    {
        if (_releasingCapture)
        {
            return;
        }

        _releasingCapture = true;
        try
        {
            ReleaseDevice(_secondaryDevice);
            ReleaseDevice(_primaryDevice);
        }
        finally
        {
            _secondaryDevice = null;
            _primaryDevice = null;
            _primaryDownTimestamp = 0;
            _secondaryDownTimestamp = 0;
            _releasingCapture = false;
        }
    }

    private void ReleaseDevice(TouchDevice? device)
    {
        if (device is not null && ReferenceEquals(device.Captured, _surface))
        {
            _surface.ReleaseTouchCapture(device);
        }
    }

    private void ClearPendingFirst() => _pendingFirst = null;

    private static long AbsoluteDifference(long first, long second) =>
        first >= second ? first - second : second - first;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _surface.PreviewTouchDown -= OnPreviewTouchDown;
        _surface.PreviewTouchMove -= OnPreviewTouchMove;
        _surface.PreviewTouchUp -= OnPreviewTouchUp;
        _surface.LostTouchCapture -= OnLostTouchCapture;
        _surface.RemoveHandler(FrameworkElement.UnloadedEvent, _unloadedHandler);

        ClearPendingFirst();
        _activeSurfaceTouchIds.Clear();
        _recognizer.Cancel(DesktopPageTouchSwipeCancelReason.Disposed);
        ReleaseCapturedTouches();
        _cancelPreview();
    }

    private readonly record struct PendingTouch(TouchDevice Device, long DownTimestampMilliseconds);
}

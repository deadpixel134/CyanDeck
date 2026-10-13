using System;

namespace CyanDeck.Input;

/// <summary>
/// Framework-neutral recognizer for Desktop Pages touch navigation.
/// Supports the documented two-finger horizontal desktop drag plus the v6.8
/// one-finger inward edge gesture as an optional compatibility path.
/// Performs no input hooking, timers, threading, shell access, or I/O.
/// </summary>
internal sealed class DesktopPageTouchSwipeRecognizer
{
    private OneFingerGesture? _oneFinger;
    private TwoFingerGesture? _twoFinger;

    internal bool IsTracking => _oneFinger is not null || _twoFinger is not null;
    internal DesktopPageTouchSwipeMode Mode => _twoFinger is not null
        ? DesktopPageTouchSwipeMode.TwoFinger
        : _oneFinger is not null
            ? DesktopPageTouchSwipeMode.OneFingerEdge
            : DesktopPageTouchSwipeMode.None;

    internal int? ActiveTouchId => _oneFinger?.TouchId;

    internal bool ContainsTouch(int touchId) =>
        (_oneFinger is OneFingerGesture one && one.TouchId == touchId) ||
        (_twoFinger is TwoFingerGesture two && (two.FirstTouchId == touchId || two.SecondTouchId == touchId));

    internal DesktopPageTouchSwipeUpdate Begin(
        DesktopPageTouchSwipeSample sample,
        DesktopPageTouchSurface surface,
        DesktopPageTouchSwipeOptions options)
    {
        options = options.Normalize();
        if (!options.Enabled || !options.OneFingerEdgeEnabled)
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(null, 0, DesktopPageTouchSwipeCancelReason.Disabled);
        }

        if (!surface.IsValid || !IsFiniteSample(sample))
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(null, 0, DesktopPageTouchSwipeCancelReason.InvalidSurface);
        }

        if (IsTracking)
        {
            return Cancel(DesktopPageTouchSwipeCancelReason.MultiTouch);
        }

        if (!surface.Contains(sample))
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(null, 0, DesktopPageTouchSwipeCancelReason.OutsideEdge);
        }

        double edgeWidth = Math.Min(options.EdgeWidthDip, surface.Width / 2.0);
        bool atLeft = sample.X <= edgeWidth;
        bool atRight = sample.X >= surface.Width - edgeWidth;
        if (!atLeft && !atRight)
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(null, 0, DesktopPageTouchSwipeCancelReason.OutsideEdge);
        }

        DesktopPageTouchSwipeDirection direction;
        if (atLeft && atRight)
        {
            direction = sample.X <= surface.Width / 2.0
                ? DesktopPageTouchSwipeDirection.Previous
                : DesktopPageTouchSwipeDirection.Next;
        }
        else
        {
            direction = atLeft
                ? DesktopPageTouchSwipeDirection.Previous
                : DesktopPageTouchSwipeDirection.Next;
        }

        _oneFinger = new OneFingerGesture(
            sample.TouchId,
            sample.X,
            sample.Y,
            sample.TimestampMilliseconds,
            direction);

        return new DesktopPageTouchSwipeUpdate(
            DesktopPageTouchSwipeUpdateKind.Started,
            direction,
            0,
            0,
            DesktopPageTouchSwipeCancelReason.None);
    }

    /// <summary>
    /// Starts a two-finger drag from the current positions of both touches. The down timestamps
    /// are supplied separately so the join window is measured from physical contact, not from
    /// when the second touch caused promotion from the one-finger compatibility gesture.
    /// </summary>
    internal DesktopPageTouchSwipeUpdate BeginTwoFinger(
        DesktopPageTouchSwipeSample first,
        long firstDownTimestampMilliseconds,
        DesktopPageTouchSwipeSample second,
        long secondDownTimestampMilliseconds,
        DesktopPageTouchSurface surface,
        DesktopPageTouchSwipeOptions options)
    {
        options = options.Normalize();
        if (!options.Enabled || !options.TwoFingerEnabled)
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(null, 0, DesktopPageTouchSwipeCancelReason.Disabled);
        }

        if (!surface.IsValid || !IsFiniteSample(first) || !IsFiniteSample(second) || first.TouchId == second.TouchId)
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(null, 0, DesktopPageTouchSwipeCancelReason.InvalidSurface);
        }

        if (!surface.Contains(first) || !surface.Contains(second))
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(null, 0, DesktopPageTouchSwipeCancelReason.InvalidSurface);
        }

        long joinDelay = AbsoluteDifference(firstDownTimestampMilliseconds, secondDownTimestampMilliseconds);
        if (joinDelay > options.TwoFingerJoinWindowMilliseconds)
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(null, 0, DesktopPageTouchSwipeCancelReason.PairJoinTimeout);
        }

        if (_twoFinger is not null)
        {
            return Cancel(DesktopPageTouchSwipeCancelReason.MultiTouch);
        }

        if (_oneFinger is OneFingerGesture one && one.TouchId != first.TouchId && one.TouchId != second.TouchId)
        {
            return Cancel(DesktopPageTouchSwipeCancelReason.MultiTouch);
        }

        _oneFinger = null;

        double centroidX = (first.X + second.X) / 2.0;
        double centroidY = (first.Y + second.Y) / 2.0;
        double separation = Distance(first.X, first.Y, second.X, second.Y);
        long startTimestamp = Math.Max(firstDownTimestampMilliseconds, secondDownTimestampMilliseconds);

        _twoFinger = new TwoFingerGesture(
            first.TouchId,
            second.TouchId,
            first.X,
            first.Y,
            second.X,
            second.Y,
            centroidX,
            centroidY,
            separation,
            startTimestamp,
            null);

        return new DesktopPageTouchSwipeUpdate(
            DesktopPageTouchSwipeUpdateKind.Started,
            null,
            0,
            0,
            DesktopPageTouchSwipeCancelReason.None);
    }

    internal DesktopPageTouchSwipeUpdate Move(
        DesktopPageTouchSwipeSample sample,
        DesktopPageTouchSwipeOptions options)
    {
        if (_oneFinger is not OneFingerGesture active || active.TouchId != sample.TouchId)
        {
            return DesktopPageTouchSwipeUpdate.None;
        }

        options = options.Normalize();
        if (!options.Enabled || !options.OneFingerEdgeEnabled)
        {
            return CancelOneFinger(sample.X, DesktopPageTouchSwipeCancelReason.Disabled);
        }

        if (!IsFiniteSample(sample))
        {
            return CancelOneFinger(sample.X, DesktopPageTouchSwipeCancelReason.InvalidSurface);
        }

        long elapsed = ElapsedMilliseconds(active.StartTimestampMilliseconds, sample.TimestampMilliseconds);
        if (elapsed > options.MaximumGestureDurationMilliseconds)
        {
            return CancelOneFinger(sample.X, DesktopPageTouchSwipeCancelReason.Timeout);
        }

        double inwardTravel = GetInwardTravel(active, sample.X);
        double verticalTravel = Math.Abs(sample.Y - active.StartY);

        if (inwardTravel < -options.WrongDirectionToleranceDip)
        {
            return CancelOneFinger(sample.X, DesktopPageTouchSwipeCancelReason.WrongDirection);
        }

        if (verticalTravel > options.MaximumVerticalDriftDip && verticalTravel > Math.Abs(inwardTravel))
        {
            return CancelOneFinger(sample.X, DesktopPageTouchSwipeCancelReason.VerticalMotion);
        }

        double progress = Math.Clamp(inwardTravel / options.MinimumCommitDistanceDip, 0.0, 1.0);
        return new DesktopPageTouchSwipeUpdate(
            DesktopPageTouchSwipeUpdateKind.Preview,
            active.Direction,
            progress,
            inwardTravel,
            DesktopPageTouchSwipeCancelReason.None);
    }

    internal DesktopPageTouchSwipeUpdate MoveTwoFinger(
        DesktopPageTouchSwipeSample first,
        DesktopPageTouchSwipeSample second,
        DesktopPageTouchSwipeOptions options)
    {
        if (_twoFinger is not TwoFingerGesture active || !MatchesPair(active, first, second))
        {
            return DesktopPageTouchSwipeUpdate.None;
        }

        options = options.Normalize();
        if (!options.Enabled || !options.TwoFingerEnabled)
        {
            return Cancel(DesktopPageTouchSwipeCancelReason.Disabled);
        }

        TwoFingerMetrics metrics = Measure(active, first, second, options);
        if (metrics.CancelReason != DesktopPageTouchSwipeCancelReason.None)
        {
            return CancelTwoFinger(metrics.SignedHorizontalTravel, metrics.CancelReason);
        }

        DesktopPageTouchSwipeDirection? direction = active.LockedDirection;
        if (direction is null && Math.Abs(metrics.CentroidDeltaX) >= options.DirectionLockDistanceDip)
        {
            direction = metrics.CentroidDeltaX > 0
                ? DesktopPageTouchSwipeDirection.Previous
                : DesktopPageTouchSwipeDirection.Next;
            _twoFinger = active with { LockedDirection = direction };
            active = _twoFinger.Value;
            metrics = Measure(active, first, second, options);
        }

        if (direction is null)
        {
            return new DesktopPageTouchSwipeUpdate(
                DesktopPageTouchSwipeUpdateKind.Preview,
                null,
                0,
                Math.Abs(metrics.CentroidDeltaX),
                DesktopPageTouchSwipeCancelReason.None);
        }

        if (metrics.SignedHorizontalTravel < -options.WrongDirectionToleranceDip)
        {
            return CancelTwoFinger(metrics.SignedHorizontalTravel, DesktopPageTouchSwipeCancelReason.WrongDirection);
        }

        double progress = Math.Clamp(metrics.SignedHorizontalTravel / options.MinimumCommitDistanceDip, 0.0, 1.0);
        return new DesktopPageTouchSwipeUpdate(
            DesktopPageTouchSwipeUpdateKind.Preview,
            direction,
            progress,
            metrics.SignedHorizontalTravel,
            DesktopPageTouchSwipeCancelReason.None);
    }

    internal DesktopPageTouchSwipeUpdate End(
        DesktopPageTouchSwipeSample sample,
        DesktopPageTouchSwipeOptions options,
        Func<DesktopPageTouchSwipeDirection, bool>? canNavigate = null)
    {
        if (_oneFinger is not OneFingerGesture active || active.TouchId != sample.TouchId)
        {
            return DesktopPageTouchSwipeUpdate.None;
        }

        options = options.Normalize();
        _oneFinger = null;

        if (!options.Enabled || !options.OneFingerEdgeEnabled)
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(active.Direction, GetInwardTravel(active, sample.X), DesktopPageTouchSwipeCancelReason.Disabled);
        }

        if (!IsFiniteSample(sample))
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(active.Direction, 0, DesktopPageTouchSwipeCancelReason.InvalidSurface);
        }

        long elapsed = ElapsedMilliseconds(active.StartTimestampMilliseconds, sample.TimestampMilliseconds);
        double inwardTravel = GetInwardTravel(active, sample.X);
        double verticalTravel = Math.Abs(sample.Y - active.StartY);

        if (elapsed > options.MaximumGestureDurationMilliseconds)
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(active.Direction, inwardTravel, DesktopPageTouchSwipeCancelReason.Timeout);
        }
        if (inwardTravel < -options.WrongDirectionToleranceDip)
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(active.Direction, inwardTravel, DesktopPageTouchSwipeCancelReason.WrongDirection);
        }
        if (verticalTravel > options.MaximumVerticalDriftDip)
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(active.Direction, inwardTravel, DesktopPageTouchSwipeCancelReason.VerticalMotion);
        }
        if (inwardTravel < options.MinimumCommitDistanceDip)
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(active.Direction, inwardTravel, DesktopPageTouchSwipeCancelReason.InsufficientDistance);
        }
        if (inwardTravel < verticalTravel * options.HorizontalDominanceRatio)
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(active.Direction, inwardTravel, DesktopPageTouchSwipeCancelReason.InsufficientHorizontalDominance);
        }
        if (canNavigate is not null && !canNavigate(active.Direction))
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(active.Direction, inwardTravel, DesktopPageTouchSwipeCancelReason.PageUnavailable);
        }

        return new DesktopPageTouchSwipeUpdate(
            DesktopPageTouchSwipeUpdateKind.Committed,
            active.Direction,
            1.0,
            inwardTravel,
            DesktopPageTouchSwipeCancelReason.None);
    }

    internal DesktopPageTouchSwipeUpdate EndTwoFinger(
        DesktopPageTouchSwipeSample first,
        DesktopPageTouchSwipeSample second,
        DesktopPageTouchSwipeOptions options,
        Func<DesktopPageTouchSwipeDirection, bool>? canNavigate = null)
    {
        if (_twoFinger is not TwoFingerGesture active || !MatchesPair(active, first, second))
        {
            return DesktopPageTouchSwipeUpdate.None;
        }

        options = options.Normalize();
        TwoFingerMetrics metrics = Measure(active, first, second, options);
        _twoFinger = null;

        if (!options.Enabled || !options.TwoFingerEnabled)
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(active.LockedDirection, metrics.SignedHorizontalTravel, DesktopPageTouchSwipeCancelReason.Disabled);
        }
        if (metrics.CancelReason != DesktopPageTouchSwipeCancelReason.None)
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(active.LockedDirection, metrics.SignedHorizontalTravel, metrics.CancelReason);
        }

        DesktopPageTouchSwipeDirection? direction = active.LockedDirection;
        if (direction is null && Math.Abs(metrics.CentroidDeltaX) >= options.DirectionLockDistanceDip)
        {
            direction = metrics.CentroidDeltaX > 0
                ? DesktopPageTouchSwipeDirection.Previous
                : DesktopPageTouchSwipeDirection.Next;
            active = active with { LockedDirection = direction };
            metrics = Measure(active, first, second, options);
        }

        if (direction is null)
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(null, Math.Abs(metrics.CentroidDeltaX), DesktopPageTouchSwipeCancelReason.InsufficientDistance);
        }
        if (metrics.SignedHorizontalTravel < -options.WrongDirectionToleranceDip)
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(direction, metrics.SignedHorizontalTravel, DesktopPageTouchSwipeCancelReason.WrongDirection);
        }
        if (metrics.CentroidVerticalTravel > options.MaximumVerticalDriftDip)
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(direction, metrics.SignedHorizontalTravel, DesktopPageTouchSwipeCancelReason.VerticalMotion);
        }
        if (metrics.SignedHorizontalTravel < options.MinimumCommitDistanceDip)
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(direction, metrics.SignedHorizontalTravel, DesktopPageTouchSwipeCancelReason.InsufficientDistance);
        }
        if (metrics.SignedHorizontalTravel < metrics.CentroidVerticalTravel * options.HorizontalDominanceRatio)
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(direction, metrics.SignedHorizontalTravel, DesktopPageTouchSwipeCancelReason.InsufficientHorizontalDominance);
        }
        if (canNavigate is not null && !canNavigate(direction.Value))
        {
            return DesktopPageTouchSwipeUpdate.Cancelled(direction, metrics.SignedHorizontalTravel, DesktopPageTouchSwipeCancelReason.PageUnavailable);
        }

        return new DesktopPageTouchSwipeUpdate(
            DesktopPageTouchSwipeUpdateKind.Committed,
            direction,
            1.0,
            metrics.SignedHorizontalTravel,
            DesktopPageTouchSwipeCancelReason.None);
    }

    internal DesktopPageTouchSwipeUpdate Cancel(DesktopPageTouchSwipeCancelReason reason)
    {
        if (_twoFinger is TwoFingerGesture two)
        {
            _twoFinger = null;
            _oneFinger = null;
            return DesktopPageTouchSwipeUpdate.Cancelled(two.LockedDirection, 0, reason);
        }

        if (_oneFinger is OneFingerGesture one)
        {
            _oneFinger = null;
            return DesktopPageTouchSwipeUpdate.Cancelled(one.Direction, 0, reason);
        }

        return DesktopPageTouchSwipeUpdate.None;
    }

    private DesktopPageTouchSwipeUpdate CancelOneFinger(double currentX, DesktopPageTouchSwipeCancelReason reason)
    {
        OneFingerGesture active = _oneFinger!.Value;
        _oneFinger = null;
        return DesktopPageTouchSwipeUpdate.Cancelled(active.Direction, GetInwardTravel(active, currentX), reason);
    }

    private DesktopPageTouchSwipeUpdate CancelTwoFinger(double horizontalTravel, DesktopPageTouchSwipeCancelReason reason)
    {
        TwoFingerGesture active = _twoFinger!.Value;
        _twoFinger = null;
        return DesktopPageTouchSwipeUpdate.Cancelled(active.LockedDirection, horizontalTravel, reason);
    }

    private static TwoFingerMetrics Measure(
        TwoFingerGesture active,
        DesktopPageTouchSwipeSample first,
        DesktopPageTouchSwipeSample second,
        DesktopPageTouchSwipeOptions options)
    {
        if (!IsFiniteSample(first) || !IsFiniteSample(second))
        {
            return TwoFingerMetrics.Cancelled(DesktopPageTouchSwipeCancelReason.InvalidSurface);
        }

        long timestamp = Math.Max(first.TimestampMilliseconds, second.TimestampMilliseconds);
        long elapsed = ElapsedMilliseconds(active.StartTimestampMilliseconds, timestamp);
        if (elapsed > options.MaximumGestureDurationMilliseconds)
        {
            return TwoFingerMetrics.Cancelled(DesktopPageTouchSwipeCancelReason.Timeout);
        }

        DesktopPageTouchSwipeSample orderedFirst = first.TouchId == active.FirstTouchId ? first : second;
        DesktopPageTouchSwipeSample orderedSecond = first.TouchId == active.SecondTouchId ? first : second;

        double centroidX = (orderedFirst.X + orderedSecond.X) / 2.0;
        double centroidY = (orderedFirst.Y + orderedSecond.Y) / 2.0;
        double centroidDeltaX = centroidX - active.StartCentroidX;
        double centroidVerticalTravel = Math.Abs(centroidY - active.StartCentroidY);
        double separation = Distance(orderedFirst.X, orderedFirst.Y, orderedSecond.X, orderedSecond.Y);
        double separationChange = Math.Abs(separation - active.StartSeparation);

        if (separationChange > options.MaximumPairSeparationChangeDip)
        {
            return TwoFingerMetrics.Cancelled(DesktopPageTouchSwipeCancelReason.PairSeparationChanged);
        }

        double firstDx = orderedFirst.X - active.FirstStartX;
        double secondDx = orderedSecond.X - active.SecondStartX;
        if (MovesDivergently(firstDx, secondDx, options.ParallelMotionToleranceDip))
        {
            return TwoFingerMetrics.Cancelled(DesktopPageTouchSwipeCancelReason.DivergentTouches);
        }

        double signedTravel = active.LockedDirection switch
        {
            DesktopPageTouchSwipeDirection.Previous => centroidDeltaX,
            DesktopPageTouchSwipeDirection.Next => -centroidDeltaX,
            _ => Math.Abs(centroidDeltaX),
        };

        return new TwoFingerMetrics(
            centroidDeltaX,
            centroidVerticalTravel,
            signedTravel,
            DesktopPageTouchSwipeCancelReason.None);
    }

    private static bool MovesDivergently(double firstDx, double secondDx, double tolerance)
    {
        if (Math.Abs(firstDx) <= tolerance || Math.Abs(secondDx) <= tolerance)
        {
            return false;
        }

        return Math.Sign(firstDx) != Math.Sign(secondDx);
    }

    private static bool MatchesPair(
        TwoFingerGesture active,
        DesktopPageTouchSwipeSample first,
        DesktopPageTouchSwipeSample second) =>
        first.TouchId != second.TouchId &&
        ((first.TouchId == active.FirstTouchId && second.TouchId == active.SecondTouchId) ||
         (first.TouchId == active.SecondTouchId && second.TouchId == active.FirstTouchId));

    private static bool IsFiniteSample(DesktopPageTouchSwipeSample sample) =>
        double.IsFinite(sample.X) && double.IsFinite(sample.Y);

    private static double GetInwardTravel(OneFingerGesture active, double currentX)
    {
        double delta = currentX - active.StartX;
        return active.Direction == DesktopPageTouchSwipeDirection.Previous ? delta : -delta;
    }

    private static long ElapsedMilliseconds(long start, long current)
    {
        if (current < start)
        {
            return long.MaxValue;
        }
        return current - start;
    }

    private static long AbsoluteDifference(long first, long second)
    {
        if (first >= second)
        {
            return first - second;
        }
        return second - first;
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private readonly record struct OneFingerGesture(
        int TouchId,
        double StartX,
        double StartY,
        long StartTimestampMilliseconds,
        DesktopPageTouchSwipeDirection Direction);

    private readonly record struct TwoFingerGesture(
        int FirstTouchId,
        int SecondTouchId,
        double FirstStartX,
        double FirstStartY,
        double SecondStartX,
        double SecondStartY,
        double StartCentroidX,
        double StartCentroidY,
        double StartSeparation,
        long StartTimestampMilliseconds,
        DesktopPageTouchSwipeDirection? LockedDirection);

    private readonly record struct TwoFingerMetrics(
        double CentroidDeltaX,
        double CentroidVerticalTravel,
        double SignedHorizontalTravel,
        DesktopPageTouchSwipeCancelReason CancelReason)
    {
        internal static TwoFingerMetrics Cancelled(DesktopPageTouchSwipeCancelReason reason) =>
            new(0, 0, 0, reason);
    }
}

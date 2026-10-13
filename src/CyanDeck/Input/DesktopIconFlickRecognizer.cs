using System;
using System.Collections.Generic;

namespace CyanDeck.Input;

/// <summary>
/// Framework-neutral single-touch flick recognizer. It does not move items, enumerate groups,
/// persist layout, capture input, or perform I/O. The WPF bridge decides when to capture and the
/// host owns target eligibility plus the actual reference move.
/// </summary>
internal sealed class DesktopIconFlickRecognizer
{
    private const int MaximumRememberedSamples = 24;

    private Gesture? _gesture;

    internal bool IsTracking => _gesture is not null;
    internal bool IsArmed => _gesture?.Armed == true;
    internal int? ActiveTouchId => _gesture?.TouchId;

    internal DesktopIconFlickUpdate Begin(
        DesktopIconFlickSample sample,
        DesktopIconFlickSurface surface,
        DesktopIconFlickOptions options)
    {
        options = options.Normalize();
        Reset();

        if (!options.Enabled)
        {
            return DesktopIconFlickUpdate.Cancelled(0, default, DesktopIconFlickCancelReason.Disabled);
        }

        if (!sample.IsFinite || !surface.IsValid || !surface.Contains(sample))
        {
            return DesktopIconFlickUpdate.Cancelled(0, default, DesktopIconFlickCancelReason.InvalidSample);
        }

        var samples = new List<DesktopIconFlickSample>(MaximumRememberedSamples) { sample };
        _gesture = new Gesture(
            sample.TouchId,
            sample.X,
            sample.Y,
            sample.TimestampMilliseconds,
            false,
            samples);

        return new DesktopIconFlickUpdate(
            DesktopIconFlickUpdateKind.Tracking,
            0,
            default,
            DesktopIconFlickCancelReason.None);
    }

    internal DesktopIconFlickUpdate Move(
        DesktopIconFlickSample sample,
        DesktopIconFlickOptions options)
    {
        if (_gesture is not Gesture active || active.TouchId != sample.TouchId)
        {
            return DesktopIconFlickUpdate.None;
        }

        options = options.Normalize();
        if (!options.Enabled)
        {
            return Cancel(DesktopIconFlickCancelReason.Disabled);
        }

        if (!sample.IsFinite)
        {
            return Cancel(DesktopIconFlickCancelReason.InvalidSample);
        }

        long elapsed = ElapsedMilliseconds(active.StartTimestampMilliseconds, sample.TimestampMilliseconds);
        if (elapsed > options.MaximumGestureDurationMilliseconds)
        {
            return Cancel(DesktopIconFlickCancelReason.Timeout);
        }

        AppendSample(active.Samples, sample);
        double travel = Distance(active.StartX, active.StartY, sample.X, sample.Y);
        DesktopIconFlickVector velocity = EstimateVelocity(active.Samples, options.VelocityWindowMilliseconds);
        double speed = velocity.Magnitude;

        if (!active.Armed)
        {
            if (travel < options.ActivationDistanceDip || speed < options.MinimumVelocityDipPerSecond)
            {
                return new DesktopIconFlickUpdate(
                    DesktopIconFlickUpdateKind.Tracking,
                    travel,
                    velocity,
                    DesktopIconFlickCancelReason.None);
            }

            _gesture = active with { Armed = true };
            return new DesktopIconFlickUpdate(
                DesktopIconFlickUpdateKind.Armed,
                travel,
                velocity,
                DesktopIconFlickCancelReason.None);
        }

        return new DesktopIconFlickUpdate(
            DesktopIconFlickUpdateKind.Preview,
            travel,
            velocity,
            DesktopIconFlickCancelReason.None);
    }

    internal DesktopIconFlickUpdate End(
        DesktopIconFlickSample sample,
        DesktopIconFlickOptions options)
    {
        if (_gesture is not Gesture active || active.TouchId != sample.TouchId)
        {
            return DesktopIconFlickUpdate.None;
        }

        options = options.Normalize();
        _gesture = null;

        if (!options.Enabled)
        {
            return DesktopIconFlickUpdate.Cancelled(0, default, DesktopIconFlickCancelReason.Disabled);
        }

        if (!sample.IsFinite)
        {
            return DesktopIconFlickUpdate.Cancelled(0, default, DesktopIconFlickCancelReason.InvalidSample);
        }

        AppendSample(active.Samples, sample);
        long elapsed = ElapsedMilliseconds(active.StartTimestampMilliseconds, sample.TimestampMilliseconds);
        double travel = Distance(active.StartX, active.StartY, sample.X, sample.Y);
        DesktopIconFlickVector velocity = EstimateVelocity(active.Samples, options.VelocityWindowMilliseconds);

        if (!active.Armed)
        {
            return DesktopIconFlickUpdate.Cancelled(
                travel,
                velocity,
                travel < options.ActivationDistanceDip
                    ? DesktopIconFlickCancelReason.InsufficientTravel
                    : DesktopIconFlickCancelReason.InsufficientVelocity);
        }

        if (elapsed > options.MaximumGestureDurationMilliseconds)
        {
            return DesktopIconFlickUpdate.Cancelled(travel, velocity, DesktopIconFlickCancelReason.Timeout);
        }

        if (travel < options.MinimumCommitTravelDip)
        {
            return DesktopIconFlickUpdate.Cancelled(travel, velocity, DesktopIconFlickCancelReason.InsufficientTravel);
        }

        if (velocity.Magnitude < options.MinimumVelocityDipPerSecond)
        {
            return DesktopIconFlickUpdate.Cancelled(travel, velocity, DesktopIconFlickCancelReason.InsufficientVelocity);
        }

        return new DesktopIconFlickUpdate(
            DesktopIconFlickUpdateKind.Committed,
            travel,
            velocity,
            DesktopIconFlickCancelReason.None);
    }

    internal DesktopIconFlickUpdate Cancel(DesktopIconFlickCancelReason reason)
    {
        if (_gesture is not Gesture active)
        {
            return DesktopIconFlickUpdate.None;
        }

        DesktopIconFlickSample last = active.Samples.Count > 0
            ? active.Samples[^1]
            : new DesktopIconFlickSample(active.TouchId, active.StartX, active.StartY, active.StartTimestampMilliseconds);
        double travel = Distance(active.StartX, active.StartY, last.X, last.Y);
        DesktopIconFlickVector velocity = EstimateVelocity(active.Samples, 120);
        _gesture = null;
        return DesktopIconFlickUpdate.Cancelled(travel, velocity, reason);
    }

    internal void Reset() => _gesture = null;

    private static void AppendSample(List<DesktopIconFlickSample> samples, DesktopIconFlickSample sample)
    {
        if (samples.Count > 0 && samples[^1].TimestampMilliseconds == sample.TimestampMilliseconds)
        {
            samples[^1] = sample;
            return;
        }

        samples.Add(sample);
        if (samples.Count > MaximumRememberedSamples)
        {
            samples.RemoveAt(0);
        }
    }

    private static DesktopIconFlickVector EstimateVelocity(
        IReadOnlyList<DesktopIconFlickSample> samples,
        int windowMilliseconds)
    {
        if (samples.Count < 2)
        {
            return default;
        }

        DesktopIconFlickSample newest = samples[^1];
        int oldestIndex = samples.Count - 2;
        for (int i = samples.Count - 2; i >= 0; i--)
        {
            long age = ElapsedMilliseconds(samples[i].TimestampMilliseconds, newest.TimestampMilliseconds);
            if (age > windowMilliseconds)
            {
                break;
            }
            oldestIndex = i;
        }

        DesktopIconFlickSample oldest = samples[oldestIndex];
        long deltaMs = ElapsedMilliseconds(oldest.TimestampMilliseconds, newest.TimestampMilliseconds);
        if (deltaMs <= 0)
        {
            return default;
        }

        double seconds = deltaMs / 1000.0;
        return new DesktopIconFlickVector(
            (newest.X - oldest.X) / seconds,
            (newest.Y - oldest.Y) / seconds);
    }

    private static long ElapsedMilliseconds(long start, long end) =>
        end >= start ? end - start : long.MaxValue;

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private sealed record Gesture(
        int TouchId,
        double StartX,
        double StartY,
        long StartTimestampMilliseconds,
        bool Armed,
        List<DesktopIconFlickSample> Samples);
}

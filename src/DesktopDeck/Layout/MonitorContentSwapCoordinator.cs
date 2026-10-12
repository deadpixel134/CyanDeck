using System;
using System.Collections.Generic;

namespace DesktopDeck.Layout;

/// <summary>
/// Coordinates a user-requested monitor content swap around DesktopDeck's
/// existing snapshot, layout-model, monitor-profile, and save pipelines.
/// </summary>
public sealed class MonitorContentSwapCoordinator
{
    private readonly MonitorContentSwapPlanner _planner;
    private readonly Func<bool> _isDisplayStable;
    private readonly Func<IReadOnlyList<MonitorSwapDescriptor>> _captureMonitors;
    private readonly Func<IReadOnlyList<ScreenContentPlacement>> _capturePlacements;
    private readonly Func<string, bool> _createUndoCheckpoint;
    private readonly Action<IReadOnlyList<ScreenContentSwapMove>> _applyMoves;
    private readonly Action _refreshWindows;
    private readonly Action _saveState;
    private readonly Action<string>? _diagnostic;

    public MonitorContentSwapCoordinator(
        MonitorContentSwapPlanner planner,
        Func<bool> isDisplayStable,
        Func<IReadOnlyList<MonitorSwapDescriptor>> captureMonitors,
        Func<IReadOnlyList<ScreenContentPlacement>> capturePlacements,
        Func<string, bool> createUndoCheckpoint,
        Action<IReadOnlyList<ScreenContentSwapMove>> applyMoves,
        Action refreshWindows,
        Action saveState,
        Action<string>? diagnostic = null)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _isDisplayStable = isDisplayStable ?? throw new ArgumentNullException(nameof(isDisplayStable));
        _captureMonitors = captureMonitors ?? throw new ArgumentNullException(nameof(captureMonitors));
        _capturePlacements = capturePlacements ?? throw new ArgumentNullException(nameof(capturePlacements));
        _createUndoCheckpoint = createUndoCheckpoint ?? throw new ArgumentNullException(nameof(createUndoCheckpoint));
        _applyMoves = applyMoves ?? throw new ArgumentNullException(nameof(applyMoves));
        _refreshWindows = refreshWindows ?? throw new ArgumentNullException(nameof(refreshWindows));
        _saveState = saveState ?? throw new ArgumentNullException(nameof(saveState));
        _diagnostic = diagnostic;
    }

    public MonitorContentSwapResult Swap(
        string firstMonitorId,
        string secondMonitorId,
        MonitorContentSwapOptions? options = null)
    {
        try
        {
            if (!_isDisplayStable())
            {
                return DisplayUnstable();
            }
        }
        catch (Exception ex)
        {
            return new MonitorContentSwapResult(
                MonitorContentSwapStatus.CaptureFailed,
                "DesktopDeck could not confirm that the display topology is stable.",
                Error: ex);
        }

        IReadOnlyList<MonitorSwapDescriptor> capturedMonitors;
        MonitorContentSwapPlan plan;
        try
        {
            capturedMonitors = _captureMonitors();
            plan = _planner.CreatePlan(
                capturedMonitors,
                _capturePlacements(),
                firstMonitorId,
                secondMonitorId,
                options);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _diagnostic?.Invoke($"Monitor content swap plan rejected: {ex.Message}");
            return new MonitorContentSwapResult(
                MonitorContentSwapStatus.InvalidRequest,
                ex.Message,
                Error: ex);
        }
        catch (Exception ex)
        {
            _diagnostic?.Invoke($"Monitor content swap capture failed: {ex.Message}");
            return new MonitorContentSwapResult(
                MonitorContentSwapStatus.CaptureFailed,
                "DesktopDeck could not capture the current monitor/content layout, so no changes were applied.",
                Error: ex);
        }

        if (!plan.HasChanges)
        {
            return new MonitorContentSwapResult(
                MonitorContentSwapStatus.NoChanges,
                "No movable DesktopDeck content is assigned to the selected monitors.",
                plan);
        }

        try
        {
            if (!_createUndoCheckpoint("Before swapping screen contents"))
            {
                return new MonitorContentSwapResult(
                    MonitorContentSwapStatus.CheckpointFailed,
                    "DesktopDeck could not create the required undo checkpoint, so no layout changes were applied.",
                    plan);
            }
        }
        catch (Exception ex)
        {
            _diagnostic?.Invoke($"Monitor content swap checkpoint failed: {ex.Message}");
            return new MonitorContentSwapResult(
                MonitorContentSwapStatus.CheckpointFailed,
                "DesktopDeck could not create the required undo checkpoint, so no layout changes were applied.",
                plan,
                ex);
        }

        try
        {
            if (!_isDisplayStable() || !SelectedMonitorsStillMatch(capturedMonitors, _captureMonitors(), firstMonitorId, secondMonitorId))
            {
                return DisplayUnstable(plan);
            }
        }
        catch (Exception ex)
        {
            _diagnostic?.Invoke($"Monitor content swap topology recheck failed: {ex.Message}");
            return new MonitorContentSwapResult(
                MonitorContentSwapStatus.CaptureFailed,
                "DesktopDeck could not re-confirm the monitor topology, so no layout changes were applied.",
                plan,
                ex);
        }

        try
        {
            _applyMoves(plan.Moves);
            _refreshWindows();
            _saveState();
            _diagnostic?.Invoke(
                $"Monitor content swap applied: {plan.GroupMoveCount} groups, {plan.LooseIconMoveCount} loose icons.");

            return new MonitorContentSwapResult(
                MonitorContentSwapStatus.Success,
                "Screen contents were swapped successfully.",
                plan);
        }
        catch (Exception applyError)
        {
            _diagnostic?.Invoke($"Monitor content swap apply failed: {applyError.Message}");

            try
            {
                var rollback = BuildRollbackMoves(plan.Moves);
                _applyMoves(rollback);
                _refreshWindows();
                _saveState();
                _diagnostic?.Invoke("Monitor content swap rollback completed and the original positions were persisted.");

                return new MonitorContentSwapResult(
                    MonitorContentSwapStatus.ApplyFailed,
                    "The swap failed and DesktopDeck restored the original positions. The failed state was not saved.",
                    plan,
                    applyError);
            }
            catch (Exception rollbackError)
            {
                _diagnostic?.Invoke($"Monitor content swap rollback failed: {rollbackError.Message}");
                return new MonitorContentSwapResult(
                    MonitorContentSwapStatus.RollbackFailed,
                    "The swap and automatic rollback both failed. Use the pre-swap recovery checkpoint to restore the layout.",
                    plan,
                    rollbackError);
            }
        }
    }


    private static bool SelectedMonitorsStillMatch(
        IReadOnlyList<MonitorSwapDescriptor> before,
        IReadOnlyList<MonitorSwapDescriptor> after,
        string firstMonitorId,
        string secondMonitorId)
    {
        return TryFind(before, firstMonitorId, out var firstBefore) &&
               TryFind(before, secondMonitorId, out var secondBefore) &&
               TryFind(after, firstMonitorId, out var firstAfter) &&
               TryFind(after, secondMonitorId, out var secondAfter) &&
               firstBefore == firstAfter &&
               secondBefore == secondAfter;
    }

    private static bool TryFind(
        IReadOnlyList<MonitorSwapDescriptor> monitors,
        string monitorId,
        out MonitorSwapDescriptor descriptor)
    {
        foreach (var monitor in monitors)
        {
            if (string.Equals(monitor.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase))
            {
                descriptor = monitor;
                return true;
            }
        }

        descriptor = null!;
        return false;
    }

    private static MonitorContentSwapResult DisplayUnstable(MonitorContentSwapPlan? plan = null) =>
        new(
            MonitorContentSwapStatus.DisplayUnstable,
            "Display topology is changing. Try the swap again after the monitors settle.",
            plan);

    private static IReadOnlyList<ScreenContentSwapMove> BuildRollbackMoves(
        IReadOnlyList<ScreenContentSwapMove> moves)
    {
        var rollback = new ScreenContentSwapMove[moves.Count];
        for (var i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            rollback[i] = move with
            {
                SourceMonitorId = move.TargetMonitorId,
                TargetMonitorId = move.SourceMonitorId,
                BeforePx = move.AfterPx,
                AfterPx = move.BeforePx
            };
        }

        return rollback;
    }
}

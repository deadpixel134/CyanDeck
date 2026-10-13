using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Interop;
using CyanDeck.Models;
using CyanDeck.Layout;
using CyanDeck.Animations;
using CyanDeck.Input;
using CyanDeck.Interop;
using CyanDeck.Appearance;
using CyanDeck.Automation;
using CyanDeck.Snapshots;
using CyanDeck.Peek;
using CyanDeck.QuickHide;
using CyanDeck.Topology;
using CyanDeck.Display;
using CyanDeck.FolderPortals;
using CyanDeck.ManagedDeployment;
using CyanDeck.Views;
using CyanDeck.Calendar;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CyanDeck.Services;

/// <summary>
/// Lifetime, attachment and visibility responsibilities for CyanDeck group windows.
/// Shared with AppController as a partial type to preserve the event callback contract.
/// Extracted without altering the existing shell or feature-switch behavior.
/// </summary>
public sealed partial class AppController
{
    private readonly Dictionary<FenceWindow, DesktopShowDesktopRecovery> _showDesktopRecoveries = new();

    private void CreateWindow(DeckFence fence)
    {
        var deferInitialItems = _state.Settings.DeferHiddenPageRendering && !fence.ShowOnAllPages && fence.PageIndex != _state.CurrentPage;
        var window = new FenceWindow(fence, _state.Settings, _icons, _state.Templates, _foregroundEvents, deferInitialItems, IsManagedCapabilityAllowed, ResolveEffectiveIconSpacing, ResolveFenceMovePosition);
        window.ModelChanged += Persist;
        window.DisplayTransitionMessage += OnFenceDisplayTransitionMessage;
        window.DeleteRequested += OnDeleteFence;
        window.NewFenceRequested += CreateNewFence;
        window.SettingsRequested += OpenSettingsWindow;
        window.AutomationRulesChanged += () =>
        {
            if (_state.Settings.KeepRulesUpdatedAtAllTimes) ReorganizeDesktop();
            else Persist();
        };
        window.SourcePathDropped += path => ImportDroppedIntoFence(window, path);
        window.ItemsDetachToDesktopRequested += OnItemsDetachToDesktopRequested;
        window.MoveCompleted += OnFenceMoveCompleted;
        window.TabTransferRequested += OnTabTransferRequested;
        window.TabDetachRequested += OnTabDetachRequested;
        window.DuplicateRequested += DuplicateFence;
        window.SaveTemplateRequested += SaveFenceTemplate;
        window.CreateFromTemplateRequested += CreateFenceFromTemplate;
        window.VisibilitySettingsChanged += UpdateWindowVisibility;
        // Attach at SourceInitialized, before WPF Show(), to avoid exposing a
        // transient taskbar button and an app-level Z-order promotion.
        window.SourceInitialized += (_, _) => _host.Attach(window);
        window.Loaded += (_, _) =>
        {
            if (window.IsVisible) _host.EnsureAttached(window); // one post-Show placement
            DiagnosticsLog.Info($"Fence window loaded: page={window.Model.PageIndex + 1}; show-all={window.Model.ShowOnAllPages}; visible={window.IsVisible}; state={window.WindowState}; x={window.Left:0.#}; y={window.Top:0.#}; w={window.ActualWidth:0.#}; h={window.ActualHeight:0.#}");
        };
        _windows.Add(window);
        _showDesktopRecoveries[window] = new DesktopShowDesktopRecovery(
            window,
            () => !_disposed && !_safeMode && !_peek && FenceVisibilityPolicy.ShouldShow(
                window.Model.PageIndex, window.Model.ShowOnAllPages, window.Model.AlwaysVisible,
                _state.CurrentPage, _quickHidden, _state.Settings.EnableQuickHide,
                _state.Settings.QuickHideMode == DeckQuickHideMode.UngroupedIconsOnly),
            () => { if (!_peek) _host.EnsureAttached(window); },
            () => _host.IsInteractiveSurfaceAttached(window));
        if (!deferInitialItems) window.Show();
    }

    private void EnsureFenceRuntimeVisible(FenceWindow window)
    {
        window.EnsureRuntimeVisible();
        if (_peek) return;
        try { _host.EnsureAttached(window); }
        catch (Exception ex) { DiagnosticsLog.Error("Failed to restore shown Fence to desktop band", ex); }
    }

    private void RemoveFenceWindow(FenceWindow window)
    {
        if (_showDesktopRecoveries.Remove(window, out var recovery)) recovery.Dispose();
        _windows.Remove(window);
        _state.Fences.Remove(window.Model);
        window.CloseFromController();
        _state.CurrentPage = Math.Clamp(_state.CurrentPage, 0, GetMaxPage());
        UpdateWindowVisibility();
    }

    private void RepairExpectedFenceVisibility()
    {
        if (_safeMode || _peek || _quickHidden) return;
        var repaired = 0;
        foreach (var window in _windows)
        {
            if (!window.Model.ShowOnAllPages && window.Model.PageIndex != _state.CurrentPage) continue;
            if (window.IsVisible && window.WindowState != WindowState.Minimized) continue;
            EnsureFenceRuntimeVisible(window);
            repaired++;
        }
        if (repaired > 0)
            DiagnosticsLog.Info($"Recovered {repaired} unexpectedly hidden/minimized group window(s) on page {_state.CurrentPage + 1}");
    }

    private void UpdateWindowVisibility()
    {
        foreach (var window in _windows)
        {
            var shouldShow = FenceVisibilityPolicy.ShouldShow(
                window.Model.PageIndex,
                window.Model.ShowOnAllPages,
                window.Model.AlwaysVisible,
                _state.CurrentPage,
                _quickHidden,
                _state.Settings.EnableQuickHide,
                _state.Settings.QuickHideMode == DeckQuickHideMode.UngroupedIconsOnly);
            if (shouldShow)
            {
                EnsureFenceRuntimeVisible(window);
                window.EnsureItemsRendered();
            }
            else if (window.IsVisible) window.Hide();
        }
        RefreshLooseDesktopIconHost();
        UpdateTrayText();
    }
}

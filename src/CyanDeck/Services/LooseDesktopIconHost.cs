using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CyanDeck.Models;
using CyanDeck.Appearance;
using CyanDeck.Input;
using CyanDeck.Layout;
using CyanDeck.QuickHide;
using CyanDeck.Views;
using Forms = System.Windows.Forms;

namespace CyanDeck.Services;

internal readonly record struct LooseDesktopFlickDestination(Guid FenceId, Guid TabId, Rectangle BoundsPx)
{
    internal string StableId => $"{FenceId:N}:{TabId:N}";
}

internal sealed class LooseDesktopIconHost : IDisposable
{
    private sealed class Entry
    {
        internal required DeckItem Item { get; set; }
        internal required LooseDesktopIconWindow Window { get; init; }
        internal Rectangle BoundsPx;
        internal string LoadedPath = "";
        internal int IconLoadGeneration;
        internal WpfDesktopIconFlickBridge? FlickBridge;
        internal DesktopShowDesktopRecovery? ShowDesktopRecovery;
    }

    private readonly DesktopHost _desktopHost;
    private readonly IconService _icons;
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly HashSet<Guid> _selected = [];
    private readonly Action _persist;
    private readonly Action<IReadOnlyList<Guid>> _autoClassify;
    private readonly Func<IReadOnlyList<Guid>, bool, Task> _setQuickHideExcludedAsync;
    private readonly Func<DesktopIconFlickOptions> _getFlickOptions;
    private readonly Func<IReadOnlyList<LooseDesktopFlickDestination>> _getFlickDestinations;
    private readonly Func<Guid, LooseDesktopFlickDestination, bool> _canFlickMove;
    private readonly Action<Guid, LooseDesktopFlickDestination> _commitFlickMove;
    private readonly Action<LooseDesktopFlickDestination?> _previewFlickTarget;
    private readonly Func<ShortcutBadgeOptions> _getShortcutBadgeOptions;
    private readonly Func<LooseIconOpacityOptions> _getOpacityOptions;
    private readonly HashSet<int> _activeTouchIds = [];
    private bool _enabled;
    private bool _quickHideEnabled;
    private bool _quickHidden;
    private DeckQuickHideMode _quickHideMode;
    private bool _disposed;
    private DateTime _recyclePlacementRetryAfterUtc = DateTime.MinValue;
    private static readonly TimeSpan RecyclePlacementCooldown = TimeSpan.FromSeconds(30);

    internal LooseDesktopIconHost(
        DesktopHost desktopHost,
        IconService icons,
        Action persist,
        Action<IReadOnlyList<Guid>> autoClassify,
        Func<IReadOnlyList<Guid>, bool, Task> setQuickHideExcludedAsync,
        Func<DesktopIconFlickOptions> getFlickOptions,
        Func<IReadOnlyList<LooseDesktopFlickDestination>> getFlickDestinations,
        Func<Guid, LooseDesktopFlickDestination, bool> canFlickMove,
        Action<Guid, LooseDesktopFlickDestination> commitFlickMove,
        Action<LooseDesktopFlickDestination?> previewFlickTarget,
        Func<ShortcutBadgeOptions> getShortcutBadgeOptions,
        Func<LooseIconOpacityOptions> getOpacityOptions)
    {
        _desktopHost = desktopHost ?? throw new ArgumentNullException(nameof(desktopHost));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _persist = persist ?? throw new ArgumentNullException(nameof(persist));
        _autoClassify = autoClassify ?? throw new ArgumentNullException(nameof(autoClassify));
        _setQuickHideExcludedAsync = setQuickHideExcludedAsync ?? throw new ArgumentNullException(nameof(setQuickHideExcludedAsync));
        _getFlickOptions = getFlickOptions ?? throw new ArgumentNullException(nameof(getFlickOptions));
        _getFlickDestinations = getFlickDestinations ?? throw new ArgumentNullException(nameof(getFlickDestinations));
        _canFlickMove = canFlickMove ?? throw new ArgumentNullException(nameof(canFlickMove));
        _commitFlickMove = commitFlickMove ?? throw new ArgumentNullException(nameof(commitFlickMove));
        _previewFlickTarget = previewFlickTarget ?? throw new ArgumentNullException(nameof(previewFlickTarget));
        _getShortcutBadgeOptions = getShortcutBadgeOptions ?? throw new ArgumentNullException(nameof(getShortcutBadgeOptions));
        _getOpacityOptions = getOpacityOptions ?? throw new ArgumentNullException(nameof(getOpacityOptions));
    }

    internal void Refresh(
        IReadOnlyList<DeckItem> items,
        bool enabled,
        bool quickHideEnabled,
        bool quickHidden,
        DeckQuickHideMode quickHideMode)
    {
        if (_disposed) return;
        _enabled = enabled;
        _quickHideEnabled = quickHideEnabled;
        _quickHidden = quickHidden;
        _quickHideMode = quickHideMode;

        var valid = items
            .Where(i => i.Id != Guid.Empty
                        && !string.IsNullOrWhiteSpace(i.Path)
                        && (File.Exists(i.Path) || Directory.Exists(i.Path) || DesktopSystemItems.IsSupportedVirtualItem(i)))
            .GroupBy(i => i.Id)
            .Select(g => g.First())
            .ToDictionary(i => i.Id);

        foreach (var stale in _entries.Keys.Where(id => !valid.ContainsKey(id)).ToArray())
            RemoveEntry(stale);

        if (!_enabled)
        {
            CancelTouchFlick();
            foreach (var entry in _entries.Values)
                if (entry.Window.IsVisible) entry.Window.Hide();
            return;
        }

        var index = 0;
        foreach (var item in valid.Values.OrderBy(i => i.AddedUtc).ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            EnsurePosition(item, index++);
            var entry = EnsureEntry(item);
            Position(entry);
            ApplyVisibility(entry);
        }
        UpdateSelectionVisuals();
    }

    internal bool HasUnexpectedHiddenVisualResidue
        => !_disposed
           && _enabled
           && _entries.Values.Any(entry => ShouldRender(entry.Item) && !entry.Window.IsVisible);

    internal bool TryGetPhysicalBounds(Guid itemId, out Rectangle bounds)
    {
        if (_entries.TryGetValue(itemId, out var entry))
        {
            bounds = entry.BoundsPx;
            return bounds.Width > 0 && bounds.Height > 0;
        }

        bounds = Rectangle.Empty;
        return false;
    }

    internal void ApplyMonitorContentSwapMove(Guid itemId, PhysicalRect target)
    {
        if (!_entries.TryGetValue(itemId, out var entry)) return;
        var size = entry.Window.GetDesiredPhysicalSize();
        var moved = ClampToDesktop(new Rectangle(
            (int)Math.Round(target.X),
            (int)Math.Round(target.Y),
            Math.Max(1, size.Width),
            Math.Max(1, size.Height)));
        entry.BoundsPx = moved;
        entry.Item.PositionX = moved.X;
        entry.Item.PositionY = moved.Y;
        try
        {
            if (DesktopSystemItems.IsRecycleBin(entry.Item))
                _desktopHost.PositionStandaloneRecycleBin(entry.Window, moved);
            else
                _desktopHost.PositionLooseIcon(entry.Window, moved);
        }
        catch { }
    }

    internal void CancelTouchFlick()
    {
        if (_disposed) return;
        foreach (var entry in _entries.Values)
        {
            try { entry.FlickBridge?.CancelExternal(DesktopIconFlickCancelReason.BlockedSource); } catch { }
            try { entry.Window.CancelPointerDragForTouchFlick(); } catch { }
        }
        _activeTouchIds.Clear();
        _previewFlickTarget(null);
    }

    internal void RefreshVisibility()
    {
        if (_disposed) return;
        foreach (var entry in _entries.Values) ApplyVisibility(entry);
    }

    internal void RefreshIcon(string path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path)) return;
        foreach (var entry in _entries.Values.Where(e => string.Equals(e.Item.Path, path, StringComparison.OrdinalIgnoreCase)))
            _ = LoadIconAsync(entry, force: true);
    }

    internal void RefreshAllIcons()
    {
        if (_disposed) return;
        foreach (var entry in _entries.Values)
        {
            entry.Window.RefreshAppearance();
            entry.Window.RefreshShortcutBadge();
            _ = LoadIconAsync(entry, force: true);
        }
    }

    internal void EnsureAttached()
    {
        if (_disposed || !_enabled) return;
        foreach (var entry in _entries.Values)
        {
            try
            {
                if (DesktopSystemItems.IsRecycleBin(entry.Item))
                    ApplyVisibility(entry);
                else
                    _desktopHost.EnsureLooseIconAttached(entry.Window, entry.BoundsPx);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("Failed to reattach loose desktop icon", ex);
            }
        }
    }

    private Entry EnsureEntry(DeckItem item)
    {
        if (_entries.TryGetValue(item.Id, out var existing))
        {
            var pathChanged = !string.Equals(existing.Item.Path, item.Path, StringComparison.OrdinalIgnoreCase);
            existing.Item = item;
            existing.Window.UpdateItem(item);
            if (pathChanged) _ = LoadIconAsync(existing, force: true);
            return existing;
        }

        var window = new LooseDesktopIconWindow(item, ShowContextMenu, _getShortcutBadgeOptions, _getOpacityOptions);
        var entry = new Entry { Item = item, Window = window };
        window.SelectionRequested += OnSelectionRequested;
        window.OpenRequested += OnOpenRequested;
        window.DragDeltaRequested += OnDragDeltaRequested;
        window.DragCompleted += _ => _persist();
        window.TouchContactChanged += OnTouchContactChanged;
        entry.FlickBridge = new WpfDesktopIconFlickBridge(
            window,
            _getFlickOptions,
            _ => new DesktopIconFlickSource(item.Id.ToString("D")),
            _ => false,
            source => BuildFlickTargets(entry, source),
            (source, target) => CanFlickMove(source, target),
            _ => window.CancelPointerDragForTouchFlick(),
            target => PreviewFlickTarget(target),
            (source, target) => CommitFlickMove(source, target),
            () => _activeTouchIds.Count);
        _entries[item.Id] = entry;
        if (DesktopSystemItems.IsRecycleBin(item))
        {
            // Reuse the Calendar/Fence one-shot native Win+D recovery policy.
            entry.ShowDesktopRecovery = new DesktopShowDesktopRecovery(
                window,
                () => !_disposed && _enabled && ShouldRender(entry.Item),
                () => ApplyVisibility(entry),
                () => _desktopHost.IsInteractiveSurfaceAttached(window));
        }

        try
        {
            var x = (int)Math.Round(item.PositionX ?? 0);
            var y = (int)Math.Round(item.PositionY ?? 0);
            var size = window.GetDesiredPhysicalSize();
            entry.BoundsPx = new Rectangle(x, y, size.Width, size.Height);
            if (DesktopSystemItems.IsRecycleBin(item))
            {
                // First Show is postponed until the desktop band can be found.
                // ApplyVisibility performs attachment, Show, and final band check.
                _desktopHost.TryEnsureStandaloneRecycleBin(window, entry.BoundsPx, out _);
            }
            else
            {
                window.Show();
                _desktopHost.AttachLooseIcon(window, new Rectangle(x, y, 1, 1));
                _desktopHost.PositionLooseIcon(window, entry.BoundsPx);
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Failed to attach loose desktop icon", ex);
        }

        _ = LoadIconAsync(entry, force: true);
        return entry;
    }

    private async Task LoadIconAsync(Entry entry, bool force)
    {
        var path = entry.Item.Path;
        if (!force && string.Equals(entry.LoadedPath, path, StringComparison.OrdinalIgnoreCase)) return;
        var generation = ++entry.IconLoadGeneration;
        try
        {
            var decision = ShortcutBadgePolicy.Evaluate(new ShortcutBadgeContext
            {
                ItemPath = path,
                IsDesktopBackedItem = true,
                IsItemInteractive = true,
            }, _getShortcutBadgeOptions());
            var image = await Task.Run(() => _icons.Get(path, 48, suppressShortcutOverlay: decision.SuppressHostShortcutOverlay));
            if (_disposed || generation != entry.IconLoadGeneration || !string.Equals(entry.Item.Path, path, StringComparison.OrdinalIgnoreCase)) return;
            entry.LoadedPath = path;
            entry.Window.SetIcon(image);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Loose desktop icon image load failed", ex);
        }
    }

    private void EnsurePosition(DeckItem item, int ordinal)
    {
        if (item.PositionX is double x && item.PositionY is double y && double.IsFinite(x) && double.IsFinite(y)) return;
        var screen = Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens.FirstOrDefault();
        var area = screen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        if (DesktopSystemItems.IsRecycleBin(item))
        {
            var inset = (int)Math.Ceiling(LooseDesktopIconLayoutProfile.EdgeInsetDip);
            item.PositionX = Math.Max(area.Left, area.Right - (int)Math.Ceiling(LooseDesktopIconLayoutProfile.RecycleBinWidthDip) - inset);
            item.PositionY = Math.Max(area.Top, area.Bottom - (int)Math.Ceiling(LooseDesktopIconLayoutProfile.RecycleBinHeightDip) - inset);
            _persist();
            return;
        }
        // The default loose-icon auto-grid must also accommodate the Recycle
        // Bin's wider native clipping rectangle. Existing manual positions
        // remain untouched, and linked Fence spacing stays at its old values.
        var slotWidth = (int)Math.Ceiling(LooseDesktopIconLayoutProfile.LooseAutoCellWidthDip);
        var slotHeight = (int)Math.Ceiling(LooseDesktopIconLayoutProfile.LooseAutoCellHeightDip);
        var edgeInset = (int)Math.Ceiling(LooseDesktopIconLayoutProfile.EdgeInsetDip);
        var rows = Math.Max(1, (area.Height - edgeInset * 2) / slotHeight);
        var column = ordinal / rows;
        var row = ordinal % rows;
        item.PositionX = area.Left + edgeInset + column * slotWidth;
        item.PositionY = area.Top + edgeInset + row * slotHeight;
        _persist();
    }

    private void Position(Entry entry)
    {
        var x = (int)Math.Round(entry.Item.PositionX ?? entry.BoundsPx.X);
        var y = (int)Math.Round(entry.Item.PositionY ?? entry.BoundsPx.Y);
        var size = entry.Window.GetDesiredPhysicalSize();
        entry.BoundsPx = ClampToDesktop(new Rectangle(x, y, size.Width, size.Height));
        entry.Item.PositionX = entry.BoundsPx.X;
        entry.Item.PositionY = entry.BoundsPx.Y;
        try
        {
            if (DesktopSystemItems.IsRecycleBin(entry.Item))
                _desktopHost.PositionStandaloneRecycleBin(entry.Window, entry.BoundsPx);
            else
                _desktopHost.PositionLooseIcon(entry.Window, entry.BoundsPx);
        }
        catch { }
    }

    private static Rectangle ClampToDesktop(Rectangle candidate)
    {
        var center = new System.Drawing.Point(candidate.Left + candidate.Width / 2, candidate.Top + candidate.Height / 2);
        var area = Forms.Screen.FromPoint(center).WorkingArea;
        var x = Math.Clamp(candidate.X, area.Left, Math.Max(area.Left, area.Right - candidate.Width));
        var y = Math.Clamp(candidate.Y, area.Top, Math.Max(area.Top, area.Bottom - candidate.Height));
        return new Rectangle(x, y, candidate.Width, candidate.Height);
    }

    private void ApplyVisibility(Entry entry)
    {
        var shouldShow = _enabled && ShouldRender(entry.Item);
        if (shouldShow && DesktopSystemItems.IsRecycleBin(entry.Item))
        {
            // Avoid a SetWindowPos/diagnostic storm every four seconds while
            // Explorer refuses the desktop band. No visible window is raised.
            if (!entry.Window.IsVisible && DateTime.UtcNow < _recyclePlacementRetryAfterUtc)
                return;
            // Do not show an unattached top-level window above ordinary apps.
            // Never substitute HWND_TOPMOST when Explorer's desktop is unavailable.
            if (!_desktopHost.TryEnsureStandaloneRecycleBin(entry.Window, entry.BoundsPx, out var problem))
            {
                if (entry.Window.IsVisible) entry.Window.Hide();
                _recyclePlacementRetryAfterUtc = DateTime.UtcNow + RecyclePlacementCooldown;
                DiagnosticsLog.Info("Standalone Recycle Bin placement deferred (30s cooldown): " + problem);
                return;
            }
            if (!entry.Window.IsVisible)
            {
                entry.Window.Show();
                // WPF Show can reorder a freshly visible HWND; place it only in
                // the desktop band again, without foreground activation.
                if (!_desktopHost.TryEnsureStandaloneRecycleBin(entry.Window, entry.BoundsPx, out problem))
                {
                    entry.Window.Hide();
                    _recyclePlacementRetryAfterUtc = DateTime.UtcNow + RecyclePlacementCooldown;
                    DiagnosticsLog.Info("Standalone Recycle Bin placement deferred after Show (30s cooldown): " + problem);
                    return;
                }
            }
            _recyclePlacementRetryAfterUtc = DateTime.MinValue;
            entry.Window.RefreshShortcutBadge();
            return;
        }
        if (shouldShow)
        {
            if (!entry.Window.IsVisible) entry.Window.Show();
            entry.Window.RefreshShortcutBadge();
            try { _desktopHost.EnsureLooseIconAttached(entry.Window, entry.BoundsPx); } catch { }
        }
        else if (entry.Window.IsVisible)
        {
            entry.Window.Hide();
            entry.Window.RefreshShortcutBadge();
        }
    }

    private bool ShouldRender(DeckItem item)
    {
        // The Recycle Bin is a persistent desktop system affordance. Quick-hide affects
        // user content and groups, never this system item.
        if (DesktopSystemItems.IsRecycleBin(item)) return _enabled;

        var mode = _quickHideMode switch
        {
            DeckQuickHideMode.GroupsOnly => QuickHideVisibilityMode.HideGroupsOnly,
            DeckQuickHideMode.UngroupedIconsOnly => QuickHideVisibilityMode.HideIconsNotInGroups,
            _ => QuickHideVisibilityMode.HideAllDesktopIconsAndGroups,
        };
        var snapshot = Snapshot(item);
        return LooseIconQuickHideVisibilityPolicy.ShouldRenderOnManagedLooseIconSurface(
            new LooseIconQuickHideVisibilityRequest(_quickHideEnabled, _quickHidden, mode, snapshot));
    }

    private static LooseDesktopItemQuickHideSnapshot Snapshot(DeckItem item)
        => new(item.Id.ToString("D"), QuickHideLooseIconSurfaceKind.CyanDeckManagedLooseIcon, true, false, item.ExcludeFromQuickHide);

    private void OnSelectionRequested(LooseDesktopIconWindow window, bool ctrl)
    {
        var id = window.Item.Id;
        if (DesktopSystemItems.IsRecycleBin(window.Item))
        {
            // Keep the system item outside multi-item operations such as classification and
            // group moves. It remains independently movable on the desktop.
            _selected.Clear();
            _selected.Add(id);
        }
        else if (ctrl)
        {
            _selected.Remove(DesktopSystemItems.RecycleBinItemId);
            if (!_selected.Add(id)) _selected.Remove(id);
        }
        else
        {
            _selected.Clear();
            _selected.Add(id);
        }
        UpdateSelectionVisuals();
    }

    private void UpdateSelectionVisuals()
    {
        _selected.RemoveWhere(id => !_entries.ContainsKey(id));
        foreach (var pair in _entries)
            pair.Value.Window.SetSelected(_selected.Contains(pair.Key));
    }

    private void OnOpenRequested(LooseDesktopIconWindow window)
    {
        try
        {
            if (DesktopSystemItems.IsRecycleBin(window.Item))
                DesktopSystemItems.OpenRecycleBin();
            else
                ShortcutLaunchService.Open(window.Item.Path);

            if (window.Item.OpenCount < long.MaxValue) window.Item.OpenCount++;
            _persist();
        }
        catch (ShortcutLaunchException ex)
        {
            ShortcutLaunchService.ReportFailure(ex);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDragDeltaRequested(LooseDesktopIconWindow source, int dx, int dy)
    {
        var ids = _selected.Contains(source.Item.Id) ? _selected.ToArray() : [source.Item.Id];
        foreach (var id in ids)
        {
            if (!_entries.TryGetValue(id, out var entry)) continue;
            var moved = ClampToDesktop(new Rectangle(entry.BoundsPx.X + dx, entry.BoundsPx.Y + dy, entry.BoundsPx.Width, entry.BoundsPx.Height));
            entry.BoundsPx = moved;
            entry.Item.PositionX = moved.X;
            entry.Item.PositionY = moved.Y;
            try
            {
                if (DesktopSystemItems.IsRecycleBin(entry.Item))
                    _desktopHost.PositionStandaloneRecycleBin(entry.Window, moved);
                else
                    _desktopHost.PositionLooseIcon(entry.Window, moved);
            }
            catch { }
        }
    }

    private void OnTouchContactChanged(int touchId, bool active)
    {
        if (active)
        {
            _activeTouchIds.Add(touchId);
            if (_activeTouchIds.Count > 1)
            {
                foreach (var entry in _entries.Values)
                {
                    try { entry.FlickBridge?.CancelExternal(DesktopIconFlickCancelReason.MultiTouch); } catch { }
                }
                _previewFlickTarget(null);
            }
        }
        else
        {
            _activeTouchIds.Remove(touchId);
        }
    }

    private IReadOnlyList<DesktopIconFlickTarget> BuildFlickTargets(Entry sourceEntry, DesktopIconFlickSource source)
    {
        if (DesktopSystemItems.IsRecycleBin(sourceEntry.Item))
            return Array.Empty<DesktopIconFlickTarget>();
        if (!Guid.TryParse(source.StableId, out var sourceId) || sourceId != sourceEntry.Item.Id)
            return Array.Empty<DesktopIconFlickTarget>();

        var scale = sourceEntry.Window.GetDpiScale();
        if (!double.IsFinite(scale) || scale <= 0) scale = 1d;
        var originX = sourceEntry.BoundsPx.Left;
        var originY = sourceEntry.BoundsPx.Top;
        var result = new List<DesktopIconFlickTarget>();
        foreach (var destination in _getFlickDestinations())
        {
            var bounds = destination.BoundsPx;
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;
            result.Add(new DesktopIconFlickTarget(
                destination.StableId,
                new DesktopIconFlickRect(
                    (bounds.Left - originX) / scale,
                    (bounds.Top - originY) / scale,
                    (bounds.Right - originX) / scale,
                    (bounds.Bottom - originY) / scale)));
        }
        return result;
    }

    private bool CanFlickMove(DesktopIconFlickSource source, DesktopIconFlickTarget target)
    {
        if (!Guid.TryParse(source.StableId, out var itemId) || !_entries.TryGetValue(itemId, out var entry))
            return false;
        if (DesktopSystemItems.IsRecycleBin(entry.Item))
            return false;
        return TryResolveDestination(target.TargetStableId, out var destination)
            && _canFlickMove(itemId, destination);
    }

    private void CommitFlickMove(DesktopIconFlickSource source, DesktopIconFlickTarget target)
    {
        if (!Guid.TryParse(source.StableId, out var itemId)) return;
        if (!TryResolveDestination(target.TargetStableId, out var destination)) return;
        _commitFlickMove(itemId, destination);
    }

    private void PreviewFlickTarget(DesktopIconFlickTarget? target)
    {
        if (target is DesktopIconFlickTarget value && TryResolveDestination(value.TargetStableId, out var destination))
            _previewFlickTarget(destination);
        else
            _previewFlickTarget(null);
    }

    private bool TryResolveDestination(string stableId, out LooseDesktopFlickDestination destination)
    {
        destination = default;
        foreach (var candidate in _getFlickDestinations())
        {
            if (string.Equals(candidate.StableId, stableId, StringComparison.Ordinal))
            {
                destination = candidate;
                return true;
            }
        }
        return false;
    }

    private void ShowContextMenu(LooseDesktopIconWindow source, System.Windows.Point screenPoint)
    {
        if (DesktopSystemItems.IsRecycleBin(source.Item))
        {
            _selected.Clear();
            _selected.Add(source.Item.Id);
            UpdateSelectionVisuals();

            // Ask the Shell namespace object itself for its menu. This preserves native
            // Recycle Bin verbs such as Empty Recycle Bin and Properties instead of
            // manufacturing a CyanDeck-only approximation.
            Func<string, bool> recycleVerbInterceptor = verb =>
            {
                if (!string.Equals(verb, "open", StringComparison.OrdinalIgnoreCase)) return false;
                OnOpenRequested(source);
                return true;
            };
            if (ShellContextMenuService.TryShow(
                    source,
                    DesktopSystemItems.RecycleBinShellPath,
                    screenPoint,
                    canonicalVerbInterceptor: recycleVerbInterceptor))
                return;

            OpenFallbackContextMenu(source, BuildFallbackContextMenu(source));
            return;
        }

        if (!_selected.Contains(source.Item.Id))
        {
            _selected.Clear();
            _selected.Add(source.Item.Id);
            UpdateSelectionVisuals();
        }

        var ids = _selected.ToArray();
        var snapshots = ids.Where(_entries.ContainsKey).Select(id => Snapshot(_entries[id].Item)).ToArray();
        var state = LooseIconQuickHideContextMenuPolicy.GetMenuState(snapshots, editCapabilityAllowed: true);
        var extras = new List<ShellContextMenuService.ExtraCommand>
        {
            new(ids.Length == 1 ? "자동 분류로 그룹에 넣기" : $"선택한 {ids.Length}개 항목 자동 분류", () => _autoClassify(ids)),
        };

        if (state.CheckState != LooseIconBatchCheckState.Hidden)
        {
            var quickLabel = state.CheckState switch
            {
                LooseIconBatchCheckState.Checked => "빠른 숨김 제외 해제",
                LooseIconBatchCheckState.Indeterminate => "빠른 숨김에서 제외 (혼합 상태 통일)",
                _ => "빠른 숨김에서 제외",
            };
            extras.Add(new ShellContextMenuService.ExtraCommand(
                quickLabel,
                () => _ = _setQuickHideExcludedAsync(ids, state.NextExcludeValue)));
        }

        var paths = ids
            .Where(_entries.ContainsKey)
            .Select(id => _entries[id].Item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        Func<string, bool>? verbInterceptor = null;
        if (ids.Length == 1)
        {
            verbInterceptor = verb =>
            {
                if (!string.Equals(verb, "open", StringComparison.OrdinalIgnoreCase)) return false;
                OnOpenRequested(source);
                return true;
            };
        }

        // Detached external references may be C:\ or Downloads. A native Shell
        // context menu would expose delete on those source folders; allow native
        // verbs only for real Desktop files, never arbitrary Collection references.
        if (paths.Length > 0 && paths.All(DesktopRecycleService.IsActualDesktopItem) &&
            ShellContextMenuService.TryShow(source, paths, screenPoint, extras, verbInterceptor))
            return;

        // Shell multi-select menus require a common parent. Mixed-parent detached items
        // therefore retain the CyanDeck fallback menu instead of showing an invalid or
        // incomplete native menu.
        OpenFallbackContextMenu(source, BuildFallbackContextMenu(source));
    }

    private static void OpenFallbackContextMenu(LooseDesktopIconWindow source, ContextMenu? menu)
    {
        if (menu is null) return;
        menu.PlacementTarget = source;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private ContextMenu? BuildFallbackContextMenu(LooseDesktopIconWindow source)
    {
        if (DesktopSystemItems.IsRecycleBin(source.Item))
        {
            _selected.Clear();
            _selected.Add(source.Item.Id);
            UpdateSelectionVisuals();
            var recycleMenu = new ContextMenu
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 32, 37)),
                Foreground = System.Windows.Media.Brushes.White,
            };
            var openRecycleBin = new MenuItem { Header = "휴지통 열기" };
            openRecycleBin.Click += (_, _) => OnOpenRequested(source);
            recycleMenu.Items.Add(openRecycleBin);
            return recycleMenu;
        }

        if (!_selected.Contains(source.Item.Id))
        {
            _selected.Clear();
            _selected.Add(source.Item.Id);
            UpdateSelectionVisuals();
        }

        var ids = _selected.ToArray();
        var snapshots = ids.Where(_entries.ContainsKey).Select(id => Snapshot(_entries[id].Item)).ToArray();
        var state = LooseIconQuickHideContextMenuPolicy.GetMenuState(snapshots, editCapabilityAllowed: true);

        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 32, 37)),
            Foreground = System.Windows.Media.Brushes.White,
        };

        if (ids.Length == 1)
        {
            var open = new MenuItem { Header = "열기" };
            open.Click += (_, _) => OnOpenRequested(source);
            menu.Items.Add(open);
        }

        var classify = new MenuItem { Header = ids.Length == 1 ? "자동 분류로 그룹에 넣기" : $"선택한 {ids.Length}개 항목 자동 분류" };
        classify.Click += (_, _) => _autoClassify(ids);
        menu.Items.Add(classify);
        menu.Items.Add(new Separator());

        if (state.CheckState != LooseIconBatchCheckState.Hidden)
        {
            var quick = new MenuItem
            {
                Header = state.CheckState == LooseIconBatchCheckState.Indeterminate ? "빠른 숨김에서 제외 (혼합)" : "빠른 숨김에서 제외",
                IsCheckable = true,
                IsChecked = state.CheckState == LooseIconBatchCheckState.Checked,
                IsEnabled = state.IsEnabled,
            };
            quick.Click += async (_, _) => await _setQuickHideExcludedAsync(ids, state.NextExcludeValue);
            menu.Items.Add(quick);
        }

        var selectedDesktopPaths = ids.Where(_entries.ContainsKey)
            .Select(id => _entries[id].Item.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (selectedDesktopPaths.Length > 0 && selectedDesktopPaths.All(DesktopRecycleService.IsActualDesktopItem))
        {
            var recycle = new MenuItem { Header = ids.Length == 1
                ? "바탕화면 원본을 휴지통으로 이동…"
                : $"바탕화면 원본 {selectedDesktopPaths.Length}개를 휴지통으로 이동…" };
            recycle.Click += (_, _) => RecycleLooseDesktopFiles(selectedDesktopPaths);
            menu.Items.Add(recycle);
        }

        if (ids.Length == 1)
        {
            menu.Items.Add(new Separator());
            var reveal = new MenuItem { Header = "파일 위치 열기" };
            reveal.Click += (_, _) => Reveal(source.Item.Path);
            menu.Items.Add(reveal);
            var properties = new MenuItem { Header = "속성" };
            properties.Click += (_, _) => ShowProperties(source.Item.Path);
            menu.Items.Add(properties);
        }
        return menu;
    }

    private void RecycleLooseDesktopFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || paths.Any(p => !DesktopRecycleService.IsActualDesktopItem(p))) return;
        var name = paths.Count == 1 ? $"'{System.IO.Path.GetFileName(paths[0])}'" : $"선택한 {paths.Count}개 항목";
        if (System.Windows.MessageBox.Show(
            $"{name}을(를) 실제 바탕화면에서 휴지통으로 이동하시겠습니까?\n원본 파일·폴더가 휴지통으로 이동합니다.",
            "CyanDeck — 실제 파일 삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var result = DesktopRecycleService.RecycleConfirmed(paths, warning =>
            System.Windows.MessageBox.Show(warning,
                "CyanDeck — 영구 삭제 위험 추가 확인", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        if (result.Recycled.Count > 0)
        {
            var moved = result.Recycled.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var id in _entries.Keys.Where(id => moved.Contains(_entries[id].Item.Path)).ToArray())
                RemoveEntry(id);
            _selected.Clear();
            _persist(); // desktop watcher will reconcile stored loose-icon references
        }
        if (result.Errors.Count > 0)
            System.Windows.MessageBox.Show(string.Join(Environment.NewLine, result.Errors.Take(8)),
                "CyanDeck — 휴지통 이동 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void Reveal(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private static void ShowProperties(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "properties" }); } catch { }
    }

    private void RemoveEntry(Guid id)
    {
        _selected.Remove(id);
        if (!_entries.Remove(id, out var entry)) return;
        try { entry.ShowDesktopRecovery?.Dispose(); } catch { }
        try { entry.FlickBridge?.Dispose(); } catch { }
        try { entry.Window.Close(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var id in _entries.Keys.ToArray()) RemoveEntry(id);
        _selected.Clear();
        _activeTouchIds.Clear();
        _previewFlickTarget(null);
    }
}

using System.Windows;
using System.Windows.Threading;
using CyanDeck.Interop;
using CyanDeck.Models;
using CyanDeck.Views;
using Forms = System.Windows.Forms;

namespace CyanDeck.Services;

/// <summary>
/// Group, page and template commands: dropping a group at a screen edge, sending a group to
/// another page, placing new groups at a free spot of the right monitor, managing templates,
/// the global item search and ending Peek after a launch. Shared with AppController as a
/// partial type; existing callbacks keep their contract.
/// </summary>
public sealed partial class AppController
{
    private readonly record struct PortalSearchSource(
        Guid FenceId, Guid TabId, string GroupTitle, string TabTitle, int PageIndex, bool ShowOnAllPages, string Path);

    private const int PortalSearchEntryLimit = 2000;

    private GlobalItemSearchWindow? _globalSearchWindow;
    private bool _launchPeekHookInstalled;
    private DispatcherTimer? _revealPeekWatchTimer;

    private void WireGroupPageOperations(FenceWindow window)
    {
        window.MaxPageIndexProvider = GetMaxPage;
        window.NewPageTargetProvider = GetNewPageTargetFor;
        window.PageEdgeDropDirectionResolver = ResolveFenceDropPageDirection;
        window.SendToPageRequested += SendFenceToPage;
        window.RenameTemplateRequested += (owner, templateId) => RenameFenceTemplate(templateId, owner);
        window.DeleteTemplateRequested += (owner, templateId) => DeleteFenceTemplate(templateId, owner);
        window.GlobalSearchRequested += OpenGlobalItemSearch;
        window.PeekEndRequested += () =>
        {
            if (_peek) SetPeek(false);
        };
        // One-step layout undo from the group menu (same command as the tray arrange submenu).
        window.LatestLayoutUndoLabelProvider = () => _safeMode || _compatibilityReadOnly ? null : DescribeLatestUndoCommand();
        window.LayoutUndoRequested += () =>
            _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(UndoLastLayoutChange));
        if (!_launchPeekHookInstalled)
        {
            _launchPeekHookInstalled = true;
            ShortcutLaunchService.Launched += OnItemLaunchedEndPeek;
        }
    }

    // ---- #3: dropping a group at a screen edge ----

    /// <summary>-1/+1 when the pointer is on a screen edge where a dropped group changes page, else 0.</summary>
    private int ResolveFenceDropPageDirection(FenceWindow source)
    {
        if (_safeMode || _peek || source.Model.ShowOnAllPages) return 0;
        if (!IsManagedCapabilityAllowed("Layout.MoveResizeGroups") || !_state.Settings.EnableEdgePageGestures) return 0;
        var cursor = Forms.Cursor.Position;
        var screen = Forms.Screen.FromPoint(cursor);
        var direction = GroupPageDropEdgePolicy.ResolveDirection(
            _desktopMonitorTopology,
            GetMonitorTopologySurfaceId(screen.DeviceName),
            cursor.X,
            cursor.Y,
            GetDesktopTopologyOptions());
        if (direction < 0 && source.Model.PageIndex <= 0) return 0;
        return direction;
    }

    private void FitMovedFenceIntoDominantMonitor(FenceWindow source)
    {
        if (source.Model.Locked || !IsManagedCapabilityAllowed("Layout.MoveResizeGroups")) return;
        if (source.FitIntoDominantMonitor()) Persist();
    }

    // ---- #9: send a group to another (or a new) desktop page ----

    private void SendFenceToPage(FenceWindow window, int requestedPage)
    {
        if (_disposed || !_windows.Contains(window)) return;
        if (!EnsureManagedCapability("Layout.MoveResizeGroups", "그룹을 다른 페이지로 보내기")) return;
        var model = window.Model;
        if (model.Locked) return;
        var target = GroupPageSendPolicy.ClampTarget(requestedPage, GetMaxPage());
        if (!model.ShowOnAllPages && model.PageIndex == target) return;
        if (_peek) SetPeek(false);

        var previousPage = model.ShowOnAllPages ? -1 : model.PageIndex;
        model.ShowOnAllPages = false;
        model.PageIndex = target;
        var groupsLeft = _state.Fences.Count(f => f.ShowOnAllPages || f.PageIndex == _state.CurrentPage);
        var follow = GroupPageSendPolicy.ShouldFollow(_state.Settings.FollowGroupSentToPage, _state.CurrentPage, target, groupsLeft);
        DiagnosticsLog.Info($"Group sent to page: {previousPage + 1}->{target + 1}; follow={follow}; groups-left-on-current={groupsLeft}");

        if (follow)
        {
            TransitionToPage(target, target > _state.CurrentPage ? 1 : -1);
            window.FlashLocationHighlight();
            return;
        }

        _state.CurrentPage = Math.Clamp(_state.CurrentPage, 0, GetMaxPage());
        UpdateWindowVisibility();
        Persist();
        RebuildTrayMenu();
        if (target == _state.CurrentPage)
        {
            window.FlashLocationHighlight();
            return;
        }
        var title = string.IsNullOrWhiteSpace(model.Title) ? "그룹" : "'" + model.Title.Trim() + "' 그룹";
        _trayController?.ShowBalloonTip(3500, "CyanDeck",
            $"{title}을(를) 바탕화면 페이지 {target + 1}로 보냈습니다.", Forms.ToolTipIcon.Info);
    }

    private int GetNewPageTargetFor(FenceWindow window)
    {
        var others = _state.Fences.Where(f => !ReferenceEquals(f, window.Model)).ToList();
        var maxOtherPage = others.Count == 0 ? -1 : others.Max(f => f.PageIndex);
        return GroupPageSendPolicy.NewPageTarget(window.Model.PageIndex, window.Model.ShowOnAllPages, maxOtherPage);
    }

    private void AddTrayPageCommands(Forms.ToolStripMenuItem pages, System.Drawing.Point trayCursor)
    {
        pages.DropDownItems.Add(new Forms.ToolStripSeparator());
        var previous = pages.DropDownItems.Add("이전 페이지", null, (_, _) => _dispatcher.Invoke(() => SwitchPage(-1)));
        previous.Enabled = !_peek && _state.CurrentPage > 0;
        var next = pages.DropDownItems.Add("다음 페이지", null, (_, _) => _dispatcher.Invoke(() => SwitchPage(1)));
        next.Enabled = !_peek && _state.CurrentPage < GetMaxPage();
        pages.DropDownItems.Add(new Forms.ToolStripSeparator());
        var create = pages.DropDownItems.Add("새 페이지에 그룹 만들기", null, (_, _) => _dispatcher.Invoke(() => CreateNewFenceOnNewPage(trayCursor)));
        create.Enabled = IsManagedCapabilityAllowed("Layout.CreateDeleteGroups");
    }

    private void CreateNewFenceOnNewPage(System.Drawing.Point screenPointPx)
    {
        if (!EnsureManagedCapability("Layout.CreateDeleteGroups", "새 페이지에 그룹 만들기")) return;
        if (_peek) SetPeek(false);
        var page = GetMaxPage() + 1;
        var window = CreateNewFenceAt(screenPointPx, null, page);
        if (window is null) return;
        GoToPage(page);
        window.FlashLocationHighlight();
    }

    // ---- #17: new groups at a visible free spot of the right monitor ----

    private void CreateNewFenceFromTray(System.Drawing.Point trayCursor)
    {
        if (!EnsureManagedCapability("Layout.CreateDeleteGroups", "새 그룹 만들기")) return;
        var window = CreateNewFenceAt(trayCursor, null, _state.CurrentPage);
        if (window is not null) AnnounceTrayCreatedGroup(window);
    }

    private void CreateNewFenceNear(FenceWindow source)
    {
        if (!EnsureManagedCapability("Layout.CreateDeleteGroups", "새 그룹 만들기")) return;
        var page = source.Model.ShowOnAllPages ? _state.CurrentPage : source.Model.PageIndex;
        CreateNewFenceAt(GetFenceScreenCenter(source) ?? Forms.Cursor.Position, source, page);
    }

    private void CreateFenceFromTemplateNear(Guid templateId, FenceWindow source)
        => CreateFenceFromTemplateAt(templateId, GetFenceScreenCenter(source) ?? Forms.Cursor.Position, source, fromTray: false);

    private FenceWindow? CreateNewFenceAt(System.Drawing.Point screenPointPx, FenceWindow? near, int pageIndex)
    {
        var placement = ResolveNewGroupPlacement(screenPointPx, near, 360, 310, pageIndex);
        var fence = new DeckFence
        {
            Title = "새 그룹",
            X = placement.X,
            Y = placement.Y,
            Width = placement.Width,
            Height = placement.Height,
            ExpandedHeight = placement.Height,
            PageIndex = Math.Max(0, pageIndex),
            Tabs = [new DeckTab { Title = "항목" }]
        };
        _state.Fences.Add(fence);
        CreateWindow(fence);
        var window = _windows.LastOrDefault(w => ReferenceEquals(w.Model, fence));
        Persist();
        RebuildTrayMenu();
        DiagnosticsLog.Info($"Created group at a free spot; monitor={placement.Monitor}; page={fence.PageIndex + 1}; overlaps={placement.Overlaps}");
        window?.FlashLocationHighlight();
        return window;
    }

    private void CreateFenceFromTemplateAt(Guid templateId, System.Drawing.Point screenPointPx, FenceWindow? near, bool fromTray)
    {
        if (!EnsureManagedCapability("Layout.CreateDeleteGroups", "템플릿에서 그룹 만들기")) return;
        var template = _state.Templates.FirstOrDefault(t => t.Id == templateId);
        if (template is null) return;
        var page = near is null ? _state.CurrentPage : near.Model.ShowOnAllPages ? _state.CurrentPage : near.Model.PageIndex;
        var placement = ResolveNewGroupPlacement(screenPointPx, near, template.Prototype.Width, template.Prototype.Height, page);
        var fence = FenceFactory.CreateFromTemplate(template, placement.X, placement.Y, page);
        // The free spot was found for a size that fits this work area; a template saved on a
        // larger monitor is shrunk to it instead of overflowing onto neighbouring groups.
        if (fence.Width > placement.Width) fence.Width = placement.Width;
        if (fence.Height > placement.Height) fence.Height = placement.Height;
        if (fence.ExpandedHeight > placement.MaxHeight) fence.ExpandedHeight = placement.MaxHeight;
        _state.Fences.Add(fence);
        CreateWindow(fence);
        var window = _windows.LastOrDefault(w => ReferenceEquals(w.Model, fence));
        Persist();
        RebuildTrayMenu();
        window?.FlashLocationHighlight();
        if (fromTray && window is not null) AnnounceTrayCreatedGroup(window);
    }

    private readonly record struct NewGroupPlacement(double X, double Y, double Width, double Height, double MaxHeight, bool Overlaps, string Monitor);

    private NewGroupPlacement ResolveNewGroupPlacement(System.Drawing.Point screenPointPx, FenceWindow? near, double width, double height, int pageIndex)
    {
        var screen = Forms.Screen.FromPoint(screenPointPx);
        var dpi = ResolveBestKnownMonitorDpi(screen.DeviceName);
        var scale = dpi is >= 48 and <= 960 ? dpi / 96d : 1d;
        var work = screen.WorkingArea;
        var workRect = new GroupPlacementRect(work.Left / scale, work.Top / scale, work.Width / scale, work.Height / scale);
        var maxWidth = Math.Max(220d, workRect.Width - 2 * GroupPlacementPolicy.Margin);
        var maxHeight = Math.Max(160d, workRect.Height - 2 * GroupPlacementPolicy.Margin);
        width = Math.Clamp(double.IsFinite(width) && width > 0 ? width : 360d, 220d, maxWidth);
        height = Math.Clamp(double.IsFinite(height) && height > 0 ? height : 310d, 160d, maxHeight);
        var occupied = _state.Fences
            .Where(f => f.ShowOnAllPages || f.PageIndex == pageIndex)
            .Select(f => new GroupPlacementRect(f.X, f.Y, Math.Max(1, f.Width), Math.Max(1, f.Height)))
            .ToList();
        double? anchorX = null;
        double? anchorY = null;
        if (near is not null)
        {
            anchorX = near.Model.X + near.Model.Width / 2;
            anchorY = near.Model.Y + near.Model.Height / 2;
        }
        var result = GroupPlacementPolicy.FindFreeSpot(
            workRect, width, height, occupied, _state.Settings.GroupSnapGap, anchorX, anchorY, _state.Fences.Count);
        return new NewGroupPlacement(result.X, result.Y, width, height, maxHeight, result.Overlaps, screen.DeviceName);
    }

    private static System.Drawing.Point? GetFenceScreenCenter(FenceWindow window)
    {
        try
        {
            if (!window.IsLoaded || !window.IsVisible || window.ActualWidth <= 0) return null;
            var center = window.PointToScreen(new System.Windows.Point(window.ActualWidth / 2, window.ActualHeight / 2));
            return new System.Drawing.Point((int)Math.Round(center.X), (int)Math.Round(center.Y));
        }
        catch { return null; }
    }

    // Groups live on the desktop layer, so a maximized window can hide a new group completely.
    private void AnnounceTrayCreatedGroup(FenceWindow window)
    {
        if (!window.IsVisible) return;
        _trayController?.ShowBalloonTip(4000, "CyanDeck",
            "새 그룹을 만들었습니다. 다른 창에 가려 보이지 않으면 Peek(창 위로 그룹 보기)로 확인하세요.",
            Forms.ToolTipIcon.Info);
    }

    private void AddTrayMoveAllGroupsCommand(Forms.ContextMenuStrip menu)
    {
        var enabled = IsManagedCapabilityAllowed("Layout.MoveResizeGroups") && _state.Fences.Count > 0;
        var screens = Forms.Screen.AllScreens;
        if (screens.Length < 2)
        {
            var single = menu.Items.Add(DesktopBackgroundRecoveryMenu.Caption, null, (_, _) =>
            {
                var monitorName = Forms.Screen.FromPoint(Forms.Cursor.Position).DeviceName;
                _dispatcher.BeginInvoke(new Action(() => RecoverAllGroupsToMonitor(monitorName)));
            });
            single.Enabled = enabled;
            return;
        }

        var move = new Forms.ToolStripMenuItem("그룹 전부 한 모니터로 옮기기") { Enabled = enabled };
        var ordered = screens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var screen = ordered[i];
            var monitorName = screen.DeviceName;
            var primaryMark = screen.Primary ? " (주 모니터)" : "";
            var item = move.DropDownItems.Add($"모니터 {i + 1}{primaryMark}", null, (_, _) =>
                _dispatcher.BeginInvoke(new Action(() => RecoverAllGroupsToMonitor(monitorName))));
            item.ToolTipText = $"{screen.Bounds.Width}×{screen.Bounds.Height}";
        }
        move.DropDownItems.Add(new Forms.ToolStripSeparator());
        move.DropDownItems.Add(new Forms.ToolStripMenuItem("바탕화면 오른쪽 클릭 메뉴에서도 그 모니터로 옮길 수 있습니다") { Enabled = false });
        menu.Items.Add(move);
    }

    // ---- #36: template management ----

    private Forms.ToolStripMenuItem BuildTrayTemplateMenu(System.Drawing.Point trayCursor)
    {
        var canCreate = IsManagedCapabilityAllowed("Layout.CreateDeleteGroups");
        var canEdit = IsManagedCapabilityAllowed("Templates.Edit");
        var templates = new Forms.ToolStripMenuItem("그룹 템플릿");
        foreach (var template in _state.Templates.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var templateId = template.Id;
            var entry = new Forms.ToolStripMenuItem(template.Name);
            var create = entry.DropDownItems.Add("이 템플릿으로 새 그룹 만들기", null, (_, _) =>
                _dispatcher.Invoke(() => CreateFenceFromTemplateAt(templateId, trayCursor, null, fromTray: true)));
            create.Enabled = canCreate;
            entry.DropDownItems.Add(new Forms.ToolStripSeparator());
            var rename = entry.DropDownItems.Add("이름 바꾸기…", null, (_, _) => QueueTrayDialog(() => RenameFenceTemplate(templateId, null)));
            rename.Enabled = canEdit;
            var delete = entry.DropDownItems.Add("삭제…", null, (_, _) => QueueTrayDialog(() => DeleteFenceTemplate(templateId, null)));
            delete.Enabled = canEdit;
            templates.DropDownItems.Add(entry);
        }
        templates.DropDownItems.Add(new Forms.ToolStripSeparator());
        var deleteAll = templates.DropDownItems.Add("모든 템플릿 삭제…", null, (_, _) => QueueTrayDialog(ConfirmDeleteAllTemplates));
        deleteAll.Enabled = canEdit;
        return templates;
    }

    private void RenameFenceTemplate(Guid templateId, Window? owner)
    {
        if (!EnsureManagedCapability("Templates.Edit", "그룹 템플릿 이름 바꾸기")) return;
        var template = _state.Templates.FirstOrDefault(t => t.Id == templateId);
        if (template is null) return;
        var name = TextPrompt.Show(owner!, "템플릿 이름 바꾸기", "새 템플릿 이름", template.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (string.Equals(name, template.Name, StringComparison.CurrentCulture)) return;
        if (_state.Templates.Any(t => t.Id != templateId && string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            ShowTemplateMessage(owner, $"'{name}' 이름의 템플릿이 이미 있습니다. 다른 이름을 입력하세요.",
                MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK);
            return;
        }
        template.Name = name;
        Persist();
        RebuildTrayMenu();
    }

    private void DeleteFenceTemplate(Guid templateId, Window? owner)
    {
        if (!EnsureManagedCapability("Templates.Edit", "그룹 템플릿 삭제")) return;
        var template = _state.Templates.FirstOrDefault(t => t.Id == templateId);
        if (template is null) return;
        var answer = ShowTemplateMessage(owner,
            $"'{template.Name}' 템플릿을 삭제하시겠습니까?\n\n이 템플릿으로 이미 만든 그룹은 그대로 남습니다.",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _state.Templates.Remove(template);
        Persist();
        RebuildTrayMenu();
    }

    private void ConfirmDeleteAllTemplates()
    {
        if (!EnsureManagedCapability("Templates.Edit", "모든 템플릿 삭제")) return;
        var count = _state.Templates.Count;
        if (count == 0) return;
        if (System.Windows.MessageBox.Show(
                $"저장된 그룹 템플릿 {count}개를 모두 삭제하시겠습니까?\n\n이미 만든 그룹은 그대로 남습니다.",
                "그룹 템플릿 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _state.Templates.Clear();
        Persist();
        RebuildTrayMenu();
    }

    /// <summary>Asks before saving would push the oldest templates past the 50-template limit.</summary>
    private bool ConfirmTemplateLimitEviction(Window owner, DeckGroupTemplate? replacing)
    {
        var evicted = GroupTemplateLimitPolicy.EvictedBySave(_state.Templates, replacing);
        if (evicted.Count == 0) return true;
        var names = string.Join(", ", evicted.Take(5).Select(t => "'" + t.Name + "'"));
        var more = evicted.Count > 5 ? " 외 " + (evicted.Count - 5) + "개" : "";
        var message = $"그룹 템플릿은 최대 {GroupTemplateLimitPolicy.MaxTemplates}개까지 저장됩니다.\n" +
            $"저장하면 가장 오래된 템플릿 {names}{more}이(가) 삭제됩니다.\n\n계속하시겠습니까?";
        return ShowTemplateMessage(owner, message, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            == MessageBoxResult.Yes;
    }

    private static MessageBoxResult ShowTemplateMessage(Window? owner, string message, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
        => owner is not null && owner.IsLoaded
            ? System.Windows.MessageBox.Show(owner, message, "그룹 템플릿", buttons, image, defaultResult)
            : System.Windows.MessageBox.Show(message, "그룹 템플릿", buttons, image, defaultResult);

    // ---- #26: global item search ----

    private void OpenGlobalItemSearch()
    {
        if (_disposed || _safeMode || !_state.Settings.EnableCyanDeck) return;
        if (_globalSearchWindow is { } existing)
        {
            existing.Activate();
            existing.FocusQuery();
            return;
        }

        var entries = new List<GlobalItemSearchEntry>();
        var portals = new List<PortalSearchSource>();
        foreach (var fence in _state.Fences)
        {
            foreach (var tab in fence.Tabs)
            {
                if (tab.Kind == DeckTabKind.FolderPortal)
                {
                    if (!string.IsNullOrWhiteSpace(tab.PortalPath))
                        portals.Add(new PortalSearchSource(fence.Id, tab.Id, fence.Title, tab.Title, fence.PageIndex, fence.ShowOnAllPages, tab.PortalPath));
                    continue;
                }
                foreach (var item in tab.Items)
                {
                    entries.Add(new GlobalItemSearchEntry(item.DisplayName, item.Path, GlobalItemSearchSource.Collection,
                        fence.Id, tab.Id, item.Id, fence.Title, tab.Title, fence.PageIndex, fence.ShowOnAllPages));
                }
            }
        }
        foreach (var item in _state.LooseDesktopItems)
        {
            entries.Add(new GlobalItemSearchEntry(item.DisplayName, item.Path, GlobalItemSearchSource.Desktop,
                null, null, item.Id, "", "", 0, false));
        }

        var window = new GlobalItemSearchWindow(entries, OpenGlobalSearchResult, RevealGlobalSearchResult);
        _globalSearchWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_globalSearchWindow, window)) _globalSearchWindow = null;
        };
        window.Show();
        window.Activate();
        window.FocusQuery();
        if (portals.Count > 0)
        {
            window.SetPortalsLoading(true);
            _ = LoadPortalSearchEntriesAsync(window, portals);
        }
        DiagnosticsLog.Info($"Item search opened; items={entries.Count}; portals={portals.Count}");
    }

    private async Task LoadPortalSearchEntriesAsync(GlobalItemSearchWindow window, IReadOnlyList<PortalSearchSource> sources)
    {
        List<GlobalItemSearchEntry> found;
        try
        {
            found = await Task.Run(() => EnumeratePortalSearchEntries(sources));
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Warn($"Item search could not read Folder Portals; errorType={ex.GetType().Name}");
            found = [];
        }
        if (!ReferenceEquals(_globalSearchWindow, window)) return;
        window.AddEntries(found);
    }

    // Background thread: only the current folder of each portal, bounded, failures skipped.
    private static List<GlobalItemSearchEntry> EnumeratePortalSearchEntries(IReadOnlyList<PortalSearchSource> sources)
    {
        var result = new List<GlobalItemSearchEntry>();
        foreach (var source in sources)
        {
            try
            {
                if (!Directory.Exists(source.Path)) continue;
                foreach (var path in Directory.EnumerateFileSystemEntries(source.Path).Take(PortalSearchEntryLimit))
                {
                    var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
                    if (string.IsNullOrEmpty(name)) continue;
                    result.Add(new GlobalItemSearchEntry(name, path, GlobalItemSearchSource.FolderPortal,
                        source.FenceId, source.TabId, null, source.GroupTitle, source.TabTitle, source.PageIndex, source.ShowOnAllPages));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            {
                // A disconnected or protected portal folder is simply not searched.
            }
        }
        return result;
    }

    private void OpenGlobalSearchResult(GlobalItemSearchEntry entry)
    {
        try
        {
            if (DesktopSystemItems.IsSupportedVirtualPath(entry.Path))
                DesktopSystemItems.OpenVirtualItem(entry.Path);
            else
                ShortcutLaunchService.Open(entry.Path);
            if (entry.Source == GlobalItemSearchSource.Collection && entry.TabId is Guid tabId && entry.ItemId is Guid itemId)
            {
                var item = _state.Fences.SelectMany(f => f.Tabs).FirstOrDefault(t => t.Id == tabId)?.Items.FirstOrDefault(i => i.Id == itemId);
                if (item is not null && item.OpenCount < long.MaxValue)
                {
                    item.OpenCount++;
                    Persist();
                }
            }
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

    /// <summary>
    /// Shows the found item: switches to its page, selects its tab, opens a rolled-up group and
    /// selects the item. Peek raises the groups above other windows so the result is visible;
    /// Esc, the tray, opening an item or switching to another app ends that Peek.
    /// </summary>
    private void RevealGlobalSearchResult(GlobalItemSearchEntry entry)
    {
        if (_disposed || entry.FenceId is not Guid fenceId || entry.TabId is not Guid tabId) return;
        var window = _windows.FirstOrDefault(w => w.Model.Id == fenceId);
        if (window is null) return;
        var pageChanged = false;
        if (!window.Model.ShowOnAllPages && window.Model.PageIndex != _state.CurrentPage)
        {
            if (_peek) SetPeek(false);
            GoToPage(window.Model.PageIndex);
            pageChanged = true;
        }

        void Reveal()
        {
            if (_disposed || !_windows.Contains(window)) return;
            if (!_peek)
            {
                SetPeek(true);
                if (_peek) WatchRevealStartedPeek();
            }
            window.RevealItem(tabId, entry.Path);
        }

        if (!pageChanged)
        {
            Reveal();
            return;
        }
        // Let the page slide finish before Peek saves and raises the group positions.
        var delay = (_state.Settings.AnimatePageTransitions ? Math.Clamp(_state.Settings.PageTransitionDurationMs, 0, 1200) : 0) + 120;
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(delay) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Reveal();
        };
        timer.Start();
    }

    // A Peek that "위치로 이동" turned on only lasts while CyanDeck stays in front: once another
    // app takes the foreground the groups drop back to the desktop instead of covering it.
    // CyanDeck's own windows, dialogs and tray menu are in this process and keep the Peek.
    private void WatchRevealStartedPeek()
    {
        if (_revealPeekWatchTimer is null)
        {
            _revealPeekWatchTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _revealPeekWatchTimer.Tick += (_, _) => CheckRevealStartedPeek();
        }
        _revealPeekWatchTimer.Stop();
        _revealPeekWatchTimer.Start();
    }

    private void CheckRevealStartedPeek()
    {
        if (_disposed || !_peek)
        {
            // Peek already ended (Esc, tray, a launch): stop watching so a later Peek is left alone.
            _revealPeekWatchTimer?.Stop();
            return;
        }
        if (IsCyanDeckInForeground()) return;
        _revealPeekWatchTimer?.Stop();
        DiagnosticsLog.Info("Peek started by item search ended: another app came to the front");
        SetPeek(false);
    }

    private static bool IsCyanDeckInForeground()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        // No foreground window happens briefly while windows switch; decide on the next tick.
        if (foreground == IntPtr.Zero) return true;
        NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    // ---- #24: end Peek after launching an item ----

    private void OnItemLaunchedEndPeek()
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_disposed || !_peek || !_state.Settings.EndPeekAfterLaunch) return;
            DiagnosticsLog.Info("Peek ended after an item was opened");
            SetPeek(false);
        }));
    }
}

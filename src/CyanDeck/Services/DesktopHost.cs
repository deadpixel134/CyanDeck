using System.Drawing;
using System.Text;
using System.Windows.Interop;
using CyanDeck.Interop;

namespace CyanDeck.Services;

public sealed class DesktopHost
{
    private IntPtr _desktopParent;
    private IntPtr _desktopDefView;
    private bool _lastBandMoveSucceeded;
    private int _lastBandMoveError;
    // Shared desktop-band surface registry: fences and the interactive calendar
    // participate in one Z-order policy, never competing Explorer hosts.
    private readonly HashSet<IntPtr> _desktopBandHandles = new();
    // Track the actual window object as well as HWND. Windows may reuse a
    // destroyed HWND for an unrelated app; that must never be skipped as a sibling.
    private readonly Dictionary<System.Windows.Window, IntPtr> _surfaceHandles = new();
    private readonly Dictionary<IntPtr, DesktopBandZOrderGuard> _bandZOrderGuards = new();

    private IDisposable? AllowExplicitDesktopBandPlacement(IntPtr hwnd)
        => _bandZOrderGuards.TryGetValue(hwnd, out var guard) ? guard.AllowHostPlacement() : null;


    private void RegisterDesktopBandSurface(System.Windows.Window window, IntPtr hwnd)
    {
        if (_surfaceHandles.TryGetValue(window, out var old))
        {
            if (old != hwnd)
            {
                _desktopBandHandles.Remove(old);
                if (_bandZOrderGuards.Remove(old, out var oldGuard)) oldGuard.Dispose();
            }
        }
        else
        {
            window.Closed += OnDesktopBandSurfaceClosed;
        }
        _surfaceHandles[window] = hwnd;
        _desktopBandHandles.Add(hwnd);
        if (!_bandZOrderGuards.ContainsKey(hwnd))
            _bandZOrderGuards.Add(hwnd, new DesktopBandZOrderGuard(hwnd));
    }

    private void OnDesktopBandSurfaceClosed(object? sender, EventArgs e)
    {
        if (sender is System.Windows.Window window) ForgetInteractiveSurface(window);
    }


    /// <summary>
    /// Re-discover the existing Explorer desktop windows without changing Explorer.
    /// The undocumented Progman 0x052C message can rebuild desktop WorkerW windows
    /// and cause wallpaper/taskbar flashing. Do not send it from ordinary startup,
    /// screen-topology recovery, or the calendar's manual repair action.
    /// </summary>
    public void PrepareShell() => RefreshDesktopShell();

    public void Attach(System.Windows.Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        RefreshDesktopShell();

        // FenceWindow uses AllowsTransparency=True, which makes it a layered WPF top-level
        // surface. Reparenting that HWND into Explorer as WS_CHILD is unreliable and can leave
        // the WPF compositor surface invisible even though Window.IsVisible remains true.
        // Keep fences as top-level tool windows and place them in the global Z-order band
        // immediately above the live Explorer desktop host instead.
        RegisterDesktopBandSurface(window, hwnd);
        // WPF ShowInTaskbar=false creates an implicit hidden owner. Never keep
        // such a relationship for a desktop-band group: owned popups cannot
        // reliably remain behind ordinary applications.
        PrepareForTopLevel(hwnd);
        if (!TryEnsureIndependentInteractiveWindow(window, hwnd, out var problem))
        {
            DiagnosticsLog.Warn("Fence desktop attachment skipped: " + problem);
            ForgetInteractiveSurface(window);
            window.Hide(); // fail closed rather than float above other programs
            return;
        }
        MakeToolWindow(hwnd);
        ReassertFenceZOrder(hwnd, frameChanged: true);
    }



    /// <summary>
    /// Attaches an interactive WPF surface to the same desktop-only band as fences.
    /// Fail closed: the caller must keep the surface hidden when Explorer's live
    /// desktop cannot be found or SetWindowPos fails. Never fall back to topmost.
    /// </summary>
    public bool TryAttachInteractiveSurface(System.Windows.Window window)
        => TryAttachInteractiveSurface(window, out _);

    public bool TryAttachInteractiveSurface(System.Windows.Window window, out string problem)
    {
        problem = "";
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        RefreshDesktopShell();
        if (_desktopParent == IntPtr.Zero || !NativeMethods.IsWindow(_desktopParent))
        {
            problem = "Explorer desktop host (Progman/WorkerW) was not found";
            return false;
        }

        RegisterDesktopBandSurface(window, hwnd);
        // For a WS_POPUP top-level window GetParent may return its OWNER, not
        // its parent. SetParent(hwnd, NULL) cannot reliably dissolve that
        // owner relationship. A hidden WPF owner would force the calendar to
        // remain above the owner rather than directly above WorkerW.
        PrepareForTopLevel(hwnd);
        if (!TryEnsureIndependentInteractiveWindow(window, hwnd, out problem))
        {
            ForgetInteractiveSurface(window);
            return false;
        }
        MakeToolWindow(hwnd);
        if (TryReassertFenceZOrder(hwnd, frameChanged: true)) return true;

        var below = NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDNEXT);
        var requestedAnchor = NativeMethods.GetWindow(_desktopParent, NativeMethods.GW_HWNDPREV);
        problem = "Cannot enter desktop Z-order band: "
            + $"window={DescribeWindow(hwnd)}, host={DescribeWindow(_desktopParent)}, "
            + $"anchor={DescribeWindow(requestedAnchor)}, below={DescribeWindow(below)}, "
            + $"SetWindowPos={_lastBandMoveSucceeded}, Win32Error={_lastBandMoveError}";

        ForgetInteractiveSurface(window);
        return false;
    }

    public void ForgetInteractiveSurface(System.Windows.Window window)
    {
        if (_surfaceHandles.Remove(window, out var hwnd))
        {
            _desktopBandHandles.Remove(hwnd);
            if (_bandZOrderGuards.Remove(hwnd, out var guard)) guard.Dispose();
            window.Closed -= OnDesktopBandSurfaceClosed;
        }
    }

    public bool IsInteractiveSurfaceAttached(System.Windows.Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || _desktopParent == IntPtr.Zero ||
            !NativeMethods.IsWindow(_desktopParent) || !NativeMethods.IsWindow(hwnd))
            return false;
        return IsAlreadyInDesktopBand(hwnd);
    }

    /// <summary>
    /// The standalone Recycle Bin is a layered WPF window and cannot reliably
    /// render as a child of Explorer's DefView. Keep it in the same independent,
    /// non-topmost desktop Z-order band as the fences and calendar.
    /// </summary>
    public bool TryEnsureStandaloneRecycleBin(System.Windows.Window window, Rectangle bounds, out string problem)
    {
        problem = "";
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (!IsInteractiveSurfaceAttached(window) && !TryAttachInteractiveSurface(window, out problem))
            return false;
        // Only restore an actually minimized HWND. Ordinary focus changes must
        // never cause a Show or Z-order operation.
        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        if (!MatchesScreenBounds(hwnd, bounds) && !NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                bounds.X, bounds.Y, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height),
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE))
        {
            problem = "Could not position standalone Recycle Bin";
            return false;
        }
        return true;
    }

    public void PositionStandaloneRecycleBin(System.Windows.Window window, Rectangle bounds)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero && !MatchesScreenBounds(hwnd, bounds))
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y,
                Math.Max(1, bounds.Width), Math.Max(1, bounds.Height),
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private static bool MatchesScreenBounds(IntPtr hwnd, Rectangle bounds)
        => NativeMethods.GetWindowRect(hwnd, out var r)
           && r.Left == bounds.X && r.Top == bounds.Y
           && r.Right - r.Left == bounds.Width && r.Bottom - r.Top == bounds.Height;

    public void AttachLooseIcon(System.Windows.Window window, Rectangle screenBounds)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        EnsureDesktopParent();
        EnsureDesktopDefView();
        if (_desktopDefView == IntPtr.Zero)
            throw new InvalidOperationException("Explorer desktop view is unavailable.");

        PrepareForDesktopChild(hwnd);
        NativeMethods.SetParent(hwnd, _desktopDefView);
        MakeToolWindow(hwnd);
        PositionLooseIcon(window, screenBounds);
    }

    public void EnsureLooseIconAttached(System.Windows.Window window, Rectangle screenBounds)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        EnsureDesktopParent();
        EnsureDesktopDefView();
        if (_desktopDefView == IntPtr.Zero) return;
        if (NativeMethods.GetParent(hwnd) != _desktopDefView)
        {
            PrepareForDesktopChild(hwnd);
            NativeMethods.SetParent(hwnd, _desktopDefView);
            MakeToolWindow(hwnd);
        }
        PositionLooseIcon(window, screenBounds);
    }

    public void PositionLooseIcon(System.Windows.Window window, Rectangle screenBounds)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        EnsureDesktopDefView();
        PositionDesktopDefViewChild(hwnd, screenBounds, NativeMethods.HWND_TOP);
    }

    public void AttachDesktopDropSurface(System.Windows.Window window, Rectangle screenBounds)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        EnsureDesktopParent();
        EnsureDesktopDefView();
        if (_desktopDefView == IntPtr.Zero)
            throw new InvalidOperationException("Explorer desktop view is unavailable.");

        PrepareForDesktopChild(hwnd);
        NativeMethods.SetParent(hwnd, _desktopDefView);
        MakeToolWindow(hwnd);
        PositionDesktopDropSurface(hwnd, screenBounds);
    }

    public void EnsureDesktopDropSurfaceAttached(System.Windows.Window window, Rectangle screenBounds)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        EnsureDesktopParent();
        EnsureDesktopDefView();
        if (_desktopDefView == IntPtr.Zero) return;
        if (NativeMethods.GetParent(hwnd) != _desktopDefView)
        {
            PrepareForDesktopChild(hwnd);
            NativeMethods.SetParent(hwnd, _desktopDefView);
            MakeToolWindow(hwnd);
        }
        PositionDesktopDropSurface(hwnd, screenBounds);
    }

    private void PositionDesktopDropSurface(IntPtr hwnd, Rectangle screenBounds)
    {
        PositionDesktopDefViewChild(hwnd, screenBounds, NativeMethods.HWND_BOTTOM);
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_BOTTOM,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    public void AttachTouchSurface(System.Windows.Window window, Rectangle screenBounds)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        EnsureDesktopParent();
        EnsureDesktopDefView();
        if (_desktopDefView == IntPtr.Zero)
            throw new InvalidOperationException("Explorer desktop view is unavailable.");

        PrepareForDesktopChild(hwnd);
        NativeMethods.SetParent(hwnd, _desktopDefView);
        MakeToolWindow(hwnd);
        PositionTouchSurface(hwnd, screenBounds);
    }

    public void EnsureTouchSurfaceAttached(System.Windows.Window window, Rectangle screenBounds)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        EnsureDesktopParent();
        EnsureDesktopDefView();
        if (_desktopDefView == IntPtr.Zero) return;
        if (NativeMethods.GetParent(hwnd) != _desktopDefView)
        {
            PrepareForDesktopChild(hwnd);
            NativeMethods.SetParent(hwnd, _desktopDefView);
            MakeToolWindow(hwnd);
        }
        PositionTouchSurface(hwnd, screenBounds);
    }

    private void PositionTouchSurface(IntPtr hwnd, Rectangle screenBounds)
        => PositionDesktopDefViewChild(hwnd, screenBounds, NativeMethods.HWND_TOP);

    private void PositionDesktopDefViewChild(IntPtr hwnd, Rectangle screenBounds, IntPtr insertAfter)
    {
        if (_desktopDefView == IntPtr.Zero || !NativeMethods.GetWindowRect(_desktopDefView, out var parentRect)) return;
        if (NativeMethods.GetWindowRect(hwnd, out var currentRect)
            && currentRect.Left == screenBounds.Left
            && currentRect.Top == screenBounds.Top
            && currentRect.Right - currentRect.Left == screenBounds.Width
            && currentRect.Bottom - currentRect.Top == screenBounds.Height)
        {
            return;
        }
        var x = screenBounds.Left - parentRect.Left;
        var y = screenBounds.Top - parentRect.Top;
        NativeMethods.SetWindowPos(
            hwnd,
            insertAfter,
            x,
            y,
            Math.Max(1, screenBounds.Width),
            Math.Max(1, screenBounds.Height),
            NativeMethods.SWP_NOACTIVATE);
    }

    public void EnsureAttached(System.Windows.Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Explorer can rebuild/reorder SHELLDLL_DefView, and a WorkerW from an old desktop can remain
        // a valid HWND after the live SHELLDLL_DefView moves elsewhere. Probe the actual DefView + FolderView pair instead
        // of treating a merely-valid cached parent as authoritative.
        RefreshDesktopShell();

        RegisterDesktopBandSurface(window, hwnd);
        PrepareForTopLevel(hwnd);
        if (!TryEnsureIndependentInteractiveWindow(window, hwnd, out var problem))
        {
            DiagnosticsLog.Warn("Fence desktop reattachment skipped: " + problem);
            ForgetInteractiveSurface(window);
            window.Hide();
            return;
        }
        MakeToolWindow(hwnd);
        ReassertFenceZOrder(hwnd, frameChanged: false);
    }

    public void SetPeek(System.Windows.Window window, bool peek)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        RegisterDesktopBandSurface(window, hwnd);
        PrepareForTopLevel(hwnd);
        if (!TryEnsureIndependentInteractiveWindow(window, hwnd, out var problem))
        {
            DiagnosticsLog.Warn("Fence peek placement skipped: " + problem);
            ForgetInteractiveSurface(window);
            return;
        }
        MakeToolWindow(hwnd);

        // Peek is the sole intentional application-overlay mode. The guard
        // permits this explicitly requested move and the matching exit move.
        using var allowPeekPlacement = AllowExplicitDesktopBandPlacement(hwnd);
        if (peek)
        {
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        }
        else
        {
            // Drop TOPMOST first, then put the fence back in the desktop band.
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
            RefreshDesktopShell();
            ReassertFenceZOrder(hwnd, frameChanged: false);
        }
    }

    public void SetNativeDesktopIconsVisible(bool visible)
    {
        var list = FindDesktopListView();
        if (list == IntPtr.Zero) return;
        if (IsWindowVisible(list) == visible) return;
        NativeMethods.ShowWindow(list, visible ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
    }

    public bool TryRestoreNativeDesktopIcons()
    {
        // Unlike the ordinary setting setter, this is an explicit emergency
        // action: report failure when Explorer's real FolderView is unavailable.
        var list = FindDesktopListView();
        if (list == IntPtr.Zero || !NativeMethods.IsWindow(list)) return false;
        if (!NativeMethods.IsWindowVisible(list))
            NativeMethods.ShowWindow(list, NativeMethods.SW_SHOW);
        return NativeMethods.IsWindowVisible(list);
    }

    public bool AreNativeDesktopIconsVisible()
    {
        var list = FindDesktopListView();
        if (list == IntPtr.Zero) return true;
        return IsWindowVisible(list);
    }

    /// <summary>
    /// Read-only, tri-state Explorer FolderView probe. A missing FolderView is
    /// UNKNOWN, not an instruction to show or hide native desktop icons.
    /// </summary>
    public bool TryGetNativeDesktopIconsVisible(out bool visible)
    {
        var list = FindDesktopListView();
        visible = list != IntPtr.Zero && IsWindowVisible(list);
        return list != IntPtr.Zero;
    }

    /// <summary>
    /// Refreshes the cached Explorer desktop HWNDs without changing any CyanDeck window.
    /// Returns true only when Explorer rebuilt/switched the live desktop host.
    /// </summary>
    public bool RefreshDesktopShellIdentity(out bool previousShellWasValid)
    {
        var oldParent = _desktopParent;
        var oldDefView = _desktopDefView;
        previousShellWasValid = oldParent != IntPtr.Zero && oldDefView != IntPtr.Zero &&
                                NativeMethods.IsWindow(oldParent) && NativeMethods.IsWindow(oldDefView);
        RefreshDesktopShell();
        return oldParent != _desktopParent || oldDefView != _desktopDefView;
    }

    /// <summary>
    /// Read-only attachment/style probe for the periodic shell recovery timer. This deliberately
    /// ignores global Z-order so steady-state ticks never fight foreground activation.
    /// </summary>
    public bool NeedsFenceAttachmentRepair(System.Windows.Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        if (_desktopParent == IntPtr.Zero || !NativeMethods.IsWindow(_desktopParent)) return true;
        if (NativeMethods.GetParent(hwnd) != IntPtr.Zero) return true;

        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        if ((style & NativeMethods.WS_CHILD) != 0 || (style & NativeMethods.WS_POPUP) == 0) return true;
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        return (exStyle & NativeMethods.WS_EX_TOOLWINDOW) == 0 || (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;
    }

    public static bool CanLocateDesktopShell() => FindDesktopParent() != IntPtr.Zero && FindDesktopListView() != IntPtr.Zero;

    private void EnsureDesktopParent()
    {
        if (_desktopParent != IntPtr.Zero && NativeMethods.IsWindow(_desktopParent)) return;
        RefreshDesktopShell();
    }

    private void EnsureDesktopDefView()
    {
        if (_desktopDefView != IntPtr.Zero && NativeMethods.IsWindow(_desktopDefView)) return;
        RefreshDesktopShell();
    }

    private void RefreshDesktopShell()
    {
        if (TryLocateDesktopShell(out var parent, out var defView, out _))
        {
            _desktopParent = parent;
            _desktopDefView = defView;
            return;
        }

        _desktopParent = IntPtr.Zero;
        _desktopDefView = IntPtr.Zero;
    }

    private static IntPtr FindDesktopDefView()
        => TryLocateDesktopShell(out _, out var defView, out _) ? defView : IntPtr.Zero;

    private static IntPtr FindDesktopParent()
        => TryLocateDesktopShell(out var parent, out _, out _) ? parent : IntPtr.Zero;

    private static IntPtr FindDesktopListView()
        => TryLocateDesktopShell(out _, out _, out var listView) ? listView : IntPtr.Zero;

    internal static bool IsExplorerDesktopWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !TryLocateDesktopShell(out var parent, out var defView, out var listView))
            return false;

        for (var i = 0; i < 8 && hwnd != IntPtr.Zero; i++)
        {
            if (hwnd == listView || hwnd == defView || hwnd == parent)
                return true;
            hwnd = NativeMethods.GetParent(hwnd);
        }
        return false;
    }

    internal static bool TryLocateDesktopShell(out IntPtr parent, out IntPtr defView, out IntPtr listView)
    {
        var foundParent = IntPtr.Zero;
        var foundDefView = IntPtr.Zero;
        var foundListView = IntPtr.Zero;

        NativeMethods.EnumWindows((top, _) =>
        {
            if (!IsDesktopHostClass(top)) return true;
            var candidateDefView = NativeMethods.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (candidateDefView == IntPtr.Zero) return true;
            var candidateList = NativeMethods.FindWindowEx(candidateDefView, IntPtr.Zero, "SysListView32", "FolderView");
            if (candidateList == IntPtr.Zero)
                candidateList = NativeMethods.FindWindowEx(candidateDefView, IntPtr.Zero, "SysListView32", null);
            if (candidateList == IntPtr.Zero) return true;

            foundParent = top;
            foundDefView = candidateDefView;
            foundListView = candidateList;
            return false;
        }, IntPtr.Zero);

        if (foundDefView == IntPtr.Zero)
        {
            var progman = NativeMethods.FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                var progmanDefView = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (progmanDefView != IntPtr.Zero)
                {
                    var progmanList = NativeMethods.FindWindowEx(progmanDefView, IntPtr.Zero, "SysListView32", "FolderView");
                    if (progmanList == IntPtr.Zero)
                        progmanList = NativeMethods.FindWindowEx(progmanDefView, IntPtr.Zero, "SysListView32", null);
                    if (progmanList != IntPtr.Zero)
                    {
                        foundParent = progman;
                        foundDefView = progmanDefView;
                        foundListView = progmanList;
                    }
                }
            }
        }

        parent = foundParent;
        defView = foundDefView;
        listView = foundListView;
        return defView != IntPtr.Zero && listView != IntPtr.Zero;
    }

    private static bool IsDesktopHostClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString() is "WorkerW" or "Progman";
    }

    private void ReassertFenceZOrder(IntPtr hwnd, bool frameChanged)
    {
        _ = TryReassertFenceZOrder(hwnd, frameChanged);
    }

    private bool TryReassertFenceZOrder(IntPtr hwnd, bool frameChanged)
    {
        using var allowPlacement = AllowExplicitDesktopBandPlacement(hwnd);
        // Fence windows stay top-level so WPF layered/transparency rendering remains valid.
        // Put each fence into a compact block immediately above Explorer's live desktop host in
        // the GLOBAL top-level Z order. GW_HWNDPREV returns the top-level window directly above
        // _desktopParent. Inserting the fence after that predecessor places it above the desktop
        // host while keeping every ordinary application/shell window above CyanDeck.
        var flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
        if (frameChanged) flags |= NativeMethods.SWP_FRAMECHANGED;

        if (_desktopParent == IntPtr.Zero || !NativeMethods.IsWindow(_desktopParent))
        {
            // No Explorer anchor means no safe desktop band. NOTOPMOST is NOT
            // sufficient: it can put the surface above every ordinary app.
            // Fail closed at HWND_BOTTOM until an explicit shell repair.
            _lastBandMoveSucceeded = NativeMethods.SetWindowPos(
                hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0, flags);
            _lastBandMoveError = _lastBandMoveSucceeded ? 0 :
                System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return false;
        }

        if (IsAlreadyInDesktopBand(hwnd))
            return true;

        var predecessor = NativeMethods.GetWindow(_desktopParent, NativeMethods.GW_HWNDPREV);
        if (predecessor == hwnd)
        {
            // Already immediately above Explorer only counts if no unrelated
            // window is between our surface and the desktop host.
            _lastBandMoveSucceeded = IsAlreadyInDesktopBand(hwnd);
            _lastBandMoveError = 0;
            return _lastBandMoveSucceeded;
        }

        // If Explorer is currently at the very top of global Z-order, inserting
        // above it would also cover every ordinary application below. Fail
        // closed instead of falling back to HWND_TOP.
        if (predecessor == IntPtr.Zero)
        {
            _lastBandMoveSucceeded = NativeMethods.SetWindowPos(
                hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0, flags);
            _lastBandMoveError = _lastBandMoveSucceeded ? 0 :
                System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return false;
        }
        var insertAfter = predecessor;
        _lastBandMoveSucceeded = NativeMethods.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, flags);
        // Capture immediately. GetLastWin32Error has no useful meaning after a successful call.
        _lastBandMoveError = _lastBandMoveSucceeded ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        if (!_lastBandMoveSucceeded && frameChanged && _lastBandMoveError == 5)
        {
            // Some Explorer/WPF transitions reject a simultaneous non-client
            // frame recalculation. Retry ONCE without SWP_FRAMECHANGED, with
            // the same non-activating desktop anchor. Never use HWND_TOPMOST.
            _lastBandMoveSucceeded = NativeMethods.SetWindowPos(
                hwnd, insertAfter, 0, 0, 0, 0, flags & ~NativeMethods.SWP_FRAMECHANGED);
            _lastBandMoveError = _lastBandMoveSucceeded ? 0 :
                System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        }
        if (!_lastBandMoveSucceeded && _lastBandMoveError == 5 &&
            IsHarmlessHiddenBandHelper(insertAfter, hwnd))
        {
            // The reported failing machines have invisible GDI+/WinForms tray
            // helper HWNDs mixed immediately above Progman. SetWindowPos can
            // reject insertion after such HWNDs even though they display no UI.
            // Move below the first real preceding window; never target HWND_TOP
            // or HWND_TOPMOST and verify every intervening HWND afterwards.
            var safeAnchor = NativeMethods.GetWindow(insertAfter, NativeMethods.GW_HWNDPREV);
            for (var hops = 0; hops < 12 && safeAnchor != IntPtr.Zero &&
                 (safeAnchor == hwnd || IsHarmlessHiddenBandHelper(safeAnchor, hwnd)); hops++)
                safeAnchor = NativeMethods.GetWindow(safeAnchor, NativeMethods.GW_HWNDPREV);

            if (safeAnchor != IntPtr.Zero && safeAnchor != hwnd && NativeMethods.IsWindow(safeAnchor))
            {
                _lastBandMoveSucceeded = NativeMethods.SetWindowPos(
                    hwnd, safeAnchor, 0, 0, 0, 0,
                    flags & ~NativeMethods.SWP_FRAMECHANGED);
                _lastBandMoveError = _lastBandMoveSucceeded ? 0 :
                    System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (_lastBandMoveSucceeded && IsAlreadyInDesktopBand(hwnd))
                {
                    DiagnosticsLog.Info("Desktop band placement recovered above verified hidden helper windows");
                    return true;
                }
            }
        }
        return _lastBandMoveSucceeded && IsAlreadyInDesktopBand(hwnd);
    }

    private bool IsHarmlessHiddenBandHelper(IntPtr candidate, IntPtr activeSurface)
        => IsHiddenExplorerDesktopAuxiliary(candidate)
            || IsHiddenOwnProcessShellHelper(candidate, activeSurface);

    // No blanket same-PID exemption. The only allowed helper HWND classes are
    // well-known invisible WinForms/GDI+ infrastructure used by CyanDeck.
    // A visible window, owned popup, arbitrary WPF window, or third-party HWND
    // still fails the desktop-band validation and is never crossed silently.
    private static bool IsHiddenOwnProcessShellHelper(IntPtr candidate, IntPtr activeSurface)
    {
        if (candidate == IntPtr.Zero || candidate == activeSurface ||
            !NativeMethods.IsWindow(candidate) || IsWindowVisible(candidate)) return false;
        if (NativeMethods.GetWindow(candidate, NativeMethods.GW_OWNER) != IntPtr.Zero ||
            NativeMethods.GetParent(candidate) != IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(activeSurface, out var surfacePid);
        NativeMethods.GetWindowThreadProcessId(candidate, out var candidatePid);
        if (surfacePid == 0 || surfacePid != candidatePid) return false;
        var className = new StringBuilder(128);
        NativeMethods.GetClassName(candidate, className, className.Capacity);
        var type = className.ToString();
        return type == "GDI+ Hook Window Class" ||
            type.StartsWith("WindowsForms10.Window.0.app.", StringComparison.Ordinal);
    }

    // Strict allow-list; never skip normal Explorer folders, dialogs, taskbar,
    // third-party windows, or any visible helper. Owner PID must be Explorer.
    private bool IsHiddenExplorerDesktopAuxiliary(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || IsWindowVisible(hwnd)) return false;
        var className = new StringBuilder(64);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        if (className.ToString() != "MSCTFIME UI") return false;
        var owner = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);
        if (owner == IntPtr.Zero || !NativeMethods.IsWindow(owner)) return false;
        NativeMethods.GetWindowThreadProcessId(_desktopParent, out var explorerPid);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var helperPid);
        NativeMethods.GetWindowThreadProcessId(owner, out var ownerPid);
        return explorerPid != 0 && helperPid == explorerPid && ownerPid == explorerPid;
    }

    private bool IsAlreadyInDesktopBand(IntPtr hwnd)
    {
        _desktopBandHandles.RemoveWhere(handle => handle == IntPtr.Zero || !NativeMethods.IsWindow(handle));

        // The Fence block must be the first CyanDeck top-level block immediately above the
        // live Explorer desktop host. Merely finding the desktop somewhere below the Fence is not
        // sufficient: an activated/shown WPF window can jump above an ordinary application while
        // the old loose check still considered it healthy.
        //
        // Windows may interleave additional Explorer WorkerW/Progman background hosts
        // between the positioned surface and the SHELLDLL_DefView owner. Requiring
        // *immediate* adjacency to only one HWND incorrectly hides the calendar.
        // Accept ONLY desktop hosts belonging to the SAME Explorer process, plus
        // our own registered desktop-band surfaces. Reject all application/dialog/
        // taskbar windows; do not silently convert the calendar to an overlay.
        NativeMethods.GetWindowThreadProcessId(_desktopParent, out var explorerPid);
        var below = NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDNEXT);
        var visited = new HashSet<IntPtr>();
        for (var hops = 0; below != IntPtr.Zero && hops < 32 && visited.Add(below); hops++)
        {
            if (below == _desktopParent) return true;
            if (_desktopBandHandles.Contains(below))
            {
                below = NativeMethods.GetWindow(below, NativeMethods.GW_HWNDNEXT);
                continue;
            }
            NativeMethods.GetWindowThreadProcessId(below, out var pid);
            if ((pid != 0 && pid == explorerPid && IsDesktopHostClass(below))
                || IsHiddenExplorerDesktopAuxiliary(below)
                || IsHiddenOwnProcessShellHelper(below, hwnd))
            {
                below = NativeMethods.GetWindow(below, NativeMethods.GW_HWNDNEXT);
                continue;
            }
            return false;
        }
        return false;
    }

    private static string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "0x0";
        var className = new StringBuilder(128);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return $"0x{hwnd.ToInt64():X}[class={className}, pid={pid}, visible={IsWindowVisible(hwnd)}, "
            + $"parentOrOwner=0x{NativeMethods.GetParent(hwnd).ToInt64():X}, "
            + $"owner=0x{NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER).ToInt64():X}]";
    }

    /// <summary>
    /// Interactive calendar surfaces must not be OWNED top-level windows.
    /// Windows keeps an owned popup above its owner, even after SetWindowPos.
    /// WPF ShowInTaskbar=false can generate an invisible owner (see v17 HWND trace).
    /// The calendar now uses ShowInTaskbar=true and WS_EX_TOOLWINDOW instead.
    /// Recover defensively if an HWND still has an implicit same-process owner.
    /// Do not change real WPF Window.Owner links or third-party ownership.
    /// </summary>
    private static bool TryEnsureIndependentInteractiveWindow(System.Windows.Window window,
        IntPtr hwnd, out string problem)
    {
        problem = "";
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        if ((style & NativeMethods.WS_CHILD) != 0)
        {
            problem = "Calendar HWND is still a WS_CHILD window";
            return false;
        }

        var owner = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);
        if (owner == IntPtr.Zero) return true;
        if (window.Owner is not null)
        {
            problem = "Calendar has an explicit WPF Owner; cannot safely enter desktop band: "
                + DescribeWindow(owner);
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var calendarPid);
        NativeMethods.GetWindowThreadProcessId(owner, out var ownerPid);
        if (calendarPid == 0 || ownerPid != calendarPid)
        {
            problem = "Calendar owner belongs to a different process: " + DescribeWindow(owner);
            return false;
        }

        // Only edit our own top-level window. GWLP_HWNDPARENT changes an owner
        // (not a child-parent relation); do not call SetParent for this case.
        // A zero result may mean 'previous owner is NULL' or failure, so also
        // verify GW_OWNER after the call before treating the repair as success.
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWLP_HWNDPARENT, IntPtr.Zero);
        var remaining = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);
        if (remaining == IntPtr.Zero) return true;

        problem = "Calendar's hidden WPF owner could not be cleared: "
            + DescribeWindow(remaining);
        return false;
    }

    private static void DetachFenceFromForeignParent(IntPtr hwnd)
    {
        if (NativeMethods.GetParent(hwnd) != IntPtr.Zero)
            NativeMethods.SetParent(hwnd, IntPtr.Zero);
    }

    private static void PrepareForDesktopChild(IntPtr hwnd)
    {
        var current = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        var desired = (current & ~NativeMethods.WS_POPUP) | NativeMethods.WS_CHILD;
        if (desired == current) return;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, new IntPtr(desired));
    }

    private static void PrepareForTopLevel(IntPtr hwnd)
    {
        var current = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        var desired = (current & ~NativeMethods.WS_CHILD) | NativeMethods.WS_POPUP;
        if (desired == current) return;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, new IntPtr(desired));
    }

    private static void MakeToolWindow(IntPtr hwnd)
    {
        var current = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        var desired = (current | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
        if (desired == current) return;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(desired));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}

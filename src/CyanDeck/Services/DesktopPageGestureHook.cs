using System.Runtime.InteropServices;
using System.Windows.Threading;
using CyanDeck.Input;
using CyanDeck.Interop;
using CyanDeck.Models;
using Forms = System.Windows.Forms;

namespace CyanDeck.Services;

/// <summary>
/// Single low-level desktop mouse hook for Desktop Pages plus v7.48 blank-desktop rectangle creation.
/// Creation only observes clicks until the configured drag threshold is crossed; short left/right
/// clicks therefore continue to Explorer unchanged. Canonical Explorer desktop identity is resolved
/// by DesktopHost as WorkerW/Progman -> SHELLDLL_DefView -> SysListView32 (FolderView); class names
/// are never accepted on their own in this hook.
/// </summary>
internal sealed class DesktopPageGestureHook : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.LowLevelMouseProc _proc;
    private DeckSettings _settings;
    private readonly Func<NativeMethods.POINT, int?>? _resolveEdgeDirection;
    private readonly Func<bool>? _canCreateGroups;
    private readonly DesktopCreationGestureGate _creationGate = new();
    private IntPtr _hook;
    private int _hookFaulted;
    private bool _tracking;
    private bool _triggered;
    private int _expectedDirection;
    private NativeMethods.POINT _start;
    private NativeMethods.POINT _creationStart;
    private bool _leftDown;
    private bool _rightDown;
    private bool _middleDown;
    private long _lastWheelTick;
    private bool _altDragSurfaceInteractive;

    internal event Action<int>? PageSwipe;
    internal event Action<DesktopCreationRectangleGesture>? CreationPreviewChanged;
    internal event Action? CreationPreviewCancelled;
    internal event Action<DesktopCreationRectangleGesture>? CreationCompleted;
    internal event Action<bool>? AltDragSurfaceInteractiveChanged;

    internal DesktopPageGestureHook(
        Dispatcher dispatcher,
        DeckSettings settings,
        Func<NativeMethods.POINT, int?>? resolveEdgeDirection = null,
        Func<bool>? canCreateGroups = null)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _resolveEdgeDirection = resolveEdgeDirection;
        _canCreateGroups = canCreateGroups;
        _proc = Callback;
    }

    internal void UpdateSettings(DeckSettings settings) => _settings = settings;

    internal void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = NativeMethods.SetWindowsHookExMouse(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (System.Threading.Volatile.Read(ref _hookFaulted) != 0)
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        try
        {
        if (nCode < 0) return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        var msg = wParam.ToInt32();
        var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
        var suppress = false;

        switch (msg)
        {
            case NativeMethods.WM_LBUTTONDOWN:
                _leftDown = true;
                CancelCreationForSecondPrimaryButton(DesktopCreationMouseButton.Left);
                if (_settings.EnableEdgePageGestures)
                {
                    int? edgeDirection = _resolveEdgeDirection?.Invoke(data.pt) ?? ResolveLegacyVirtualEdgeDirection(data.pt);
                    if (edgeDirection is int resolved && IsCyanDeckSurface(data.pt)) StartTracking(data.pt, resolved);
                }
                if (_settings.EnablePageBothButtonsDrag && _rightDown && IsCyanDeckSurface(data.pt)) StartTracking(data.pt);
                TryBeginCreation(DesktopCreationMouseButton.Left, data.pt);
                UpdateAltDragSurfaceState();
                break;

            case NativeMethods.WM_RBUTTONDOWN:
                _rightDown = true;
                CancelCreationForSecondPrimaryButton(DesktopCreationMouseButton.Right);
                if (_settings.EnablePageBothButtonsDrag && _leftDown && IsCyanDeckSurface(data.pt)) StartTracking(data.pt);
                TryBeginCreation(DesktopCreationMouseButton.Right, data.pt);
                UpdateAltDragSurfaceState();
                break;

            case NativeMethods.WM_MBUTTONDOWN:
                _middleDown = true;
                if (_settings.EnablePageMiddleDrag && IsCyanDeckSurface(data.pt)) StartTracking(data.pt);
                break;

            case NativeMethods.WM_MOUSEMOVE:
                UpdateAltDragSurfaceState();
                if (_tracking && !_triggered)
                {
                    var dx = data.pt.X - _start.X;
                    if (Math.Abs(dx) >= 110)
                    {
                        var direction = dx < 0 ? 1 : -1;
                        if (_expectedDirection == 0 || direction == _expectedDirection)
                        {
                            _triggered = true;
                            RaisePage(direction);
                        }
                        else
                        {
                            StopTracking();
                        }
                    }
                }
                suppress = HandleCreationMove(data.pt);
                break;

            case NativeMethods.WM_MOUSEWHEEL:
                if (_settings.EnablePageAltMouseWheel && IsDown(NativeMethods.VK_MENU) && IsCyanDeckSurface(data.pt))
                {
                    var now = Environment.TickCount64;
                    if (now - _lastWheelTick >= 180)
                    {
                        _lastWheelTick = now;
                        var delta = unchecked((short)((data.mouseData >> 16) & 0xFFFF));
                        if (delta != 0) RaisePage(delta > 0 ? -1 : 1);
                    }
                }
                break;

            case NativeMethods.WM_LBUTTONUP:
                suppress = HandleCreationRelease(DesktopCreationMouseButton.Left, data.pt);
                _leftDown = false;
                UpdateAltDragSurfaceState();
                if (!_middleDown && !_rightDown) StopTracking();
                break;
            case NativeMethods.WM_RBUTTONUP:
                suppress = HandleCreationRelease(DesktopCreationMouseButton.Right, data.pt);
                _rightDown = false;
                UpdateAltDragSurfaceState();
                if (!_middleDown && !_leftDown) StopTracking();
                break;
            case NativeMethods.WM_MBUTTONUP:
                _middleDown = false;
                if (!_leftDown && !_rightDown) StopTracking();
                break;
        }

        return suppress
            ? new IntPtr(1)
            : NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
        catch (Exception)
        {
            if (System.Threading.Interlocked.Exchange(ref _hookFaulted, 1) == 0)
            {
                try { _dispatcher.BeginInvoke(new Action(() => { Dispose(); DiagnosticsLog.Warn("Desktop gesture hook disabled after callback error; input passed to Windows."); })); }
                catch { /* Fail open without blocking any mouse input. */ }
            }
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
    }

    private void UpdateAltDragSurfaceState()
    {
        var interactive = (_leftDown || _rightDown) && IsDown(NativeMethods.VK_MENU);
        if (interactive == _altDragSurfaceInteractive) return;
        _altDragSurfaceInteractive = interactive;
        Dispatch(() => AltDragSurfaceInteractiveChanged?.Invoke(interactive));
    }

    private void TryBeginCreation(DesktopCreationMouseButton button, NativeMethods.POINT point)
    {
        if (_creationGate.IsActive) return;
        var isBlankDesktop = IsBlankDesktopPoint(point);
        var context = new CreationGestureContext(
            HostCanCreateGroups: _canCreateGroups?.Invoke() == true,
            IsBlankDesktop: isBlankDesktop,
            IsDesktopInputEligible: isBlankDesktop,
            LeftButtonDown: _leftDown,
            RightButtonDown: _rightDown,
            ConflictingPageGestureActive: _tracking,
            ModifierGestureActive: IsCreationModifierActive());

        var decision = _creationGate.Begin(
            button,
            new CreationGesturePoint(point.X, point.Y),
            context,
            GetCreationOptions(point));
        if (decision == CreationGestureArmDecision.ArmWithoutConsuming)
            _creationStart = point;
    }

    private bool HandleCreationMove(NativeMethods.POINT point)
    {
        if (!_creationGate.IsActive) return false;
        var wasPromoted = _creationGate.IsPromoted;
        var decision = _creationGate.Move(
            new CreationGesturePoint(point.X, point.Y),
            _leftDown,
            _rightDown,
            GetCreationOptions(point));

        switch (decision)
        {
            case CreationGestureMoveDecision.PromoteAndConsume:
                CancelExplorerPointerMode(_creationStart);
                RaiseCreationPreview(point);
                return true;
            case CreationGestureMoveDecision.ContinueCreationConsumed:
                RaiseCreationPreview(point);
                return true;
            case CreationGestureMoveDecision.CancelToHost:
                if (wasPromoted) RaiseCreationCancelled();
                return false;
            default:
                return false;
        }
    }

    private bool HandleCreationRelease(DesktopCreationMouseButton button, NativeMethods.POINT point)
    {
        if (!_creationGate.IsActive) return false;
        var wasPromoted = _creationGate.IsPromoted;
        var decision = _creationGate.Release(button);
        if (decision == CreationGestureReleaseDecision.CompleteAndConsume)
        {
            RaiseCreationCompleted(point, button);
            return true;
        }
        if (decision == CreationGestureReleaseDecision.CancelToHost && wasPromoted)
            RaiseCreationCancelled();
        return false;
    }

    private void CancelCreationForSecondPrimaryButton(DesktopCreationMouseButton pressedButton)
    {
        if (!_creationGate.IsActive || _creationGate.ArmedButton == pressedButton) return;
        var wasPromoted = _creationGate.IsPromoted;
        _creationGate.CancelToHost();
        if (wasPromoted) RaiseCreationCancelled();
    }

    private CreationMouseButtonOptions GetCreationOptions(NativeMethods.POINT point)
    {
        var mode = _settings.CreateGroupMouseButtonMode switch
        {
            DeckCreationMouseButtonMode.LeftOnly => CreationMouseButtonMode.LeftOnly,
            DeckCreationMouseButtonMode.Either => CreationMouseButtonMode.Either,
            _ => CreationMouseButtonMode.RightOnly,
        };
        var dip = WpfCreationGestureAdapter.WithSystemDragThresholds(mode);
        var scale = ResolveDpiScale(point);
        return new CreationMouseButtonOptions(
            mode,
            dip.HorizontalDragThresholdDip * scale,
            dip.VerticalDragThresholdDip * scale).Normalize();
    }

    private static double ResolveDpiScale(NativeMethods.POINT point)
    {
        try
        {
            var hwnd = NativeMethods.WindowFromPoint(point);
            var dpi = hwnd == IntPtr.Zero ? 96u : NativeMethods.GetDpiForWindow(hwnd);
            return dpi is >= 48 and <= 960 ? dpi / 96d : 1d;
        }
        catch { return 1d; }
    }

    private static bool IsCreationModifierActive()
        => IsDown(NativeMethods.VK_SHIFT)
            || IsDown(NativeMethods.VK_CONTROL)
            || IsDown(NativeMethods.VK_MENU)
            || IsDown(NativeMethods.VK_LWIN)
            || IsDown(NativeMethods.VK_RWIN);

    private static void CancelExplorerPointerMode(NativeMethods.POINT point)
    {
        try
        {
            if (!DesktopHost.TryLocateDesktopShell(out var parent, out var defView, out var listView)) return;
            var hit = NativeMethods.WindowFromPoint(point);
            if (!IsExplorerDesktopHit(hit, parent, defView, listView)) return;

            // Only cancel pointer modes on the canonical Explorer desktop chain. Never walk and
            // message arbitrary third-party HWND parents merely because the drag started nearby.
            foreach (var hwnd in new[] { listView, defView, parent })
            {
                if (hwnd == IntPtr.Zero) continue;
                NativeMethods.SendMessageTimeout(
                    hwnd,
                    NativeMethods.WM_CANCELMODE,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_BLOCK,
                    30,
                    out _);
            }
        }
        catch { }
    }

    private void RaiseCreationPreview(NativeMethods.POINT current)
    {
        var gesture = new DesktopCreationRectangleGesture(
            _creationStart.X, _creationStart.Y, current.X, current.Y, _creationGate.ArmedButton);
        Dispatch(() => CreationPreviewChanged?.Invoke(gesture));
    }

    private void RaiseCreationCompleted(NativeMethods.POINT current, DesktopCreationMouseButton button)
    {
        var gesture = new DesktopCreationRectangleGesture(
            _creationStart.X, _creationStart.Y, current.X, current.Y, button);
        Dispatch(() => CreationCompleted?.Invoke(gesture));
    }

    private void RaiseCreationCancelled() => Dispatch(() => CreationPreviewCancelled?.Invoke());

    private void Dispatch(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(DispatcherPriority.Input, action);
    }

    private void StartTracking(NativeMethods.POINT point, int expectedDirection = 0)
    {
        _tracking = true;
        _triggered = false;
        _expectedDirection = Math.Sign(expectedDirection);
        _start = point;
    }

    internal void CancelTracking()
    {
        StopTracking();
        var wasPromoted = _creationGate.IsPromoted;
        if (_creationGate.CancelToHost() != CreationGestureReleaseDecision.NoActiveGesture && wasPromoted)
            RaiseCreationCancelled();
    }

    private void StopTracking()
    {
        _tracking = false;
        _triggered = false;
        _expectedDirection = 0;
    }

    private static int? ResolveLegacyVirtualEdgeDirection(NativeMethods.POINT point)
    {
        var virtualScreen = Forms.SystemInformation.VirtualScreen;
        if (point.X <= virtualScreen.Left + 3) return -1;
        if (point.X >= virtualScreen.Right - 4) return 1;
        return null;
    }

    private void RaisePage(int direction)
    {
        if (PageSwipe is { } handler) Dispatch(() => handler(direction));
    }

    private static bool IsCyanDeckSurface(NativeMethods.POINT point)
    {
        if (!DesktopHost.TryLocateDesktopShell(out var parent, out var defView, out var listView))
            return IsCyanDeckProcessPoint(point);

        var hwnd = NativeMethods.WindowFromPoint(point);
        for (var i = 0; i < 8 && hwnd != IntPtr.Zero; i++)
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == (uint)Environment.ProcessId) return true;
            if (hwnd == listView) return IsBlankListViewPoint(listView, point);
            if (hwnd == defView || hwnd == parent) return true;
            hwnd = NativeMethods.GetParent(hwnd);
        }
        return false;
    }

    private static bool IsBlankDesktopPoint(NativeMethods.POINT point)
    {
        if (!DesktopHost.TryLocateDesktopShell(out var parent, out var defView, out var listView))
            return false;

        var hwnd = NativeMethods.WindowFromPoint(point);
        for (var i = 0; i < 8 && hwnd != IntPtr.Zero; i++)
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == (uint)Environment.ProcessId) return false;
            if (hwnd == listView) return IsBlankListViewPoint(listView, point);
            if (hwnd == defView || hwnd == parent) return true;
            hwnd = NativeMethods.GetParent(hwnd);
        }
        return false;
    }

    private static bool IsCyanDeckProcessPoint(NativeMethods.POINT point)
    {
        var hwnd = NativeMethods.WindowFromPoint(point);
        for (var i = 0; i < 8 && hwnd != IntPtr.Zero; i++)
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == (uint)Environment.ProcessId) return true;
            hwnd = NativeMethods.GetParent(hwnd);
        }
        return false;
    }

    private static bool IsExplorerDesktopHit(IntPtr hwnd, IntPtr parent, IntPtr defView, IntPtr listView)
    {
        for (var i = 0; i < 8 && hwnd != IntPtr.Zero; i++)
        {
            if (hwnd == listView || hwnd == defView || hwnd == parent) return true;
            hwnd = NativeMethods.GetParent(hwnd);
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LVHITTESTINFO
    {
        public NativeMethods.POINT pt;
        public uint flags;
        public int iItem;
        public int iSubItem;
        public int iGroup;
    }

    private static bool IsBlankListViewPoint(IntPtr hwnd, NativeMethods.POINT screenPoint)
    {
        const int LVM_HITTEST = 0x1012;
        IntPtr process = IntPtr.Zero;
        IntPtr remote = IntPtr.Zero;
        IntPtr local = IntPtr.Zero;
        try
        {
            // LVM_HITTEST is >= WM_USER and carries an LVHITTESTINFO pointer. User32 does not
            // marshal such pointers across processes, so allocate the structure inside Explorer
            // and send only that remote address. Fail closed if Explorer is unavailable or busy.
            if (!DesktopHost.TryLocateDesktopShell(out _, out _, out var desktopListView) || hwnd != desktopListView)
                return false;

            var client = screenPoint;
            if (!ScreenToClient(hwnd, ref client)) return false;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return false;

            process = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_VM_WRITE,
                false,
                pid);
            if (process == IntPtr.Zero) return false;

            var size = Marshal.SizeOf<LVHITTESTINFO>();
            remote = NativeMethods.VirtualAllocEx(
                process,
                IntPtr.Zero,
                new UIntPtr((uint)size),
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);
            if (remote == IntPtr.Zero) return false;

            local = Marshal.AllocHGlobal(size);
            var hit = new LVHITTESTINFO { pt = client, iItem = -1 };
            Marshal.StructureToPtr(hit, local, false);
            if (!NativeMethods.WriteProcessMemory(process, remote, local, new UIntPtr((uint)size), out var written)
                || written.ToUInt64() != (ulong)size)
            {
                return false;
            }

            var sent = NativeMethods.SendMessageTimeout(
                hwnd,
                LVM_HITTEST,
                IntPtr.Zero,
                remote,
                NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_BLOCK,
                40,
                out var result);
            return sent != IntPtr.Zero && result.ToInt64() < 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (local != IntPtr.Zero) Marshal.FreeHGlobal(local);
            if (remote != IntPtr.Zero && process != IntPtr.Zero)
                NativeMethods.VirtualFreeEx(process, remote, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
            if (process != IntPtr.Zero) NativeMethods.CloseHandle(process);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref NativeMethods.POINT lpPoint);

    private static bool IsDown(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        CancelTracking();
        if (_altDragSurfaceInteractive)
        {
            _altDragSurfaceInteractive = false;
            Dispatch(() => AltDragSurfaceInteractiveChanged?.Invoke(false));
        }
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopDeck.Interop;
using DesktopDeck.Services;
using Forms = System.Windows.Forms;

namespace DesktopDeck.FolderPortals;

/// <summary>
/// Owns one low-z-order WPF OLE drop target per monitor. The HWND remains mouse-transparent
/// during ordinary desktop use and becomes hit-testable only while Alt plus a drag button is
/// down, so blank-desktop clicks and context menus continue to belong to Explorer.
/// </summary>
internal sealed class DesktopFolderPortalDropSurfaceHost : IDisposable
{
    private readonly DesktopHost _desktopHost;
    private readonly Func<bool> _masterEnabled;
    private readonly Func<bool> _safeMode;
    private readonly Func<bool> _capabilityAllowed;
    private readonly Func<AltDragFolderPortalCreateRequest, AltDragFolderPortalCreateResult> _createFolderPortal;
    private readonly List<SurfaceRegistration> _surfaces = [];
    private bool _enabled;
    private bool _interactive;
    private bool _disposed;

    internal DesktopFolderPortalDropSurfaceHost(
        DesktopHost desktopHost,
        Func<bool> masterEnabled,
        Func<bool> safeMode,
        Func<bool> capabilityAllowed,
        Func<AltDragFolderPortalCreateRequest, AltDragFolderPortalCreateResult> createFolderPortal)
    {
        _desktopHost = desktopHost ?? throw new ArgumentNullException(nameof(desktopHost));
        _masterEnabled = masterEnabled ?? throw new ArgumentNullException(nameof(masterEnabled));
        _safeMode = safeMode ?? throw new ArgumentNullException(nameof(safeMode));
        _capabilityAllowed = capabilityAllowed ?? throw new ArgumentNullException(nameof(capabilityAllowed));
        _createFolderPortal = createFolderPortal ?? throw new ArgumentNullException(nameof(createFolderPortal));
    }

    internal void Refresh(bool enabled)
    {
        if (_disposed) return;
        _enabled = enabled;
        if (!enabled) _interactive = false;

        if (!enabled)
        {
            CloseSurfaces();
            return;
        }

        var screens = Forms.Screen.AllScreens
            .OrderBy(screen => screen.Bounds.X)
            .ThenBy(screen => screen.Bounds.Y)
            .ToArray();

        if (MatchesCurrentScreens(screens))
        {
            EnsureAttached();
            return;
        }

        CloseSurfaces();
        foreach (var screen in screens)
        {
            var bounds = screen.Bounds;
            var window = new DesktopFolderPortalDropWindow(screen.DeviceName);
            var adapter = new SurfaceHostAdapter(this, window);
            var bridge = new WpfAltDragFolderPortalBridge(window, adapter);

            try
            {
                window.Show();
                _desktopHost.AttachDesktopDropSurface(window, bounds);
                window.SetInteractive(_interactive);
                _surfaces.Add(new SurfaceRegistration(screen.DeviceName, bounds, window, bridge));
            }
            catch (Exception ex)
            {
                bridge.Dispose();
                try { window.Close(); } catch { }
                DiagnosticsLog.Error("Failed to attach desktop Folder Portal drop surface", ex);
            }
        }

        DiagnosticsLog.Info($"Desktop Folder Portal Alt-drop surfaces: enabled={enabled}; monitors={_surfaces.Count}");
    }

    internal void EnsureAttached()
    {
        if (_disposed || !_enabled) return;
        foreach (var surface in _surfaces.ToArray())
        {
            try
            {
                _desktopHost.EnsureDesktopDropSurfaceAttached(surface.Window, surface.Bounds);
                surface.Window.SetInteractive(_interactive);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("Failed to reattach desktop Folder Portal drop surface", ex);
            }
        }
    }

    private bool MatchesCurrentScreens(IReadOnlyList<Forms.Screen> screens)
    {
        if (_surfaces.Count != screens.Count) return false;
        for (var i = 0; i < screens.Count; i++)
        {
            var existing = _surfaces[i];
            var screen = screens[i];
            if (!string.Equals(existing.DeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                existing.Bounds != screen.Bounds)
            {
                return false;
            }
        }
        return true;
    }

    internal void SetInteractive(bool interactive)
    {
        if (_disposed) return;
        var next = _enabled && interactive;
        if (_interactive == next) return;
        _interactive = next;
        foreach (var surface in _surfaces.ToArray())
        {
            try
            {
                surface.Window.SetInteractive(next);
                if (!next) surface.Window.SetDropPreview(false);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("Failed to update desktop Folder Portal drop-surface input state", ex);
            }
        }
    }

    private void SetPreview(DesktopFolderPortalDropWindow window, bool active)
    {
        if (_disposed || !_enabled) active = false;
        window.SetDropPreview(active);
    }

    private AltDragFolderPortalCreateResult CreateFolderPortal(in AltDragFolderPortalCreateRequest request)
    {
        if (_disposed || !_enabled || !_masterEnabled() || _safeMode() || !_capabilityAllowed())
            return AltDragFolderPortalCreateResult.Rejected;
        return _createFolderPortal(request);
    }

    private void CloseSurfaces()
    {
        foreach (var surface in _surfaces)
        {
            try { surface.Bridge.Dispose(); } catch { }
            try { surface.Window.Close(); } catch { }
        }
        _surfaces.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        CloseSurfaces();
    }

    private sealed class SurfaceHostAdapter : IAltDragFolderPortalHost
    {
        private readonly DesktopFolderPortalDropSurfaceHost _owner;
        private readonly DesktopFolderPortalDropWindow _window;

        internal SurfaceHostAdapter(DesktopFolderPortalDropSurfaceHost owner, DesktopFolderPortalDropWindow window)
        {
            _owner = owner;
            _window = window;
        }

        public bool MasterEnabled => _owner._enabled && _owner._masterEnabled();
        public bool IsSafeMode => _owner._safeMode();
        public bool CanCreateFromDesktopAltDrop => _owner._capabilityAllowed();

        public bool IsBlankDesktopTarget(DependencyObject? originalSource)
        {
            // This adapter is attached only to the dedicated low-z-order blank-desktop surface.
            // Fence, loose-icon, and tab HWNDs remain above it and therefore own their own drops.
            return originalSource is null || ReferenceEquals(originalSource, _window) ||
                   IsDescendantOf(originalSource, _window);
        }

        public void SetAltDragFolderPortalPreview(bool active) => _owner.SetPreview(_window, active);

        public AltDragFolderPortalCreateResult CreateFolderPortal(in AltDragFolderPortalCreateRequest request) =>
            _owner.CreateFolderPortal(request);

        private static bool IsDescendantOf(DependencyObject source, DependencyObject root)
        {
            DependencyObject? current = source;
            for (var i = 0; i < 16 && current is not null; i++)
            {
                if (ReferenceEquals(current, root)) return true;
                current = current is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return false;
        }
    }

    private sealed record SurfaceRegistration(
        string DeviceName,
        Rectangle Bounds,
        DesktopFolderPortalDropWindow Window,
        WpfAltDragFolderPortalBridge Bridge);
}

internal sealed class DesktopFolderPortalDropWindow : Window
{
    private readonly Border _previewBorder;
    private readonly Border _previewBadge;
    private HwndSource? _source;
    private bool _interactive;

    internal DesktopFolderPortalDropWindow(string monitorName)
    {
        Title = $"DesktopDeck Folder Portal Drop Surface - {monitorName}";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsEnabled = false;
        AllowsTransparency = true;
        AllowDrop = true;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0));
        Width = 1;
        Height = 1;
        Left = -32000;
        Top = -32000;

        _previewBorder = new Border
        {
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(210, 122, 162, 255)),
            BorderThickness = new Thickness(2),
            Background = System.Windows.Media.Brushes.Transparent,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _previewBadge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(18),
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 24, 27, 33)),
            Child = new TextBlock
            {
                Text = "놓아서 Folder Portal 만들기",
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 12,
            }
        };

        var grid = new Grid { IsHitTestVisible = true };
        grid.Children.Add(_previewBorder);
        grid.Children.Add(_previewBadge);
        _previewBadge.Visibility = Visibility.Collapsed;
        Content = grid;

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    internal void SetDropPreview(bool active)
    {
        var visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _previewBorder.Visibility = visibility;
        _previewBadge.Visibility = visibility;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WindowMessageHook);
        NativeMethods.EnableWindow(hwnd, _interactive);
    }

    internal void SetInteractive(bool interactive)
    {
        _interactive = interactive;
        IsEnabled = interactive;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) NativeMethods.EnableWindow(hwnd, interactive);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_NCHITTEST)
        {
            handled = true;
            return _interactive && IsAltDragButtonDown() ? new IntPtr(NativeMethods.HTCLIENT) : new IntPtr(NativeMethods.HTTRANSPARENT);
        }

        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }

        return IntPtr.Zero;
    }

    private static bool IsAltDragButtonDown()
    {
        var alt = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
        if (!alt) return false;
        var left = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        var right = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RBUTTON) & 0x8000) != 0;
        return left || right;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_source is not null)
        {
            _source.RemoveHook(WindowMessageHook);
            _source = null;
        }
    }
}

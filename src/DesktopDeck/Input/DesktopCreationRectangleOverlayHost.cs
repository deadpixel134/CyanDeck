using System;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopDeck.Interop;
using DesktopDeck.Services;
using Forms = System.Windows.Forms;

namespace DesktopDeck.Input;

/// <summary>
/// Mouse-transparent desktop child used only to visualize a promoted blank-desktop creation drag.
/// It never owns the pointer; the existing DesktopPageGestureHook remains the single mouse hook.
/// </summary>
internal sealed class DesktopCreationRectangleOverlayHost : IDisposable
{
    private readonly DesktopHost _desktopHost;
    private DesktopCreationRectanglePreviewWindow? _window;
    private Rectangle _bounds;
    private string _deviceName = string.Empty;
    private bool _disposed;

    internal DesktopCreationRectangleOverlayHost(DesktopHost desktopHost)
        => _desktopHost = desktopHost ?? throw new ArgumentNullException(nameof(desktopHost));

    internal void Show(in DesktopCreationRectangleGesture gesture)
    {
        if (_disposed) return;
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(gesture.StartScreenX, gesture.StartScreenY));
        if (_window is null || !string.Equals(_deviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase) || _bounds != screen.Bounds)
        {
            CloseWindow();
            _deviceName = screen.DeviceName;
            _bounds = screen.Bounds;
            _window = new DesktopCreationRectanglePreviewWindow(_deviceName, _bounds);
            _window.Show();
            _desktopHost.AttachTouchSurface(_window, _bounds);
        }
        else
        {
            _desktopHost.EnsureTouchSurfaceAttached(_window, _bounds);
        }
        _window.UpdateGesture(gesture);
    }

    internal void Hide()
    {
        if (_disposed) return;
        _window?.HideGesture();
    }

    internal void EnsureAttached()
    {
        if (_disposed || _window is null) return;
        _desktopHost.EnsureTouchSurfaceAttached(_window, _bounds);
    }

    private void CloseWindow()
    {
        if (_window is null) return;
        try { _window.Close(); } catch { }
        _window = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseWindow();
    }
}

internal sealed class DesktopCreationRectanglePreviewWindow : Window
{
    private readonly Rectangle _screenBounds;
    private readonly Canvas _canvas;
    private readonly Border _outline;
    private readonly Border _badge;
    private HwndSource? _source;

    internal DesktopCreationRectanglePreviewWindow(string monitorName, Rectangle screenBounds)
    {
        _screenBounds = screenBounds;
        Title = $"DesktopDeck Creation Preview - {monitorName}";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Width = 1;
        Height = 1;
        Left = -32000;
        Top = -32000;
        IsHitTestVisible = false;

        _outline = new Border
        {
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(235, 114, 157, 255)),
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(34, 114, 157, 255)),
            CornerRadius = new CornerRadius(4),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _badge = new Border
        {
            Padding = new Thickness(9, 5, 9, 5),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(226, 22, 25, 31)),
            Child = new TextBlock
            {
                Text = "놓아서 생성 메뉴 열기",
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 12,
            },
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _canvas = new Canvas { Background = System.Windows.Media.Brushes.Transparent, IsHitTestVisible = false };
        _canvas.Children.Add(_outline);
        _canvas.Children.Add(_badge);
        Content = _canvas;

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    internal void UpdateGesture(in DesktopCreationRectangleGesture gesture)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var sx = Math.Max(0d, dpi.DpiScaleX);
        var sy = Math.Max(0d, dpi.DpiScaleY);
        if (sx <= 0) sx = 1d;
        if (sy <= 0) sy = 1d;
        var maxX = _screenBounds.Width / sx;
        var maxY = _screenBounds.Height / sy;
        var x1 = Math.Clamp((gesture.StartScreenX - _screenBounds.Left) / sx, 0d, maxX);
        var y1 = Math.Clamp((gesture.StartScreenY - _screenBounds.Top) / sy, 0d, maxY);
        var x2 = Math.Clamp((gesture.EndScreenX - _screenBounds.Left) / sx, 0d, maxX);
        var y2 = Math.Clamp((gesture.EndScreenY - _screenBounds.Top) / sy, 0d, maxY);
        var left = Math.Min(x1, x2);
        var top = Math.Min(y1, y2);
        var width = Math.Max(1d, Math.Abs(x2 - x1));
        var height = Math.Max(1d, Math.Abs(y2 - y1));

        Canvas.SetLeft(_outline, left);
        Canvas.SetTop(_outline, top);
        _outline.Width = width;
        _outline.Height = height;
        _outline.Visibility = Visibility.Visible;

        Canvas.SetLeft(_badge, Math.Clamp(left + 8d, 0d, Math.Max(0d, maxX - 170d)));
        Canvas.SetTop(_badge, Math.Clamp(top + 8d, 0d, Math.Max(0d, maxY - 34d)));
        _badge.Visibility = Visibility.Visible;
    }

    internal void HideGesture()
    {
        _outline.Visibility = Visibility.Collapsed;
        _badge.Visibility = Visibility.Collapsed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_NCHITTEST)
        {
            handled = true;
            return new IntPtr(NativeMethods.HTTRANSPARENT);
        }
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_source is null) return;
        _source.RemoveHook(WindowMessageHook);
        _source = null;
    }
}

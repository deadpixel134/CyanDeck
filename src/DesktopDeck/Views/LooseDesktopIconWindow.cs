using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DesktopDeck.Interop;
using DesktopDeck.Appearance;
using DesktopDeck.Layout;
using DesktopDeck.Models;
using DesktopDeck.Services;
using Forms = System.Windows.Forms;

namespace DesktopDeck.Views;

internal sealed class LooseDesktopIconWindow : Window
{
    private readonly Border _selectionBorder;
    private readonly Image _image;
    private readonly TextBlock _label;
    private readonly Action<LooseDesktopIconWindow, Point> _contextMenuPresenter;
    private readonly Func<LooseIconOpacityOptions> _getOpacityOptions;
    private System.Drawing.Point _lastCursor;
    private bool _dragCandidate;
    private bool _dragging;
    private HwndSource? _source;
    private readonly WpfShortcutBadgeBridge _shortcutBadgeBridge;

    internal LooseDesktopIconWindow(
        DeckItem item,
        Action<LooseDesktopIconWindow, Point> contextMenuPresenter,
        Func<ShortcutBadgeOptions> getShortcutBadgeOptions,
        Func<LooseIconOpacityOptions> getOpacityOptions)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        _contextMenuPresenter = contextMenuPresenter ?? throw new ArgumentNullException(nameof(contextMenuPresenter));
        ArgumentNullException.ThrowIfNull(getShortcutBadgeOptions);
        _getOpacityOptions = getOpacityOptions ?? throw new ArgumentNullException(nameof(getOpacityOptions));

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        // A hidden WPF owner can prevent a top-level desktop-band system icon from
        // entering Explorer's desktop Z-order band (observed in Calendar v17-v19).
        // The shell tool-window style is applied by DesktopHost before showing it.
        ShowInTaskbar = DesktopSystemItems.IsRecycleBin(item);
        ShowActivated = false;
        Focusable = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        var isRecycleBin = DesktopSystemItems.IsRecycleBin(item);
        // An HWND clips its children regardless of ClipToBounds. Reserve a
        // generously sized native rectangle for the Recycle Bin, including
        // caption, selection background, drop shadow and high-DPI rounding.
        Width = isRecycleBin ? LooseDesktopIconLayoutProfile.RecycleBinWidthDip : LooseDesktopIconLayoutProfile.ItemWidthDip;
        Height = isRecycleBin ? LooseDesktopIconLayoutProfile.RecycleBinHeightDip : LooseDesktopIconLayoutProfile.ItemHeightDip;
        Left = -32000;
        Top = -32000;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        // Keep the Explorer-class bitmap scale. Growing the source image would
        // only magnify any existing bitmap-edge crop; grow the *surroundings*.
        var iconDisplaySize = isRecycleBin ? LooseDesktopIconLayoutProfile.RecycleBinVisualDip : 48d;
        _image = new Image
        {
            Width = iconDisplaySize,
            Height = iconDisplaySize,
            Margin = isRecycleBin ? new Thickness(10, 9, 10, 7) : new Thickness(0),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        _label = new TextBlock
        {
            Text = item.DisplayName,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11.5,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = isRecycleBin ? 44 : 34,
            Margin = isRecycleBin ? new Thickness(6, 7, 6, 0) : new Thickness(2, 4, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.95 },
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = false,
            IsHitTestVisible = false,
        };
        stack.Children.Add(_image);
        stack.Children.Add(_label);

        _selectionBorder = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = isRecycleBin ? new Thickness(13, 12, 13, 12) : new Thickness(4, 5, 4, 3),
            ClipToBounds = false,
            Child = stack,
            ToolTip = item.Path,
        };
        Content = new System.Windows.Documents.AdornerDecorator { Child = _selectionBorder };
        _shortcutBadgeBridge = new WpfShortcutBadgeBridge(
            _image,
            () => new ShortcutBadgeContext
            {
                ItemPath = Item.Path,
                IsDesktopBackedItem = true,
                IsItemInteractive = IsVisible,
            },
            getShortcutBadgeOptions,
            _ => { });

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
        PreviewMouseLeftButtonUp += OnMouseLeftButtonUp;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseRightButtonUp += OnMouseRightButtonUp;
        PreviewTouchDown += OnTouchDown;
        PreviewTouchUp += OnTouchUp;
        TouchLeave += OnTouchLeave;
        LostTouchCapture += OnLostTouchCapture;
        MouseEnter += (_, _) => UpdateSelectionVisual();
        MouseLeave += (_, _) => UpdateSelectionVisual();
        // Apply idle appearance before the HWND is first shown to avoid an
        // initial fully-opaque frame on the standalone Recycle Bin.
        UpdateSelectionVisual();
    }

    internal DeckItem Item { get; private set; }
    internal bool IsSelected { get; private set; }

    internal event Action<LooseDesktopIconWindow, bool>? SelectionRequested;
    internal event Action<LooseDesktopIconWindow>? OpenRequested;
    internal event Action<LooseDesktopIconWindow, int, int>? DragDeltaRequested;
    internal event Action<LooseDesktopIconWindow>? DragCompleted;
    internal event Action<int, bool>? TouchContactChanged;

    internal void UpdateItem(DeckItem item)
    {
        Item = item;
        _label.Text = item.DisplayName;
        _selectionBorder.ToolTip = item.Path;
        UpdateSelectionVisual();
        try { _shortcutBadgeBridge.Refresh(); } catch { }
    }

    internal void SetIcon(ImageSource image)
    {
        _image.Source = image;
        try { _shortcutBadgeBridge.Refresh(); } catch { }
    }

    internal void RefreshShortcutBadge()
    {
        try { _shortcutBadgeBridge.Refresh(); } catch { }
    }

    internal void RefreshAppearance() => UpdateSelectionVisual();

    internal void SetSelected(bool selected)
    {
        IsSelected = selected;
        UpdateSelectionVisual();
    }

    internal double GetDpiScale()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var dpi = hwnd == IntPtr.Zero ? 96u : NativeMethods.GetDpiForWindow(hwnd);
        return dpi is >= 48 and <= 960 ? dpi / 96d : 1d;
    }

    internal void CancelPointerDragForTouchFlick()
    {
        _dragCandidate = false;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        UpdateSelectionVisual();
    }

    internal System.Drawing.Size GetDesiredPhysicalSize()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var dpi = hwnd == IntPtr.Zero ? 96u : NativeMethods.GetDpiForWindow(hwnd);
        var scale = dpi is >= 48 and <= 960 ? dpi / 96d : 1d;
        return new System.Drawing.Size(
            Math.Max(64, (int)Math.Ceiling(Width * scale)),
            Math.Max(72, (int)Math.Ceiling(Height * scale)));
    }

    private void UpdateSelectionVisual()
    {
        var isRecycleBin = DesktopSystemItems.IsRecycleBin(Item);
        // The Recycle Bin cannot become the active WPF window: it deliberately uses
        // WS_EX_NOACTIVATE. Use pointer interaction instead of Window.IsActive.
        var interacting = IsMouseOver || _dragCandidate || _dragging;
        // A selected standalone Recycle Bin must not look perpetually focused
        // after the pointer has left. Other loose icons retain their selection UX.
        var showSelection = IsSelected && (!isRecycleBin || interacting);
        if (showSelection)
        {
            _selectionBorder.Background = new SolidColorBrush(Color.FromArgb(82, 72, 132, 214));
            _selectionBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(210, 116, 174, 255));
        }
        else if (IsMouseOver)
        {
            _selectionBorder.Background = new SolidColorBrush(Color.FromArgb(42, 255, 255, 255));
            _selectionBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(75, 255, 255, 255));
        }
        else
        {
            _selectionBorder.Background = Brushes.Transparent;
            _selectionBorder.BorderBrush = Brushes.Transparent;
        }

        // Fade the *WPF content*, not the top-level HWND: native window reordering
        // risks disturbing Explorer's desktop band and causing focus flicker.
        // The optional standalone policy is independent from group Chameleon.
        // When disabled for the Recycle Bin, retain its original Chameleon fade.
        var targetOpacity = _getOpacityOptions().Resolve(isRecycleBin, interacting);
        if (Math.Abs(_selectionBorder.Opacity - targetOpacity) > 0.001)
            _selectionBorder.Opacity = targetOpacity;
    }

    private void OnTouchDown(object? sender, TouchEventArgs e)
        => TouchContactChanged?.Invoke(e.TouchDevice.Id, true);

    private void OnTouchUp(object? sender, TouchEventArgs e)
        => TouchContactChanged?.Invoke(e.TouchDevice.Id, false);

    private void OnTouchLeave(object? sender, TouchEventArgs e)
    {
        if (!ReferenceEquals(e.TouchDevice.Captured, this))
            TouchContactChanged?.Invoke(e.TouchDevice.Id, false);
    }

    private void OnLostTouchCapture(object? sender, TouchEventArgs e)
        => TouchContactChanged?.Invoke(e.TouchDevice.Id, false);

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        SelectionRequested?.Invoke(this, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        if (e.ClickCount >= 2)
        {
            _dragCandidate = false;
            _dragging = false;
            ReleaseMouseCapture();
            OpenRequested?.Invoke(this);
            e.Handled = true;
            return;
        }

        _lastCursor = Forms.Cursor.Position;
        _dragCandidate = true;
        _dragging = false;
        CaptureMouse();
        UpdateSelectionVisual();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || e.LeftButton != MouseButtonState.Pressed) return;
        var now = Forms.Cursor.Position;
        var dx = now.X - _lastCursor.X;
        var dy = now.Y - _lastCursor.Y;
        if (!_dragging)
        {
            var start = SystemParameters.MinimumHorizontalDragDistance;
            if (Math.Abs(dx) < start && Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragging = true;
        }

        if (dx == 0 && dy == 0) return;
        _lastCursor = now;
        DragDeltaRequested?.Invoke(this, dx, dy);
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var wasDragging = _dragging;
        _dragCandidate = false;
        _dragging = false;
        ReleaseMouseCapture();
        UpdateSelectionVisual();
        if (wasDragging) DragCompleted?.Invoke(this);
        e.Handled = true;
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsSelected) SelectionRequested?.Invoke(this, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        var localPoint = e.GetPosition(this);
        var screenPoint = PointToScreen(localPoint);
        _contextMenuPresenter(this, screenPoint);
        e.Handled = true;
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
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try { _shortcutBadgeBridge.Dispose(); } catch { }
        if (_source is null) return;
        _source.RemoveHook(WindowMessageHook);
        _source = null;
    }
}

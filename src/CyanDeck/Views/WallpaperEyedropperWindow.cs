using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CyanDeck.Views;

/// <summary>
/// One-shot, user-initiated wallpaper color capture surface. It never polls or installs hooks.
/// The physical cursor position is queried only in direct response to the click that commits the pick.
/// </summary>
internal sealed class WallpaperEyedropperWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    internal (int X, int Y)? PhysicalScreenPoint { get; private set; }

    internal WallpaperEyedropperWindow()
    {
        Title = "바탕화면 색상 선택";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Cursor = Cursors.Cross;
        Topmost = true;
        ShowInTaskbar = false;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = Math.Max(1d, SystemParameters.VirtualScreenWidth);
        Height = Math.Max(1d, SystemParameters.VirtualScreenHeight);
        Focusable = true;

        PreviewMouseLeftButtonDown += CommitPick;
        PreviewMouseRightButtonDown += CancelPick;
        PreviewKeyDown += HandleKeyDown;
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            Keyboard.Focus(this);
        };
    }

    private void CommitPick(object sender, MouseButtonEventArgs e)
    {
        if (!GetCursorPos(out NativePoint point))
        {
            return;
        }

        PhysicalScreenPoint = (point.X, point.Y);
        DialogResult = true;
        Close();
        e.Handled = true;
    }

    private void CancelPick(object sender, MouseButtonEventArgs e)
    {
        DialogResult = false;
        Close();
        e.Handled = true;
    }

    private void HandleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        DialogResult = false;
        Close();
        e.Handled = true;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace CyanDeck.Views;

public static class TextPrompt
{
    // Returns the trimmed text, or null when the user cancels. The dialog opens over the
    // window it was requested from (a desktop group may sit on another monitor), grows with
    // its label, and keeps 확인 disabled while the box is empty instead of silently cancelling.
    public static string? Show(Window owner, string title, string label, string initial)
    {
        var box = new TextBox
        {
            Text = initial,
            Margin = new Thickness(0, 6, 0, 4),
            Padding = new Thickness(8, 6, 8, 6),
            MinWidth = 300,
            Background = new SolidColorBrush(Color.FromRgb(38, 41, 48)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(75, 82, 96)),
            CaretBrush = Brushes.White
        };
        var hint = new TextBlock
        {
            Text = "내용을 입력해야 확인할 수 있습니다.",
            Foreground = new SolidColorBrush(Color.FromRgb(160, 170, 184)),
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var ok = new Button { Content = "확인", MinWidth = 78, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), IsDefault = true };
        var cancel = new Button { Content = "취소", MinWidth = 78, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(210, 216, 226)),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(box);
        panel.Children.Add(hint);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(27, 29, 34)),
            Content = panel
        };
        PlaceOverOwner(window, owner);

        void Validate()
        {
            var empty = string.IsNullOrWhiteSpace(box.Text);
            ok.IsEnabled = !empty;
            hint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        }
        box.TextChanged += (_, _) => Validate();
        Validate();

        string? result = null;
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return;
            result = box.Text.Trim();
            window.DialogResult = true;
        };
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        window.ShowDialog();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static void PlaceOverOwner(Window prompt, Window? owner)
    {
        try
        {
            if (owner is null || !owner.IsLoaded || !owner.IsVisible || owner.ActualWidth <= 0) return;
            var source = PresentationSource.FromVisual(owner);
            if (source?.CompositionTarget is null) return;
            var fromDevice = source.CompositionTarget.TransformFromDevice;
            var centerDevice = owner.PointToScreen(new Point(owner.ActualWidth / 2, owner.ActualHeight / 2));
            var center = fromDevice.Transform(centerDevice);
            var work = Forms.Screen.FromPoint(new System.Drawing.Point((int)centerDevice.X, (int)centerDevice.Y)).WorkingArea;
            var workTopLeft = fromDevice.Transform(new Point(work.Left, work.Top));
            var workBottomRight = fromDevice.Transform(new Point(work.Right, work.Bottom));
            prompt.WindowStartupLocation = WindowStartupLocation.Manual;
            prompt.Left = center.X - prompt.Width / 2;
            prompt.Top = center.Y - 90;
            // Final placement once the height is known, kept inside the owner's monitor.
            prompt.Loaded += (_, _) =>
            {
                var maxLeft = Math.Max(workTopLeft.X, workBottomRight.X - prompt.ActualWidth);
                var maxTop = Math.Max(workTopLeft.Y, workBottomRight.Y - prompt.ActualHeight);
                prompt.Left = Math.Clamp(center.X - prompt.ActualWidth / 2, workTopLeft.X, maxLeft);
                prompt.Top = Math.Clamp(center.Y - prompt.ActualHeight / 2, workTopLeft.Y, maxTop);
            };
        }
        catch (InvalidOperationException)
        {
            // Owner not connected to a presentation source: keep the centred default.
        }
    }
}

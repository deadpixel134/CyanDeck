using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace CyanDeck.Peek;

/// <summary>
/// A deliberately off-screen top-level window whose taskbar button acts as a Peek command surface.
/// It does not pin itself. If the user pins the button, AppUserModel relaunch metadata routes the
/// pinned item back through CyanDeck's --peek-toggle command.
/// </summary>
public sealed class WpfPeekTaskbarButtonWindow : Window
{
    public const string PeekAppUserModelId = "CyanDeck.Peek";
    private const int WmSysCommand = 0x0112;
    private const int ScRestore = 0xF120;

    private readonly Action _togglePeek;
    private readonly PeekTaskbarActivationGate _activationGate = new();
    private readonly int _debounceMilliseconds;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _ready;

    public WpfPeekTaskbarButtonWindow(
        string executablePath,
        PeekTaskbarButtonIcon icon,
        int debounceMilliseconds,
        Action togglePeek)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Executable path is required.", nameof(executablePath));
        }

        _togglePeek = togglePeek ?? throw new ArgumentNullException(nameof(togglePeek));
        _debounceMilliseconds = Math.Clamp(debounceMilliseconds, 150, 1000);

        Title = "CyanDeck Peek";
        Width = 240;
        Height = 96;
        Left = -32000;
        Top = -32000;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        ShowActivated = false;
        WindowState = WindowState.Minimized;
        Background = SystemColors.WindowBrush;
        Foreground = SystemColors.WindowTextBrush;
        Content = BuildPreviewContent();

        var imageSource = PeekTaskbarButtonIconResolver.TryLoadImageSource(icon);
        if (imageSource is not null)
        {
            Icon = imageSource;
        }

        SourceInitialized += (_, _) => InitializeNativeIdentity(executablePath, icon);
        Closed += (_, _) => DetachHook();
    }

    private UIElement BuildPreviewContent()
    {
        return new Border
        {
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "CyanDeck Peek",
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 16,
                    },
                    new TextBlock
                    {
                        Text = "Click the taskbar button to show or hide your groups.",
                        Margin = new Thickness(0, 6, 0, 0),
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        };
    }

    private void InitializeNativeIdentity(string executablePath, PeekTaskbarButtonIcon icon)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        try
        {
            var relaunchCommand = $"\"{Path.GetFullPath(executablePath)}\" --peek-toggle";
            WindowsAppUserModelPropertyStore.ApplyPeekIdentity(
                _hwnd,
                PeekAppUserModelId,
                relaunchCommand,
                "CyanDeck Peek",
                icon.RelaunchIconResource);
        }
        catch
        {
            // The live taskbar button remains usable even if Shell metadata cannot be committed.
            // Integration should log this as a non-fatal diagnostic without exposing custom paths.
        }

        _ready = true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_ready)
        {
            return IntPtr.Zero;
        }

        // Keep the window permanently minimized so Windows owns a stable taskbar button.
        // Do not treat WM_ACTIVATE as a Peek click: right-clicking a taskbar button can
        // activate its window while Explorer is opening the context menu, which used to
        // toggle Peek and immediately dismiss that menu. A left-click/keyboard restore of
        // a minimized taskbar button arrives as SC_RESTORE, which is the only command we
        // consume here.
        if (msg == WmSysCommand)
        {
            var command = unchecked((int)(long)wParam) & 0xFFF0;
            if (command == ScRestore)
            {
                TriggerFromTaskbar();
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void TriggerFromTaskbar()
    {
        if (!_activationGate.TryAccept(DateTimeOffset.UtcNow, _debounceMilliseconds))
        {
            return;
        }

        _togglePeek();

        // SC_RESTORE is handled, so the command surface must stay minimized instead of
        // becoming an off-screen foreground window. The taskbar button therefore remains
        // present in both normal mode and Peek mode without stealing focus.
        if (WindowState != WindowState.Minimized)
        {
            WindowState = WindowState.Minimized;
        }
    }

    public void EnsureTaskbarSurfaceVisible()
    {
        if (!_ready) return;
        if (!IsVisible) Show();
        if (WindowState != WindowState.Minimized) WindowState = WindowState.Minimized;
    }

    private void DetachHook()
    {
        _ready = false;
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

}

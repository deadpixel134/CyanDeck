using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using CyanDeck.QuickHide;
using CyanDeck.Views;
using Forms = System.Windows.Forms;

namespace CyanDeck.Services;

/// <summary>
/// v8.0.43 input discoverability: double-click quick-hide choices and one-time hint,
/// single-instance desktop commands for Windows shortcut keys (no keyboard hook),
/// the "단축키·제스처 안내" entry point and the one-time "what's new" notice.
/// </summary>
public sealed partial class AppController
{
    // Runs on the dedicated mouse-hook thread: plain settings/flag reads only.
    private bool IsDesktopDoubleClickQuickHideActive() =>
        InputHintPolicy.ShouldHandleDesktopDoubleClick(
            _state.Settings.EnableDesktopDoubleClickQuickHide,
            _state.Settings.EnableQuickHide,
            _quickHidden);

    private void HandleDesktopDoubleClick()
    {
        if (_disposed || !IsDesktopDoubleClickQuickHideActive()) return;
        ToggleQuickHide(QuickHideInvocationSource.DesktopDoubleClick);
        ShowQuickHideFirstUseHintIfNeeded(fromTray: false);
    }

    private void HandleTrayDoubleClick()
    {
        var command = InputHintPolicy.ResolveTrayDoubleClick(
            _state.Settings.TrayDoubleClickAction,
            _safeMode,
            _state.Settings.EnableCyanDeck,
            _quickHidden);
        switch (command)
        {
            case TrayDoubleClickCommand.OpenSettings:
                QueueOpenSettingsWindow();
                break;
            case TrayDoubleClickCommand.ToggleQuickHide:
                ToggleQuickHide(QuickHideInvocationSource.Tray);
                ShowQuickHideFirstUseHintIfNeeded(fromTray: true);
                break;
        }
    }

    /// <summary>
    /// Explains once what a double-click just did and how to undo or turn it off.
    /// Clicking the notification opens Settings.
    /// </summary>
    private void ShowQuickHideFirstUseHintIfNeeded(bool fromTray)
    {
        if (!InputHintPolicy.ShouldShowQuickHideHint(
                fromDoubleClick: true,
                becameHidden: _quickHidden,
                alreadyShown: _state.Settings.QuickHideDoubleClickHintShown))
            return;

        _state.Settings.QuickHideDoubleClickHintShown = true;
        Persist();
        DiagnosticsLog.Info($"Quick-hide first-use hint shown (tray={fromTray})");
        _trayController?.ShowBalloonTip(
            9000,
            InputHintPolicy.QuickHideHintTitle,
            InputHintPolicy.QuickHideHintMessage(fromTray),
            Forms.ToolTipIcon.Info,
            QueueOpenSettingsWindow);
    }

    /// <summary>
    /// Commands sent by a second CyanDeck.exe launch (--quick-hide-toggle, --desktop-display-toggle,
    /// --page-next, --page-previous, --peek-toggle). Windows shortcut keys on the Start menu
    /// shortcuts start that launch, so no system-wide keyboard input is monitored.
    /// </summary>
    internal void HandleExternalDesktopCommand(ExternalDesktopCommand command)
    {
        if (_disposed) return;
        if (command == ExternalDesktopCommand.PeekToggle)
        {
            TogglePeekFromExternalCommand();
            return;
        }

        var name = ExternalDesktopCommandCatalog.Get(command).PipeCommand;
        if (_safeMode)
        {
            // Same as --peek-toggle: show where CyanDeck is instead of silently ignoring the key.
            OpenSettingsWindow();
            return;
        }
        if (!_state.Settings.EnableCyanDeck || !_desktopRuntimeStarted)
        {
            DiagnosticsLog.Info($"External desktop command ignored because desktop groups are not running: {name}");
            return;
        }

        DiagnosticsLog.Info($"External desktop command: {name}");
        switch (command)
        {
            case ExternalDesktopCommand.QuickHideToggle:
                ToggleQuickHide(QuickHideInvocationSource.Hotkey);
                break;
            case ExternalDesktopCommand.DesktopDisplayToggle:
                ToggleDesktopDisplayFromCommand();
                break;
            case ExternalDesktopCommand.NextPage:
            case ExternalDesktopCommand.PreviousPage:
                // The former "page keyboard navigation" preference now gates these commands.
                if (!_state.Settings.EnablePageKeyboardNavigation) return;
                SwitchPage(command == ExternalDesktopCommand.NextPage ? 1 : -1);
                break;
        }
    }

    private void ToggleDesktopDisplayFromCommand()
    {
        if (!_state.Settings.EnableDesktopDisplay) return;
        if (_peek) SetPeek(false);
        _desktopDisplay.Toggle();
        UpdateWindowVisibility();
        RefreshDesktopTouchPageHost();
        RefreshDesktopFolderPortalDropHost();
        RefreshLooseDesktopIconHost();
        UpdateTrayText();
        RebuildTrayMenu();
    }

    // Tray → 도구 및 도움말 → 단축키·제스처 안내… (routed by its stable tag).
    private void AddInputGuideTrayItem(Forms.ContextMenuStrip menu)
    {
        var item = menu.Items.Add("단축키·제스처 안내…", null, (_, _) => QueueTrayDialog(OpenInputGuide));
        item.Tag = "help.input-guide";
        AddReleaseNotesTrayItem(menu);
    }

    // Tray → 도구 및 도움말 → 새 기능 보기: the release notes stay reachable even when the
    // one-time notice was hidden by Focus Assist or replaced by another notification.
    private void AddReleaseNotesTrayItem(Forms.ContextMenuStrip menu)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is null) return;
        var normalized = InputHintPolicy.NormalizeVersion(version);
        var url = InputHintPolicy.ReleaseNotesUrl(version);
        var item = menu.Items.Add($"새 기능 보기 (v{normalized})…", null, (_, _) => OpenReleaseNotes(url));
        item.Tag = "help.release-notes";
    }

    private void OpenInputGuide() => InputGuideWindow.ShowGuide(null);

    private void ScheduleWhatsNewNotice() =>
        _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShowWhatsNewNoticeIfNeeded));

    /// <summary>
    /// Once per newer version (normally right after an update): a notification that opens
    /// the release notes when clicked. Fresh installs are stamped by the first-run setup.
    /// </summary>
    private void ShowWhatsNewNoticeIfNeeded()
    {
        if (_disposed || _safeMode || _compatibilityReadOnly || _trayController is null) return;
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is null) return;
        if (!InputHintPolicy.ShouldShowWhatsNew(_state.Settings.LastSeenFeatureNoticeVersion, version)) return;

        var normalized = InputHintPolicy.NormalizeVersion(version);
        _state.Settings.LastSeenFeatureNoticeVersion = normalized;
        Persist();
        var url = InputHintPolicy.ReleaseNotesUrl(version);
        DiagnosticsLog.Info($"What's-new notice shown for v{normalized}");
        // Calendar-only mode has no guide item (the guide is about desktop groups), so point
        // only to the release notes item that both tray menus have.
        var message = _state.Settings.EnableCyanDeck
            ? "새 기능과 변경 사항을 보려면 이 알림을 누르세요. 나중에는 트레이 → 도구 및 도움말 → 새 기능 보기에서, 조작 방법은 단축키·제스처 안내에서 볼 수 있습니다."
            : "새 기능과 변경 사항을 보려면 이 알림을 누르세요. 나중에는 트레이 → 도구 및 도움말 → 새 기능 보기에서 볼 수 있습니다.";
        _trayController.ShowBalloonTip(
            9000,
            $"CyanDeck {normalized} 업데이트",
            message,
            Forms.ToolTipIcon.Info,
            () => OpenReleaseNotes(url));
    }

    private static void OpenReleaseNotes(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Release notes could not be opened", ex);
        }
    }
}

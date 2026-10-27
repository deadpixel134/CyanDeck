using CyanDeck.Models;

namespace CyanDeck.Services;

// Pure, UI-free policies for v8.0.44 input discoverability: external single-instance
// commands (usable from Windows shortcut keys without any keyboard hook), first-use
// hints, the tray double-click choice, the "what's new" notice and the mouse-hook
// watchdog. Compiled into tests/FenceVisibilityHarness.

/// <summary>Commands another CyanDeck.exe launch can send to the running instance.</summary>
internal enum ExternalDesktopCommand
{
    PeekToggle,
    QuickHideToggle,
    DesktopDisplayToggle,
    NextPage,
    PreviousPage
}

internal sealed record ExternalDesktopCommandInfo(
    ExternalDesktopCommand Command,
    string Argument,
    string PipeCommand,
    string ShortcutFileName,
    string Description);

internal static class ExternalDesktopCommandCatalog
{
    // Start menu folder for the optional command shortcuts. ASCII so uninstall.ps1
    // (Windows PowerShell 5.1, no BOM) can name it without a code-page dependency.
    internal const string ShortcutFolderName = "CyanDeck Shortcuts";

    internal static IReadOnlyList<ExternalDesktopCommandInfo> All { get; } = new[]
    {
        new ExternalDesktopCommandInfo(ExternalDesktopCommand.PeekToggle,
            "--peek-toggle", "peek-toggle", "CyanDeck Peek 전환.lnk", "Peek: 창 위로 그룹 보기 / 끝내기"),
        new ExternalDesktopCommandInfo(ExternalDesktopCommand.QuickHideToggle,
            "--quick-hide-toggle", "quick-hide-toggle", "CyanDeck 빠른 숨김 전환.lnk", "빠른 숨김 켜기 / 끄기"),
        new ExternalDesktopCommandInfo(ExternalDesktopCommand.DesktopDisplayToggle,
            "--desktop-display-toggle", "desktop-display-toggle", "CyanDeck 바탕화면 보기 전환.lnk", "바탕화면 보기 (모든 창 최소화 / 되돌리기)"),
        new ExternalDesktopCommandInfo(ExternalDesktopCommand.NextPage,
            "--page-next", "page-next", "CyanDeck 다음 페이지.lnk", "다음 바탕화면 페이지"),
        new ExternalDesktopCommandInfo(ExternalDesktopCommand.PreviousPage,
            "--page-previous", "page-previous", "CyanDeck 이전 페이지.lnk", "이전 바탕화면 페이지"),
    };

    internal static ExternalDesktopCommandInfo Get(ExternalDesktopCommand command) =>
        All.First(info => info.Command == command);

    internal static bool TryParseArguments(IEnumerable<string>? args, out ExternalDesktopCommand command)
    {
        if (args is not null)
        {
            foreach (var arg in args)
            {
                var match = All.FirstOrDefault(info => string.Equals(info.Argument, arg, StringComparison.OrdinalIgnoreCase));
                if (match is null) continue;
                command = match.Command;
                return true;
            }
        }
        command = default;
        return false;
    }

    internal static bool TryParsePipeCommand(string? text, out ExternalDesktopCommand command)
    {
        var match = string.IsNullOrWhiteSpace(text)
            ? null
            : All.FirstOrDefault(info => string.Equals(info.PipeCommand, text.Trim(), StringComparison.OrdinalIgnoreCase));
        command = match?.Command ?? default;
        return match is not null;
    }
}

internal enum TrayDoubleClickCommand
{
    None,
    ToggleQuickHide,
    OpenSettings
}

internal static class InputHintPolicy
{
    internal const string QuickHideHintTitle = "CyanDeck 빠른 숨김";

    /// <summary>
    /// Safe mode and calendar-only mode keep opening Settings. While quick-hidden, "None"
    /// still restores the desktop so a double-click can never leave it stuck hidden.
    /// </summary>
    internal static TrayDoubleClickCommand ResolveTrayDoubleClick(
        DeckTrayDoubleClickAction action, bool safeMode, bool deckEnabled, bool quickHidden)
    {
        if (safeMode || !deckEnabled) return TrayDoubleClickCommand.OpenSettings;
        return action switch
        {
            DeckTrayDoubleClickAction.OpenSettings => TrayDoubleClickCommand.OpenSettings,
            DeckTrayDoubleClickAction.None => quickHidden ? TrayDoubleClickCommand.ToggleQuickHide : TrayDoubleClickCommand.None,
            _ => TrayDoubleClickCommand.ToggleQuickHide,
        };
    }

    /// <summary>
    /// Blank-desktop double-click toggles quick hide when its own switch is on. While the desktop
    /// is quick-hidden it always restores, so turning the switch off can never strand it hidden.
    /// </summary>
    internal static bool ShouldHandleDesktopDoubleClick(bool doubleClickEnabled, bool quickHideEnabled, bool quickHidden) =>
        quickHidden || (quickHideEnabled && doubleClickEnabled);

    /// <summary>The one-time hint is shown only when a double-click actually hid the desktop.</summary>
    internal static bool ShouldShowQuickHideHint(bool fromDoubleClick, bool becameHidden, bool alreadyShown) =>
        fromDoubleClick && becameHidden && !alreadyShown;

    internal static string QuickHideHintMessage(bool fromTray) => fromTray
        ? "트레이 아이콘을 더블클릭해 그룹과 아이콘을 숨겼습니다. 다시 더블클릭하면 다시 표시됩니다. 이 알림을 누르면 설정에서 더블클릭 동작을 바꿀 수 있습니다."
        : "빈 바탕화면을 더블클릭해 그룹과 아이콘을 숨겼습니다. 다시 더블클릭하면 다시 표시됩니다. 이 알림을 누르면 설정에서 끌 수 있습니다.";

    internal static string NormalizeVersion(Version version) =>
        string.Join(".", version.Major, version.Minor, Math.Max(0, version.Build));

    /// <summary>
    /// Shows the "what's new" notice once per newer version. A missing value means the
    /// settings predate v8.0.44 (an update); fresh installs are stamped by the first-run setup.
    /// </summary>
    internal static bool ShouldShowWhatsNew(string? lastSeenVersion, Version current)
    {
        if (string.IsNullOrWhiteSpace(lastSeenVersion)) return true;
        if (!Version.TryParse(lastSeenVersion.Trim(), out var last)) return true;
        var normalizedCurrent = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        var normalizedLast = new Version(last.Major, last.Minor, Math.Max(0, last.Build));
        return normalizedCurrent > normalizedLast;
    }

    internal static string ReleaseNotesUrl(Version current) =>
        "https://github.com/deadpixel134/CyanDeck/releases/tag/v" + NormalizeVersion(current);
}

/// <summary>One row of the in-app "단축키·제스처 안내" window.</summary>
internal sealed record InputGuideRow(string Gesture, string Description);

internal sealed record InputGuideSection(string Title, IReadOnlyList<InputGuideRow> Rows);

internal static class InputGuideContent
{
    internal static IReadOnlyList<InputGuideSection> Sections { get; } = new[]
    {
        new InputGuideSection("바탕화면", new[]
        {
            new InputGuideRow("빈 바탕화면에 마우스로 사각형 그리기", "새 그룹 또는 Folder Portal 만들기 (사용할 버튼은 설정에서 선택)"),
            new InputGuideRow("탐색기의 폴더를 Alt를 누른 채 빈 바탕화면에 놓기", "그 폴더의 Folder Portal 바로 만들기"),
            new InputGuideRow("빈 바탕화면 더블클릭", "빠른 숨김 켜기 / 끄기 (설정에서 끌 수 있음)"),
            new InputGuideRow("트레이 아이콘 더블클릭", "빠른 숨김 켜기 / 끄기 (설정에서 동작 변경)"),
        }),
        new InputGuideSection("바탕화면 페이지 이동 (설정에서 켜고 끔)", new[]
        {
            new InputGuideRow("화면 왼쪽·오른쪽 끝을 누른 채 안쪽으로 끌기", "이전 / 다음 페이지"),
            new InputGuideRow("바탕화면에서 Alt + 마우스 휠", "이전 / 다음 페이지"),
            new InputGuideRow("가운데 버튼 또는 양쪽 버튼을 누른 채 좌우로 끌기", "이전 / 다음 페이지"),
            new InputGuideRow("터치: 두 손가락으로 좌우로 밀기", "이전 / 다음 페이지"),
        }),
        new InputGuideSection("그룹 안", new[]
        {
            new InputGuideRow("Ctrl+C / Ctrl+X / Ctrl+V", "파일 복사 · 잘라내기 · 붙여넣기 (오른쪽 클릭 메뉴에도 있음)"),
            new InputGuideRow("F2", "선택한 항목 이름 바꾸기"),
            new InputGuideRow("Ctrl+F", "그룹 안에서 검색"),
            new InputGuideRow("Ctrl+Tab / Ctrl+1~9", "다음 탭 / 해당 번호의 탭으로 이동"),
            new InputGuideRow("Ctrl+마우스 휠", "아이콘 크기 바꾸기"),
            new InputGuideRow("Ctrl+A · Enter · Delete", "모두 선택 · 열기 · 삭제(확인 후)"),
            new InputGuideRow("Alt+← / Alt+→ / Alt+↑", "폴더 포털: 뒤로 · 앞으로 · 상위 폴더"),
        }),
        new InputGuideSection("키보드 단축키 (선택)", new[]
        {
            new InputGuideRow("시작 메뉴 바로 가기 + Windows '바로 가기 키'",
                "CyanDeck은 키 입력을 감시하지 않습니다. 아래 버튼으로 바로 가기를 만든 뒤 속성에서 '바로 가기 키'를 지정하면 Peek·빠른 숨김·바탕화면 보기·페이지 이동을 키보드로 실행할 수 있습니다."),
        }),
    };
}

internal readonly record struct MouseHookWatchdogDecision(int Strikes, bool Reinstall);

/// <summary>
/// Decides when a WH_MOUSE_LL hook that Windows silently removed must be reinstalled.
/// Evidence is "the cursor moved during a watchdog interval but the hook saw no callback";
/// two consecutive intervals are required and reinstalls are rate-limited, so a false
/// positive (cursor moved by SetCursorPos, secure desktop) only costs a cheap re-hook.
/// </summary>
internal static class MouseHookWatchdogPolicy
{
    internal const int StrikesBeforeReinstall = 2;
    internal const long MinimumReinstallIntervalMs = 15000;

    internal static MouseHookWatchdogDecision Evaluate(
        bool cursorMoved, bool callbacksObserved, int strikes, long msSinceLastReinstall, bool hasHookHandle)
    {
        var intervalElapsed = msSinceLastReinstall >= MinimumReinstallIntervalMs;
        // Installation failed earlier: retry, rate-limited.
        if (!hasHookHandle) return new MouseHookWatchdogDecision(0, intervalElapsed);
        if (callbacksObserved) return new MouseHookWatchdogDecision(0, false);
        if (!cursorMoved) return new MouseHookWatchdogDecision(strikes, false);
        var next = strikes + 1;
        if (next < StrikesBeforeReinstall) return new MouseHookWatchdogDecision(next, false);
        return intervalElapsed
            ? new MouseHookWatchdogDecision(0, true)
            : new MouseHookWatchdogDecision(next, false);
    }
}

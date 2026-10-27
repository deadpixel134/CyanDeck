#!/usr/bin/env python3
"""v8.0.43: input discoverability and reliability (quick-hide double-click hint and
choices, keyboard-free command shortcuts, dedicated mouse-hook thread with watchdog,
Peek icon / hot-corner backend, shortcut and gesture guide, README paths).
Non-destructive static checks; hooks, shortcuts and notifications are verified on Windows by hand."""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
def read(name): return (root / name).read_text(encoding="utf-8-sig")

hook_thread = read("src/CyanDeck/Services/LowLevelMouseHookThread.cs")
double_click = read("src/CyanDeck/Services/DesktopDoubleClickHook.cs")
gesture = read("src/CyanDeck/Services/DesktopPageGestureHook.cs")
policy = read("src/CyanDeck/Services/InputCommandPolicy.cs")
shortcuts = read("src/CyanDeck/Services/CommandShortcutService.cs")
hints = read("src/CyanDeck/Services/AppController.InputHints.cs")
app = read("src/CyanDeck/Services/AppController.cs")
startup = read("src/CyanDeck/App.xaml.cs")
tray = read("src/CyanDeck/Services/TrayIconController.cs")
routing = read("src/CyanDeck/Services/TrayMenuRoutingPolicy.cs")
models = read("src/CyanDeck/Models/Models.cs")
tray_action = read("src/CyanDeck/Models/DeckTrayDoubleClickAction.cs")
audit = read("src/CyanDeck/Services/SettingsAuditService.cs")
diag = read("src/CyanDeck/Services/DiagnosticBundleService.cs")
adapter = read("src/CyanDeck/Services/CyanDeckManagedDeploymentAdapter.cs")
resolver = read("src/CyanDeck/Peek/PeekTaskbarButtonIconResolver.cs")
guide = read("src/CyanDeck/Views/InputGuideWindow.cs")
wizard = read("src/CyanDeck/Views/FirstRunWizardWindow.cs")
fence = read("src/CyanDeck/Views/FenceWindow.xaml.cs")
group_menu = read("src/CyanDeck/Views/FenceWindow.ContextMenu.cs")
clipboard_menu = read("src/CyanDeck/Views/FenceWindow.ClipboardMenu.cs")
uninstall = read("uninstall.ps1")
readme = read("README.md")
harness = read("tests/FenceVisibilityHarness/Program.cs")
harness_project = read("tests/FenceVisibilityHarness/FenceVisibilityHarness.csproj")
schema = read("src/CyanDeck/Services/StateSchema.cs")

all_sources = "\n".join(p.read_text(encoding="utf-8-sig") for p in (root / "src").rglob("*.cs"))
peek_surfaces = app.split("private void StartPeekCommandSurfaces()", 1)[1].split("private void RefreshPeekCommandSurfaces()", 1)[0]
begin_creation = gesture.split("private void TryBeginCreation(", 1)[1].split("private bool HandleCreationMove(", 1)[0]

checks = {
    # #30 mouse hooks: dedicated thread, minimal callback work, watchdog reinstall.
    "hooks run on a dedicated thread with its own message loop":
        "var thread = new Thread(Run)" in hook_thread and
        "GetMessage(out var msg, IntPtr.Zero, 0, 0)" in hook_thread and
        "PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);" in hook_thread,
    "watchdog timer reinstalls hooks Windows silently removed":
        "SetTimer(IntPtr.Zero, UIntPtr.Zero, WatchdogIntervalMs, IntPtr.Zero)" in hook_thread and
        "MouseHookWatchdogPolicy.Evaluate(" in hook_thread and
        "client.ReinstallHookOnHookThread();" in hook_thread and
        "internal const int StrikesBeforeReinstall = 2;" in policy and
        "internal const long MinimumReinstallIntervalMs = 15000;" in policy,
    "both hooks install on the shared thread and register with the watchdog":
        all("LowLevelMouseHookThread.TryGetShared(out var thread)" in s and "thread.Register(this);" in s and
            "ILowLevelMouseHookClient.ReinstallHookOnHookThread()" in s and "thread.Invoke(InstallHook)" in s
            for s in (double_click, gesture)),
    "faulted hooks still fail open and are disposed off the native callback":
        all("PostDisposal(() => { Dispose(); DiagnosticsLog.Warn(" in s and
            "CallNextHookEx(_hook, nCode, wParam, lParam)" in s for s in (double_click, gesture)),
    "UI-thread cancel and dispose are marshalled to the hook thread":
        "thread.Post(CancelTrackingCore);" in gesture and "thread.Invoke(DisposeOnHookThread)" in gesture and
        "thread.Invoke(UninstallHook)" in double_click,
    "hook callbacks use the cached desktop HWND chain":
        "DesktopShellHandleCache.TryLocate(" in double_click and "DesktopShellHandleCache.TryLocate(" in gesture and
        "DesktopHost.TryLocateDesktopShell(" not in double_click and "DesktopHost.TryLocateDesktopShell(" not in gesture and
        "private const long MaximumAgeMs = 1500;" in hook_thread,
    "creation checks button mode and host state before the Explorer hit test":
        "if (!CreationMouseButtonPolicy.Allows(ResolveCreationMode(), button) || _canCreateGroups?.Invoke() != true)" in begin_creation and
        begin_creation.index("CreationMouseButtonPolicy.Allows(ResolveCreationMode(), button)") <
        begin_creation.index("var isBlankDesktop = IsBlankDesktopPoint(point);"),
    "double-click hook skips its hit test while the command is off":
        "if (_isEnabled is not null && !_isEnabled())" in double_click,
    "no keyboard hook or global hotkey anywhere":
        "WH_KEYBOARD_LL" not in all_sources and "RegisterHotKey" not in all_sources,

    # #6 quick-hide double-click: hint, choices, tray state.
    "double-click settings are additive with old behavior as default":
        "public bool EnableDesktopDoubleClickQuickHide { get; set; } = true;" in models and
        "public DeckTrayDoubleClickAction TrayDoubleClickAction { get; set; } = DeckTrayDoubleClickAction.QuickHide;" in models and
        "public bool QuickHideDoubleClickHintShown { get; set; }" in models and
        "public enum DeckTrayDoubleClickAction { QuickHide, OpenSettings, None }" in tray_action,
    "desktop and tray double-click route through the new handlers":
        "new DesktopDoubleClickHook(_dispatcher, IsDesktopDoubleClickQuickHideActive)" in app and
        "_desktopDoubleClick.DoubleClicked += HandleDesktopDoubleClick;" in app and
        "            HandleTrayDoubleClick," in app and
        "InputHintPolicy.ResolveTrayDoubleClick(" in hints,
    "first double-click hide shows a one-time hint that opens Settings":
        "ShowQuickHideFirstUseHintIfNeeded(fromTray: false);" in hints and
        "ShowQuickHideFirstUseHintIfNeeded(fromTray: true);" in hints and
        "_state.Settings.QuickHideDoubleClickHintShown = true;" in hints and
        "QueueOpenSettingsWindow);" in hints and
        "다시 더블클릭하면 다시 표시됩니다." in policy,
    "turning double-click off never strands the desktop hidden":
        "quickHidden || (quickHideEnabled && doubleClickEnabled);" in policy and
        "DeckTrayDoubleClickAction.None => quickHidden ? TrayDoubleClickCommand.ToggleQuickHide : TrayDoubleClickCommand.None," in policy,
    "tray icon is dimmed while quick-hidden and balloons can be clicked":
        "_trayController.SetQuickHiddenAppearance(_quickHidden);" in app and
        "public void SetQuickHiddenAppearance(bool hidden)" in tray and
        "_notify.BalloonTipClicked +=" in tray and "DestroyIcon(handle);" in tray,
    "getting-started guide exposes the double-click switch":
        'Check("빈 바탕화면 더블클릭으로 그룹과 아이콘 빠른 숨김", _settings.EnableDesktopDoubleClickQuickHide,' in wizard,
    "notice bookkeeping is excluded from the settings audit":
        "nameof(DeckSettings.QuickHideDoubleClickHintShown)," in audit and
        "nameof(DeckSettings.LastSeenFeatureNoticeVersion)" in audit,

    # #28 keyboard-free commands and shortcut creation.
    "new single-instance commands are cataloged next to --peek-toggle":
        all(x in policy for x in ('"--peek-toggle", "peek-toggle"', '"--quick-hide-toggle", "quick-hide-toggle"',
                                  '"--desktop-display-toggle", "desktop-display-toggle"',
                                  '"--page-next", "page-next"', '"--page-previous", "page-previous"')),
    "second launch forwards the command and the running app handles it":
        "ExternalDesktopCommandCatalog.TryParseArguments(e.Args, out var parsedDesktopCommand)" in startup and
        ': peekToggle ? "peek-toggle" : desktopCommand ?? "activate");' in startup and
        "_controller.HandleExternalDesktopCommand(desktopCommand);" in startup and
        "ToggleQuickHide(QuickHideInvocationSource.Hotkey);" in hints and
        "ToggleDesktopDisplayFromCommand();" in hints and
        "if (!_state.Settings.EnablePageKeyboardNavigation) return;" in hints,
    "first-run wizard ordering is unchanged":
        startup.index("wizard.ShowDialog()") < startup.index("_controller = new AppController("),
    "Start menu command shortcuts keep the user's assigned key":
        "Environment.SpecialFolder.Programs" in shortcuts and "persist.Load(path, 0);" in shortcuts and
        "link.SetArguments(info.Argument);" in shortcuts and "persist.Save(path, true);" in shortcuts and
        'internal const string ShortcutFolderName = "CyanDeck Shortcuts";' in policy,
    "uninstall removes only command shortcuts that target the installed exe":
        r"'Microsoft\Windows\Start Menu\Programs\CyanDeck Shortcuts'" in uninstall and
        "$expectedCommandExe" in uninstall and "Remove-Item $installDir -Recurse -Force" not in uninstall,
    "dead hotkey claims removed while managed keys still parse":
        "keyboard={state.Settings.EnablePageKeyboardNavigation}" not in diag and
        "global-hotkeys=none, keyboard-hook=none" in diag and
        "watchers, and global hotkeys are disabled" not in app and
        '"General.PeekActivationMode" => NormalizeEnum<DeckHotkeyActivation>(value),' in adapter and
        "compatibility-only" in adapter,

    # #35 Peek taskbar icon / hot corner backend.
    "hot corner desktop view honors the desktop view switch":
        "if (!_state.Settings.EnableDesktopDisplay) return;" in peek_surfaces,
    "icon picker can explain why a .ico is rejected":
        "public static string? DescribeCustomIconProblem(string? configuredPath)" in resolver and
        "4MB 이하의 .ico 파일만 사용할 수 있습니다." in resolver,
    "diagnostics report the hot-corner thickness":
        "hot-corner-px={state.Settings.PeekHotCornerHitThicknessPx}" in diag,

    # #27 discoverability.
    "tray tools menu opens the shortcut and gesture guide":
        "AddInputGuideTrayItem(menu);" in app and 'item.Tag = "help.input-guide";' in hints and
        'case "help.input-guide": return TrayMenuSection.Tools;' in routing and
        app.count("CompactTrayMenu(menu);") == 3,
    "guide lists existing conveniences and offers shortcut creation":
        "InputGuideContent.Sections" in guide and "CommandShortcutService.CreateInteractive(this)" in guide and
        "Alt를 누른 채 빈 바탕화면에 놓기" in policy and "SystemParameters.HighContrast" in guide,
    "getting-started last step links the guide":
        'Button("단축키·제스처 안내 보기", () => InputGuideWindow.ShowGuide(this));' in wizard,
    "item menus offer copy/cut and rename shows F2":
        "AddClipboardMenuItems(menu, existing);" in fence and "AddClipboardMenuItems(menu, [tile]);" in fence and
        'WithGesture(MakeItem("이름 바꾸기…", () => RenameItem(tile)), "F2")' in fence and
        'WithGesture(MakeItem("복사", () => CopyPathsToShellClipboard(paths, cut: false)), "Ctrl+C")' in clipboard_menu,
    "group menu offers paste only with files and keeps portal path checks":
        "AddPasteMenuItem(menu);" in group_menu and "Clipboard.ContainsFileDropList()" in clipboard_menu and
        "source => { ValidatePortalImport(source, destination); return null; }" in clipboard_menu,
    "tab and icon-size shortcuts are visible in menus":
        "AnnotateTabShortcutGestures(tabsMenu);" in group_menu and
        'iconSizeMenu.ToolTip = "그룹 위에서 Ctrl+마우스 휠로도 바꿀 수 있습니다.";' in group_menu,
    "what's-new notice once per update, fresh installs stamped":
        "ScheduleWhatsNewNotice();" in app and "InputHintPolicy.ShouldShowWhatsNew(" in hints and
        "configured.LastSeenFeatureNoticeVersion = InputHintPolicy.NormalizeVersion(installedVersion);" in startup and
        "_trayController.ShowBalloonTip(" in hints,
    "release notes stay reachable from both tray menus after the notice":
        'item.Tag = "help.release-notes";' in hints and
        'case "help.release-notes":' in routing and
        app.count("AddReleaseNotesTrayItem(menu);") == 1 and hints.count("AddReleaseNotesTrayItem(menu);") == 1 and
        "var message = _state.Settings.EnableCyanDeck" in hints and
        'Route("help.release-notes",' in harness,
    "cold-start limit of command shortcuts is documented":
        "CyanDeck만 시작하고 명령은 적용하지 않습니다(Peek 제외)." in shortcuts and
        "CyanDeck만 시작하고 명령은 적용하지 않습니다(Peek 제외)." in readme,
    "README paths match the current tray layout":
        "약 1초간 누른 뒤" in readme and "약 2초간 누른 뒤" not in readme and
        "트레이 → 그룹 및 화면 → 레이아웃 스냅샷" in readme and
        "트레이 → 도구 및 도움말 → 단축키·제스처 안내…" in readme and
        "**트레이 → `진단 묶음 만들기…`**" not in readme,

    # Tests and schema.
    "pure policies are exercised by the compiled harness":
        "InputCommandPolicy.cs" in harness_project and "DeckTrayDoubleClickAction.cs" in harness_project and
        "MouseHookWatchdogPolicy.Evaluate(" in harness and "InputHintPolicy.ResolveTrayDoubleClick(" in harness,
    "schema version unchanged":
        "Current = 35" in schema,
}
for label, ok in checks.items(): print(("PASS: " if ok else "FAIL: ") + label)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()): raise SystemExit(1)

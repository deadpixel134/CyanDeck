#!/usr/bin/env python3
"""v8.0.36: protect the code-review hardening fixes (BOM-tolerant update manifests,
locale-independent dates/rules, low-level hook dispatch, corrupt image handling,
linked-spacing presets and the build.cmd entry point)."""
from pathlib import Path
import re
root = Path(__file__).resolve().parents[1]
def read(name): return (root / name).read_text(encoding="utf-8-sig")

release_source = read("src/CyanDeck/Services/GitHubReleaseSource.cs")
update_service = read("src/CyanDeck/Services/UpdateService.cs")
package = read("package.ps1")
google_sync = read("src/CyanDeck/Calendar/GoogleCalendarSync.cs")
native = read("src/CyanDeck/Interop/NativeMethods.cs")
hook = read("src/CyanDeck/Services/DesktopPageGestureHook.cs")
sampler = read("src/CyanDeck/Appearance/WallpaperBitmapSampler.cs")
blur_cache = read("src/CyanDeck/Appearance/WallpaperBlurBitmapCache.cs")
preview_store = read("src/CyanDeck/Snapshots/SnapshotScreenPreviewStore.cs")
rules = read("src/CyanDeck/Services/RuleEngine.cs")
settings = read("src/CyanDeck/Views/SettingsWindow.xaml.cs")
build_cmd = read("build.cmd")
project = read("src/CyanDeck/CyanDeck.csproj")

version = tuple(map(int, re.search(r"<Version>(\d+\.\d+\.\d+)</Version>", project).group(1).split(".")))

checks = {
    "GitHub manifest reader tolerates a UTF-8 BOM":
        "detectEncodingFromByteOrderMarks: true" in release_source,
    "local/URL manifest payload strips a leading BOM":
        "TrimStart('\\uFEFF')" in update_service,
    "packaged update manifest is written without a BOM":
        "WriteAllText($manifestPath" in package and "[Text.UTF8Encoding]::new($false)" in package,
    "Google all-day dates use the invariant culture":
        google_sync.count('ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)') >= 4,
    "Google timestamps are parsed with the invariant culture":
        "CultureInfo.InvariantCulture, DateTimeStyles.None" in google_sync,
    "FindWindow/FindWindowEx marshal Unicode strings":
        len(re.findall(r'CharSet = CharSet\.Unicode, SetLastError = true\)\]\s*internal static extern IntPtr FindWindow(?:Ex)?\(', native)) == 2,
    "gesture handlers are deferred while inside the WH_MOUSE_LL callback":
        "_inHookCallback" in hook and "!_inHookCallback" in hook and
        "BeginInvoke(DispatcherPriority.Input, action)" in hook,
    "hook still always chains to the next hook":
        "CallNextHookEx(_hook, nCode, wParam, lParam)" in hook,
    "corrupt wallpaper images cannot escape the eyedropper sampler":
        "System.IO.FileFormatException" in sampler,
    "corrupt wallpaper images cannot escape the blur cache":
        "System.IO.FileFormatException" in blur_cache,
    "corrupt snapshot preview PNG yields no preview instead of a crash":
        "catch (FileFormatException)" in preview_store,
    "within-days rules parse invariant numbers and reject negatives":
        "CultureInfo.InvariantCulture, out var days)" in rules and "if (days < 0) return false;" in rules,
    "group presets keep linked spacing in the dormant per-group values":
        "linkedDraft with { IconSpacingX = spacingX, IconSpacingY = spacingY }" in settings,
    "build.cmd launches the real build script":
        '-File "%~dp0build.ps1"' in build_cmd,
    "review hardening beta version or later":
        version >= (8, 0, 36),
}
for label, ok in checks.items(): print(("PASS: " if ok else "FAIL: ") + label)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()): raise SystemExit(1)

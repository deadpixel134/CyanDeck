#!/usr/bin/env python3
"""Static geometry and lifecycle regressions for v40's wallpaper alignment.

Does not emulate or claim to test a live Windows WPF compositor.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
source = (ROOT / 'src/CyanDeck/Views/FenceWindow.xaml.cs').read_text(encoding='utf-8-sig')
native = (ROOT / 'src/CyanDeck/Interop/NativeMethods.cs').read_text(encoding='utf-8-sig')
xaml = (ROOT / 'src/CyanDeck/Views/FenceWindow.xaml').read_text(encoding='utf-8-sig')
planner = (ROOT / 'src/CyanDeck/Appearance/WallpaperBackdropSlicePlanner.cs').read_text(encoding='utf-8-sig')
presenter = (ROOT / 'src/CyanDeck/Appearance/WallpaperBlurBackdropPresenter.cs').read_text(encoding='utf-8-sig')
model = (ROOT / 'src/CyanDeck/Models/Models.cs').read_text(encoding='utf-8-sig')

def method_body(name, next_name):
    return source.split(name, 1)[1].split(next_name, 1)[0]

moving = method_body('LocationChanged += (_, _) =>', 'SizeChanged += (_, _) =>')
resize = method_body('SizeChanged += (_, _) =>', 'Closing += (_, e) =>')
refresh = method_body('private void RefreshWallpaperBlur()', 'private void ApplyChameleonDecision(')
queue = method_body('private void QueueWallpaperBlurRefresh()', 'private void RefreshWallpaperBlur()')
wndmsg = method_body('private IntPtr WindowMessageHook(', 'internal void RefreshAfterStableDisplayTransition()')
checks = {
    'wallpaper backdrop is a dedicated non-interactive layer': '<Canvas x:Name="WallpaperBlurBackdrop"' in xaml and 'IsHitTestVisible="False"' in xaml,
    'location events schedule wallpaper repaint BEFORE layout persistence early return': 0 <= moving.find('QueueWallpaperBlurRefresh();') < moving.find('if (!IsLoaded || _applyingLayout'),
    'size events schedule wallpaper repaint BEFORE persistence early return': 0 <= resize.find('QueueWallpaperBlurRefresh();') < resize.find('if (!IsLoaded || _applyingLayout'),
    'timer is coalesced and not restarted by every move event': 'if (!_wallpaperBlurRefreshTimer.IsEnabled) _wallpaperBlurRefreshTimer.Start();' in queue,
    'refresh timer interval limited': 'Interval = TimeSpan.FromMilliseconds(60)' in source,
    'refresh timer stops once per tick': '_wallpaperBlurRefreshTimer.Stop();\n            RefreshWallpaperBlur();' in source,
    'refresh timer stops on group close': 'Closed += (_, _) =>\n        {\n            _wallpaperBlurRefreshTimer.Stop();' in source,
    'blur timer restricted to visible/enabled groups': '!IsLoaded || !IsVisible || !_settings.BlurWallpaperBehindGroups' in queue,
    'background uses dedicated Canvas bounds not outer window bounds': 'canvas.PointToScreen(new Point(0, 0))' in refresh and 'canvas.PointToScreen(new Point(canvas.ActualWidth, canvas.ActualHeight))' in refresh,
    'no width multiplied by mixed screen DPI to form end rect': 'ActualWidth * dpi.DpiScaleX' not in refresh and 'ActualHeight * dpi.DpiScaleY' not in refresh,
    'per-monitor DPI for local canvas rendering': 'VisualTreeHelper.GetDpi(WallpaperBlurBackdrop)' in refresh,
    'render rectangles still use actual physical screen coordinates': 'new PixelRect(left, top, right, bottom)' in refresh,
    'group loaded queues refresh': 'EnsureVisibleOnMonitor(); UpdateTitlebarPlacement(); QueueWallpaperBlurRefresh();' in source,
    'WM_SETTINGCHANGE value correctly declared': 'WM_SETTINGCHANGE = 0x001A' in native,
    'wallpaper change invalidates cache & queues refresh': 'msg == NativeMethods.WM_SETTINGCHANGE' in wndmsg and '_wallpaperBlurController.InvalidateWallpaper();\n            QueueWallpaperBlurRefresh();' in wndmsg,
    'display and DPI messages keep prior restoration': 'msg is NativeMethods.WM_DPICHANGED or NativeMethods.WM_DISPLAYCHANGE' in wndmsg and 'handler(msg)' in wndmsg,
    'fast explicit move-complete refresh unchanged': 'RefreshWallpaperBlur();\n        MoveCompleted?.Invoke(this);' in source,
    'fast explicit resize-complete refresh unchanged': 'RefreshWallpaperBlur();\n        if (_resizeIconMovementToken' in source,
    'preview/peek bounds refresh retained': source.count('RefreshWallpaperBlur();') >= 7,
    'wallpaper reconstruction uses virtual monitor geometry': 'snapshot.VirtualBounds' in planner and 'monitor.Bounds' in planner,
    'individual slice source offsets are relative to physical clip': 'imageRect.Left - clip.Left' in presenter and 'imageRect.Top - clip.Top' in presenter,
    'blur applies only to backing canvas': '_backdropCanvas.Effect = new BlurEffect' in presenter,
    'no screen capture calls added': all(v not in refresh + queue for v in ('CopyFromScreen(', 'BitBlt(', 'GetPixel(')),
    'no user settings schema modification': 'public int Version { get; set; } = 35' in model,
    'exception-safe fallback preserved': 'catch\n        {\n            // Retain the previous fail-closed behavior' in refresh,
}
for name, ok in checks.items():
    print(('PASS' if ok else 'FAIL') + ': ' + name)
print(f'v40 wallpaper alignment: {sum(checks.values())}/{len(checks)}')
if not all(checks.values()): raise SystemExit(1)

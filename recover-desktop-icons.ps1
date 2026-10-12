# DesktopDeck emergency native desktop icon recovery (Windows Explorer shell only).
# Safe to run after a crash, even when DesktopDeck is not running.
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DesktopDeckExplorerIconRescue {
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr arg);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)]
  public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string name, string caption);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)]
  public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int count);
}
'@
$found = [IntPtr]::Zero
$enum = [DesktopDeckExplorerIconRescue+EnumProc] {
  param([IntPtr]$window, [IntPtr]$unused)
  $name = New-Object System.Text.StringBuilder 256
  [void][DesktopDeckExplorerIconRescue]::GetClassName($window, $name, 256)
  if ($name.ToString() -ne 'Progman' -and $name.ToString() -ne 'WorkerW') { return $true }
  $defview = [DesktopDeckExplorerIconRescue]::FindWindowEx($window, [IntPtr]::Zero, 'SHELLDLL_DefView', $null)
  if ($defview -eq [IntPtr]::Zero) { return $true }
  $list = [DesktopDeckExplorerIconRescue]::FindWindowEx($defview, [IntPtr]::Zero, 'SysListView32', 'FolderView')
  if ($list -ne [IntPtr]::Zero) { $script:found = $list; return $false }
  return $true
}
[void][DesktopDeckExplorerIconRescue]::EnumWindows($enum, [IntPtr]::Zero)
if ($found -eq [IntPtr]::Zero) { throw 'Explorer desktop FolderView not located. No window was modified.' }
[void][DesktopDeckExplorerIconRescue]::ShowWindow($found, 5)
if (-not [DesktopDeckExplorerIconRescue]::IsWindowVisible($found)) {
  throw 'Explorer desktop FolderView was found but remains hidden.'
}
Write-Host 'Windows Explorer native desktop icons restored. No files or settings were changed.'

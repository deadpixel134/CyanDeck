#!/usr/bin/env python3
from pathlib import Path
r=Path(__file__).resolve().parents[1]
s=(r/'src/DesktopDeck/Services/RecycleBinPreflight.cs').read_text()
f=(r/'src/DesktopDeck/Views/FenceWindow.xaml.cs').read_text(encoding='utf-8-sig')
l=(r/'src/DesktopDeck/Services/LooseDesktopIconHost.cs').read_text(encoding='utf-8-sig')
d=(r/'src/DesktopDeck/Services/DesktopRecycleService.cs').read_text()
c={
 'large folder enumeration bounded': 'MaxEnumeratedEntries = 4096' in s,
 'reparse points avoided': 'FileAttributes.ReparsePoint' in s,
 'no traversal deletion': 'Directory.Delete(' not in s and 'File.Delete(' not in s,
 'volume-specific limits': 'GetVolumeNameForVolumeMountPoint' in s and 'MaxCapacity' in s,
 'bin disabled flag': 'NukeOnDelete' in s,
 'bin occupancy': 'SHQueryRecycleBin' in s,
 'oversize detected': 'totalBytes > maxBytes' in s,
 'overflow/occupancy detected': 'usedBytes > maxBytes - totalBytes' in s,
 'uncertainty asks again': 'return Unknown(' in s,
 'cancel by default': 'MessageBoxResult.No' in f and 'MessageBoxResult.No' in l,
 'all real desktop recycle uses shared check': 'RecycleBinPreflight.Assess(candidatePaths)' in d,
 'portal guarded': 'RecycleBinPreflight.Assess(existing.Select(tile => tile.Path))' in f,
 'standalone icons guarded': 'DesktopRecycleService.RecycleConfirmed(paths, warning =>' in l,
 'portal recheck protected paths': 'if (IsProtectedDeletionTarget(tile.Path))' in f,
 'portal checks source still exists': '휴지통 이동이 확인되지 않아' in f,
}
for k,v in c.items():print(('PASS' if v else 'FAIL'),k)
print('RESULT',sum(c.values()),'/',len(c))
raise SystemExit(0 if all(c.values()) else 1)

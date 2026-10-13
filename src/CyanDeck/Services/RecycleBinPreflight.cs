using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace CyanDeck.Services;

/// <summary>
/// Read-only, conservative preflight. Windows may bypass the Recycle Bin when an
/// item exceeds the configured per-volume limit, recycling is disabled, or the
/// limit/size cannot be determined. Unknown means WARN, never "safe".
/// Does not delete anything or change Recycle Bin settings.
/// </summary>
internal static class RecycleBinPreflight
{
    internal sealed record Assessment(bool NeedsAdditionalConfirmation, string Warning);

    private const int MaxEnumeratedEntries = 4096;
    private const string BitBucketKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket";

    internal static Assessment Assess(IEnumerable<string> paths)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var full = Path.GetFullPath(path);
                var mount = new StringBuilder(512);
                if (!GetVolumePathName(full, mount, mount.Capacity) || mount.Length == 0)
                    return Unknown("대상 파일의 저장 볼륨을 확인하지 못했습니다.");
                var volumeRoot = mount.ToString();
                if (!groups.TryGetValue(volumeRoot, out var group)) groups[volumeRoot] = group = new List<string>();
                group.Add(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return Unknown("대상 경로를 확인하지 못했습니다.");
            }
        }
        if (groups.Count == 0) return new Assessment(false, "");

        foreach (var group in groups)
        {
            if (!TryGetRecycleLimits(group.Key, out var maxBytes, out var usedBytes, out var recyclingDisabled))
                return Unknown($"{group.Key} 드라이브의 휴지통 용량 설정을 읽을 수 없습니다.");
            if (recyclingDisabled)
                return Unknown($"{group.Key} 드라이브의 휴지통 사용 안 함(즉시 삭제) 설정이 감지되었습니다.");

            ulong totalBytes = 0;
            var entries = 0;
            foreach (var source in group.Value)
            {
                if (!TryGetSize(source, ref entries, out var size))
                    return Unknown("폴더 내부 전체 용량을 확정하지 못했습니다(접근 제한·링크·대형 폴더 등).");
                if (ulong.MaxValue - totalBytes < size)
                    return Unknown("삭제 예정 데이터의 전체 크기가 너무 큽니다.");
                totalBytes += size;
            }
            if (totalBytes > maxBytes)
                return Unknown($"삭제 대상의 총 용량({ToReadable(totalBytes)})이 {group.Key} 드라이브 휴지통 최대 용량({ToReadable(maxBytes)})을 초과합니다.");
            // Shell may purge older Recycle Bin contents to make room. This is
            // important even when the new item alone fits its configured limit.
            if (usedBytes > maxBytes - totalBytes)
                return Unknown($"{group.Key} 드라이브의 휴지통 사용량({ToReadable(usedBytes)})과 삭제 대상({ToReadable(totalBytes)})의 합이 설정 용량({ToReadable(maxBytes)})을 초과합니다. 기존 휴지통 항목이 자동 제거될 수 있습니다.");
        }
        return new Assessment(false, "");
    }

    private static Assessment Unknown(string cause) => new(true,
        cause + "\n\nWindows 설정에 따라 일부 또는 전체 파일·폴더가 휴지통에 들어가지 않고 영구 삭제될 수 있으며, 기존 휴지통 항목이 삭제될 수도 있습니다.\n\n" +
        "이 위험을 이해하고 실제 파일 삭제를 계속하시겠습니까?\n[아니요]를 선택하면 원본을 그대로 둡니다.");

    private static bool TryGetSize(string start, ref int scanned, out ulong bytes)
    {
        bytes = 0;
        try
        {
            var stack = new Stack<string>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                if (++scanned > MaxEnumeratedEntries) return false;
                var item = stack.Pop();
                var attrs = File.GetAttributes(item);
                if ((attrs & FileAttributes.ReparsePoint) != 0) return false;
                if ((attrs & FileAttributes.Directory) == 0)
                {
                    var length = new FileInfo(item).Length;
                    if (length < 0 || ulong.MaxValue - bytes < (ulong)length) return false;
                    bytes += (ulong)length;
                }
                else
                {
                    foreach (var child in Directory.EnumerateFileSystemEntries(item))
                    {
                        if (stack.Count + scanned > MaxEnumeratedEntries) return false;
                        stack.Push(child);
                    }
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryGetRecycleLimits(string root, out ulong maxBytes, out ulong usedBytes, out bool disabled)
    {
        maxBytes = 0; usedBytes = 0; disabled = false;
        try
        {
            var volume = new StringBuilder(512);
            if (!GetVolumeNameForVolumeMountPoint(root, volume, volume.Capacity)) return false;
            var name = volume.ToString().TrimEnd('\\');
            const string prefix = @"\\?\";
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            var keyName = name[prefix.Length..];
            if (!keyName.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase) || !keyName.EndsWith('}')) return false;
            using var key = Registry.CurrentUser.OpenSubKey(BitBucketKey + "\\" + keyName);
            if (key is null) return false;
            if (key.GetValue("NukeOnDelete") is int nuke && nuke != 0)
            {
                disabled = true;
                return true;
            }
            if (key.GetValue("MaxCapacity") is not int maxMb || maxMb <= 0) return false;
            maxBytes = (ulong)maxMb * 1024UL * 1024UL;
            var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
            if (SHQueryRecycleBin(root, ref info) != 0 || info.i64Size < 0) return false;
            usedBytes = (ulong)info.i64Size;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string ToReadable(ulong bytes)
    {
        var gb = bytes / 1073741824d;
        if (gb >= 1) return $"{gb:N2} GB";
        return $"{bytes / 1048576d:N1} MB";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumePathNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, StringBuilder volumePathName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, StringBuilder volumeName, int bufferLength);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(string rootPath, ref SHQUERYRBINFO recycleBinInfo);
}

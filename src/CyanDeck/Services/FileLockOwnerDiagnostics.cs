using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CyanDeck.Services;

/// <summary>
/// Best-effort read-only lookup of processes using a file. Restart Manager does
/// not prove that any reported process caused a particular save failure.
/// No process is terminated and no file handle is modified.
/// </summary>
internal static class FileLockOwnerDiagnostics
{
    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int MaxProcessCount = 128;

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public uint ApplicationType;
        public uint AppStatus;
        public uint SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint handle, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint handle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint handle, uint countFiles,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] paths,
        uint countApplications, IntPtr applications, uint countServices, IntPtr services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint handle, out uint needed, ref uint count,
        [In, Out] RmProcessInfo[]? processes, ref uint rebootReasons);

    public static string Describe(params string[] paths)
    {
        if (!OperatingSystem.IsWindows()) return "Windows 환경에서만 조회할 수 있습니다.";
        var existing = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (existing.Length == 0) return "조회 대상 파일이 현재 존재하지 않습니다.";

        uint handle = 0;
        var opened = false;
        try
        {
            var status = RmStartSession(out handle, 0, new StringBuilder(33));
            if (status != ErrorSuccess) return $"잠금 조회 실패 (Restart Manager 오류 {status}).";
            opened = true;
            status = RmRegisterResources(handle, (uint)existing.Length, existing, 0, IntPtr.Zero, 0, IntPtr.Zero);
            if (status != ErrorSuccess) return $"파일 사용 정보 등록 실패 (오류 {status}).";

            uint needed = 0, count = 0, reasons = 0;
            status = RmGetList(handle, out needed, ref count, null, ref reasons);
            if (status == ErrorSuccess && needed == 0) return "현재 조회된 파일 사용 프로세스가 없습니다.";
            if (status != ErrorMoreData && status != ErrorSuccess)
                return $"파일 사용자 조회 실패 (오류 {status}).";

            // The list may change between calls. Do not allocate unbounded buffers.
            for (var i = 0; i < 2; i++)
            {
                if (needed == 0 || needed > MaxProcessCount)
                    return "조회된 프로세스 수가 제한을 초과하거나 조회할 정보가 없습니다.";
                var infos = new RmProcessInfo[(int)needed];
                count = (uint)infos.Length;
                status = RmGetList(handle, out needed, ref count, infos, ref reasons);
                if (status == ErrorMoreData) continue;
                if (status != ErrorSuccess) return $"파일 사용자 조회 실패 (오류 {status}).";
                var entries = infos.Take((int)count)
                    .Where(p => p.Process.ProcessId > 0)
                    .Select(p => FormatProcess(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToArray();
                return entries.Length == 0 ? "현재 조회된 파일 사용 프로세스가 없습니다." : string.Join("; ", entries);
            }
            return "조회 중 파일 사용자 목록이 변경되었습니다.";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or
            UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"잠금 조회 불가 ({ex.GetType().Name}).";
        }
        finally
        {
            if (opened) try { RmEndSession(handle); } catch { }
        }
    }

    private static string FormatProcess(RmProcessInfo info)
    {
        var name = info.AppName ?? "";
        try
        {
            using var p = Process.GetProcessById(info.Process.ProcessId);
            name = p.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception) { }
        // A display name supplied by another process may contain a full path.
        name = Path.GetFileName(name);
        // Disallow control characters from OS-supplied app names in a
        // report that users will paste into a messenger/chat window.
        name = new string(name.Where(c => !char.IsControl(c)).Take(80).ToArray());
        if (string.IsNullOrWhiteSpace(name)) name = "이름 조회 불가";
        return $"{name} (PID {info.Process.ProcessId})";
    }
}

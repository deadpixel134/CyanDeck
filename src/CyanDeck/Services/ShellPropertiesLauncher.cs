using System.Runtime.InteropServices;

namespace CyanDeck.Services;

/// <summary>
/// Opens the Windows "Properties" dialog for a file, folder, shortcut or shell item
/// (This PC, Recycle Bin, ...). Process.Start cannot do this reliably: "properties" is not a
/// registered verb and needs SEE_MASK_INVOKEIDLIST, which ProcessStartInfo never sets.
/// </summary>
internal static class ShellPropertiesLauncher
{
    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
    private const int SW_SHOW = 5;

    /// <summary>"shell:::{CLSID}" item paths are passed as the "::{CLSID}" parsing name.</summary>
    internal static string NormalizeTarget(string path)
    {
        var trimmed = path.Trim();
        return trimmed.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase)
            ? trimmed.Substring("shell:".Length)
            : trimmed;
    }

    /// <summary>Returns false with a readable reason instead of failing silently.</summary>
    internal static bool TryShow(string? path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "대상 경로가 없습니다.";
            return false;
        }
        var info = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
            // Our own message replaces the shell's error UI; the dialog itself is still shown.
            fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_FLAG_NO_UI,
            hwnd = IntPtr.Zero,
            lpVerb = "properties",
            lpFile = NormalizeTarget(path),
            nShow = SW_SHOW
        };
        if (ShellExecuteEx(ref info)) return true;
        error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ShellExecuteExW")]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);
}

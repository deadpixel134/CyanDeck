using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace CyanDeck.Services;

/// <summary>
/// v8.0.43: creates Start menu shortcuts for CyanDeck's single-instance commands
/// (Peek, quick hide, desktop view, next/previous page). Users assign a key in the
/// shortcut's Properties ("바로 가기 키"); Windows itself handles that key, so CyanDeck
/// still installs no keyboard hook and registers no global hotkey. Re-running keeps
/// the key a user already assigned. Only files inside CyanDeck's own Start menu
/// folder are written; nothing is deleted.
/// </summary>
internal static class CommandShortcutService
{
    internal sealed record CreationResult(string FolderPath, int Created, int Updated, IReadOnlyList<string> Failures);

    internal static string FolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        ExternalDesktopCommandCatalog.ShortcutFolderName);

    internal static CreationResult CreateOrUpdate(string executablePath)
    {
        var folder = FolderPath;
        var failures = new List<string>();
        var created = 0;
        var updated = 0;
        if (Directory.Exists(folder) && IsReparsePoint(folder))
            throw new IOException("바로 가기 폴더가 연결 지점(junction)이라 변경하지 않았습니다.");
        Directory.CreateDirectory(folder);

        foreach (var info in ExternalDesktopCommandCatalog.All)
        {
            var path = Path.Combine(folder, info.ShortcutFileName);
            try
            {
                var existed = File.Exists(path);
                if (existed && IsReparsePoint(path))
                {
                    failures.Add(info.ShortcutFileName);
                    continue;
                }
                WriteShortcut(path, executablePath, info, existed);
                if (existed) updated++;
                else created++;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("Command shortcut could not be written", ex);
                failures.Add(info.ShortcutFileName);
            }
        }

        DiagnosticsLog.Info($"Command shortcuts: created={created}; updated={updated}; failed={failures.Count}");
        return new CreationResult(folder, created, updated, failures);
    }

    /// <summary>Creates the shortcuts and explains how to assign a Windows shortcut key.</summary>
    internal static void CreateInteractive(Window? owner)
    {
        const string title = "CyanDeck 단축키용 바로 가기";
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            ShowMessage(owner, "CyanDeck 실행 파일 위치를 확인할 수 없어 바로 가기를 만들지 못했습니다.",
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CreationResult result;
        try
        {
            result = CreateOrUpdate(executable);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Command shortcut folder could not be prepared", ex);
            ShowMessage(owner, "바로 가기를 만들지 못했습니다.\n\n" + ex.Message,
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var folderName = ExternalDesktopCommandCatalog.ShortcutFolderName;
        var total = result.Created + result.Updated;
        var text = new StringBuilder();
        text.Append($"시작 메뉴의 '{folderName}' 폴더에 바로 가기 {total}개를 준비했습니다.");
        if (result.Failures.Count > 0)
            text.Append($" ({result.Failures.Count}개는 만들지 못했습니다.)");
        text.Append("\n\n키보드로 실행하려면 바로 가기를 오른쪽 클릭 → 속성 → '바로 가기 키' 칸을 누른 뒤 원하는 조합(예: Ctrl+Alt+H)을 누르고 확인을 누르세요. ");
        text.Append("Win+D처럼 Windows가 이미 쓰는 조합은 피하세요. 이미 지정한 바로 가기 키는 그대로 유지됩니다.");
        text.Append("\n\nCyanDeck이 실행 중이 아닐 때 누르면 CyanDeck만 시작하고 명령은 적용하지 않습니다(Peek 제외).");
        text.Append("\n\nCyanDeck 폴더를 옮기면 이 버튼을 다시 눌러 주세요.\n\n폴더를 지금 열까요?");
        if (ShowMessage(owner, text.ToString(), title, MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + result.FolderPath + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Command shortcut folder could not be opened", ex);
        }
    }

    private static MessageBoxResult ShowMessage(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage image) =>
        owner is not null && owner.IsLoaded
            ? MessageBox.Show(owner, text, caption, buttons, image)
            : MessageBox.Show(text, caption, buttons, image);

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    private static void WriteShortcut(string path, string executablePath, ExternalDesktopCommandInfo info, bool existed)
    {
        var link = (IShellLinkW)new ShellLinkObject();
        try
        {
            var persist = (ComTypes.IPersistFile)link;
            if (existed)
            {
                // Load first so a key the user assigned (and other user edits) survive the update.
                try { persist.Load(path, 0); }
                catch (Exception ex) { DiagnosticsLog.Warn($"Existing command shortcut could not be read and is rewritten: {ex.Message}"); }
            }
            link.SetPath(executablePath);
            link.SetArguments(info.Argument);
            link.SetWorkingDirectory(Path.GetDirectoryName(executablePath) ?? "");
            link.SetDescription(info.Description);
            link.SetIconLocation(executablePath, 0);
            persist.Save(path, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}

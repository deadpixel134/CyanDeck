using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace DesktopDeck.Services;

/// <summary>
/// FileDrop clipboard interop for a focused DesktopDeck item view. No keyboard hooks.
/// Clipboard COM and Shell file operations run on a dedicated STA worker so delays
/// in Explorer/clipboard providers do not freeze the WPF input dispatcher.
/// </summary>
internal static class DeckShellClipboardService
{
    internal sealed record PasteResult(bool Completed, bool WasCut, string[] CreatedPaths, string? Error);
    private const uint FoCopy = 0x0002;
    private const uint FoMove = 0x0001;
    private const ushort FofRenameOnCollision = 0x0008;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConfirmMkDir = 0x0200;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileOperation
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref ShellFileOperation operation);

    internal static void CopyFiles(string[] files, bool cut, Dispatcher dispatcher, Action<string?> completed)
    {
        RunSta(dispatcher, () =>
        {
            var valid = files.Where(p => File.Exists(p) || Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (valid.Length == 0) return "복사할 파일 또는 폴더가 없습니다.";
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, valid);
            // Preferred DropEffect is the Windows Explorer CFSTR_PREFERREDDROPEFFECT.
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 1)));
            Clipboard.SetDataObject(data, true);
            return (string?)null;
        }, completed);
    }

    internal static void PasteFiles(string destination, Dispatcher dispatcher, Action<PasteResult> completed,
        Func<string, string?>? validateSource = null)
    {
        RunSta(dispatcher, () =>
        {
            if (!Directory.Exists(destination))
                return new PasteResult(false, false, [], "붙여넣을 폴더가 존재하지 않습니다.");
            var data = Clipboard.GetDataObject();
            if (data is null || !data.GetDataPresent(DataFormats.FileDrop))
                return new PasteResult(false, false, [], "클립보드에 복사된 파일이나 폴더가 없습니다.");
            if (data.GetData(DataFormats.FileDrop) is not string[] names)
                return new PasteResult(false, false, [], "클립보드 파일 목록을 읽을 수 없습니다.");
            var files = names.Where(p => File.Exists(p) || Directory.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0)
                return new PasteResult(false, false, [], "원본 파일이나 폴더가 존재하지 않습니다.");
            // Validate the ENTIRE selection before SHFileOperation can mutate any
            // path. Never partially move a safe subset when one source is unsafe.
            if (validateSource is not null)
            {
                foreach (var source in files)
                {
                    try
                    {
                        var reason = validateSource(source);
                        if (!string.IsNullOrWhiteSpace(reason))
                            return new PasteResult(false, false, [], reason);
                    }
                    catch (Exception ex)
                    {
                        return new PasteResult(false, false, [], $"보호된 파일 작업을 차단했습니다: {ex.Message}");
                    }
                }
            }
            var cut = IsCut(data);
            var before = Directory.EnumerateFileSystemEntries(destination).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var operation = new ShellFileOperation
            {
                wFunc = cut ? FoMove : FoCopy,
                pFrom = string.Join("\0", files) + "\0\0",
                pTo = destination + "\0\0",
                fFlags = (ushort)(FofRenameOnCollision | FofAllowUndo | FofNoConfirmMkDir)
            };
            var code = SHFileOperation(ref operation);
            if (code != 0 || operation.fAnyOperationsAborted)
            {
                var error = operation.fAnyOperationsAborted ? "사용자가 붙여넣기를 취소했습니다." :
                    $"Windows 파일 작업에 실패했습니다. 오류 코드: 0x{code:X}";
                return new PasteResult(false, cut, [], error);
            }
            var created = Directory.EnumerateFileSystemEntries(destination)
                .Where(path => !before.Contains(path)).ToArray();
            if (cut)
            {
                // A completed Explorer-style move consumes a cut clipboard only if
                // the user has not copied something else while the operation ran.
                try
                {
                    var current = Clipboard.GetDataObject();
                    if (current is not null && IsCut(current) &&
                        current.GetData(DataFormats.FileDrop) is string[] currentFiles &&
                        currentFiles.SequenceEqual(names, StringComparer.OrdinalIgnoreCase))
                        Clipboard.Clear();
                }
                catch { /* Do not undo a completed file move due to clipboard contention. */ }
            }
            // Moving existing sources within the same folder need not create new paths.
            return new PasteResult(true, cut, created, null);
        }, completed);
    }

    /// <summary>Cancel a pending local move only when our cut paths still own
    /// the current clipboard. Uses an STA thread; foreign clipboard data is safe.</summary>
    internal static void CancelPendingCut(Dispatcher dispatcher)
    {
        var paths = DeckCutClipboardVisualState.Snapshot().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0) return;
        RunSta(dispatcher, () =>
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data is null || !IsCut(data) || data.GetData(DataFormats.FileDrop) is not string[] current)
                    return false;
                if (!current.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(paths)) return false;
                Clipboard.Clear();
                return true;
            }
            catch { return false; }
        }, _ => { });
    }

    /// <summary>
    /// An external Explorer copy/cut may replace our clipboard. Verify on an STA
    /// worker when the pointer re-enters a fence, without blocking WPF input.
    /// </summary>
    internal static void VerifyCutClipboard(Dispatcher dispatcher)
    {
        if (!DeckCutClipboardVisualState.HasCut) return;
        var expectedVersion = DeckCutClipboardVisualState.Version;
        var expectedPaths = DeckCutClipboardVisualState.Snapshot().ToHashSet(StringComparer.OrdinalIgnoreCase);
        RunSta(dispatcher, () =>
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data is null || !IsCut(data) || data.GetData(DataFormats.FileDrop) is not string[] files)
                    return false;
                var paths = files.Where(p => !string.IsNullOrWhiteSpace(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return paths.Count > 0 && paths.SetEquals(expectedPaths);
            }
            catch { return true; } // clipboard temporarily locked: do not lose visual state
        }, matched =>
        {
            if (DeckCutClipboardVisualState.Version == expectedVersion && !matched)
                DeckCutClipboardVisualState.Clear();
        });
    }

    private static bool IsCut(IDataObject data)
    {
        try
        {
            if (data.GetData("Preferred DropEffect") is MemoryStream ms)
            {
                ms.Position = 0;
                var bytes = new byte[4];
                return ms.Read(bytes, 0, 4) == 4 && (BitConverter.ToInt32(bytes, 0) & 2) != 0;
            }
            if (data.GetData("Preferred DropEffect") is byte[] bytes2 && bytes2.Length >= 4)
                return (BitConverter.ToInt32(bytes2, 0) & 2) != 0;
        }
        catch { /* Older clipboard producers omit this optional format. */ }
        return false;
    }

    private static void RunSta<T>(Dispatcher dispatcher, Func<T> operation, Action<T> completed)
    {
        var worker = new Thread(() =>
        {
            T result;
            try { result = operation(); }
            catch (Exception ex)
            {
                // Instead of crashing an unobserved STA worker, relay errors to UI.
                dispatcher.BeginInvoke(new Action(() =>
                    MessageBox.Show(ex.Message, "DesktopDeck", MessageBoxButton.OK, MessageBoxImage.Warning)));
                return;
            }
            dispatcher.BeginInvoke(new Action(() => completed(result)));
        });
        worker.IsBackground = true;
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }
}

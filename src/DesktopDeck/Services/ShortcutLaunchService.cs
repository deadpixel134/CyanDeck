using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace DesktopDeck.Services;

/// <summary>
/// Hands .lnk activation to the real explorer.exe command-line handler.
/// The earlier Shell.Application FolderItem.InvokeVerb approach invokes a Shell
/// command but does not guarantee that Explorer is the activating process.
/// This version starts explorer.exe itself with the shortcut as its *sole*
/// argument; Explorer handles shortcut resolution, arguments and working folder.
/// No cmd/PowerShell, direct shortcut target execution, forced elevation or
/// global keyboard hooks are involved.
/// </summary>
internal static class ShortcutLaunchService
{
    internal static void Open(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            // Preserve standard Windows Shell behavior for all non-.lnk items.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }

        var shortcut = Path.GetFullPath(path);
        if (!File.Exists(shortcut))
            throw new FileNotFoundException("바로가기 파일이 존재하지 않습니다.", shortcut);

        try
        {
            LaunchShortcutViaExplorerProcess(shortcut);
            // The Explorer request was accepted, but that does NOT prove the
            // referenced target actually opened (later Shell failures are async).
            // Do not clear a previous error merely because explorer.exe started.
            DiagnosticsLog.Info("Shortcut .lnk activation request sent to Explorer; target launch unverified.");
        }
        catch (Exception launchFailure) when (launchFailure is Win32Exception or
            IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // No source path or target contents in logs that may be shared.
            DiagnosticsLog.Warn($"Shortcut Explorer activation request failed; errorType={launchFailure.GetType().Name}; hresult=0x{launchFailure.HResult:X8}; target launch unverified.");
            // Do not retry ShellExecute(.lnk) or COM InvokeVerb: an asynchronous
            // error could lead to duplicates. Instead reveal for a manual open.
            Exception? revealFailure = null;
            try { RevealShortcutInExplorer(shortcut); }
            catch (Exception ex) when (ex is Win32Exception or IOException or
                UnauthorizedAccessException or InvalidOperationException)
            {
                revealFailure = ex;
            }
            if (revealFailure is not null)
                DiagnosticsLog.Warn($"Shortcut Explorer reveal failed; errorType={revealFailure.GetType().Name}; hresult=0x{revealFailure.HResult:X8}.");
            else
                DiagnosticsLog.Info("Shortcut Explorer reveal request accepted; manual opening may still fail.");
            throw new ShortcutLaunchException(shortcut, launchFailure, revealFailure);
        }
    }

    internal static void ReportFailure(ShortcutLaunchException failure)
    {
        DiagnosticsLog.Error("Shortcut Explorer process activation failed", failure);
        var situation = failure.RevealFailure is null
            ? "Windows 탐색기 프로세스로 바로가기 열기를 요청했지만 시작 단계에서 실패했습니다. " +
              "탐색기에 해당 바로가기를 선택해 두었으니 그곳에서 직접 더블클릭하거나 Enter를 눌러 주세요. " +
              "HRESULT: 0x" + failure.ShellFailure.HResult.ToString("X8")
            : "탐색기 프로세스 실행과 바로가기 위치 표시가 모두 실패했습니다. " +
              "실행 HRESULT: 0x" + failure.ShellFailure.HResult.ToString("X8") +
              ", 탐색기 HRESULT: 0x" + failure.RevealFailure.HResult.ToString("X8");
        ProblemReportService.ShowOnce("shortcut-open:" + failure.ShortcutPath,
            "바로가기 실행", ".lnk", failure.ShellFailure,
            situation + ". 개인 파일 경로와 바로가기 대상 정보는 진단에 포함하지 않습니다.");
    }

    private static void LaunchShortcutViaExplorerProcess(string shortcut)
    {
        // Use the Windows-installed binary instead of PATH search. ArgumentList
        // performs platform quoting for Korean names, spaces and parentheses.
        var executable = ExplorerExecutable();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add(shortcut);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Windows 탐색기 프로세스를 시작할 수 없습니다.");
        // Do not wait: Explorer may hand the request to its existing instance.
    }

    private static void RevealShortcutInExplorer(string shortcut)
    {
        var executable = ExplorerExecutable();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            Arguments = "/select,\"" + shortcut + "\""
        };
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Windows 탐색기를 열 수 없습니다.");
    }

    private static string ExplorerExecutable()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
            throw new DirectoryNotFoundException("Windows 설치 폴더를 확인할 수 없습니다.");
        var executable = Path.Combine(windows, "explorer.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("Windows 탐색기 실행 파일을 찾을 수 없습니다.", executable);
        return executable;
    }
}

internal sealed class ShortcutLaunchException : Exception
{
    internal string ShortcutPath { get; }
    internal Exception ShellFailure { get; }
    internal Exception? RevealFailure { get; }

    internal ShortcutLaunchException(string shortcutPath, Exception shellFailure, Exception? revealFailure)
        : base("Windows 탐색기를 통해 바로가기를 열지 못했습니다.", shellFailure)
    {
        ShortcutPath = shortcutPath;
        ShellFailure = shellFailure;
        RevealFailure = revealFailure;
        HResult = shellFailure.HResult;
    }
}

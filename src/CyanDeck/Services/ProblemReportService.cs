using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CyanDeck.Views;

namespace CyanDeck.Services;

/// <summary>
/// Reports serious, actionable failures without demanding that end users find
/// log files. A persistent failure is shown only once per incident, not once
/// per auto-save timer tick. Clipboard delivery is local; nothing is uploaded.
/// </summary>
internal static class ProblemReportService
{
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);

    public static void ShowOnce(string key, string area, string fileName, Exception ex, string situation,
        params string[] filePaths)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        if (!dispatcher.CheckAccess())
        {
            try { _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ShowOnce(key, area, fileName, ex, situation, filePaths))); }
            catch (InvalidOperationException) { }
            return;
        }
        if (!Reported.Add(key)) return;
        ShowInternal(area, fileName, ex, situation, false, filePaths);
    }

    public static void Reset(string key)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        if (!dispatcher.CheckAccess())
        {
            try { _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Reported.Remove(key))); }
            catch (InvalidOperationException) { }
        }
        else Reported.Remove(key);
    }

    public static void ShowFatal(string area, Exception ex)
        => ShowInternal(area, "해당 없음", ex, "처리되지 않은 오류로 프로그램을 종료합니다.", true, Array.Empty<string>());

    private static void ShowInternal(string area, string fileName, Exception ex, string situation,
        bool fatal, string[] paths)
    {
        try
        {
            var report = ProblemReportText.Create(area, fileName, ex, situation);
            var popup = new ProblemReportWindow(report, fatal);
            // A passive tool window with a taskbar entry is findable, but it
            // never activates the Calendar/desktop icon host or goes topmost.
            if (fatal) popup.ShowDialog();
            else popup.Show();

            if (!fatal && paths.Length > 0 && (ex is IOException or UnauthorizedAccessException))
            {
                // Restart Manager is a diagnostic API, and may take time on a
                // busy machine. Never query from the WPF dispatcher thread.
                _ = Task.Run(() => FileLockOwnerDiagnostics.Describe(paths))
                    .ContinueWith(task =>
                    {
                        var owners = task.Status == TaskStatus.RanToCompletion
                            ? task.Result : "잠금 프로세스를 조회하지 못했습니다.";
                        var updated = ProblemReportText.Create(area, fileName, ex, situation, owners);
                        try
                        {
                            _ = popup.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                            {
                                if (popup.IsVisible) popup.UpdateLockOwners(updated);
                            }));
                        }
                        catch (InvalidOperationException) { }
                    }, TaskScheduler.Default);
            }
        }
        catch (Exception popupError)
        {
            DiagnosticsLog.Error("Could not display or copy a problem report", popupError);
            if (fatal)
            {
                try { MessageBox.Show("CyanDeck에 문제가 발생했습니다. 오류 창을 표시할 수 없습니다.",
                    "CyanDeck 오류", MessageBoxButton.OK, MessageBoxImage.Error); }
                catch { }
            }
        }
    }
}

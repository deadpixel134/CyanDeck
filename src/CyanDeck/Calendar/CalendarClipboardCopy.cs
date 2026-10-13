using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace CyanDeck.Calendar;

// WPF's built-in TextBox Ctrl+C clipboard path is synchronous on the window UI
// thread. If another process owns the clipboard, COM retries can stall the UI
// and (historically) the low-level keyboard hook hosted on that same thread.
// Transfer the selected immutable text from the UI thread to a background STA.
internal static class CalendarClipboardCopy
{
    private static int _inFlight;

    public static void Queue(string selectedText, Dispatcher ui, Action<string> reportFailure)
    {
        if (string.IsNullOrEmpty(selectedText)) return;
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return;
        try
        {
            var worker = new Thread(() =>
            {
                try { Clipboard.SetText(selectedText); }
                catch (Exception ex)
                {
                    if (!ui.HasShutdownStarted && !ui.HasShutdownFinished)
                        ui.BeginInvoke((Action)(() => reportFailure("텍스트 복사 실패: " + ex.Message)),
                            DispatcherPriority.Background);
                }
                finally { Interlocked.Exchange(ref _inFlight, 0); }
            })
            {
                Name = "CyanDeck calendar clipboard STA",
                IsBackground = true
            };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _inFlight, 0);
            reportFailure("텍스트 복사 시작 실패: " + ex.Message);
        }
    }
}

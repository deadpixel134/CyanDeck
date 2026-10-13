using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;
using DesktopDeck.Services;
using Forms = System.Windows.Forms;

namespace DesktopDeck;

public partial class App : Application
{
    private const string ActivationPipeName = "DesktopDeck.Activation.v1";
    private Mutex? _mutex;
    private AppController? _controller;
    private CancellationTokenSource? _activationCts;
    private Task? _activationTask;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        WaitForPriorInstanceIfRequested(e.Args);
        var safeMode = e.Args.Any(a => string.Equals(a, "--safe-mode", StringComparison.OrdinalIgnoreCase));
        var ignoreCrashLoop = e.Args.Any(a => string.Equals(a, "--ignore-crash-loop", StringComparison.OrdinalIgnoreCase));
        var peekToggle = e.Args.Any(a => string.Equals(a, "--peek-toggle", StringComparison.OrdinalIgnoreCase));
        var recoverGroups = e.Args.Any(a => string.Equals(a, DesktopBackgroundRecoveryMenu.Argument, StringComparison.OrdinalIgnoreCase));
        // Capture the physical cursor monitor *before* Explorer can close/reposition its menu.
        var recoveryMonitor = recoverGroups ? Forms.Screen.FromPoint(Forms.Cursor.Position).DeviceName : null;
        var managedPolicyPath = GetOptionValue(e.Args, "--managed-policy");

        _mutex = new Mutex(true, @"Local\DesktopDeck.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            NotifyExistingInstance(recoverGroups && recoveryMonitor is not null
                ? "recover-groups|" + recoveryMonitor
                : peekToggle ? "peek-toggle" : "activate");
            Shutdown();
            return;
        }

        // A command-line activation request must never be silently ignored:
        // otherwise a launcher could claim CyanDeck was activated while all
        // stores continue writing to the legacy profile. Fail before startup.
        if (e.Args.Any(a => string.Equals(a, "--activate-cyandeck", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "CyanDeck profile activation is not supported by this build. " +
                "The legacy DesktopDeck profile remains authoritative.");
        }

        // Keep the legacy profile authoritative until the old/new writer fence
        // and downgrade path are implemented. A verified migration journal alone
        // must never trigger an automatic profile switch.
        if (CyanDeckCutoverJournal.IsSafeToActivateAtStartup())
        {
            throw new InvalidOperationException(
                "CyanDeck profile activation requires coordinated writer fencing.");
        }

        _controller = new AppController(Dispatcher, safeMode, ignoreCrashLoop, managedPolicyPath);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) DiagnosticsLog.Error("AppDomain unhandled exception", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagnosticsLog.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        DispatcherUnhandledException += (_, args) =>
        {
            DiagnosticsLog.Error("Dispatcher unhandled exception", args.Exception);
            try { _controller?.MarkAbnormalTermination(); } catch { }
            try { _controller?.Dispose(); } catch { }
            ProblemReportService.ShowFatal("프로그램 실행 중 치명적 오류", args.Exception);
            args.Handled = true;
            Shutdown(-1);
        };
        DiagnosticsLog.Info("DesktopDeck Calendar v26 여러 캘린더/저장 대상 build starting: " + (Environment.ProcessPath ?? "unknown exe"));
        _controller.Start();
        StartActivationServer();
        if (peekToggle) Dispatcher.BeginInvoke(() => _controller?.TogglePeekFromExternalCommand());
        if (recoverGroups && recoveryMonitor is not null)
            Dispatcher.BeginInvoke(() => _controller?.RecoverAllGroupsToMonitor(recoveryMonitor));
    }

    private static string? GetOptionValue(string[] args, string option)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, option, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            DiagnosticsLog.Info($"Command-line option {option} was provided without a value.");
            return string.Empty;
        }
        return args[index + 1];
    }

    private static void WaitForPriorInstanceIfRequested(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, "--wait-for-pid", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out var pid) || pid <= 0) return;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            process.WaitForExit(8000);
        }
        catch { }
    }

    private void StartActivationServer()
    {
        _activationCts = new CancellationTokenSource();
        var token = _activationCts.Token;
        _activationTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        ActivationPipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
                    var command = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (string.Equals(command, "activate", StringComparison.OrdinalIgnoreCase))
                        _ = Dispatcher.BeginInvoke(() => _controller?.ActivateFromSecondInstance());
                    else if (string.Equals(command, "peek-toggle", StringComparison.OrdinalIgnoreCase))
                        _ = Dispatcher.BeginInvoke(() => _controller?.TogglePeekFromExternalCommand());
                    else if (command is not null && command.StartsWith("recover-groups|", StringComparison.Ordinal))
                    {
                        var monitorName = command.Substring("recover-groups|".Length);
                        _ = Dispatcher.BeginInvoke(() => _controller?.RecoverAllGroupsToMonitor(monitorName));
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error("Single-instance activation pipe failed", ex);
                    try { await Task.Delay(250, token).ConfigureAwait(false); } catch { break; }
                }
            }
        }, token);
    }

    private static void NotifyExistingInstance(string command)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", ActivationPipeName, PipeDirection.Out, PipeOptions.None);
                client.Connect(350);
                using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
                writer.WriteLine(string.IsNullOrWhiteSpace(command) ? "activate" : command);
                return;
            }
            catch
            {
                if (attempt < 2) Thread.Sleep(120);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _activationCts?.Cancel(); } catch { }
        _controller?.Dispose();
        _activationCts?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

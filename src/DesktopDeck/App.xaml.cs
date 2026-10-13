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
    private LegacySingleInstanceGate? _instanceGate;
    private LegacyProfileWriterGate? _legacyWriterGate;
    private AppController? _controller;
    private CancellationTokenSource? _activationCts;
    private Task? _activationTask;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Reject unsupported cutover requests even when an instance is already
        // running. Otherwise the second instance would send an activation IPC
        // and exit successfully without ever examining the invalid option.
        if (e.Args.Any(a => string.Equals(a, "--activate-cyandeck", StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(
                "이 버전은 CyanDeck 데이터 저장 경로 전환을 지원하지 않습니다.\n" +
                "기존 데이터는 변경되지 않았습니다.",
                "CyanDeck - 데이터 보호", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(-1);
            return;
        }

        WaitForPriorInstanceIfRequested(e.Args);
        var safeMode = e.Args.Any(a => string.Equals(a, "--safe-mode", StringComparison.OrdinalIgnoreCase));
        var ignoreCrashLoop = e.Args.Any(a => string.Equals(a, "--ignore-crash-loop", StringComparison.OrdinalIgnoreCase));
        var peekToggle = e.Args.Any(a => string.Equals(a, "--peek-toggle", StringComparison.OrdinalIgnoreCase));
        var recoverGroups = e.Args.Any(a => string.Equals(a, DesktopBackgroundRecoveryMenu.Argument, StringComparison.OrdinalIgnoreCase));
        // Capture the physical cursor monitor *before* Explorer can close/reposition its menu.
        var recoveryMonitor = recoverGroups ? Forms.Screen.FromPoint(Forms.Cursor.Position).DeviceName : null;
        var managedPolicyPath = GetOptionValue(e.Args, "--managed-policy");

        _instanceGate = LegacySingleInstanceGate.TryAcquire();
        if (!_instanceGate.IsFirst)
        {
            NotifyExistingInstance(recoverGroups && recoveryMonitor is not null
                ? "recover-groups|" + recoveryMonitor
                : peekToggle ? "peek-toggle" : "activate");
            Shutdown();
            return;
        }

        // Both mutexes stay owned until all durable saves finish on shutdown.
        // The shared writer lease spans executable-identity changes, while the
        // existing singleton mutex also excludes old, non-cooperating builds.
        try
        {
            _legacyWriterGate = LegacyProfileWriterGate.Acquire();
            CyanDeckCutoverJournal.ThrowIfLegacyWriterFenced();
            // A verified journal is not sufficient to activate a data root.
            if (CyanDeckCutoverJournal.IsSafeToActivateAtStartup())
                throw new InvalidOperationException(
                    "CyanDeck profile activation requires coordinated writer fencing.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            MessageBox.Show(
                "데이터 저장소 보호 검사로 인해 CyanDeck 실행을 중단했습니다.\n" +
                "데이터는 변경하지 않았습니다. 호환 버전 또는 전환 상태를 확인하세요.\n\n" + ex.Message,
                "CyanDeck - 데이터 보호", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
            return;
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
#if CYANDECK_NATIVE
        // Full WPF app with legacy profile/IPC while signed cutover is incomplete.
        DiagnosticsLog.Info("CyanDeck native compatibility build starting: " + (Environment.ProcessPath ?? "unknown exe"));
#else
        DiagnosticsLog.Info("DesktopDeck Calendar v26 여러 캘린더/저장 대상 build starting: " + (Environment.ProcessPath ?? "unknown exe"));
#endif
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
        try { _controller?.Dispose(); }
        finally
        {
            // Keep the writer lease until AppController has flushed its data.
            try { _legacyWriterGate?.Dispose(); }
            finally
            {
                _activationCts?.Dispose();
                // Release the legacy singleton on a clean exit, not merely its
                // owned handle: an abandoned mutex would block migration.
                _instanceGate?.Dispose();
                base.OnExit(e);
            }
        }
    }
}

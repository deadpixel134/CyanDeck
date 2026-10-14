using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;
using CyanDeck.Services;
using CyanDeck.Views;
using Forms = System.Windows.Forms;

namespace CyanDeck;

public partial class App : Application
{
    private const string ActivationPipeName = "CyanDeck.Activation.v1";
    private CyanDeckSingleInstanceGate? _instanceGate;
    private AppController? _controller;
    private CancellationTokenSource? _activationCts;
    private Task? _activationTask;
    private StartupUpdateWindow? _startupUpdateWindow;
    private readonly List<string> _pendingActivationCommands = new();

    protected override async void OnStartup(StartupEventArgs e)
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

        _instanceGate = CyanDeckSingleInstanceGate.TryAcquire();
        if (!_instanceGate.IsFirst)
        {
            NotifyExistingInstance(recoverGroups && recoveryMonitor is not null
                ? "recover-groups|" + recoveryMonitor
                : peekToggle ? "peek-toggle" : "activate");
            Shutdown();
            return;
        }

        // Do not create desktop group or calendar HWNDs until auto-update preflight
        // has decided whether this process must hand off to the installer.
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
        DiagnosticsLog.Info("CyanDeck starting");
        // Accept second-instance commands even while the startup updater is checking/downloading.
        StartActivationServer();
        var preflight = await RunStartupUpdatePreflightAsync(
            safeMode, ignoreCrashLoop, peekToggle || recoverGroups, managedPolicyPath);
        if (preflight.ExitForUpdate) return;
        _controller = new AppController(Dispatcher, safeMode, ignoreCrashLoop, managedPolicyPath,
            suppressStartupUpdateCheck: preflight.Attempted);
        _controller.Start();
        foreach (var command in _pendingActivationCommands) HandleActivationCommand(command);
        _pendingActivationCommands.Clear();
        if (peekToggle) Dispatcher.BeginInvoke(() => _controller?.TogglePeekFromExternalCommand());
        if (recoverGroups && recoveryMonitor is not null)
            Dispatcher.BeginInvoke(() => _controller?.RecoverAllGroupsToMonitor(recoveryMonitor));
    }


    /// <summary>
    /// Runs BEFORE any Fence, calendar, native icon hiding, shell hook or session
    /// marker. Existing windows are never shown only to disappear for auto-install.
    /// Network checks are bounded and failure falls back to the installed version.
    /// </summary>
    private async Task<(bool ExitForUpdate, bool Attempted)> RunStartupUpdatePreflightAsync(
        bool safeMode, bool ignoreCrashLoop, bool externalCommand, string? managedPolicyPath)
    {
        StartupUpdateWindow? splash = null;
        try
        {
            var store = new StateStore();
            var loaded = store.Load();
            var settings = loaded.Settings;
            var crashSafeMode = !ignoreCrashLoop && settings.EnableCrashLoopProtection
                && StartupHealthService.GetSnapshot(settings.CrashLoopWindowMinutes,
                    settings.CrashLoopThreshold).ShouldUseSafeMode;
            var readOnly = store.EncounteredUnsupportedState;
            if (!StartupUpdatePolicy.ShouldPreflight(settings.EnableAutomaticUpdateChecks,
                    settings.InstallUpdatesAutomatically, safeMode || crashSafeMode,
                    readOnly, UpdateService.HasPendingRecovery(), externalCommand))
                return (false, false);

            using var skip = new CancellationTokenSource();
            splash = new StartupUpdateWindow();
            _startupUpdateWindow = splash;
            splash.SkipRequested += () => skip.Cancel();
            splash.SetStage("새 버전을 확인하고 있습니다.", true);
            splash.Show();
            // Give WPF a frame to paint before the asynchronous network request.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(skip.Token);
            checkTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            var source = UpdateService.ResolveManifestSource(settings.UpdateManifestSource,
                settings.EnableBetaUpdateChannel);
            var check = await UpdateService.CheckAsync(source, true,
                settings.UpdatePublicKeyPath, checkTimeout.Token);
            if (!check.UpdateAvailable)
            {
                DiagnosticsLog.Info("Startup update preflight: installed version is current");
                return (false, true);
            }

            splash.SetStage($"CyanDeck {check.LatestVersion.ToString(3)} 업데이트를 다운로드하고 검증 중입니다.\\n설치 전까지 바탕화면 그룹은 표시하지 않습니다.", true);
            var package = await UpdateService.StagePackageAsync(check, skip.Token);
            skip.Token.ThrowIfCancellationRequested();
            store.SaveRecoveryCheckpoint(loaded, $"before-startup-auto-update-{check.LatestVersion}");
            splash.SetStage("업데이트 설치 프로그램을 실행합니다.\\n설치가 끝나면 CyanDeck이 자동으로 시작됩니다.", false);
            // Keep the WPF window visible until the installer has actually painted
            // its independent WinForms progress window. A fixed sleep cannot do this.
            var readyName = "Local\\CyanDeck.UpdateReady." + Guid.NewGuid().ToString("N");
            using var readySignal = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
            using var installer = UpdateService.StartInstall(package, check.Manifest.Sha256,
                Environment.ProcessId, relaunch: true, readyEventName: readyName)
                ?? throw new InvalidOperationException("업데이트 설치 프로그램을 시작하지 못했습니다.");
            var installerReady = await Task.Run(() => readySignal.WaitOne(TimeSpan.FromSeconds(8)));
            if (!StartupUpdatePolicy.ShouldHandOffToInstaller(installerReady, !installer.HasExited))
            {
                // The old application has not exited yet, so the installer cannot
                // have replaced files. Abort the stalled installer and start normally.
                if (!installer.HasExited)
                {
                    try
                    {
                        installer.Kill(entireProcessTree: true);
                        await installer.WaitForExitAsync();
                    }
                    catch (Exception killError)
                    {
                        DiagnosticsLog.Error("Could not stop updater after handoff timeout", killError);
                    }
                }
                throw new InvalidOperationException("설치 진행 화면이 준비되지 않아 업데이트를 취소했습니다.");
            }
            DiagnosticsLog.Info($"Startup update preflight confirmed installer UI ready: version={check.LatestVersion}");
            splash.Close();
            splash = null;
            _startupUpdateWindow = null;
            Shutdown();
            return (true, true);
        }
        catch (OperationCanceledException)
        {
            DiagnosticsLog.Info("Startup update preflight skipped (timeout or user choice); opening installed application");
            return (false, true);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Startup update preflight failed; opening installed application", ex);
            return (false, true);
        }
        finally
        {
            _startupUpdateWindow = null;
            try { splash?.Close(); } catch { }
        }
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
                    if (!string.IsNullOrEmpty(command))
                        _ = Dispatcher.BeginInvoke(() => HandleActivationCommand(command));
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

    private void HandleActivationCommand(string command)
    {
        // Called on the WPF dispatcher, so queued commands preserve arrival order.
        if (_controller is null)
        {
            if (string.Equals(command, "activate", StringComparison.OrdinalIgnoreCase))
                _startupUpdateWindow?.Activate();
            else if (_pendingActivationCommands.Count < 32)
                _pendingActivationCommands.Add(command);
            return;
        }

        if (string.Equals(command, "activate", StringComparison.OrdinalIgnoreCase))
            _controller.ActivateFromSecondInstance();
        else if (string.Equals(command, "peek-toggle", StringComparison.OrdinalIgnoreCase))
            _controller.TogglePeekFromExternalCommand();
        else if (command.StartsWith("recover-groups|", StringComparison.Ordinal))
            _controller.RecoverAllGroupsToMonitor(command.Substring("recover-groups|".Length));
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
            _activationCts?.Dispose();
            _instanceGate?.Dispose();
            base.OnExit(e);
        }
    }
}

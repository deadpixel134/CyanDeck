using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;
using CyanDeck.Services;
using CyanDeck.Views;
using CyanDeck.Models;
using Microsoft.Win32;
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
    // Set on WPF's native Windows logoff/shutdown path. No I/O or shell
    // interaction is permitted in the session-end query callback.
    private bool _windowsSessionEnding;
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

        // Only the user-confirmed, token-authorized second process can erase
        // local application state, after the previous instance has stopped.
        var completeResetTicket = GetOptionValue(e.Args, "--complete-local-reset");
        if (completeResetTicket is not null)
        {
            try
            {
                CompleteLocalResetService.ExecutePending(DataRootPaths.ActiveRoaming, completeResetTicket);
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                    key?.DeleteValue("CyanDeck", throwOnMissingValue: false);
                }
                catch (Exception startupEx)
                {
                    MessageBox.Show("사용자 데이터는 삭제했지만 Windows 자동 시작 항목을 지우지 못했습니다.\n" +
                        startupEx.Message, "CyanDeck 완전 초기화", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception resetEx)
            {
                MessageBox.Show("완전 초기화에 실패했습니다. 데이터 보호를 위해 앱을 시작하지 않습니다.\n\n" +
                    resetEx.Message, "CyanDeck 완전 초기화 실패", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }
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
        // Brand-new installs configure both modules before hiding Explorer
        // icons, creating groups, registering hooks or running updates.
        var firstRun = FirstRunOnboardingPolicy.ShouldOffer(DataRootPaths.ActiveRoaming,
            safeMode, peekToggle || recoverGroups || managedPolicyPath is not null,
            UpdateService.HasPendingRecovery());
        var preflight = firstRun ? (ExitForUpdate: false, Attempted: false)
            : await RunStartupUpdatePreflightAsync(
                safeMode, ignoreCrashLoop, peekToggle || recoverGroups, managedPolicyPath);
        if (preflight.ExitForUpdate) return;
        if (firstRun)
        {
            try
            {
                Directory.CreateDirectory(DataRootPaths.ActiveRoaming);
                File.WriteAllText(Path.Combine(DataRootPaths.ActiveRoaming,
                    FirstRunOnboardingPolicy.PendingMarker), "First-run setup incomplete");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("First-run marker creation failed; Explorer left intact", ex);
                MessageBox.Show("첫 실행 설정 파일을 만들 수 없어 안전하게 종료합니다.",
                    "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(); return;
            }
            var firstStore = new StateStore();
            var wizard = new FirstRunWizardWindow(new DeckSettings(), configured =>
            {
                if (!FirstRunOnboardingPolicy.ShouldOffer(DataRootPaths.ActiveRoaming))
                    throw new IOException("기존 설정 또는 복구 데이터가 감지되어 덮어쓰지 않습니다.");
                firstStore.Save(new DeckState { Settings = configured });
                try { File.Delete(Path.Combine(DataRootPaths.ActiveRoaming, FirstRunOnboardingPolicy.PendingMarker)); }
                catch (IOException) { /* Durable layout.json is the completion sentinel. */ }
                DiagnosticsLog.Info("First-run configuration committed before desktop startup");
                return true;
            });
            if (wizard.ShowDialog() != true)
            {
                DiagnosticsLog.Info("First-run setup closed without any desktop changes");
                Shutdown(); return;
            }
        }
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
        long splashShownAt = 0;
        bool userRequestedSkip = false;
        try
        {
            var store = new StateStore();
            // The controller's own Load keeps the copies of unreadable files; do not copy twice.
            var loaded = store.Load(archiveUnreadable: false);
            var settings = loaded.Settings;
            var crashSafeMode = !ignoreCrashLoop && settings.EnableCrashLoopProtection
                && StartupHealthService.GetSnapshot(settings.CrashLoopWindowMinutes,
                    settings.CrashLoopThreshold).ShouldUseSafeMode;
            // v8.0.43: unreadable layout files start protected; never update over them.
            var readOnly = store.EncounteredUnsupportedState || store.EncounteredUnreadableState;
            if (!StartupUpdatePolicy.ShouldPreflight(settings.EnableAutomaticUpdateChecks,
                    settings.InstallUpdatesAutomatically, safeMode || crashSafeMode,
                    readOnly, UpdateService.HasPendingRecovery(), externalCommand))
                return (false, false);

            using var skip = new CancellationTokenSource();
            void ShowStartupStatus(string message, bool allowSkip)
            {
                if (splash is null)
                {
                    splash = new StartupUpdateWindow();
                    _startupUpdateWindow = splash;
                    splash.SkipRequested += () =>
                    {
                        userRequestedSkip = true;
                        skip.Cancel();
                    };
                    splash.SetStage(message, allowSkip);
                    splash.Show();
                    splashShownAt = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                else splash.SetStage(message, allowSkip);
            }

            using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(skip.Token);
            checkTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            var source = UpdateService.ResolveManifestSource(settings.UpdateManifestSource,
                settings.EnableBetaUpdateChannel);
            // Fast up-to-date starts should not create a transient top-level
            // progress window at all. Only display status if network checking
            // exceeds the grace period; all desktop/Calendar HWNDs still wait.
            var checkTask = UpdateService.CheckAsync(source, true,
                settings.UpdatePublicKeyPath, checkTimeout.Token);
            var graceTask = Task.Delay(StartupUpdatePresentationPolicy.CheckingGraceMilliseconds);
            if (await Task.WhenAny(checkTask, graceTask) != checkTask &&
                StartupUpdatePresentationPolicy.ShouldShowChecking(checkTask.IsCompleted,
                    StartupUpdatePresentationPolicy.CheckingGraceMilliseconds))
            {
                ShowStartupStatus("새 버전을 확인하고 있습니다.\\n이 화면이 끝나면 바탕화면이 열립니다.", true);
                await Dispatcher.InvokeAsync(() => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            var check = await checkTask;
            if (!check.UpdateAvailable)
            {
                DiagnosticsLog.Info("Startup update preflight: installed version is current");
                splash?.SetStage("현재 버전으로 CyanDeck을 시작합니다.", false);
                return (false, true);
            }
            // The independent installer may fail after the previous process
            // has handed off control. Repeatedly attempting the SAME signed
            // SHA-256 on every launch would create an endless update/relaunch
            // loop. Skip only automatic startup installation for 12 hours.
            // Manual update checks/installations remain available in Settings.
            if (FailedUpdateRetryPolicy.IsAutomaticRetryDeferred(
                DataRootPaths.ActiveLocal, check.Manifest.Sha256, DateTime.UtcNow))
            {
                DiagnosticsLog.Warn($"Startup update v{check.LatestVersion.ToString(3)} deferred because its package failed previously; manual update remains available");
                splash?.SetStage("이전 자동 업데이트에 실패하여 현재 버전으로 실행합니다.\\n환경설정에서 업데이트를 직접 다시 시도할 수 있습니다.", false);
                return (false, true);
            }
            // v8.0.43: the user rolled back from this version. Skip automatic reinstall until a
            // newer release is published (which releases the hold) or the user updates manually.
            if (UpdateRollbackHold.ShouldSkipAutomaticInstall(DataRootPaths.ActiveLocal,
                check.LatestVersion, out var heldVersion))
            {
                var heldText = heldVersion is null ? "?" : UpdateRollbackHold.Format(heldVersion);
                DiagnosticsLog.Info($"Startup update v{check.LatestVersion.ToString(3)} skipped: rolled back from v{heldText}; manual update remains available");
                splash?.SetStage("되돌린 버전은 자동으로 다시 설치하지 않습니다.\\n현재 버전으로 CyanDeck을 시작합니다.", false);
                return (false, true);
            }

            ShowStartupStatus($"CyanDeck {check.LatestVersion.ToString(3)} 업데이트를 다운로드하고 검증 중입니다.\\n설치 전까지 바탕화면 그룹은 표시하지 않습니다.", true);
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
            splash?.SetStage("업데이트 확인을 건너뛰고 현재 버전으로 시작합니다.", false);
            return (false, true);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Startup update preflight failed; opening installed application", ex);
            splash?.SetStage("업데이트를 확인할 수 없어 현재 버전으로 실행합니다.", false);
            return (false, true);
        }
        finally
        {
            // If a slow check finishes just after the status appeared, closing
            // in the same frame looks like a crash. Keep a minimum useful
            // reading interval. A user pressing Skip never incurs this wait.
            if (splash is not null && splashShownAt != 0)
            {
                var elapsed = (long)System.Diagnostics.Stopwatch
                    .GetElapsedTime(splashShownAt).TotalMilliseconds;
                var remaining = StartupUpdatePresentationPolicy.RemainingVisibleMilliseconds(
                    elapsed, userRequestedSkip);
                if (remaining > 0) await Task.Delay(remaining);
            }
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
            process.WaitForExit(args.Any(a => string.Equals(a, "--complete-local-reset",
                StringComparison.OrdinalIgnoreCase)) ? 25000 : 8000);
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

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // WM_QUERYENDSESSION must be acknowledged quickly. WPF raises Exit
        // after the session end is accepted; heavy cleanup belongs neither in
        // this query nor in the OS shutdown path. Never cancel PC shutdown.
        _windowsSessionEnding = true;
        e.Cancel = false;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _activationCts?.Cancel(); } catch { }
        try
        {
            if (_windowsSessionEnding)
                _controller?.DisposeForWindowsSessionEnd();
            else
                _controller?.Dispose();
        }
        finally
        {
            _activationCts?.Dispose();
            _instanceGate?.Dispose();
            base.OnExit(e);
        }
    }
}

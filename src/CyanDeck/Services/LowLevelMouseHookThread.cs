using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using CyanDeck.Interop;

namespace CyanDeck.Services;

/// <summary>
/// Implemented by each CyanDeck WH_MOUSE_LL hook owner so the shared input thread can
/// notice that Windows silently removed a hook (LowLevelHooksTimeout) and reinstall it.
/// Every member is called on the dedicated hook thread only.
/// </summary>
internal interface ILowLevelMouseHookClient
{
    string HookName { get; }
    // Started, not disposed and not disabled after a callback failure.
    bool WantsHook { get; }
    bool HasHookHandle { get; }
    long CallbackCount { get; }
    void ReinstallHookOnHookThread();
}

/// <summary>
/// v8.0.44: one dedicated background thread with its own message loop owns every CyanDeck
/// low-level mouse hook. Windows delivers WH_MOUSE_LL callbacks to the installing thread
/// while the whole system's mouse input waits, so a busy WPF dispatcher (layout, saving,
/// a modal dialog) must never be that thread. Hook callbacks only classify the event and
/// marshal work to the UI asynchronously. A watchdog timer on the same thread detects a
/// hook that stopped receiving input while the cursor moved and reinstalls it.
/// No keyboard hook is ever installed here.
/// </summary>
internal sealed class LowLevelMouseHookThread
{
    private const uint WmTimer = 0x0113;
    private const uint WmInvoke = 0x8000 + 0x5C; // WM_APP range; posted to this thread only.
    private const uint PmNoRemove = 0x0000;
    private const uint WatchdogIntervalMs = 2000;

    private static readonly object SharedGate = new();
    private static LowLevelMouseHookThread? _shared;

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly List<ClientState> _clients = []; // hook thread only
    private Thread? _thread;
    private volatile uint _nativeThreadId;
    private int _managedThreadId = -1;
    private NativeMethods.POINT _lastCursor;
    private bool _hasLastCursor;
    private int _reinstallLogCount;

    private sealed class ClientState
    {
        internal ClientState(ILowLevelMouseHookClient client) => Client = client;
        internal ILowLevelMouseHookClient Client { get; }
        internal long LastCallbackCount;
        internal int Strikes;
        internal long LastReinstallMs = long.MinValue / 4;
        internal int Reinstalls;
    }

    private LowLevelMouseHookThread() { }

    internal bool IsCurrentThread => Environment.CurrentManagedThreadId == Volatile.Read(ref _managedThreadId);

    private bool IsRunning => _thread is { IsAlive: true } && _nativeThreadId != 0;

    /// <summary>Returns the process-wide hook thread, starting it on first use.</summary>
    internal static bool TryGetShared([NotNullWhen(true)] out LowLevelMouseHookThread? thread)
    {
        lock (SharedGate)
        {
            if (_shared is { IsRunning: true } existing)
            {
                thread = existing;
                return true;
            }

            var created = new LowLevelMouseHookThread();
            if (created.TryStart())
            {
                _shared = created;
                thread = created;
                return true;
            }
        }

        thread = null;
        return false;
    }

    private bool TryStart()
    {
        try
        {
            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "CyanDeck desktop mouse hook",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread = thread;
            thread.Start();
            if (_ready.Wait(TimeSpan.FromSeconds(3)) && _nativeThreadId != 0) return true;
            DiagnosticsLog.Warn("Dedicated desktop mouse hook thread did not start; hooks stay on the UI thread.");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Dedicated desktop mouse hook thread could not start", ex);
        }
        return false;
    }

    private void Run()
    {
        try
        {
            Volatile.Write(ref _managedThreadId, Environment.CurrentManagedThreadId);
            // Create this thread's message queue before PostThreadMessage can target it.
            PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
            _nativeThreadId = GetCurrentThreadId();
            if (SetTimer(IntPtr.Zero, UIntPtr.Zero, WatchdogIntervalMs, IntPtr.Zero) == UIntPtr.Zero)
                DiagnosticsLog.Warn("Mouse hook watchdog timer could not be created; removed hooks are not reinstalled automatically.");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Desktop mouse hook thread initialization failed", ex);
        }
        finally
        {
            _ready.Set();
        }

        while (true)
        {
            var result = GetMessage(out var msg, IntPtr.Zero, 0, 0);
            if (result == 0 || result == -1) break;
            if (msg.hwnd == IntPtr.Zero && msg.message == WmInvoke)
            {
                DrainWork();
                continue;
            }
            if (msg.hwnd == IntPtr.Zero && msg.message == WmTimer)
            {
                // A failed PostThreadMessage (full queue) is recovered here as well.
                DrainWork();
                RunWatchdog();
                continue;
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        _nativeThreadId = 0;
        DiagnosticsLog.Warn("Desktop mouse hook thread message loop ended.");
    }

    /// <summary>Queues work to run on the hook thread without waiting.</summary>
    internal void Post(Action action)
    {
        _work.Enqueue(action);
        var id = _nativeThreadId;
        if (id != 0) PostThreadMessage(id, WmInvoke, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Runs work on the hook thread and waits (bounded) for it to finish.</summary>
    internal bool Invoke(Action action, int timeoutMs = 3000)
    {
        if (IsCurrentThread)
        {
            action();
            return true;
        }

        var done = new ManualResetEventSlim(false);
        Exception? failure = null;
        Post(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        });
        if (!done.Wait(timeoutMs))
        {
            DiagnosticsLog.Warn("Desktop mouse hook thread did not answer in time.");
            return false;
        }
        if (failure is not null)
        {
            DiagnosticsLog.Error("Desktop mouse hook thread operation failed", failure);
            return false;
        }
        return true;
    }

    internal void Register(ILowLevelMouseHookClient client) => Post(() =>
    {
        if (_clients.Any(state => ReferenceEquals(state.Client, client))) return;
        _clients.Add(new ClientState(client) { LastCallbackCount = client.CallbackCount });
    });

    internal void Unregister(ILowLevelMouseHookClient client) =>
        Post(() => _clients.RemoveAll(state => ReferenceEquals(state.Client, client)));

    private void DrainWork()
    {
        while (_work.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { DiagnosticsLog.Error("Desktop mouse hook thread work item failed", ex); }
        }
    }

    private void RunWatchdog()
    {
        if (_clients.Count == 0) return;

        var cursorMoved = false;
        // GetCursorPos fails on the secure desktop (UAC, lock screen): no evidence either way.
        if (GetCursorPos(out var cursor))
        {
            cursorMoved = _hasLastCursor && (cursor.X != _lastCursor.X || cursor.Y != _lastCursor.Y);
            _lastCursor = cursor;
            _hasLastCursor = true;
        }

        var now = Environment.TickCount64;
        foreach (var state in _clients.ToArray())
        {
            try
            {
                var client = state.Client;
                if (!client.WantsHook)
                {
                    state.Strikes = 0;
                    continue;
                }

                var count = client.CallbackCount;
                var observed = count != state.LastCallbackCount;
                state.LastCallbackCount = count;
                var decision = MouseHookWatchdogPolicy.Evaluate(
                    cursorMoved, observed, state.Strikes, now - state.LastReinstallMs, client.HasHookHandle);
                state.Strikes = decision.Strikes;
                if (!decision.Reinstall) continue;

                state.LastReinstallMs = now;
                state.Reinstalls++;
                client.ReinstallHookOnHookThread();
                state.LastCallbackCount = client.CallbackCount;
                if (_reinstallLogCount < 20 || state.Reinstalls % 50 == 0)
                {
                    _reinstallLogCount++;
                    var name = client.HookName;
                    var installed = client.HasHookHandle;
                    DiagnosticsLog.Warn($"Desktop mouse hook reinstalled: hook={name}; count={state.Reinstalls}; installed={installed}; cursor-moved={cursorMoved}.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("Desktop mouse hook watchdog failed", ex);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public NativeMethods.POINT pt;
        public uint lPrivate;
    }

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage lpMsg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(ref NativeMessage lpMsg);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativeMethods.POINT lpPoint);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}

/// <summary>
/// Short-lived cache of the canonical Explorer desktop HWND chain for hook callbacks.
/// Avoids an EnumWindows/GetClassName walk on every click; the chain is revalidated with
/// IsWindow/GetParent on each use and refreshed at least every 1.5 seconds.
/// </summary>
internal static class DesktopShellHandleCache
{
    private const long MaximumAgeMs = 1500;
    private sealed record Entry(IntPtr Parent, IntPtr DefView, IntPtr ListView, long TimestampMs);
    private static Entry? _entry;

    internal static bool TryLocate(out IntPtr parent, out IntPtr defView, out IntPtr listView)
    {
        var now = Environment.TickCount64;
        var cached = Volatile.Read(ref _entry);
        if (cached is not null && now - cached.TimestampMs <= MaximumAgeMs && IsStillValid(cached))
        {
            parent = cached.Parent;
            defView = cached.DefView;
            listView = cached.ListView;
            return true;
        }

        if (DesktopHost.TryLocateDesktopShell(out parent, out defView, out listView))
        {
            Volatile.Write(ref _entry, new Entry(parent, defView, listView, now));
            return true;
        }

        Volatile.Write(ref _entry, null);
        return false;
    }

    private static bool IsStillValid(Entry entry) =>
        NativeMethods.IsWindow(entry.ListView)
        && NativeMethods.IsWindow(entry.DefView)
        && NativeMethods.GetParent(entry.ListView) == entry.DefView
        && NativeMethods.GetParent(entry.DefView) == entry.Parent;
}

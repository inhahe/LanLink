using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;

namespace LanLink;

public partial class App : Application
{
    private const string MutexName     = "LanLink_SingleInstance_37656";
    private const string ShowEventName = "LanLink_ShowWindow_37656";
    private const string ExitEventName = "LanLink_Exit_37656";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _exitEvent;
    private Thread? _listenerThread;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool wantExit    = e.Args.Contains("--exit",      StringComparer.OrdinalIgnoreCase);
        bool wantMinimal = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

        // Try to acquire single-instance mutex.
        _instanceMutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            // Another instance is already running.
            if (wantExit)
            {
                // IPC "exit" command (used by the installer to shut LanLink down
                // before upgrading).  Signal the running instance to quit and
                // wait for it to actually terminate so the exe is unlocked.
                SignalRunningInstanceToExit();
            }
            else if (!wantMinimal)
            {
                // A normal manual re-launch — bring the existing window forward.
                SignalRunningInstance(ShowEventName);
            }
            // else: duplicate autostart launch (--minimized) — do nothing so we
            // don't pop the window open; just exit quietly.

            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        // We are the first (and only) instance.

        if (wantExit)
        {
            // Nothing is running to stop — just leave.
            try { _instanceMutex.ReleaseMutex(); } catch { }
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        // Create the IPC events and start listening for show/exit signals.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _listenerThread = new Thread(ListenForActivation)
        {
            IsBackground = true,
            Name = "SingleInstanceListener"
        };
        _listenerThread.Start();

        // Start hidden if launched with --minimized OR if the user enabled
        // "Start minimized to tray" in Settings.  Relying on the CLI flag alone
        // meant the setting was ignored on a normal (manual) launch and only
        // worked when Windows started us via the registry Run key.
        bool startHidden = wantMinimal || AppSettings.Load().StartMinimized;

        var win = new MainWindow();
        MainWindow = win;

        if (startHidden)
            win.Initialize();   // start networking without showing the window
        else
            win.Show();
    }

    // ------------------------------------------------------------------ IPC helpers

    private static void SignalRunningInstance(string eventName)
    {
        try
        {
            var evt = EventWaitHandle.OpenExisting(eventName);
            evt.Set();
            evt.Dispose();
        }
        catch { /* the other instance may have just exited */ }
    }

    /// <summary>
    /// Tell the running instance to exit, then block until it has actually
    /// terminated.  Called for the <c>--exit</c> IPC command so an installer
    /// can replace LanLink.exe without a file-in-use conflict.
    /// </summary>
    private static void SignalRunningInstanceToExit()
    {
        SignalRunningInstance(ExitEventName);

        try
        {
            int me = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("LanLink"))
            {
                if (p.Id == me) { p.Dispose(); continue; }
                try { p.WaitForExit(15_000); } catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ listener

    private void ListenForActivation()
    {
        var showEvt = _showEvent;
        var exitEvt = _exitEvent;
        if (showEvt is null || exitEvt is null) return;

        var handles = new WaitHandle[] { showEvt, exitEvt };

        while (true)
        {
            int idx;
            try { idx = WaitHandle.WaitAny(handles, 1000); }
            catch (ObjectDisposedException) { break; }

            if (idx == WaitHandle.WaitTimeout) continue;

            if (idx == 0)
            {
                // Show / activate the window.
                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is MainWindow win)
                    {
                        win.Show();
                        win.WindowState = WindowState.Normal;
                        win.Activate();
                    }
                });
            }
            else // idx == 1 → exit requested via IPC
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is MainWindow win) win.RequestExit();
                    else                              Shutdown();
                });
                break;
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showEvent?.Dispose();
        _showEvent = null;
        _exitEvent?.Dispose();
        _exitEvent = null;
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        base.OnExit(e);
    }
}

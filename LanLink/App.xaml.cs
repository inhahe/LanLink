using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace LanLink;

public partial class App : Application
{
    private const string MutexName = "LanLink_SingleInstance_48656";
    private const string EventName = "LanLink_ShowWindow_48656";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private Thread? _listenerThread;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Try to acquire single-instance mutex.
        _instanceMutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            // Another instance is already running — signal it to show and exit.
            try
            {
                var evt = EventWaitHandle.OpenExisting(EventName);
                evt.Set();
                evt.Dispose();
            }
            catch { }

            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        // We are the first instance — create the show-window event and listen for it.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        _listenerThread = new Thread(ListenForActivation)
        {
            IsBackground = true,
            Name = "SingleInstanceListener"
        };
        _listenerThread.Start();

        bool startHidden = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

        var win = new MainWindow();
        MainWindow = win;

        if (startHidden)
        {
            // Initialize networking without showing the window.
            win.Initialize();
        }
        else
        {
            win.Show();
        }
    }

    private void ListenForActivation()
    {
        while (_showEvent is not null)
        {
            try
            {
                if (!_showEvent.WaitOne(1000))
                    continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            // Signal received — bring window to foreground on the UI thread.
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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showEvent?.Dispose();
        _showEvent = null;
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        base.OnExit(e);
    }
}

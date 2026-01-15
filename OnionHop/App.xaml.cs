using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace OnionHop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "OnionHop.SingleInstance";
    private const string ActivateEventName = "OnionHop.Activate";
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCts;
    private bool _helperMode;

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupLogger.Write($"App starting. Args: {string.Join(" ", e.Args)}");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            if (AdminHelperServer.IsHelperMode(e.Args))
            {
                _helperMode = true;
                StartupLogger.Write("Helper mode detected.");
                AdminHelperServer.Run();
                StartupLogger.Write("Helper mode finished.");
                Shutdown();
                return;
            }

            _instanceMutex = new Mutex(true, SingleInstanceMutexName, out _ownsMutex);

            if (!_ownsMutex)
            {
                StartupLogger.Write("Second instance detected. Signaling existing instance.");
                TrySignalExistingInstance();
                Shutdown();
                return;
            }

            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activationCts = new CancellationTokenSource();
            StartActivationListener(_activationEvent, _activationCts.Token);

            // Clean up any orphaned helper processes from previous runs
            CleanupOrphanedHelperProcesses();

            base.OnStartup(e);

            StartupLogger.Write("Creating MainWindow.");
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            StartupLogger.Write("MainWindow shown.");
        }
        catch (Exception ex)
        {
            StartupLogger.Write("Unhandled exception during startup.", ex);
            if (!_helperMode)
            {
                System.Windows.MessageBox.Show("OnionHop failed to start. See startup.log in %LOCALAPPDATA%\\OnionHop.", "OnionHop");
            }
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_helperMode)
        {
            base.OnExit(e);
            return;
        }

        _activationCts?.Cancel();
        _activationEvent?.Dispose();
        if (_instanceMutex != null)
        {
            if (_ownsMutex)
            {
                try
                {
                    _instanceMutex.ReleaseMutex();
                }
                catch (Exception ex)
                {
                    StartupLogger.Write($"OnExit: Failed to release mutex: {ex.Message}");
                }
            }
            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    public void ReleaseSingleInstance()
    {
        _activationCts?.Cancel();
        _activationEvent?.Dispose();
        _activationEvent = null;
        _activationCts = null;

        if (_instanceMutex != null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch
            {
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
        }
    }

    public bool TryReacquireSingleInstance()
    {
        if (_instanceMutex != null)
        {
            return true;
        }

        var createdNew = false;
        _instanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
        if (!createdNew)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activationCts = new CancellationTokenSource();
        StartActivationListener(_activationEvent, _activationCts.Token);
        return true;
    }

    private void StartActivationListener(EventWaitHandle activationEvent, CancellationToken token)
    {
        _ = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!activationEvent.WaitOne(Timeout.Infinite))
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                Dispatcher.Invoke(ActivateMainWindow);
            }
        }, token);
    }

    private void ActivateMainWindow()
    {
        if (Current?.MainWindow is MainWindow mainWindow)
        {
            mainWindow.RestoreFromExternalActivation();
            return;
        }

        Current?.MainWindow?.Show();
        Current?.MainWindow?.Activate();
    }

    private static void TrySignalExistingInstance()
    {
        try
        {
            using var evt = EventWaitHandle.OpenExisting(ActivateEventName);
            evt.Set();
        }
        catch
        {
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        StartupLogger.Write("Dispatcher unhandled exception.", e.Exception);
        if (!_helperMode)
        {
            System.Windows.MessageBox.Show("OnionHop encountered an error. See startup.log in %LOCALAPPDATA%\\OnionHop.", "OnionHop");
        }
        e.Handled = true;
        Shutdown();
    }

    private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        StartupLogger.Write("AppDomain unhandled exception.", e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        StartupLogger.Write("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }

    private static void CleanupOrphanedHelperProcesses()
    {
        try
        {
            var currentPid = Environment.ProcessId;
            var currentExeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "OnionHop");
            
            foreach (var proc in Process.GetProcessesByName(currentExeName))
            {
                try
                {
                    // Don't kill ourselves
                    if (proc.Id == currentPid)
                    {
                        continue;
                    }
                    
                    // Only kill processes that have been running longer than us
                    // (they're likely orphaned helpers from previous runs)
                    if (!proc.HasExited && proc.StartTime < Process.GetCurrentProcess().StartTime)
                    {
                        StartupLogger.Write($"Killing orphaned process: PID {proc.Id}, started at {proc.StartTime}");
                        proc.Kill();
                        proc.WaitForExit(2000);
                    }
                }
                catch (Exception ex)
                {
                    StartupLogger.Write($"Failed to kill orphaned process {proc.Id}: {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"CleanupOrphanedHelperProcesses failed: {ex.Message}");
        }
    }
}

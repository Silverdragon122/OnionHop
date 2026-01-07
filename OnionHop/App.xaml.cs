using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace OnionHop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "OnionHop.SingleInstance";
    private const string ActivateEventName = "OnionHop.Activate";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        var createdNew = false;
        _instanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);

        if (!createdNew)
        {
            TrySignalExistingInstance();
            Shutdown();
            return;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activationCts = new CancellationTokenSource();
        StartActivationListener(_activationEvent, _activationCts.Token);

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationCts?.Cancel();
        _activationEvent?.Dispose();
        if (_instanceMutex != null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }

        base.OnExit(e);
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
}

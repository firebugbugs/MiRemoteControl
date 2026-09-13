using Avalonia;
using Avalonia.Threading;
using MiRemoteControl.Desktop.Infrastructure;

namespace MiRemoteControl.Desktop;

internal static class Program
{
    private const string InstanceMutexName = "MiRemoteControl.Desktop.SingleInstance.v1";
    private const string ActivateEventName = "MiRemoteControl.Desktop.Activate.v1";
    internal static bool IsBackgroundLaunch { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (HidTapHelperHost.TryParseArguments(args, out var helperArguments))
        {
            HidTapHelperHost.Run(helperArguments);
            return;
        }

        IsBackgroundLaunch = args.Contains("--background", StringComparer.Ordinal);
        using var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        using var instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (!IsBackgroundLaunch) activateEvent.Set();
            return;
        }

        var listenForActivation = true;
        var activationListener = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    activateEvent.WaitOne();
                    if (!Volatile.Read(ref listenForActivation)) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (Application.Current is App app) app.ShowMainWindow();
                    });
                }
            }
            catch (ObjectDisposedException) { }
        })
        {
            IsBackground = true,
            Name = "Mi Remote Studio activation listener"
        };
        activationListener.Start();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Volatile.Write(ref listenForActivation, false);
            activateEvent.Set();
            activationListener.Join(millisecondsTimeout: 500);
            instanceMutex.ReleaseMutex();
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}

using System;
using Avalonia;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.Avalonia;

sealed class Program
{
    private static Mutex? _singleInstanceMutex;

    internal static event Action<string[]>? SecondaryInstanceArgsReceived;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashHandler.ReportFatalException((Exception)e.ExceptionObject);

        // Handle pre-UI CLI arguments (settings manipulation, help, etc.) without
        // launching the Avalonia UI. Mirrors WinUI's EntryPoint.cs dispatch logic.
        if (AvaloniaCliHandler.HandlePreUiArgs(args) is { } exitCode)
        {
            Environment.Exit(exitCode);
            return;
        }

        CoreData.WasDaemon = CoreData.IsDaemon = args.Contains(AvaloniaCliHandler.DAEMON);

        if (!TryRegisterSingleInstance(args))
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static bool TryRegisterSingleInstance(string[] args)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: CoreData.MainWindowIdentifier,
            createdNew: out bool createdNew
        );

        if (createdNew)
        {
            SingleInstanceRedirector.StartListener(args =>
                SecondaryInstanceArgsReceived?.Invoke(args)
            );
            return true;
        }

        if (SingleInstanceRedirector.TryForwardToFirstInstance(args))
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return false;
        }

        Logger.Warn("Could not redirect to the existing Avalonia instance; starting a new one");
        return true;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

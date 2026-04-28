using System;
using Avalonia;
using UniGetUI.Core.Data;

namespace UniGetUI.Avalonia;

sealed class Program
{
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

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

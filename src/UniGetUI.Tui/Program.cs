using Avalonia;
using Consolonia;
using Consolonia.Fonts;
using Consolonia.ManagedWindows.Storage;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Tui.Infrastructure;

namespace UniGetUI.Tui;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Error("Unobserved task exception in UniGetUI TUI:");
            Logger.Error(e.Exception);
            e.SetObserved();
        };

        PrintBanner();

        // Load-then-show: fully initialize the engine before the first frame so the shell can render
        // real manager state immediately. Manager initialization is internally time-bounded.
        Console.WriteLine("Initializing UniGetUI engine (loading package managers)...");
        try
        {
            TuiBootstrapper.Initialize();
        }
        catch (Exception ex)
        {
            Logger.Error("Fatal error during TUI engine bootstrap:");
            Logger.Error(ex);
            Console.Error.WriteLine($"Failed to initialize UniGetUI engine: {ex.Message}");
            Environment.Exit(1);
            return;
        }

        BuildAvaloniaApp().StartWithConsoleLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .LogToException()
            .UseSkia()
            .UseConsoloniaStorage()
            .UseConsolonia()
            .UseAutoDetectedConsole()
            .WithConsoleFonts()
            .ThrowOnErrors();

    private static void PrintBanner()
    {
        Console.WriteLine();
        Console.WriteLine("  UniGetUI — Terminal UI");
        Console.WriteLine($"  Version {CoreData.VersionName} (build {CoreData.BuildNumber})");
        Console.WriteLine("  Powered by the UniGetUI package engine · Consolonia · Avalonia 12");
        Console.WriteLine();
    }
}

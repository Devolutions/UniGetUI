using Avalonia;
using System.ComponentModel;
using Consolonia;
using Consolonia.Fonts;
using Consolonia.ManagedWindows.Storage;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Interface;
using UniGetUI.Tui.FakeData;
using UniGetUI.Tui.Infrastructure;

namespace UniGetUI.Tui;

public static class TuiAppHost
{
    public const string Command = "tui";

    public static bool IsCommand(IReadOnlyList<string> args)
        => args.Count > 0 && args[0].Equals(Command, StringComparison.OrdinalIgnoreCase);

    public static bool TryRunFakeCacheCommand(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] != "cache"
            || Environment.GetEnvironmentVariable(FakePackageManagerProcess.ChildMarkerVariable) != "1")
            return false;

        WindowsConsoleHost.PrepareCliIO();
        return FakePackageManagerProcess.TryRun(args, out exitCode);
    }

    public static int Run(string[] args)
    {
        // A fake-data operation re-enters this binary as its "package manager" process. Handle that
        // before anything else so the child never initializes settings, the engine or the console UI.
        if (args.Length > 0 && args[0] is FakePackageManagerProcess.PmFlag or FakePackageManagerProcess.ElevateFlag)
        {
            WindowsConsoleHost.PrepareCliIO();
            if (FakePackageManagerProcess.TryRun(args, out int fakeExitCode))
                return fakeExitCode;
        }

        if (TryRunFakeCacheCommand(args, out int cacheExitCode))
            return cacheExitCode;

        CoreData.SetSanitizedProcessArguments([Command, .. args]);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Error("Unobserved task exception in UniGetUI TUI:");
            Logger.Error(e.Exception);
            e.SetObserved();
        };

        TuiCommandLine commandLine;
        try
        {
            commandLine = TuiCommandLine.Parse(args);
        }
        catch (ArgumentException ex)
        {
            WindowsConsoleHost.PrepareCliIO();
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Run with tui --help for usage.");
            return 2;
        }

        if (commandLine.ShowHelp)
        {
            WindowsConsoleHost.PrepareCliIO();
            Console.WriteLine(TuiCommandLine.HelpText);
            Console.WriteLine("Themes:");
            foreach (UniGetUI.Tui.Theme.TuiTheme theme in UniGetUI.Tui.Theme.TuiThemes.All)
                Console.WriteLine($"  {theme.Id,-30}  {theme.Name}");
            return 0;
        }

        if (commandLine.HasHeadlessCommand)
        {
            WindowsConsoleHost.PrepareCliIO();
        }
        else
        {
            try
            {
                WindowsConsoleHost.PrepareInteractiveIO();
            }
            catch (Exception ex) when (ex is IOException or Win32Exception)
            {
                ReportConsoleStartupFailure(ex);
                return 1;
            }
        }

        if (commandLine.FakeData)
        {
            FakeDataEnvironment env = FakeDataEnvironment.Activate(commandLine.FakeDataDirectory);
            Console.WriteLine($"FAKE DATA MODE: sandbox at {env.SandboxDirectory}");
            Console.WriteLine("No real package manager is called and nothing is installed.");
        }

        // Settings-only commands (import/export/enable/disable) run headless and exit.
        if (commandLine.TryRunHeadlessCommand(out int headlessExitCode))
            return headlessExitCode;

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
            return 1;
        }

        TuiStartup.Current = commandLine;
        AppBuilder app;
        try
        {
            app = BuildAvaloniaApp();
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            ReportConsoleStartupFailure(ex);
            return 1;
        }
        return app.StartWithConsoleLifetime(commandLine.RemainingArgs);
    }

    private static void ReportConsoleStartupFailure(Exception exception)
    {
        Logger.Error("Failed to start the terminal console:");
        Logger.Error(exception);
        Console.Error.WriteLine($"Failed to start the terminal console: {exception.Message}");
        if (OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("In PowerShell, run the adjacent uniget.exe tui instead of launching UniGetUI.exe directly.");
            Console.Error.WriteLine("To launch UniGetUI.exe itself, use Start-Process with -NoNewWindow -Wait.");
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .LogToException()
            // Consolonia renders and shapes terminal text without initializing the desktop Skia backend.
            .UseConsoloniaStorage()
            .UseConsolonia()
            .UseAutoDetectedConsole()
            .WithConsoleFonts()
            .WithoutConsolePasteSniffer()
            .ThrowOnErrors();

    private static void PrintBanner()
    {
        Console.WriteLine();
        Console.WriteLine("  UniGetUI — Terminal UI");
        Console.WriteLine($"  Version {CoreData.VersionName}");
        Console.WriteLine();
    }
}

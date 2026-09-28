using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Avalonia;
using Consolonia;
using Consolonia.Fonts;
using Consolonia.ManagedWindows.Storage;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Tui.FakeData;
using UniGetUI.Tui.Infrastructure;

namespace UniGetUI.Tui;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // A fake-data operation re-enters this binary as its "package manager" process. Handle that
        // before anything else so the child never initializes settings, the engine or the console UI.
        if (FakePackageManagerProcess.TryRun(args, out int fakeExitCode))
            return fakeExitCode;

        // Translations and the translator / contributor lists are embedded in this executable (UniGetUI.Tui.csproj),
        // so a published TUI is a single file with no Assets folder next to it.
        BundledAssets.Provider = static path => Assembly.GetExecutingAssembly().GetManifestResourceStream("UniGetUI.Assets/" + path);

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
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Run with --help for usage.");
            return 2;
        }

        if (commandLine.ShowHelp)
        {
            Console.WriteLine(TuiCommandLine.HelpText);
            Console.WriteLine("Themes:");
            foreach (UniGetUI.Tui.Theme.TuiTheme theme in UniGetUI.Tui.Theme.TuiThemes.All)
                Console.WriteLine($"  {theme.Id,-30}  {theme.Name}");
            return 0;
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
        return BuildAvaloniaApp().StartWithConsoleLifetime(commandLine.RemainingArgs);
    }

    // Consolonia creates these internal Avalonia types by name through reflection (UseClipboard and the console
    // window's launcher). Nothing references them statically, so trimming / NativeAOT would remove them.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors,
        "Avalonia.Input.Platform.Clipboard", "Avalonia.Base")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors,
        "Avalonia.Platform.Storage.FileIO.BclLauncher", "Avalonia.Base")]
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .LogToException()
            // No .UseSkia(): Consolonia renders and shapes text itself and only uses a fallback renderer for bitmaps,
            // which the TUI never draws. Leaving Skia out drops libSkiaSharp.dll and libHarfBuzzSharp.dll.
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
        Console.WriteLine($"  Version {CoreData.VersionName} (build {CoreData.BuildNumber})");
        Console.WriteLine();
    }
}

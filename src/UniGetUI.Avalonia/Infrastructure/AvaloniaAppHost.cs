using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
#if WINDOWS
using Avalonia.Win32;
#endif
using Avalonia.Threading;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface;

namespace UniGetUI.Avalonia.Infrastructure;

public static class AvaloniaAppHost
{
    private static Mutex? _singleInstanceMutex;
    private static FileStream? _singleInstanceLock;

    public static event Action<string[]>? SecondaryInstanceArgsReceived;

    public static void Run(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashHandler.ReportFatalException((Exception)e.ExceptionObject);

        if (ShouldPrepareCliConsole(args))
        {
            WindowsConsoleHost.PrepareCliIO();
        }

        if (AvaloniaCliHandler.HandlePreUiArgs(args) is { } exitCode)
        {
            Environment.Exit(exitCode);
            return;
        }

        if (IpcCliSyntax.IsIpcCommand(args))
        {
            Environment.ExitCode = IpcCliCommandRunner.RunAsync(args, Console.Out, Console.Error)
                .GetAwaiter()
                .GetResult();
            return;
        }

        if (HeadlessModeOptions.IsHeadless(args))
        {
            Environment.ExitCode = HeadlessDaemonHost.RunAsync().GetAwaiter().GetResult();
            return;
        }

        CoreData.WasDaemon = CoreData.IsDaemon = args.Contains(AvaloniaCliHandler.DAEMON);

        string textart = $"""
               __  __      _ ______     __  __  ______
              / / / /___  (_) ____/__  / /_/ / / /  _/
             / / / / __ \/ / / __/ _ \/ __/ / / // /
            / /_/ / / / / / /_/ /  __/ /_/ /_/ // /
            \____/_/ /_/_/\____/\___/\__/\____/___/
                Welcome to UniGetUI Version {CoreData.VersionName}
            """;

        Logger.RedactUsername = Core.SettingsEngine.Settings.Get(Core.SettingsEngine.Settings.K.RedactUsernameInLog);

        Logger.ImportantInfo(textart);
        Logger.ImportantInfo("  ");
        Logger.ImportantInfo($"Build {CoreData.BuildNumber}");
        Logger.ImportantInfo("UI Framework: Avalonia");
        Logger.ImportantInfo($"Data directory {CoreData.UniGetUIDataDirectory}");
        Logger.ImportantInfo($"OS: {RuntimeInformation.OSDescription}");
        Logger.ImportantInfo($"Process arch: {RuntimeInformation.ProcessArchitecture} (OS: {RuntimeInformation.OSArchitecture})");
        Logger.ImportantInfo($"Runtime: {RuntimeInformation.FrameworkDescription}");
        Logger.ImportantInfo($"Elevated: {CoreTools.IsAdministrator()}");
        Logger.ImportantInfo($"Packaged (MSIX): {CoreTools.IsPackagedApp()}");
        Logger.ImportantInfo($"Args: {(args.Length > 0 ? string.Join(" ", args) : "(none)")}");

        // Bind Avalonia's UI-thread dispatcher to this (main/STA) thread before the single-instance
        // listener starts: if a second instance connects mid-startup, the listener's Dispatcher.UIThread.Post
        // would otherwise bind it to the worker thread and make Win32Platform.Initialize throw.
        _ = Dispatcher.UIThread;

        if (!TryRegisterSingleInstance(args))
        {
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        AppBuilder builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();

#if WINDOWS
        if (WindowsAvaloniaRenderingPolicy.ShouldUseSoftwareRendering)
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software],
            });
        }
#endif

        return builder.LogToTrace();
    }

    private static bool ShouldPrepareCliConsole(IReadOnlyList<string> args)
    {
        return IpcCliSyntax.HasVerbCommand(args);
    }

    private static bool TryRegisterSingleInstance(string[] args)
    {
        // macOS uses a file lock instead of a Mutex: named Mutexes are not shared across processes
        // under NativeAOT (what ships), so they can't detect the first instance.
        if (OperatingSystem.IsWindows())
            return TryRegisterWithMutex(args);

        if (OperatingSystem.IsMacOS())
            return TryRegisterWithFileLock(args);

        return true;
    }

    private static bool TryRegisterWithMutex(string[] args)
    {
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: CoreData.MainWindowIdentifier,
            createdNew: out bool createdNew
        );

        if (createdNew)
        {
            SingleInstanceRedirector.StartListener(a => SecondaryInstanceArgsReceived?.Invoke(a));
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

    private static bool TryRegisterWithFileLock(string[] args)
    {
        // FileShare.None is an flock() advisory lock the OS releases on exit (even on crash), so the
        // lock file never goes stale. The static FileStream holds the lock for the process lifetime.
        string lockPath = Path.Combine(Path.GetTempPath(), $"UniGetUI_{Environment.UserName}.lock");
        try
        {
            _singleInstanceLock = new FileStream(
                lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            if (SingleInstanceRedirector.TryForwardToFirstInstance(args))
                return false;

            Logger.Warn("Could not redirect to the existing Avalonia instance; starting a new one");
            return true;
        }

        SingleInstanceRedirector.StartListener(a => SecondaryInstanceArgsReceived?.Invoke(a));
        return true;
    }
}

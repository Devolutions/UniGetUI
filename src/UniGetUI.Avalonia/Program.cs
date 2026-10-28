using System;
using Avalonia;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Data;
#if WINDOWS
using UniGetUI.Interface;
using UniGetUI.PackageEngine.Managers.InstallerFeeds;
using UniGetUI.PackageEngine.Managers.WinInstallerManager;
#else
using UniGetUI.PackageEngine.Managers.MacPkgManager;
#endif
using UniGetUI.Tui;

namespace UniGetUI.Avalonia;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
#if !WINDOWS
        // The root helper runs first, before anything that could log to a folder other users share.
        if (PkgHelperCommand.IsCommand(args))
        {
            Environment.ExitCode = PkgHelperCommand.Run(args[1..], Console.Out, Console.Error);
            return;
        }
#endif

        // Both front-ends must wait while the installer replaces their shared files.
        try
        {
            if (UpdateInProgressGuard.IsUpdateInProgress())
            {
                Environment.ExitCode = 0;
                return;
            }
        }
        catch { }

        if (TuiAppHost.TryRunFakeCacheCommand(args, out int fakeExitCode))
        {
            Environment.ExitCode = fakeExitCode;
            return;
        }

        // Terminal sessions are independent of the desktop UI and its single-instance redirector.
        if (TuiAppHost.IsCommand(args))
        {
            Environment.ExitCode = TuiAppHost.Run(args[1..]);
            return;
        }

#if WINDOWS
        if (FeedIndexCommand.IsCommand(args))
        {
            WindowsConsoleHost.PrepareCliIO();
            Environment.ExitCode = FeedIndexCommand.Run(args[1..], Console.Out, Console.Error, new WinInstallerIndexer());
            return;
        }
#else
        if (PkgIndexCommand.IsCommand(args))
        {
            Environment.ExitCode = PkgIndexCommand.Run(args[1..], Console.Out, Console.Error);
            return;
        }
#endif

#if WINDOWS
        // Stamp the AUMID onto this process before anything else so the shell attributes
        // Action-Center toasts to UniGetUI. Must match the AUMID stamped on the Start Menu
        // shortcut by the installer.
        Win32ToastNotifier.SetProcessAumid();
#endif

        AvaloniaAppHost.Run(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AvaloniaAppHost.BuildAvaloniaApp();
}

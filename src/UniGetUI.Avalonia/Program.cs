using System;
using Avalonia;
using UniGetUI.AgentSkills.Cli;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Data;
using UniGetUI.Interface;
using UniGetUI.Tui;

namespace UniGetUI.Avalonia;

sealed class Program
{
    // Do not initialize Avalonia or other GUI services before AppMain.
    // The skills command runs independently of the GUI.
    [STAThread]
    public static void Main(string[] args)
    {
        // All front-ends must wait while the installer replaces their shared files.
        try
        {
            if (UpdateInProgressGuard.IsUpdateInProgress())
            {
                Environment.ExitCode = 0;
                return;
            }
        }
        catch { }

        if (args.Length > 0 && args[0] == "skills")
        {
            WindowsConsoleHost.PrepareCliIO();
            Environment.ExitCode = SkillsCli.RunAsync(args[1..]).GetAwaiter().GetResult();
            return;
        }

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

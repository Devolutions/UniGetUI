using System;
using Avalonia;
using UniGetUI.AgentSkills.Cli;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Data;
using UniGetUI.Interface;

namespace UniGetUI.Avalonia;

sealed class Program
{
    // Do not initialize Avalonia or other GUI services before AppMain.
    // The skills command runs independently of the GUI.
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "skills")
        {
            WindowsConsoleHost.PrepareCliIO();
            Environment.ExitCode = SkillsCli.RunAsync(args[1..]).GetAwaiter().GetResult();
            return;
        }

        // Bail out if the installer is mid-swap (try/catch so the guard never blocks a normal launch).
        try
        {
            if (UpdateInProgressGuard.IsUpdateInProgress())
            {
                Environment.ExitCode = 0;
                return;
            }
        }
        catch { }

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

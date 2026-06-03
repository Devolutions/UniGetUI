using System;
using Avalonia;
using UniGetUI.Avalonia.Infrastructure;
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

        AvaloniaAppHost.Run(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AvaloniaAppHost.BuildAvaloniaApp();
}

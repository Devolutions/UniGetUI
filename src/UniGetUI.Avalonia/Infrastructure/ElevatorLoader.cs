using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class ElevatorLoader
{
    public static async Task LoadAsync()
    {
        try
        {
            if (Settings.Get(Settings.K.ProhibitElevation))
            {
                Logger.Warn("UniGetUI Elevator has been disabled since elevation is prohibited!");
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                await LoadLinuxElevatorAsync();
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                Logger.Warn("No elevation tool is available on macOS; operations that require administrator rights will fail.");
                return;
            }

            if (SecureSettings.Get(SecureSettings.K.ForceUserGSudo))
            {
                var res = await CoreTools.WhichAsync("gsudo.exe");
                if (res.Item1)
                {
                    CoreData.ElevatorPath = res.Item2;
                    Logger.Warn($"Using user GSudo (forced by user) at {CoreData.ElevatorPath}");
                    return;
                }
            }

#if DEBUG
            Logger.Warn($"Using system GSudo since UniGetUI Elevator is not available in DEBUG builds");
            CoreData.ElevatorPath = (await CoreTools.WhichAsync("gsudo.exe")).Item2;
#else
            string? bundledPath = ResolveBundledElevatorPath(CoreData.UniGetUIExecutableDirectory);
            if (bundledPath is not null)
            {
                CoreData.ElevatorPath = bundledPath;
                Logger.Debug($"Using built-in UniGetUI Elevator at {CoreData.ElevatorPath}");
                return;
            }

            Logger.Error("UniGetUI Elevator was not found; operations that require administrator rights will fail.");
#endif
        }
        catch (Exception ex)
        {
            Logger.Error("Elevator/GSudo failed to be loaded!");
            Logger.Error(ex);
        }
    }

    internal static string? ResolveBundledElevatorPath(string executableDirectory)
    {
        string localPath = Path.Join(
            executableDirectory,
            "Assets",
            "Utilities",
            "UniGetUI Elevator.exe"
        );

        if (File.Exists(localPath))
        {
            return localPath;
        }

        string? parentDirectory = Path.GetDirectoryName(executableDirectory);
        if (!string.IsNullOrEmpty(parentDirectory))
        {
            string parentPath = Path.Join(
                parentDirectory,
                "Assets",
                "Utilities",
                "UniGetUI Elevator.exe"
            );

            if (File.Exists(parentPath))
            {
                return parentPath;
            }
        }

        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static async Task LoadLinuxElevatorAsync()
    {
        // Prefer sudo over pkexec: sudo caches credentials on disk (per user, not per
        // process), so the user is only prompted once per ~15-minute window regardless
        // of how many packages are installed. pkexec prompts on every single invocation
        // because polkit ties its authorization cache to the calling process PID.
        var results = await Task.WhenAll(
            CoreTools.WhichAsync("sudo"),
            CoreTools.WhichAsync("pkexec"),
            CoreTools.WhichAsync("zenity"));
        var (sudoFound, sudoPath) = results[0];
        var (pkexecFound, pkexecPath) = results[1];
        var (zenityFound, zenityPath) = results[2];

        if (sudoFound)
        {
            // Find a graphical askpass helper so sudo can prompt without a terminal.
            // Most DEs (KDE, XFCE, ...) pre-set SSH_ASKPASS to their native tool;
            // GNOME doesn't, so we fall back to zenity with a small wrapper script
            // (zenity --password ignores positional args, so it needs the wrapper
            // to forward the prompt text via --text="$1").
            string? askpass = null;
            var envAskpass = Environment.GetEnvironmentVariable("SSH_ASKPASS");
            if (!string.IsNullOrEmpty(envAskpass) && File.Exists(envAskpass))
                askpass = envAskpass;
            else if (zenityFound)
            {
                askpass = Path.Join(CoreData.UniGetUIDataDirectory, "linux-askpass.sh");
                await File.WriteAllTextAsync(askpass,
                    $"#!/bin/sh\n\"{zenityPath}\" --password --title=\"UniGetUI\" --text=\"$1\"\n");
                File.SetUnixFileMode(askpass,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            if (askpass != null)
            {
                Environment.SetEnvironmentVariable("SUDO_ASKPASS", askpass);
                CoreData.ElevatorPath = sudoPath;
                CoreData.ElevatorArgs = "-A";
                Logger.Debug($"Using sudo -A with askpass '{askpass}'");
                return;
            }
        }

        // Fall back to pkexec when no usable sudo+askpass combination is found.
        // pkexec handles its own graphical prompt via polkit but prompts every invocation.
        if (pkexecFound)
        {
            CoreData.ElevatorPath = pkexecPath;
            Logger.Warn($"Using pkexec at {pkexecPath} (prompts on every operation)");
            return;
        }

        if (sudoFound)
        {
            CoreData.ElevatorPath = sudoPath;
            Logger.Warn($"Falling back to sudo without graphical askpass at {sudoPath}");
            return;
        }

        Logger.Warn("No elevation tool found (pkexec/sudo). Admin operations will fail.");
    }
}

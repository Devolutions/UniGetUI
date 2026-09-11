using System.ComponentModel;
using System.Diagnostics;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Core.Tools;

public static class ShortcutFileRemover
{
    public const string CliArgument = "--delete-shortcuts";

    private const int ErrorCancelled = 1223;

    private const int ElevatorCancelled = 999;

    private const int MaxElevatorOutputLines = 20;

    private const string ElevatorCancelledByUser = "The operation was canceled by the user";

    private const int ElevationWaitMilliseconds = 60_000;

    private static readonly Lock ElevationLock = new();

    private static Task<bool>? _runningElevation;

    private static bool _elevationLeftUnanswered;

    private static readonly string[] ShortcutExtensions = [".lnk", ".url"];

    private static readonly Environment.SpecialFolder[] ShortcutFolders =
    [
        Environment.SpecialFolder.DesktopDirectory,
        Environment.SpecialFolder.CommonDesktopDirectory,
        Environment.SpecialFolder.Programs,
        Environment.SpecialFolder.CommonPrograms,
    ];

    public static IReadOnlyList<string>? TEST_ShortcutRootsOverride;

    public static bool Delete(string shortcutPath)
    {
        if (TryDelete(shortcutPath, out bool accessDenied))
            return true;

        if (!accessDenied)
            return false;

        DeleteElevated([shortcutPath]);
        return !File.Exists(shortcutPath);
    }

    public static void Delete(IReadOnlyList<string> shortcutPaths)
    {
        List<string> denied = [];

        foreach (string shortcutPath in shortcutPaths)
        {
            if (!TryDelete(shortcutPath, out bool accessDenied) && accessDenied)
                denied.Add(shortcutPath);
        }

        if (denied.Count > 0)
            DeleteElevated(denied);
    }

    private static bool TryDelete(string shortcutPath, out bool accessDenied)
    {
        accessDenied = false;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                File.Delete(shortcutPath);
                return true;
            }
            catch (UnauthorizedAccessException e)
            {
                if (attempt is 0 && TryClearReadOnlyAttribute(shortcutPath))
                    continue;

                accessDenied = true;
                Logger.Warn(
                    $"Not allowed to delete shortcut {{shortcutPath={shortcutPath}}}: {e.Message}"
                );
                return false;
            }
            catch (Exception e)
            {
                Logger.Error(
                    $"Failed to delete shortcut {{shortcutPath={shortcutPath}}}: {e.Message}"
                );
                return false;
            }
        }

        return false;
    }

    private static bool TryClearReadOnlyAttribute(string shortcutPath)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(shortcutPath);
            if (!attributes.HasFlag(FileAttributes.ReadOnly))
                return false;

            File.SetAttributes(shortcutPath, attributes & ~FileAttributes.ReadOnly);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool DeleteElevated(IReadOnlyList<string> shortcutPaths)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (TEST_ShortcutRootsOverride is not null)
        {
            Logger.Warn(
                "The shortcut roots are overridden for testing, no elevation will be requested"
            );
            return false;
        }

        if (Settings.Get(Settings.K.ProhibitElevation))
        {
            Logger.Warn("Elevation is prohibited, protected shortcuts will be left on disk");
            return false;
        }

        if (CoreTools.IsAdministrator())
            return false;

        List<string> targets = [];
        foreach (string shortcutPath in shortcutPaths)
        {
            if (!IsRemovableShortcutPath(shortcutPath))
            {
                Logger.Warn(
                    $"Refusing to elevate the deletion of {{shortcutPath={shortcutPath}}}, it is "
                        + "not a shortcut under a known desktop or Start Menu folder"
                );
                continue;
            }

            if (!targets.Contains(shortcutPath, StringComparer.OrdinalIgnoreCase))
                targets.Add(shortcutPath);
        }

        if (targets.Count is 0)
            return false;

        Task<bool> elevation;
        lock (ElevationLock)
        {
            if (_elevationLeftUnanswered || _runningElevation is { IsCompleted: false })
            {
                Logger.Warn(
                    "A UAC prompt to delete protected shortcuts is still waiting to be answered, "
                        + "no new one will be raised"
                );
                return false;
            }

            Logger.Info(
                $"Relaunching UniGetUI elevated to delete {targets.Count} protected shortcut(s)"
            );

            elevation = Task.Run(() => RunElevatedDeletion(targets));
            _runningElevation = elevation;
        }

        if (elevation.Wait(ElevationWaitMilliseconds))
            return elevation.GetAwaiter().GetResult();

        lock (ElevationLock)
        {
            _elevationLeftUnanswered = true;
        }

        elevation.ContinueWith(
            _ =>
            {
                lock (ElevationLock)
                {
                    _elevationLeftUnanswered = false;
                }
            },
            TaskScheduler.Default
        );

        Logger.Warn(
            "The UAC prompt to delete protected shortcuts was left unanswered, UniGetUI will stop "
                + "waiting for it and will not raise another one until it is answered"
        );
        return false;
    }

    private enum ElevationResult
    {
        Succeeded,
        Cancelled,
        ElevatorUnusable,
    }

    private static bool RunElevatedDeletion(IReadOnlyList<string> targets)
    {
        if (CoreData.ElevatorPath.Length is 0)
        {
            Logger.Warn(
                "No elevator is available, the deletion of protected shortcuts will be elevated "
                    + "through Windows instead, which cannot reuse cached administrator rights"
            );
            return RunThroughWindows(targets);
        }

        return RunThroughElevator(targets) switch
        {
            ElevationResult.Succeeded => true,
            ElevationResult.Cancelled => false,
            _ => RunThroughWindows(targets),
        };
    }

    private static ElevationResult RunThroughElevator(IReadOnlyList<string> targets)
    {
        try
        {
            using Process process = new();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = CoreData.ElevatorPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (
                string argument in CoreData.ElevatorArgs.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries
                )
            )
                process.StartInfo.ArgumentList.Add(argument);

            process.StartInfo.ArgumentList.Add(CoreData.UniGetUIExecutableFile);
            process.StartInfo.ArgumentList.Add(CliArgument);
            foreach (string target in targets)
                process.StartInfo.ArgumentList.Add(target);

            List<string> output = [];
            void Collect(object _, DataReceivedEventArgs line)
            {
                if (line.Data is null)
                    return;

                lock (output)
                {
                    if (output.Count < MaxElevatorOutputLines)
                        output.Add(line.Data);
                }
            }

            process.OutputDataReceived += Collect;
            process.ErrorDataReceived += Collect;

            CoreTools.PrepareForegroundForElevation();
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            if (process.ExitCode is 0)
                return ElevationResult.Succeeded;

            string details;
            bool cancelledByUser;
            lock (output)
            {
                details = output.Count is 0 ? "no output" : string.Join(" | ", output);
                cancelledByUser = output.Any(line =>
                    line.Contains(ElevatorCancelledByUser, StringComparison.OrdinalIgnoreCase)
                );
            }

            if (process.ExitCode is ElevatorCancelled && cancelledByUser)
            {
                Logger.Warn(
                    "The elevator's UAC prompt to delete protected shortcuts was canceled, the "
                        + "shortcuts will be left on disk"
                );
                return ElevationResult.Cancelled;
            }

            Logger.Warn(
                $"The elevator could not elevate the deletion of protected shortcuts (code "
                    + $"{process.ExitCode}: {details}), falling back to a UAC prompt raised by "
                    + "Windows"
            );
            return ElevationResult.ElevatorUnusable;
        }
        catch (Exception e)
        {
            Logger.Error("Could not run the elevator to delete protected shortcuts");
            Logger.Error(e);
            return ElevationResult.ElevatorUnusable;
        }
    }

    private static bool RunThroughWindows(IReadOnlyList<string> targets)
    {
        try
        {
            using Process process = new();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = CoreData.UniGetUIExecutableFile,
                UseShellExecute = true,
                CreateNoWindow = true,
                Verb = "runas",
            };

            process.StartInfo.ArgumentList.Add(CliArgument);
            foreach (string target in targets)
                process.StartInfo.ArgumentList.Add(target);

            CoreTools.PrepareForegroundForElevation();
            process.Start();
            process.WaitForExit();

            return process.ExitCode is 0;
        }
        catch (Win32Exception e) when (e.NativeErrorCode is ErrorCancelled)
        {
            Logger.Warn(
                "The UAC prompt raised to delete protected shortcuts was canceled, the shortcuts "
                    + "will be left on disk"
            );
            return false;
        }
        catch (Exception e)
        {
            Logger.Error("Could not relaunch UniGetUI elevated to delete protected shortcuts");
            Logger.Error(e);
            return false;
        }
    }

    public static int DeleteAsElevatedInstance(IReadOnlyList<string> shortcutPaths)
    {
        bool everythingDeleted = true;

        foreach (string shortcutPath in shortcutPaths)
        {
            if (!IsRemovableShortcutPath(shortcutPath))
            {
                Logger.Error(
                    $"Refused to delete {{shortcutPath={shortcutPath}}}, it is not a shortcut "
                        + "under a known desktop or Start Menu folder"
                );
                everythingDeleted = false;
                continue;
            }

            if (!TryDelete(shortcutPath, out _))
                everythingDeleted = false;
        }

        return everythingDeleted ? 0 : 1;
    }

    public static IReadOnlyList<string> GetShortcutRoots()
    {
        if (TEST_ShortcutRootsOverride is not null)
            return TEST_ShortcutRootsOverride;

        List<string> roots = [];
        foreach (Environment.SpecialFolder folder in ShortcutFolders)
        {
            string root = Environment.GetFolderPath(folder);
            if (root.Length > 0)
                roots.Add(root);
        }

        return roots;
    }

    public static bool IsRemovableShortcutPath(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath))
            return false;

        if (shortcutPath.Contains('*') || shortcutPath.Contains('?'))
            return false;

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(shortcutPath));
        }
        catch (Exception)
        {
            return false;
        }

        string fileName = Path.GetFileName(fullPath);
        if (fileName.Contains(':'))
            return false;

        if (
            !ShortcutExtensions.Contains(
                Path.GetExtension(fileName),
                StringComparer.OrdinalIgnoreCase
            )
        )
            return false;

        foreach (string root in GetShortcutRoots())
        {
            string fullRoot;
            try
            {
                fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            }
            catch (Exception)
            {
                continue;
            }

            if (
                !fullPath.StartsWith(
                    fullRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                continue;

            return !CrossesReparsePoint(fullRoot, fullPath);
        }

        return false;
    }

    private static bool CrossesReparsePoint(string fullRoot, string fullPath)
    {
        try
        {
            string? current = Path.GetDirectoryName(fullPath);
            while (
                current is not null
                && !string.Equals(
                    Path.TrimEndingDirectorySeparator(current),
                    fullRoot,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                DirectoryInfo directory = new(current);
                if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return true;

                current = Path.GetDirectoryName(current);
            }

            return current is null;
        }
        catch (Exception)
        {
            return true;
        }
    }
}

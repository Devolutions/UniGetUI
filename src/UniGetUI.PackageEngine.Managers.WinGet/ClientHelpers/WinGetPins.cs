using System.Diagnostics;
using System.Text;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.WingetManager;

internal static class WinGetPins
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly object CacheLock = new();
    private static (DateTime WriteTime, long Length, IReadOnlyList<string> Ids)? _cache;

    private static string PinDatabasePath =>
        Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe",
            "LocalState",
            "pinning.db"
        );

    public static IReadOnlyList<string>? Read(WinGet manager)
    {
        FileInfo database = new(PinDatabasePath);
        (DateTime, long)? stamp = database.Exists
            ? (database.LastWriteTimeUtc, database.Length)
            : null;

        lock (CacheLock)
        {
            if (stamp is { } current && _cache is { } cached && (cached.WriteTime, cached.Length) == current)
                return cached.Ids;
        }

        IReadOnlyList<string>? ids = ReadFromCli(manager);
        if (ids is not null && stamp is { } readStamp)
        {
            lock (CacheLock)
                _cache = (readStamp.Item1, readStamp.Item2, ids);
        }

        return ids;
    }

    private static IReadOnlyList<string>? ReadFromCli(WinGet manager)
    {
        try
        {
            using var _cliLock = WinGet.AcquireCliLock();
            using Process p = new()
            {
                StartInfo = new()
                {
                    FileName = manager.Status.ExecutablePath,
                    Arguments =
                        manager.Status.ExecutableCallArgs
                        + " pin list --disable-interactivity --accept-source-agreements",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                },
            };

            IProcessTaskLogger logger = manager.TaskLogger.CreateNew(LoggableTaskType.OtherTask, p);
            if (CoreTools.IsAdministrator())
            {
                string winGetTemp = AppPaths.RedirectTempToElevatedDirectory(p.StartInfo);
                logger.AddToStdErr(
                    $"[WARN] Redirecting %TEMP% folder to {winGetTemp}, since UniGetUI was run as admin"
                );
            }

            p.Start();
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(Timeout))
            {
                p.Kill(entireProcessTree: true);
                logger.AddToStdErr("winget pin list did not finish in time");
                logger.Close(1);
                return null;
            }

            string output = stdout.GetAwaiter().GetResult();
            logger.AddToStdOut(output);
            logger.AddToStdErr(stderr.GetAwaiter().GetResult());
            logger.Close(p.ExitCode);

            return p.ExitCode == 0 ? Parse(output.Split('\n')) : null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read the WinGet pins: {ex.Message}");
            return null;
        }
    }

    internal static IReadOnlyList<string> Parse(IEnumerable<string> outputLines)
    {
        List<string> ids = [];
        foreach (WinGetTable table in WinGetTableLayout.ReadTables(outputLines.Select(l => l.TrimEnd('\r'))))
        {
            if (table.Layout.ColumnCount < 2)
                continue;

            foreach (string row in table.Rows)
            {
                string id = table.Layout.GetCell(row, WinGetTableLayout.IdColumn);
                if (id.Length > 0)
                    ids.Add(id);
            }
        }

        return ids;
    }

    internal static bool IsPinned(IReadOnlyList<string> pinnedIds, IEnumerable<string?> packageIdentifiers)
    {
        string[] identifiers = [.. packageIdentifiers.OfType<string>().Where(id => id.Length > 0)];
        foreach (string pinned in pinnedIds)
        {
            foreach (string identifier in identifiers)
            {
                if (pinned.EndsWith('…'))
                {
                    if (identifier.StartsWith(pinned[..^1], StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (string.Equals(pinned, identifier, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

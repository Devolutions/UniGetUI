using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Managers.WingetManager;

internal sealed class WinGetCliHelper : IWinGetManagerHelper
{
    // "winget search a" returns ~12k results; cap to the most relevant to avoid the freeze/RAM spike.
    private const int MAX_SEARCH_RESULTS = 100;

    private readonly WinGet Manager;
    private readonly string _cliExecutablePath;
    private readonly IPingetPackageDetailsProvider _packageDetailsProvider;

    public WinGetCliHelper(WinGet manager, string cliExecutablePath)
        : this(
            manager,
            cliExecutablePath,
            File.Exists(WinGet.GetBundledPingetExecutablePath())
                ? new PingetCliPackageDetailsProvider(WinGet.GetBundledPingetExecutablePath())
                : new PingetPackageDetailsProvider()
        )
    { }

    internal WinGetCliHelper(
        WinGet manager,
        string cliExecutablePath,
        IPingetPackageDetailsProvider packageDetailsProvider
    )
    {
        Manager = manager;
        _cliExecutablePath = cliExecutablePath;
        _packageDetailsProvider = packageDetailsProvider;
    }

    public IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
    {
        using var _cliLock = WinGet.AcquireCliLock();
        List<Package> Packages = [];
        using Process p = new()
        {
            StartInfo = new()
            {
                FileName = _cliExecutablePath,
                Arguments =
                    Manager.Status.ExecutableCallArgs
                    + " update --include-unknown  --accept-source-agreements "
                    + WinGet.GetProxyArgument(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            },
        };

        IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(LoggableTaskType.ListUpdates, p);

        if (CoreTools.IsAdministrator())
        {
            string WinGetTemp = Path.Join(AppPaths.ScratchDirectory, "ElevatedWinGetTemp");
            logger.AddToStdErr(
                $"[WARN] Redirecting %TEMP% folder to {WinGetTemp}, since UniGetUI was run as admin"
            );
            p.StartInfo.Environment["TEMP"] = WinGetTemp;
            p.StartInfo.Environment["TMP"] = WinGetTemp;
        }

        p.Start();

        Packages.AddRange(ParseAvailableUpdates(Manager, ReadOutputLines(p, logger)));

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);

        return Packages;
    }

    private static IEnumerable<string> ReadOutputLines(Process p, IProcessTaskLogger logger)
    {
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            yield return line;
        }
    }

    internal static IReadOnlyList<Package> ParseAvailableUpdates(
        WinGet manager,
        IEnumerable<string> outputLines
    )
    {
        List<Package> packages = [];

        foreach (
            WinGetTable table in WinGetTableLayout.ReadTables(
                outputLines.Where(line => !line.Contains("have pins"))
            )
        )
        {
            WinGetTableLayout layout = table.Layout;
            if (layout.ColumnCount < 4)
            {
                continue;
            }

            foreach (string line in table.Rows)
            {
                if (!layout.IsRowReaching(line, WinGetTableLayout.AvailableColumn))
                {
                    continue;
                }

                string name = layout.GetCell(line, WinGetTableLayout.NameColumn);
                string id = layout.GetCell(line, WinGetTableLayout.IdColumn);
                string version = layout.GetCell(line, WinGetTableLayout.VersionColumn);

                string newVersion;
                IManagerSource source;
                if (layout.HasSourceColumn)
                {
                    newVersion = layout.GetCell(
                        line,
                        WinGetTableLayout.AvailableColumn,
                        layout.LastColumn
                    );
                    string sourceName = layout.GetCell(line, layout.LastColumn);
                    source =
                        sourceName.Length == 0
                            ? manager.DefaultSource
                            : manager.SourcesHelper.Factory.GetSourceOrDefault(sourceName);
                }
                else
                {
                    newVersion = layout.GetCell(
                        line,
                        WinGetTableLayout.AvailableColumn,
                        layout.ColumnCount
                    );
                    source = manager.DefaultSource;
                }

                // Restore the version we last upgraded to when WinGet reports it as unknown (#5158).
                bool versionUnknown = WinGetPkgOperationHelper.IsUnknownVersion(version);
                if (versionUnknown)
                    version = WinGetPkgOperationHelper.GetLastInstalledVersion(id);

                var package = new Package(name, id, version, newVersion, source, manager);
                // Skip one-shot suppression for unknown versions so the restored mark isn't cleared.
                if (
                    versionUnknown
                    || !WinGetPkgOperationHelper.ConsumeAlreadyUpgradedSuppression(package)
                )
                {
                    packages.Add(package);
                }
                else
                {
                    Logger.Warn(
                        $"WinGet package {package.Id} not being shown as an updated as this version has already been marked as installed"
                    );
                }
            }
        }

        return packages;
    }

    public IReadOnlyList<Package> GetInstalledPackages_UnSafe()
    {
        using var _cliLock = WinGet.AcquireCliLock();
        List<Package> Packages = [];
        using Process p = new()
        {
            StartInfo = new()
            {
                FileName = _cliExecutablePath,
                Arguments =
                    Manager.Status.ExecutableCallArgs
                    + " list  --accept-source-agreements "
                    + WinGet.GetProxyArgument(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            },
        };

        IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(
            LoggableTaskType.ListInstalledPackages,
            p
        );

        if (CoreTools.IsAdministrator())
        {
            string WinGetTemp = Path.Join(AppPaths.ScratchDirectory, "ElevatedWinGetTemp");
            logger.AddToStdErr(
                $"[WARN] Redirecting %TEMP% folder to {WinGetTemp}, since UniGetUI was run as admin"
            );
            p.StartInfo.Environment["TEMP"] = WinGetTemp;
            p.StartInfo.Environment["TMP"] = WinGetTemp;
        }

        p.Start();

        Packages.AddRange(ParseInstalledPackages(Manager, ReadOutputLines(p, logger)));

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);

        return Packages;
    }

    internal static IReadOnlyList<Package> ParseInstalledPackages(
        WinGet manager,
        IEnumerable<string> outputLines
    )
    {
        List<Package> packages = [];

        foreach (WinGetTable table in WinGetTableLayout.ReadTables(outputLines))
        {
            WinGetTableLayout layout = table.Layout;
            foreach (string line in table.Rows)
            {
                try
                {
                    string name = layout.GetCell(line, WinGetTableLayout.NameColumn);
                    string id = layout.GetCell(line, WinGetTableLayout.IdColumn);
                    string version = layout.GetCell(line, WinGetTableLayout.VersionColumn);

                    string sourceName =
                        layout.HasSourceColumn
                            ? layout.GetCell(line, layout.LastColumn)
                            : "";

                    IManagerSource source =
                        sourceName.Length == 0
                            ? manager.GetLocalSource(id) // Load Winget Local Sources
                            : manager.SourcesHelper.Factory.GetSourceOrDefault(sourceName);

                    version = WinGetPkgOperationHelper.ResolveReportedInstalledVersion(id, version);
                    packages.Add(new Package(name, id, version, source, manager));
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                }
            }
        }

        return packages;
    }

    public IReadOnlyList<Package> FindPackages_UnSafe(string query)
    {
        using var _cliLock = WinGet.AcquireCliLock();
        List<Package> Packages = [];
        using Process p = new()
        {
            StartInfo = new()
            {
                FileName = _cliExecutablePath,
                Arguments =
                    Manager.Status.ExecutableCallArgs
                    + " search \""
                    + query
                    + "\" --count "
                    + MAX_SEARCH_RESULTS
                    + " --accept-source-agreements "
                    + WinGet.GetProxyArgument(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            },
        };

        IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(LoggableTaskType.FindPackages, p);

        if (CoreTools.IsAdministrator())
        {
            string WinGetTemp = Path.Join(AppPaths.ScratchDirectory, "ElevatedWinGetTemp");
            logger.AddToStdErr(
                $"[WARN] Redirecting %TEMP% folder to {WinGetTemp}, since UniGetUI was run as admin"
            );
            p.StartInfo.Environment["TEMP"] = WinGetTemp;
            p.StartInfo.Environment["TMP"] = WinGetTemp;
        }

        p.Start();

        Packages.AddRange(ParseFoundPackages(Manager, ReadOutputLines(p, logger)));

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);

        return Packages;
    }

    internal static IReadOnlyList<Package> ParseFoundPackages(
        WinGet manager,
        IEnumerable<string> outputLines
    )
    {
        List<Package> packages = [];

        foreach (WinGetTable table in WinGetTableLayout.ReadTables(outputLines))
        {
            WinGetTableLayout layout = table.Layout;
            foreach (string line in table.Rows)
            {
                string name = layout.GetCell(line, WinGetTableLayout.NameColumn);
                string id = layout.GetCell(line, WinGetTableLayout.IdColumn);
                string version = layout.GetCell(line, WinGetTableLayout.VersionColumn);

                string sourceName =
                    layout.HasSourceColumn
                        ? layout.GetCell(line, layout.LastColumn)
                        : "";

                IManagerSource source =
                    sourceName.Length == 0
                        ? manager.DefaultSource
                        : manager.SourcesHelper.Factory.GetSourceOrDefault(sourceName);

                packages.Add(new Package(name, id, version, source, manager));
            }
        }

        return packages;
    }

    public void GetPackageDetails_UnSafe(IPackageDetails details)
    {
        if (details.Package.Source.Name == "winget")
        {
            details.ManifestUrl = new Uri(
                "https://github.com/microsoft/winget-pkgs/tree/master/manifests/"
                    + details.Package.Id[0].ToString().ToLower()
                    + "/"
                    + details.Package.Id.Split('.')[0]
                    + "/"
                    + string.Join(
                        "/",
                        details.Package.Id.Contains('.')
                            ? details.Package.Id.Split('.')[1..]
                            : details.Package.Id.Split('.')
                    )
            );
        }
        else if (details.Package.Source.Name == "msstore")
        {
            details.ManifestUrl = new Uri(
                "https://apps.microsoft.com/detail/" + details.Package.Id
            );
        }

        INativeTaskLogger logger = Manager.TaskLogger.CreateNew(LoggableTaskType.LoadPackageDetails);
        bool metadataLoaded = _packageDetailsProvider.LoadPackageDetails(details, logger);
        logger.Close(metadataLoaded ? 0 : 1);
    }

    public IReadOnlyList<string> GetInstallableVersions_Unsafe(IPackage package)
    {
        using var _cliLock = WinGet.AcquireCliLock();
        using Process p = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _cliExecutablePath,
                Arguments =
                    Manager.Status.ExecutableCallArgs
                    + " show "
                    + WinGetPkgOperationHelper.GetIdNamePiece(package)
                    + $" --versions --accept-source-agreements "
                    + " "
                    + WinGet.GetProxyArgument(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            },
        };

        IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(
            LoggableTaskType.LoadPackageVersions,
            p
        );
        if (CoreTools.IsAdministrator())
        {
            string WinGetTemp = Path.Join(AppPaths.ScratchDirectory, "ElevatedWinGetTemp");
            Logger.Warn(
                $"[WARN] Redirecting %TEMP% folder to {WinGetTemp}, since UniGetUI was run as admin"
            );
            p.StartInfo.Environment["TEMP"] = WinGetTemp;
            p.StartInfo.Environment["TMP"] = WinGetTemp;
        }
        p.Start();

        string? line;
        List<string> versions = [];
        bool DashesPassed = false;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            if (!DashesPassed)
            {
                if (line.Contains("---"))
                {
                    DashesPassed = true;
                }
            }
            else
            {
                versions.Add(line.Trim());
            }
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);
        return versions;
    }

    public IReadOnlyList<IManagerSource> GetSources_UnSafe()
    {
        using var _cliLock = WinGet.AcquireCliLock();
        List<IManagerSource> sources = [];

        using Process p = new()
        {
            StartInfo = new()
            {
                FileName = Manager.Status.ExecutablePath,
                Arguments =
                    Manager.Status.ExecutableCallArgs + " source list " + WinGet.GetProxyArgument(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            },
        };

        IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(LoggableTaskType.FindPackages, p);
        if (CoreTools.IsAdministrator())
        {
            string WinGetTemp = Path.Join(AppPaths.ScratchDirectory, "ElevatedWinGetTemp");
            Logger.Warn(
                $"[WARN] Redirecting %TEMP% folder to {WinGetTemp}, since UniGetUI was run as admin"
            );
            p.StartInfo.Environment["TEMP"] = WinGetTemp;
            p.StartInfo.Environment["TMP"] = WinGetTemp;
        }
        p.Start();

        bool dashesPassed = false;
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            try
            {
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                if (!dashesPassed)
                {
                    if (line.Contains("---"))
                    {
                        dashesPassed = true;
                    }
                }
                else
                {
                    string[] parts = Regex.Replace(line.Trim(), " {2,}", " ").Split(' ');
                    if (parts.Length > 1)
                    {
                        sources.Add(
                            new ManagerSource(Manager, parts[0].Trim(), new Uri(parts[1].Trim()))
                        );
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Warn(e);
            }
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);
        return sources;
    }
}

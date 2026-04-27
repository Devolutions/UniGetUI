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
            string WinGetTemp = Path.Join(Path.GetTempPath(), "UniGetUI", "ElevatedWinGetTemp");
            logger.AddToStdErr(
                $"[WARN] Redirecting %TEMP% folder to {WinGetTemp}, since UniGetUI was run as admin"
            );
            p.StartInfo.Environment["TEMP"] = WinGetTemp;
            p.StartInfo.Environment["TMP"] = WinGetTemp;
        }

        p.Start();

        string OldLine = "";
        int IdIndex = -1;
        int VersionIndex = -1;
        int NewVersionIndex = -1;
        int SourceIndex = -1;
        bool DashesPassed = false;
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);

            if (line.Contains("have pins"))
            {
                continue;
            }

            if (!DashesPassed && line.Contains("---"))
            {
                string HeaderPrefix = OldLine.Contains("SearchId") ? "Search" : "";
                string HeaderSuffix = OldLine.Contains("SearchId") ? "Header" : "";
                IdIndex = OldLine.IndexOf(HeaderPrefix + "Id", StringComparison.InvariantCulture);
                VersionIndex = OldLine.IndexOf(
                    HeaderPrefix + "Version",
                    StringComparison.InvariantCulture
                );
                NewVersionIndex = OldLine.IndexOf(
                    "Available" + HeaderSuffix,
                    StringComparison.InvariantCulture
                );
                SourceIndex = OldLine.IndexOf(
                    HeaderPrefix + "Source",
                    StringComparison.InvariantCulture
                );
                DashesPassed = true;
            }
            else if (line.Trim() == "")
            {
                DashesPassed = false;
            }
            else if (
                DashesPassed
                && IdIndex > 0
                && VersionIndex > 0
                && NewVersionIndex > 0
                && IdIndex < VersionIndex
                && VersionIndex < NewVersionIndex
                && NewVersionIndex < line.Length
            )
            {
                int offset = 0; // Account for non-unicode character length
                while (line[IdIndex - offset - 1] != ' ' || offset > (IdIndex - 5))
                {
                    offset++;
                }

                string name = line[..(IdIndex - offset)].Trim();
                string id = line[(IdIndex - offset)..].Trim().Split(' ')[0];
                string version = line[(VersionIndex - offset)..(NewVersionIndex - offset)].Trim();
                string newVersion;
                if (SourceIndex != -1)
                {
                    newVersion = line[(NewVersionIndex - offset)..(SourceIndex - offset)].Trim();
                }
                else
                {
                    newVersion = line[(NewVersionIndex - offset)..].Trim().Split(' ')[0];
                }

                IManagerSource source;
                if (SourceIndex == -1 || SourceIndex >= line.Length)
                {
                    source = Manager.DefaultSource;
                }
                else
                {
                    string sourceName = line[(SourceIndex - offset)..].Trim().Split(' ')[0];
                    source = Manager.SourcesHelper.Factory.GetSourceOrDefault(sourceName);
                }

                var package = new Package(name, id, version, newVersion, source, Manager);
                if (!WinGetPkgOperationHelper.UpdateAlreadyInstalled(package))
                {
                    Packages.Add(package);
                }
                else
                {
                    Logger.Warn(
                        $"WinGet package {package.Id} not being shown as an updated as this version has already been marked as installed"
                    );
                }
            }
            OldLine = line;
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);

        return Packages;
    }

    public IReadOnlyList<Package> GetInstalledPackages_UnSafe()
    {
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
            string WinGetTemp = Path.Join(Path.GetTempPath(), "UniGetUI", "ElevatedWinGetTemp");
            logger.AddToStdErr(
                $"[WARN] Redirecting %TEMP% folder to {WinGetTemp}, since UniGetUI was run as admin"
            );
            p.StartInfo.Environment["TEMP"] = WinGetTemp;
            p.StartInfo.Environment["TMP"] = WinGetTemp;
        }

        p.Start();

        string OldLine = "";
        int IdIndex = -1;
        int VersionIndex = -1;
        int SourceIndex = -1;
        int NewVersionIndex = -1;
        bool DashesPassed = false;
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            try
            {
                logger.AddToStdOut(line);
                if (!DashesPassed && line.Contains("---"))
                {
                    string HeaderPrefix = OldLine.Contains("SearchId") ? "Search" : "";
                    string HeaderSuffix = OldLine.Contains("SearchId") ? "Header" : "";
                    IdIndex = OldLine.IndexOf(
                        HeaderPrefix + "Id",
                        StringComparison.InvariantCulture
                    );
                    VersionIndex = OldLine.IndexOf(
                        HeaderPrefix + "Version",
                        StringComparison.InvariantCulture
                    );
                    NewVersionIndex = OldLine.IndexOf(
                        "Available" + HeaderSuffix,
                        StringComparison.InvariantCulture
                    );
                    SourceIndex = OldLine.IndexOf(
                        HeaderPrefix + "Source",
                        StringComparison.InvariantCulture
                    );
                    DashesPassed = true;
                }
                else if (
                    DashesPassed
                    && IdIndex > 0
                    && VersionIndex > 0
                    && IdIndex < VersionIndex
                    && VersionIndex < line.Length
                )
                {
                    int offset = 0; // Account for non-unicode character length
                    while (
                        ((IdIndex - offset) <= line.Length && line[IdIndex - offset - 1] != ' ')
                        || offset > (IdIndex - 5)
                    )
                    {
                        offset++;
                    }

                    string name = line[..(IdIndex - offset)].Trim();
                    string id = line[(IdIndex - offset)..].Trim().Split(' ')[0];
                    if (NewVersionIndex == -1 && SourceIndex != -1)
                    {
                        NewVersionIndex = SourceIndex;
                    }
                    else if (NewVersionIndex == -1 && SourceIndex == -1)
                    {
                        NewVersionIndex = line.Length - 1;
                    }

                    string version = line[(VersionIndex - offset)..(NewVersionIndex - offset)]
                        .Trim();

                    IManagerSource source;
                    if (SourceIndex == -1 || (SourceIndex - offset) >= line.Length)
                    {
                        source = Manager.GetLocalSource(id); // Load Winget Local Sources
                    }
                    else
                    {
                        string sourceName = line[(SourceIndex - offset)..]
                            .Trim()
                            .Split(' ')[0]
                            .Trim();
                        source = Manager.SourcesHelper.Factory.GetSourceOrDefault(sourceName);
                    }
                    Packages.Add(new Package(name, id, version, source, Manager));
                }
                OldLine = line;
            }
            catch (Exception e)
            {
                Logger.Error(e);
            }
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);

        return Packages;
    }

    public IReadOnlyList<Package> FindPackages_UnSafe(string query)
    {
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
                    + "\"  --accept-source-agreements "
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
            string WinGetTemp = Path.Join(Path.GetTempPath(), "UniGetUI", "ElevatedWinGetTemp");
            logger.AddToStdErr(
                $"[WARN] Redirecting %TEMP% folder to {WinGetTemp}, since UniGetUI was run as admin"
            );
            p.StartInfo.Environment["TEMP"] = WinGetTemp;
            p.StartInfo.Environment["TMP"] = WinGetTemp;
        }

        p.Start();

        string OldLine = "";
        int IdIndex = -1;
        int VersionIndex = -1;
        int SourceIndex = -1;
        bool DashesPassed = false;
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            if (!DashesPassed && line.Contains("---"))
            {
                string HeaderPrefix = OldLine.Contains("SearchId") ? "Search" : "";
                IdIndex = OldLine.IndexOf(HeaderPrefix + "Id", StringComparison.InvariantCulture);
                VersionIndex = OldLine.IndexOf(
                    HeaderPrefix + "Version",
                    StringComparison.InvariantCulture
                );
                SourceIndex = OldLine.IndexOf(
                    HeaderPrefix + "Source",
                    StringComparison.InvariantCulture
                );
                DashesPassed = true;
            }
            else if (
                DashesPassed
                && IdIndex > 0
                && VersionIndex > 0
                && IdIndex < VersionIndex
                && VersionIndex < line.Length
            )
            {
                int offset = 0; // Account for non-unicode character length
                while (line[IdIndex - offset - 1] != ' ' || offset > (IdIndex - 5))
                {
                    offset++;
                }

                string name = line[..(IdIndex - offset)].Trim();
                string id = line[(IdIndex - offset)..].Trim().Split(' ')[0];
                string version = line[(VersionIndex - offset)..].Trim().Split(' ')[0];
                IManagerSource source;
                if (SourceIndex == -1 || SourceIndex >= line.Length)
                {
                    source = Manager.DefaultSource;
                }
                else
                {
                    string sourceName = line[(SourceIndex - offset)..].Trim().Split(' ')[0];
                    source = Manager.SourcesHelper.Factory.GetSourceOrDefault(sourceName);
                }
                Packages.Add(new Package(name, id, version, source, Manager));
            }
            OldLine = line;
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);

        return Packages;
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
            string WinGetTemp = Path.Join(Path.GetTempPath(), "UniGetUI", "ElevatedWinGetTemp");
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
            string WinGetTemp = Path.Join(Path.GetTempPath(), "UniGetUI", "ElevatedWinGetTemp");
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

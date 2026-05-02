using System.Diagnostics;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Managers.FlatpakManager;

public partial class Flatpak : PackageManager
{
    private static readonly string[] FLATPAK_PATHS =
    [
        "/usr/bin/flatpak",
        "/usr/local/bin/flatpak",
    ];

    public Flatpak()
    {
        Dependencies = [];

        var flathubSource = new ManagerSource(this, "flathub", new Uri("https://dl.flathub.org/repo/"));

        Capabilities = new ManagerCapabilities
        {
            CanRunAsAdmin = true,
            CanSkipIntegrityChecks = true,
            SupportsCustomSources = true,
            Sources = new SourceCapabilities
            {
                KnowsPackageCount = false,
                KnowsUpdateDate = false,
            },
            SupportsProxy = ProxySupport.No,
            SupportsProxyAuth = false,
        };

        Properties = new ManagerProperties
        {
            Name = "Flatpak",
            Description = CoreTools.Translate(
                "The universal Linux package manager for desktop applications.<br>Contains: <b>Flatpak applications from configured remotes</b>"
            ),
            IconId = IconType.Flatpak,
            ColorIconId = "flatpak",
            ExecutableFriendlyName = "flatpak",
            InstallVerb = "install",
            UpdateVerb = "update",
            UninstallVerb = "uninstall",
            DefaultSource = flathubSource,
            KnownSources = [flathubSource],
        };

        SourcesHelper = new FlatpakSourceHelper(this);
        DetailsHelper = new FlatpakPkgDetailsHelper(this);
        OperationHelper = new FlatpakPkgOperationHelper(this);
    }

    public override IReadOnlyList<string> FindCandidateExecutableFiles()
    {
        var candidates = new List<string>(CoreTools.WhichMultiple("flatpak"));
        foreach (var path in FLATPAK_PATHS)
        {
            if (File.Exists(path) && !candidates.Contains(path))
                candidates.Add(path);
        }
        return candidates;
    }

    protected override void _loadManagerExecutableFile(
        out bool found,
        out string path,
        out string callArguments)
    {
        (found, path) = GetExecutableFile();
        callArguments = "";
    }

    protected override void _loadManagerVersion(out string version)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Status.ExecutablePath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        p.Start();
        var line = p.StandardOutput.ReadLine()?.Trim() ?? "";
        version = line.Replace("Flatpak ", "").Trim();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }

    public override void RefreshPackageIndexes()
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Status.ExecutablePath,
                Arguments = "update --appstream",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.RefreshIndexes, p);
        p.Start();
        logger.AddToStdOut(p.StandardOutput.ReadToEnd());
        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);
    }

    protected override IReadOnlyList<Package> FindPackages_UnSafe(string query)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Status.ExecutablePath,
                Arguments = $"search {CoreTools.EnsureSafeQueryString(query)}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        p.StartInfo.Environment["LANG"] = "C";
        p.StartInfo.Environment["LC_ALL"] = "C";
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.FindPackages, p);
        p.Start();

        List<string> outputLines = [];
        while (p.StandardOutput.ReadLine() is { } line)
        {
            logger.AddToStdOut(line);
            outputLines.Add(line);
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);

        return ParseSearchResults(outputLines, DefaultSource, this);
    }

    protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe()
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Status.ExecutablePath,
                Arguments = "list --app --columns=application,version,branch,origin,name",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        p.StartInfo.Environment["LANG"] = "C";
        p.StartInfo.Environment["LC_ALL"] = "C";
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListInstalledPackages, p);
        p.Start();

        List<string> outputLines = [];
        while (p.StandardOutput.ReadLine() is { } line)
        {
            logger.AddToStdOut(line);
            outputLines.Add(line);
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);

        return ParseInstalledPackages(outputLines, DefaultSource, this);
    }

    protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Status.ExecutablePath,
                Arguments = "remote-ls --updates --columns=application,version,branch,origin,name",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        p.StartInfo.Environment["LANG"] = "C";
        p.StartInfo.Environment["LC_ALL"] = "C";
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListUpdates, p);

        p.Start();

        List<string> outputLines = [];
        while (p.StandardOutput.ReadLine() is { } line)
        {
            logger.AddToStdOut(line);
            outputLines.Add(line);
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);

        return ParseAvailableUpdates(outputLines, DefaultSource, this, GetInstalledPackages_UnSafe());
    }
}

using System.Diagnostics;
using System.Text.RegularExpressions;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Managers.SnapManager;

public partial class Snap : PackageManager
{
    [GeneratedRegex(@"^(\S+)\s+(\S+)")]
    private static partial Regex InstalledLineRegex();

    [GeneratedRegex(@"^(\S+)\s+(\S+)")]
    private static partial Regex UpdateLineRegex();

    [GeneratedRegex(@"^(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(.+)$")]
    private static partial Regex FindLineRegex();

    public Snap()
    {
        Dependencies = [];

        Capabilities = new ManagerCapabilities
        {
            CanRunAsAdmin = true,
            CanSkipIntegrityChecks = true,
            SupportsCustomSources = false,
            SupportsProxy = ProxySupport.No,
            SupportsProxyAuth = false,
        };

        var snapcraftSource = new ManagerSource(this, "snapcraft", new Uri("https://snapcraft.io"));

        Properties = new ManagerProperties
        {
            Id = "snap",
            Name = "Snap",
            Description = CoreTools.Translate(
                "The universal Linux package manager by Canonical.<br>Contains: <b>Snap packages from the Snapcraft store</b>"
            ),
            IconId = IconType.Snap,
            ColorIconId = "snap",
            ExecutableFriendlyName = "snap",
            InstallVerb = "install",
            UpdateVerb = "refresh",
            UninstallVerb = "remove",
            DefaultSource = snapcraftSource,
            KnownSources = [snapcraftSource],
        };

        DetailsHelper = new SnapPkgDetailsHelper(this);
        OperationHelper = new SnapPkgOperationHelper(this);
    }

    public override IReadOnlyList<string> FindCandidateExecutableFiles()
    {
        var candidates = new List<string>(CoreTools.WhichMultiple("snap"));
        foreach (var path in new[] { "/usr/bin/snap", "/usr/local/bin/snap" })
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
        var parts = line.Split(' ');
        version = parts.Length >= 2 ? parts[1] : line;
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
                Arguments = "refresh --list",
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
                Arguments = $"find {CoreTools.EnsureSafeQueryString(query)}",
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
                Arguments = "list",
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
                Arguments = "refresh --list",
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

using System.Diagnostics;
using System.Text.RegularExpressions;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Structs;

namespace UniGetUI.PackageEngine.Managers.AptManager;

public class Apt : PackageManager
{
    public Apt()
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

        Properties = new ManagerProperties
        {
            Name = "Apt",
            Description = CoreTools.Translate(
                "The default package manager for Debian/Ubuntu-based Linux distributions.<br>Contains: <b>Debian/Ubuntu packages</b>"
            ),
            IconId = IconType.Apt,
            ColorIconId = "debian",
            ExecutableFriendlyName = "apt",
            InstallVerb = "install",
            UpdateVerb = "install",
            UninstallVerb = "remove",
            DefaultSource = new ManagerSource(this, "apt", new Uri("https://packages.debian.org")),
            KnownSources = [new ManagerSource(this, "apt", new Uri("https://packages.debian.org"))],
        };

        DetailsHelper = new AptPkgDetailsHelper(this);
        OperationHelper = new AptPkgOperationHelper(this);
    }

    // ── Executable discovery ───────────────────────────────────────────────

    public override IReadOnlyList<string> FindCandidateExecutableFiles()
    {
        var candidates = new List<string>(CoreTools.WhichMultiple("apt"));
        foreach (var path in new[] { "/usr/bin/apt", "/usr/local/bin/apt" })
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
        // First line: "apt X.Y.Z (arch)"
        var line = p.StandardOutput.ReadLine()?.Trim() ?? "";
        var parts = line.Split(' ');
        version = parts.Length >= 2 ? parts[1] : line;
        p.WaitForExit();
    }

    // ── Index refresh ──────────────────────────────────────────────────────

    public override void RefreshPackageIndexes()
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Status.ExecutablePath,
                Arguments = "update",
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

    // ── Package listing ────────────────────────────────────────────────────

    protected override IReadOnlyList<Package> FindPackages_UnSafe(string query)
    {
        var packages = new List<Package>();

        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "apt-cache",
                Arguments = $"search -- {query}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.FindPackages, p);
        p.Start();

        // Output format: "<id> - <description>"
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            var dashIdx = line.IndexOf(" - ", StringComparison.Ordinal);
            if (dashIdx <= 0) continue;

            var id = line[..dashIdx].Trim();
            if (id.Length == 0) continue;

            packages.Add(new Package(
                CoreTools.FormatAsName(id),
                id,
                CoreTools.Translate("Latest"),
                Properties.DefaultSource!,
                this));
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);
        return packages;
    }

    protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe()
    {
        var packages = new List<Package>();

        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Status.ExecutablePath,
                Arguments = "list --installed",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListInstalledPackages, p);
        p.Start();

        // Output format: "<id>/<source>,... <version> <arch> [installed,...]"
        // First line is "Listing..." header — skip it.
        var idVersionPattern = new Regex(@"^([^/\s]+)/\S+\s+(\S+)");
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            var m = idVersionPattern.Match(line);
            if (!m.Success) continue;

            var id = m.Groups[1].Value;
            var version = m.Groups[2].Value;
            packages.Add(new Package(
                CoreTools.FormatAsName(id),
                id,
                version,
                Properties.DefaultSource!,
                this));
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);
        return packages;
    }

    protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
    {
        var packages = new List<Package>();

        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Status.ExecutablePath,
                Arguments = "list --upgradable",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListUpdates, p);
        p.Start();

        // Output format: "<id>/<source>,... <new-ver> <arch> [upgradable from: <old-ver>]"
        var pattern = new Regex(@"^([^/\s]+)/\S+\s+(\S+)\s+\S+\s+\[upgradable from: ([^\]]+)\]");
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            var m = pattern.Match(line);
            if (!m.Success) continue;

            var id = m.Groups[1].Value;
            var newVersion = m.Groups[2].Value;
            var oldVersion = m.Groups[3].Value;
            packages.Add(new Package(
                CoreTools.FormatAsName(id),
                id,
                oldVersion,
                newVersion,
                Properties.DefaultSource!,
                this));
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);
        return packages;
    }
}

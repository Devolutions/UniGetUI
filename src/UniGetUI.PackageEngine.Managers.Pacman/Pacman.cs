using System.Diagnostics;
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

namespace UniGetUI.PackageEngine.Managers.PacmanManager;

public class Pacman : PackageManager
{
    public Pacman()
    {
        Dependencies = [];

        Capabilities = new ManagerCapabilities
        {
            CanRunAsAdmin = true,
            CanSkipIntegrityChecks = false,
            SupportsCustomSources = false,
            SupportsProxy = ProxySupport.No,
            SupportsProxyAuth = false,
        };

        Properties = new ManagerProperties
        {
            Id = "pacman",
            Name = "Pacman",
            Description = CoreTools.Translate(
                "The default package manager for Arch Linux and its derivatives.<br>Contains: <b>Arch Linux packages</b>"
            ),
            IconId = IconType.Pacman,
            ColorIconId = "pacman",
            ExecutableFriendlyName = "pacman",
            InstallVerb = "-S",
            UpdateVerb = "-S",
            UninstallVerb = "-Rs",
            DefaultSource = new ManagerSource(this, "arch", new Uri("https://archlinux.org/packages/")),
            KnownSources = [new ManagerSource(this, "arch", new Uri("https://archlinux.org/packages/"))],
        };

        DetailsHelper = new PacmanPkgDetailsHelper(this);
        OperationHelper = new PacmanPkgOperationHelper(this);
    }

    // ── Executable discovery ───────────────────────────────────────────────

    public override IReadOnlyList<string> FindCandidateExecutableFiles()
    {
        var candidates = new List<string>(CoreTools.WhichMultiple("pacman"));
        foreach (var path in new[] { "/usr/bin/pacman", "/usr/local/bin/pacman" })
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
                Arguments = "-Q pacman",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        p.Start();
        // First line: "pacman X.Y.Z-N"
        var line = p.StandardOutput.ReadLine()?.Trim() ?? "";
        var parts = line.Split(' ');
        version = parts.Length >= 2 ? parts[1] : line;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
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
                Arguments = "-Sy",
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
                FileName = Status.ExecutablePath,
                Arguments = $"-Ss {query}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.FindPackages, p);
        p.Start();

        // Output format: "<repo>/<name> <version> [groups]\n    <description>"
        // Name lines start at column 0; description lines are indented.
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            if (line.Length == 0 || line.StartsWith(' ')) continue;

            var slashIdx = line.IndexOf('/');
            if (slashIdx < 0) continue;

            var afterSlash = line[(slashIdx + 1)..];
            var spaceIdx = afterSlash.IndexOf(' ');
            if (spaceIdx <= 0) continue;

            var id = afterSlash[..spaceIdx];
            packages.Add(new Package(
                CoreTools.FormatAsName(id),
                id,
                CoreTools.Translate("Latest"),
                Properties.DefaultSource!,
                this));
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        // pacman -Ss exits 1 when no packages match the query — not an error
        logger.Close(p.ExitCode == 1 && packages.Count == 0 ? 0 : p.ExitCode);
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
                Arguments = "-Q",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListInstalledPackages, p);
        p.Start();

        // Output format: "<name> <version>"
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            packages.Add(new Package(
                CoreTools.FormatAsName(parts[0]),
                parts[0],
                parts[1],
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
                Arguments = "-Qu",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListUpdates, p);
        p.Start();

        // Output format: "<name> <old-version> -> <new-version>"
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || parts[2] != "->") continue;

            packages.Add(new Package(
                CoreTools.FormatAsName(parts[0]),
                parts[0],
                parts[1],
                parts[3],
                Properties.DefaultSource!,
                this));
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        // pacman -Qu exits 1 when there are no upgradable packages — not an error
        logger.Close(p.ExitCode == 1 && packages.Count == 0 ? 0 : p.ExitCode);
        return packages;
    }
}

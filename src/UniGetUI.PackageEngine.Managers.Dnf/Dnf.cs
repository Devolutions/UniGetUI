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

namespace UniGetUI.PackageEngine.Managers.DnfManager;

public class Dnf : PackageManager
{
    // Known RPM architectures — used to strip the trailing .<arch> from package names.
    private static readonly HashSet<string> _knownArches =
        ["x86_64", "aarch64", "noarch", "i686", "i386", "ppc64le", "s390x", "src"];

    public Dnf()
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
            Id = "dnf",
            Name = "Dnf",
            Description = CoreTools.Translate(
                "The default package manager for RHEL/Fedora-based Linux distributions.<br>Contains: <b>RPM packages</b>"
            ),
            IconId = IconType.Dnf,
            ColorIconId = "dnf",
            ExecutableFriendlyName = "dnf",
            InstallVerb = "install",
            UpdateVerb = "upgrade",
            UninstallVerb = "remove",
            DefaultSource = new ManagerSource(this, "dnf", new Uri("https://fedoraproject.org/wiki/DNF")),
            KnownSources = [new ManagerSource(this, "dnf", new Uri("https://fedoraproject.org/wiki/DNF"))],
        };

        DetailsHelper = new DnfPkgDetailsHelper(this);
        OperationHelper = new DnfPkgOperationHelper(this);
    }

    // ── Executable discovery ───────────────────────────────────────────────

    public override IReadOnlyList<string> FindCandidateExecutableFiles()
    {
        var candidates = new List<string>(CoreTools.WhichMultiple("dnf5"));
        foreach (var path in CoreTools.WhichMultiple("dnf"))
        {
            if (!candidates.Contains(path))
                candidates.Add(path);
        }
        foreach (var path in new[] { "/usr/bin/dnf5", "/usr/bin/dnf", "/usr/local/bin/dnf" })
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
        // First line is the version number: "X.Y.Z"
        version = p.StandardOutput.ReadLine()?.Trim() ?? "";
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
                Arguments = "makecache",
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
                Arguments = $"search --quiet {query}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.FindPackages, p);
        p.Start();

        // Output: "<name>.<arch> : <description>"
        // Section headers look like "===== Name Matched: <q> =====" — skip them.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            if (line.StartsWith('=') || line.Length == 0) continue;

            var colonIdx = line.IndexOf(" : ", StringComparison.Ordinal);
            if (colonIdx <= 0) continue;

            var nameArch = line[..colonIdx].Trim();
            var id = StripArch(nameArch);
            if (id.Length == 0 || !seen.Add(id)) continue;

            packages.Add(new Package(
                CoreTools.FormatAsName(id),
                id,
                CoreTools.Translate("Latest"),
                Properties.DefaultSource!,
                this));
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        // dnf search exits 1 when no packages match the query — not an error
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
                Arguments = "list --installed",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListInstalledPackages, p);
        p.Start();

        // Output: "<name>.<arch>   <version>-<release>   <@repo>"
        // Skip header/metadata lines (e.g. "Installed Packages",
        // "Last metadata expiration check: ...") by requiring a known arch suffix.
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !IsPackageLine(parts[0])) continue;

            var id = StripArch(parts[0]);
            var version = parts[1];
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
                Arguments = "list --upgrades",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        // Build lookup before starting the process — reading from its pipe
        // while a second process runs risks filling the pipe buffer and deadlocking.
        Dictionary<string, string> installed = [];
        foreach (var pkg in GetInstalledPackages_UnSafe())
            installed.TryAdd(pkg.Id, pkg.VersionString);

        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListUpdates, p);
        p.Start();

        // Output: "<name>.<arch>   <version>-<release>   <repo>"
        // Skip header/metadata lines (e.g. "Available Upgrades",
        // "Last metadata expiration check: ...") by requiring a known arch suffix.
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !IsPackageLine(parts[0])) continue;

            var id = StripArch(parts[0]);
            var newVersion = parts[1];
            installed.TryGetValue(id, out var oldVersion);
            packages.Add(new Package(
                CoreTools.FormatAsName(id),
                id,
                oldVersion ?? "",
                newVersion,
                Properties.DefaultSource!,
                this));
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);
        return packages;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Strips the trailing .<arch> from a DNF package token (e.g. "vim.x86_64" → "vim").
    /// Package names that do not end in a known arch are returned unchanged.
    /// </summary>
    internal static string StripArch(string nameArch)
    {
        var dot = nameArch.LastIndexOf('.');
        if (dot > 0 && _knownArches.Contains(nameArch[(dot + 1)..]))
            return nameArch[..dot];
        return nameArch;
    }

    /// <summary>
    /// Returns true when <paramref name="token"/> looks like a DNF package entry
    /// (i.e. ends in a known architecture suffix such as ".x86_64" or ".noarch").
    /// Used to skip header/metadata lines in dnf list output.
    /// </summary>
    private static bool IsPackageLine(string token)
    {
        var dot = token.LastIndexOf('.');
        return dot > 0 && _knownArches.Contains(token[(dot + 1)..]);
    }
}

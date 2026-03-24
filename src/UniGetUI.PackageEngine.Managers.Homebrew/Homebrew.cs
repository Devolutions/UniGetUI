using System.Diagnostics;
using System.Text.RegularExpressions;
using UniGetUI.Core.Logging;
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

namespace UniGetUI.PackageEngine.Managers.HomebrewManager;

/// <summary>
/// A ManagerSource whose display name is just the source name, without the "Manager: " prefix.
/// </summary>
internal sealed class HomebrewSource : ManagerSource
{
    public HomebrewSource(IPackageManager manager, string name, Uri url)
        : base(manager, name, url)
    {
        AsString_DisplayName = name;
    }
}

public class Homebrew : PackageManager
{
    // Standard Homebrew installation paths, in priority order
    private static readonly string[] BREW_PATHS =
    [
        "/opt/homebrew/bin/brew",                        // Apple Silicon
        "/usr/local/bin/brew",                           // Intel Mac
        "/home/linuxbrew/.linuxbrew/bin/brew",           // Linux
    ];

    public Homebrew()
    {
        Dependencies = [];

        Capabilities = new ManagerCapabilities
        {
            CanRunAsAdmin = false,
            CanDownloadInstaller = false,
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
            Name = "Homebrew",
            Description = CoreTools.Translate(
                "The Missing Package Manager for macOS (or Linux).<br>Contains: <b>Formulae, Casks</b>"
            ),
            IconId = IconType.Homebrew,
            ColorIconId = "homebrew_color",
            ExecutableFriendlyName = "brew",
            InstallVerb = "install",
            UpdateVerb = "upgrade",
            UninstallVerb = "uninstall",
            KnownSources =
            [
                new HomebrewSource(this, "Homebrew", new Uri("https://github.com/Homebrew/homebrew-core")),
                new HomebrewSource(this, "Homebrew Cask", new Uri("https://github.com/Homebrew/homebrew-cask")),
            ],
            DefaultSource = new HomebrewSource(this, "Homebrew", new Uri("https://github.com/Homebrew/homebrew-core")),
        };

        SourcesHelper = new HomebrewSourceHelper(this);
        DetailsHelper = new HomebrewPkgDetailsHelper(this);
        OperationHelper = new HomebrewPkgOperationHelper(this);
    }

    // ── Executable discovery ───────────────────────────────────────────────

    public override IReadOnlyList<string> FindCandidateExecutableFiles()
    {
        var candidates = new List<string>(CoreTools.WhichMultiple("brew"));
        foreach (var path in BREW_PATHS)
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
        // First line: "Homebrew 4.x.x"
        version = p.StandardOutput.ReadLine()?.Replace("Homebrew ", "").Trim() ?? "";
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

        IManagerSource formulaeSource = SourcesHelper.Factory.GetSourceOrDefault("Homebrew");
        IManagerSource caskSource = SourcesHelper.Factory.GetSourceOrDefault("Homebrew Cask");
        IManagerSource currentSection = formulaeSource;

        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Status.ExecutablePath,
                Arguments = $"search {query}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.FindPackages, p);
        p.Start();

        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            if (line.StartsWith("==> Formulae")) { currentSection = formulaeSource; continue; }
            if (line.StartsWith("==> Casks")) { currentSection = caskSource; continue; }

            foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var id = token.Trim();
                if (id.Length == 0) continue;
                packages.Add(new Package(
                    CoreTools.FormatAsName(id),
                    id,
                    CoreTools.Translate("Latest"),
                    currentSection,
                    this));
            }
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);
        return packages;
    }

    protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe()
    {
        var packages = new List<Package>();
        packages.AddRange(ListInstalledByType("--formula", "Homebrew"));
        packages.AddRange(ListInstalledByType("--cask", "Homebrew Cask"));
        return packages;
    }

    private IReadOnlyList<Package> ListInstalledByType(string typeFlag, string sourceName)
    {
        var packages = new List<Package>();
        IManagerSource source = SourcesHelper.Factory.GetSourceOrDefault(sourceName);

        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Status.ExecutablePath,
                Arguments = $"list {typeFlag} --versions",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListInstalledPackages, p);
        p.Start();

        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var id = parts[0].Trim();
            var version = parts[1].Trim();
            packages.Add(new Package(CoreTools.FormatAsName(id), id, version, source, this));
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);
        return packages;
    }

    protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
    {
        var packages = new List<Package>();

        // Build a lookup of installed packages to retrieve their sources
        Dictionary<string, IPackage> installed = [];
        foreach (var pkg in GetInstalledPackages())
            installed.TryAdd(pkg.Id, pkg);

        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Status.ExecutablePath,
                Arguments = "outdated --verbose",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListUpdates, p);
        p.Start();

        // Format: "name (old_version) < new_version"
        var pattern = new Regex(@"^(\S+)\s+\(([^)]+)\)\s+<\s+(.+)$");
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            var m = pattern.Match(line);
            if (!m.Success) continue;

            var id = m.Groups[1].Value.Trim();
            var oldVersion = m.Groups[2].Value.Trim();
            var newVersion = m.Groups[3].Value.Trim();

            installed.TryGetValue(id, out var installedPkg);
            var source = installedPkg?.Source
                ?? SourcesHelper.Factory.GetSourceOrDefault("Homebrew");

            packages.Add(new Package(
                CoreTools.FormatAsName(id),
                id,
                oldVersion,
                newVersion,
                source,
                this));
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);
        return packages;
    }
}

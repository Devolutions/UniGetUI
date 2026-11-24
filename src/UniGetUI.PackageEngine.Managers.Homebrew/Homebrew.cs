using System.Diagnostics;
using System.Runtime.InteropServices;
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

public partial class Homebrew : PackageManager
{
    protected override IEnumerable<ManagerMaintenanceAction> GetExtraMaintenanceActions()
    {
        yield return new ManagerMaintenanceAction
        {
            Id = "clear-cache",
            Title = CoreTools.Translate("Clear cache"),
            Description = CoreTools.Translate("Remove old versions and the download cache Homebrew keeps."),
            RunAsync = () => RunMaintenanceCommandAsync(Status.ExecutablePath, WithCallArguments("cleanup -s")),
        };

        IReadOnlyList<string> broken = BrokenCasks;
        if (broken.Count > 0)
        {
            yield return new ManagerMaintenanceAction
            {
                Id = "uninstall-broken-casks",
                Title = CoreTools.Translate("Uninstall casks Homebrew can no longer find"),
                Description = CoreTools.Translate(
                    "Homebrew can no longer find {0}, so it cannot update them. Uninstall them, then install any you still need under their new name.",
                    string.Join(", ", broken)
                ),
                RunAsync = () => RunMaintenanceCommandAsync(
                    Status.ExecutablePath,
                    WithCallArguments($"uninstall --cask {string.Join(' ', broken)}")
                ),
            };
        }
    }

    // Installed casks brew could not load during the last cask listing; set by ListInstalledByType.
    private volatile IReadOnlyList<string> _brokenCasks = [];

    internal IReadOnlyList<string> BrokenCasks => _brokenCasks;

    // Standard Homebrew installation paths, in priority order
    private static readonly string[] BREW_PATHS =
    [
        "/opt/homebrew/bin/brew",                        // Apple Silicon
        "/usr/local/bin/brew",                           // Intel Mac
        "/home/linuxbrew/.linuxbrew/bin/brew",           // Linux
    ];

    private const string INSTALL_COMMAND =
        "/bin/bash -c \"$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)\"";

    public Homebrew()
    {
        Dependencies = [];

        Capabilities = new ManagerCapabilities
        {
            CanReinstallOnUpdate = true,
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
            KnowsPackageReleaseDate = PackageReleaseDateSupport.No,
        };

        Properties = new ManagerProperties
        {
            Id = "homebrew",
            Name = "Homebrew",
            SetupGuide = "PACKAGE_MANAGERS.md#homebrew",
            InstallCommands = new(MacOS: INSTALL_COMMAND, Linux: INSTALL_COMMAND),
            Description = CoreTools.Translate(
                "The Missing Package Manager for macOS (or Linux).<br>Contains: <b>Formulae, Casks</b>"
            ),
            IconId = IconType.Homebrew,
            ColorIconId = "homebrew_color",
            ExecutableFriendlyName = "brew",
            InstallVerb = "install",
            UpdateVerb = "upgrade",
            UninstallVerb = "uninstall",
            KnownSources = CreateBuiltInSources(this, OperatingSystem.IsMacOS()),
            DefaultSource = new HomebrewSource(this, "Homebrew", new Uri("https://github.com/Homebrew/homebrew-core")),
        };

        SourcesHelper = new HomebrewSourceHelper(this);
        DetailsHelper = new HomebrewPkgDetailsHelper(this);
        OperationHelper = new HomebrewPkgOperationHelper(this);
    }

    /// <summary>
    /// The sources Homebrew serves from its API without a tap: formulae everywhere, casks on macOS
    /// only (Homebrew on Linux has no casks).
    /// </summary>
    internal static IManagerSource[] CreateBuiltInSources(Homebrew manager, bool isMacOS)
    {
        var formulae = new HomebrewSource(manager, "Homebrew", new Uri("https://github.com/Homebrew/homebrew-core"));
        if (!isMacOS)
            return [formulae];

        return [formulae, new HomebrewSource(manager, "Homebrew Cask", new Uri("https://github.com/Homebrew/homebrew-cask"))];
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
        // Force ARM64 when brew is at the Apple Silicon prefix. Without this,
        // .NET's posix_spawn may select the x86_64 slice of universal binaries
        // (bash, ruby) in the brew script chain, causing Homebrew to detect a
        // Rosetta 2 context even when UniGetUI itself is ARM64-native.
        if (path == BREW_PATHS[0]
            && OperatingSystem.IsMacOS()
            && RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
        {
            callArguments = $"-arm64 {path}";
            path = "/usr/bin/arch";
        }
        else
        {
            callArguments = "";
        }
    }

    internal ProcessStartInfo MakeBrewStartInfo(string arguments) => new()
    {
        FileName = Status.ExecutablePath,
        Arguments = Status.ExecutableCallArgs.Length > 0
            ? $"{Status.ExecutableCallArgs} {arguments}"
            : arguments,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };

    protected override void _loadManagerVersion(out string version)
    {
        using var p = new Process { StartInfo = MakeBrewStartInfo("--version") };
        p.Start();
        // First line: "Homebrew 4.x.x"
        version = p.StandardOutput.ReadLine()?.Replace("Homebrew ", "").Trim() ?? "";
        p.WaitForExit();
    }

    // ── Index refresh ──────────────────────────────────────────────────────

    public override void RefreshPackageIndexes()
    {
        RunBrewListing("update", LoggableTaskType.RefreshIndexes);
    }

    // ── Package listing ────────────────────────────────────────────────────

    protected override IReadOnlyList<Package> FindPackages_UnSafe(string query)
    {
        var packages = new List<Package>();

        IManagerSource formulaeSource = SourcesHelper.Factory.GetSourceOrDefault("Homebrew");
        IManagerSource caskSource = SourcesHelper.Factory.GetSourceOrDefault("Homebrew Cask");
        IManagerSource currentSection = formulaeSource;

        foreach (var line in RunBrewListing($"search {query}", LoggableTaskType.FindPackages).StdOut)
        {
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
        IManagerSource source = SourcesHelper.Factory.GetSourceOrDefault(sourceName);
        BrewOutput result = RunBrewListing(
            $"list {typeFlag} --versions",
            LoggableTaskType.ListInstalledPackages
        );
        List<(string Id, string Version)> entries = ParseListVersionsOutput(result.StdOut);

        if (typeFlag == "--cask" && result.ExitCode == 0)
            _brokenCasks = [];

        if (result.ExitCode != 0 && typeFlag == "--cask" && !ListingCancellationToken.IsCancellationRequested)
        {
            // brew loads every cask before printing any, so one it cannot load leaves stdout empty.
            if (TryReadCasksFromCaskroom(out var fromCaskroom))
            {
                List<string> broken = ParseBrokenCasks(result.StdErr);
                _brokenCasks = [.. broken.Where(token => fromCaskroom.Any(cask => cask.Id == token))];
                LogCaskListingRecovered(result.StdErr, broken, listedAny: entries.Count > 0);
                return ToPackages(MergeInstalledEntries(entries, fromCaskroom), source);
            }
        }

        ThrowIfListingFailed(result.ExitCode, result.StdErr);
        return ToPackages(entries, source);
    }

    private List<Package> ToPackages(IEnumerable<(string Id, string Version)> entries, IManagerSource source) =>
        [.. entries.Select(e => new Package(CoreTools.FormatAsName(e.Id), e.Id, e.Version, source, this))];

    private readonly record struct BrewOutput(List<string> StdOut, string StdErr, int ExitCode);

    private BrewOutput RunBrewListing(string arguments, LoggableTaskType taskType)
    {
        using var p = new Process { StartInfo = MakeBrewStartInfo(arguments) };
        IProcessTaskLogger logger = TaskLogger.CreateNew(taskType, p);
        p.Start();
        // Drain stderr concurrently so a chatty stderr cannot fill its pipe and stall brew.
        Task<string> stderr = p.StandardError.ReadToEndAsync();

        var lines = new List<string>();
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            lines.Add(line);
        }

        string error = stderr.GetAwaiter().GetResult();
        logger.AddToStdErr(error);
        p.WaitForExit();
        logger.Close(p.ExitCode);
        return new BrewOutput(lines, error, p.ExitCode);
    }

    internal static List<(string Id, string Version)> ParseListVersionsOutput(IEnumerable<string> lines)
    {
        var entries = new List<(string, string)>();
        foreach (var line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            entries.Add((parts[0].Trim(), parts[1].Trim()));
        }
        return entries;
    }

    // e.g. "Error: Cask 'powershell' is unavailable: No Cask with this name exists."
    // Tokens are restricted to Homebrew's charset, since they are passed back to `brew uninstall`.
    [GeneratedRegex(@"^Error: Cask '([a-z0-9][a-z0-9@._+-]*)'", RegexOptions.Multiline)]
    private static partial Regex CaskErrorRegex();

    internal static List<string> ParseBrokenCasks(string stderr) =>
        [.. CaskErrorRegex().Matches(stderr).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    private static void LogCaskListingRecovered(string stderr, List<string> broken, bool listedAny)
    {
        string error = stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Error:"))
            ?? stderr.Trim().Split('\n')[0].Trim();
        string what = listedAny ? "listed only some casks" : "listed no cask";
        Logger.Warn(
            broken.Count > 0
                ? $"Homebrew cannot load the installed cask(s) {string.Join(", ", broken)}, so `brew list --cask "
                    + $"--versions` failed and {what} ({error}). Installed casks were read from the Caskroom instead; "
                    + "uninstalling the cask(s) from the Manager Health page fixes this."
                : $"`brew list --cask --versions` failed and {what} ({error}). Installed casks were read from the "
                    + "Caskroom instead."
        );
    }

    private bool TryReadCasksFromCaskroom(out List<(string Id, string Version)> casks)
    {
        casks = [];
        BrewOutput caskroom = RunBrewListing("--caskroom", LoggableTaskType.ListInstalledPackages);
        string path = caskroom.StdOut.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        if (caskroom.ExitCode != 0 || path.Length == 0 || !Directory.Exists(path))
            return false;

        try
        {
            casks = ReadInstalledCasks(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Could not read the Homebrew Caskroom at {path}: {ex.Message}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Lists installed casks and their versions from the Caskroom the way Homebrew does
    /// (Caskroom.cask_installed_version), without loading any cask definition.
    /// </summary>
    internal static List<(string Id, string Version)> ReadInstalledCasks(string caskroomPath)
    {
        var tokens = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var dir in new DirectoryInfo(caskroomPath).EnumerateDirectories())
        {
            // A cask renamed upstream may leave its old token as a symlink to the new one.
            FileSystemInfo? target = dir.LinkTarget is null ? null : dir.ResolveLinkTarget(returnFinalTarget: true);
            tokens.Add(target?.Name ?? dir.Name);
        }

        var casks = new List<(string, string)>();
        foreach (var token in tokens)
        {
            var caskDir = new DirectoryInfo(Path.Combine(caskroomPath, token));
            if (!caskDir.Exists || caskDir.LinkTarget is not null) continue;

            var metadata = new DirectoryInfo(Path.Combine(caskDir.FullName, ".metadata"));
            if (!metadata.Exists) continue;

            // The newest install is the timestamp directory with the greatest name, across versions.
            DirectoryInfo? latest = metadata
                .EnumerateDirectories()
                .SelectMany(version => version.EnumerateDirectories())
                .MaxBy(timestamp => timestamp.Name, StringComparer.Ordinal);
            if (latest is null) continue;

            string casksDir = Path.Combine(latest.FullName, "Casks");
            if (!File.Exists(Path.Combine(casksDir, token + ".json"))
                && !File.Exists(Path.Combine(casksDir, token + ".rb")))
                continue;

            casks.Add((token, latest.Parent!.Name));
        }
        return casks;
    }

    internal static List<(string Id, string Version)> MergeInstalledEntries(
        IEnumerable<(string Id, string Version)> listed,
        IEnumerable<(string Id, string Version)> fromCaskroom)
    {
        var merged = listed.ToList();
        var seen = new HashSet<string>(merged.Select(e => e.Id), StringComparer.Ordinal);
        merged.AddRange(fromCaskroom.Where(e => seen.Add(e.Id)));
        return merged;
    }

    private void ThrowIfListingFailed(int exitCode, string error)
    {
        if (exitCode != 0)
        {
            var failure = new InvalidOperationException(
                $"{Name} exited with code {exitCode} while listing packages: {error.Trim()}"
            );
            Logger.Warn(failure.Message);
            PackageListingScope.ReportIncomplete(failure);
        }
    }

    // "brew outdated --verbose" prints a different comparison operator per package type:
    //   Formula: "name (old_version) < new_version"  (versions are comparable)
    //   Cask:    "name (old_version) != new_version" (versions are opaque strings)
    // Both operators must be accepted, otherwise cask updates are silently dropped.
    [GeneratedRegex(@"^(\S+)\s+\(([^)]+)\)\s+(?:<|!=)\s+(.+)$")]
    private static partial Regex OutdatedLineRegex();

    protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
    {
        // Sources (formula or cask) come from the installed list, so it must have succeeded.
        IReadOnlyList<IPackage> installed = GetInstalledPackages(out bool installedFailed);
        if (installedFailed)
        {
            var failure = new InvalidOperationException(
                $"{Name} could not list its installed packages, so its updates cannot be checked."
            );
            Logger.Warn(failure.Message);
            PackageListingScope.ReportIncomplete(failure);
        }

        BrewOutput result = RunBrewListing("outdated --verbose", LoggableTaskType.ListUpdates);
        ThrowIfListingFailed(result.ExitCode, result.StdErr);

        return ParseAvailableUpdates(result.StdOut, installed);
    }

    internal IReadOnlyList<Package> ParseAvailableUpdates(
        IEnumerable<string> lines,
        IEnumerable<IPackage> installedPackages)
    {
        // Build a lookup of installed packages to retrieve their sources (Formula vs Cask)
        Dictionary<string, IPackage> installed = [];
        foreach (var pkg in installedPackages)
            installed.TryAdd(pkg.Id, pkg);

        var packages = new List<Package>();
        foreach (var line in lines)
        {
            var m = OutdatedLineRegex().Match(line);
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

        return packages;
    }
}

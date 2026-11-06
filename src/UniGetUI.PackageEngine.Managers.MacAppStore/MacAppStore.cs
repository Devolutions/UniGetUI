using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Managers.MacAppStoreManager.Internal;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Managers.MacAppStoreManager;

/// <summary>
/// Apps from the Mac App Store, driven by the mas command-line tool (https://github.com/mas-cli/mas).
/// Packages are identified by their App Store ID. Descriptions, icons and screenshots come from
/// Apple's public lookup API (<see cref="AppStoreCatalog"/>).
/// </summary>
public partial class MacAppStore : PackageManager
{
    internal const string INSTALL_COMMAND = "brew install mas";

    // Homebrew's prefixes, where mas usually lives, in case they are not on UniGetUI's PATH.
    private static readonly string[] MAS_PATHS = ["/opt/homebrew/bin/mas", "/usr/local/bin/mas"];

    internal AppStoreCatalog Catalog { get; }

    private readonly ConcurrentDictionary<string, string> _installPaths = new();
    private readonly Lock _storefrontLock = new();
    private string? _storefront;

    /// <summary>The major version of mas, which decides its verbs, its output and whether it needs root.</summary>
    internal int MasMajorVersion { get; set; }

    public MacAppStore()
    {
        Dependencies = [];

        Capabilities = new ManagerCapabilities
        {
            CanRunAsAdmin = false,
            CanSkipIntegrityChecks = false,
            CanRunInteractively = false,
            CanReinstallOnUpdate = false,
            CanDownloadInstaller = false,
            SupportsReleaseNotes = true,
            SupportsCustomVersions = false,
            SupportsCustomVersionsOnUpdate = false,
            CanListVersions = false,
            SupportsCustomPackageIcons = true,
            SupportsCustomPackageScreenshots = true,
            // The App Store installs one app at a time, and every operation may prompt for the password.
            SerializesOperations = true,
            SupportsCustomSources = false,
            Sources = new SourceCapabilities { KnowsPackageCount = false, KnowsUpdateDate = false },
            SupportsProxy = ProxySupport.No,
            SupportsProxyAuth = false,
            KnowsPackageReleaseDate = PackageReleaseDateSupport.No,
        };

        var appStore = new ManagerSource(this, "App Store", new Uri("https://apps.apple.com"));
        Properties = new ManagerProperties
        {
            Id = "mas",
            Name = "MacAppStore",
            DisplayName = "Mac App Store",
            SetupGuide = "PACKAGE_MANAGERS.md#mac-app-store",
            InstallCommands = new(MacOS: INSTALL_COMMAND),
            Description = CoreTools.Translate(
                "Apple's app store for macOS, through the mas command-line tool.<br>Contains: <b>Apps from the Mac App Store</b>"
            ),
            IconId = IconType.MsStore,
            ColorIconId = "mas_color",
            ExecutableFriendlyName = "mas",
            InstallVerb = "get",
            UpdateVerb = "update",
            UninstallVerb = "uninstall",
            DefaultSource = appStore,
            KnownSources = [appStore],
        };

        Catalog = new AppStoreCatalog(() => Storefront);
        DetailsHelper = new MacAppStorePkgDetailsHelper(this);
        OperationHelper = new MacAppStorePkgOperationHelper(this);
    }

    // ── Executable discovery ───────────────────────────────────────────────

    public override IReadOnlyList<string> FindCandidateExecutableFiles()
    {
        if (!OperatingSystem.IsMacOS())
            return [];

        var candidates = new List<string>(CoreTools.WhichMultiple("mas"));
        foreach (string path in MAS_PATHS)
        {
            if (File.Exists(path) && !candidates.Contains(path))
                candidates.Add(path);
        }
        return candidates;
    }

    protected override void _loadManagerExecutableFile(out bool found, out string path, out string callArguments)
    {
        (found, path) = GetExecutableFile();
        callArguments = "";
    }

    internal ProcessStartInfo MakeStartInfo(string arguments) => new()
    {
        FileName = Status.ExecutablePath,
        Arguments = arguments,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    protected override void _loadManagerVersion(out string version)
    {
        using var p = new Process { StartInfo = MakeStartInfo("version") };
        p.Start();
        string output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();

        version = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        MasMajorVersion = MasOutput.ParseMajorVersion(version);
        lock (_storefrontLock)
            _storefront = null;
    }

    // mas 7 prints JSON on request; older releases only print tables.
    private string JsonFlag => MasMajorVersion >= 7 ? " --json" : "";

    // ── Storefront ─────────────────────────────────────────────────────────

    /// <summary>
    /// The two-letter country of the App Store account, which decides the catalog lookups see (an
    /// app sold in one country may not exist in another). Asked of mas once per session.
    /// </summary>
    internal string Storefront
    {
        get
        {
            lock (_storefrontLock)
                return _storefront ??= ReadStorefront();
        }
    }

    private string ReadStorefront()
    {
        if (MasMajorVersion >= 3 && Status.Found)
        {
            try
            {
                using var p = new Process { StartInfo = MakeStartInfo("config" + JsonFlag) };
                p.Start();
                string output = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (MasOutput.ParseStorefront(output.Split('\n')) is { } store)
                    return store;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not read the App Store country from mas: {ex.Message}");
            }
        }

        try
        {
            string region = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            if (region.Length == 2 && region.All(char.IsAsciiLetter) && region != "IV")
                return region.ToUpperInvariant();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read the system region: {ex.Message}");
        }
        return "US";
    }

    // ── Package listing ────────────────────────────────────────────────────

    private List<string> RunListing(LoggableTaskType type, string arguments, out int exitCode, out string errors)
    {
        using var p = new Process { StartInfo = MakeStartInfo(arguments) };
        IProcessTaskLogger logger = TaskLogger.CreateNew(type, p);
        p.Start();
        RegisterListingProcess(p);
        Task<string> stderr = p.StandardError.ReadToEndAsync();

        List<string> lines = [];
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            lines.Add(line);
        }

        errors = stderr.GetAwaiter().GetResult();
        logger.AddToStdErr(errors);
        p.WaitForExit();
        exitCode = p.ExitCode;
        logger.Close(exitCode);
        return lines;
    }

    /// <summary>
    /// Search terms as mas reads them: it joins its arguments with spaces. A term starting with a
    /// dash would be read as an option, so leading dashes are dropped.
    /// </summary>
    internal static string SearchTerms(string query) =>
        string.Join(' ', CoreTools.EnsureSafeQueryString(query)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(term => term.TrimStart('-'))
            .Where(term => term.Length > 0));

    protected override IReadOnlyList<Package> FindPackages_UnSafe(string query)
    {
        string terms = SearchTerms(query);
        if (terms.Length == 0)
            return [];

        var lines = RunListing(LoggableTaskType.FindPackages, $"search{JsonFlag} {terms}", out int exitCode, out string errors);
        var apps = MasOutput.ParseApps(lines, outdated: false);
        // mas fails a search that finds nothing.
        if (apps.Count == 0 && exitCode != 0 && !errors.Contains("No apps found", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"mas search failed with exit code {exitCode}: {errors.Trim()}");

        foreach (MasApp app in apps)
            Catalog.Remember(app.Catalog);
        return ToPackages(apps);
    }

    protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe()
    {
        var lines = RunListing(LoggableTaskType.ListInstalledPackages, "list" + JsonFlag, out int exitCode, out string errors);
        var apps = MasOutput.ParseApps(lines, outdated: false);
        if (apps.Count == 0 && exitCode != 0)
            throw new InvalidOperationException($"mas list failed with exit code {exitCode}: {errors.Trim()}");

        foreach (MasApp app in apps)
            if (!string.IsNullOrEmpty(app.Path))
                _installPaths[app.Id] = app.Path;
        Catalog.Want(apps.Select(app => app.Id));
        return ToPackages(apps);
    }

    protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
    {
        var lines = RunListing(LoggableTaskType.ListUpdates, "outdated" + JsonFlag, out int exitCode, out string errors);
        var apps = MasOutput.ParseApps(lines, outdated: true);
        // mas also fails when it cannot look up one installed app (one removed from the store, say),
        // while still listing the others, so a failure only counts when nothing was listed.
        if (apps.Count == 0 && exitCode != 0)
            Logger.Warn($"mas outdated exited with code {exitCode}: {errors.Trim()}");

        Catalog.Want(apps.Select(app => app.Id));
        return ToPackages(apps);
    }

    private Package[] ToPackages(IEnumerable<MasApp> apps) =>
        apps.Select(app => app.NewVersion is null
                ? new Package(app.Name, app.Id, app.Version, DefaultSource, this)
                : new Package(app.Name, app.Id, app.Version, app.NewVersion, DefaultSource, this))
            .ToArray();

    internal string? InstallPathOf(IPackage package)
    {
        if (_installPaths.TryGetValue(package.Id, out string? path) && Directory.Exists(path))
            return path;
        // mas before 7 does not report paths; App Store apps install to /Applications by name.
        string guess = Path.Join("/Applications", package.Name + ".app");
        return Directory.Exists(guess) ? guess : null;
    }
}

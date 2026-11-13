using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
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
/// Built-in Mac App Store package manager, using Apple's Spotlight metadata, iTunes Search/Lookup
/// API (<see cref="AppStoreCatalog"/>), and <c>CommerceKit</c>/<c>StoreFoundation</c> through
/// UniGetUI's bundled <c>UniGetUIMasHelper</c>. Requires no third-party CLI tools.
/// </summary>
public partial class MacAppStore : PackageManager
{
    internal AppStoreCatalog Catalog { get; }

    public static Task<MasTouchIdStatus> GetTouchIdStatusAsync() => NativeMas.GetStatusAsync();

    public static string DescribeTouchIdStatus(MasTouchIdStatus status) => NativeMas.DescribeStatus(status);

    public static Task<string> ConfigureTouchIdAsync(bool enable) => NativeMas.ConfigureAsync(enable);

    private readonly ConcurrentDictionary<string, string> _installPaths = new();
    private readonly Lock _storefrontLock = new();
    private string? _storefront;

    internal bool IsAppleSilicon { get; init; } =
        RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;

    internal Version CurrentMacOSVersion { get; init; } = Environment.OSVersion.Version;

    protected override IEnumerable<ManagerMaintenanceAction> GetExtraMaintenanceActions()
    {
        yield return new ManagerMaintenanceAction
        {
            Id = "reset-cache",
            Title = CoreTools.Translate("Reset App Store cache"),
            Description = CoreTools.Translate("Restart stuck App Store background agents and clear cached downloads."),
            RunAsync = () => RunMaintenanceCommandAsync(Status.ExecutablePath, "reset"),
        };
    }

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
            // The App Store installs one app at a time, and every operation may require authorization.
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
            Description = CoreTools.Translate(
                "Apple's official app store for macOS, built into UniGetUI.<br>Contains: <b>Apps from the Mac App Store</b>"
            ),
            IconId = IconType.MacAppStore,
            ColorIconId = "mas_color",
            ExecutableFriendlyName = "UniGetUIMasHelper",
            InstallVerb = "get",
            UpdateVerb = "update",
            UninstallVerb = "uninstall",
            DefaultSource = appStore,
            KnownSources = [appStore],
        };
        appStore.RefreshSourceNames();

        Catalog = new AppStoreCatalog(() => Storefront);
        DetailsHelper = new MacAppStorePkgDetailsHelper(this);
        OperationHelper = new MacAppStorePkgOperationHelper(this);
    }

    // ── Built-in helper discovery ──────────────────────────────────────────

    public override IReadOnlyList<string> FindCandidateExecutableFiles() =>
        OperatingSystem.IsMacOS() && NativeMas.HelperPath() is { } helper ? [helper] : [];

    protected override void _loadManagerExecutableFile(out bool found, out string path, out string callArguments)
    {
        var candidates = FindCandidateExecutableFiles();
        found = candidates.Count > 0;
        path = found ? candidates[0] : "";
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
        version = $"macOS {Environment.OSVersion.Version}";
        lock (_storefrontLock)
            _storefront = null;
    }

    public override void RefreshPackageIndexes()
    {
        Catalog.Clear();
        lock (_storefrontLock)
            _storefront = null;
    }

    // ── Storefront ─────────────────────────────────────────────────────────

    /// <summary>
    /// The two-letter country of the signed-in App Store account (via <c>ISAccountService</c>),
    /// falling back to the normalized macOS region (<see cref="AppStoreRegion"/>).
    /// </summary>
    internal string Storefront
    {
        get
        {
            lock (_storefrontLock)
                return _storefront ??= ReadStorefront();
        }
        init
        {
            lock (_storefrontLock)
                _storefront = value;
        }
    }

    private string ReadStorefront()
    {
        if (Status.Found && File.Exists(Status.ExecutablePath))
        {
            try
            {
                using var p = new Process { StartInfo = MakeStartInfo("storefront") };
                p.Start();
                string output = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (MasOutput.ParseStorefront(output.Split('\n')) is { } store)
                    return store;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not read the App Store country from StoreFoundation: {ex.Message}");
            }
        }

        try
        {
            return AppStoreRegion.NormalizeTerritory(RegionInfo.CurrentRegion.TwoLetterISORegionName);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read the system region: {ex.Message}");
            return "US";
        }
    }

    // ── Package listing & catalog search ───────────────────────────────────

    private List<string> RunHelperListing(LoggableTaskType type, string arguments, out int exitCode, out string errors)
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

        var logger = TaskLogger.CreateNew(LoggableTaskType.FindPackages);
        try
        {
            // Numeric queries resolve exact App Store IDs first (for package links and CLI by-ID installs).
            if (terms.All(char.IsAsciiDigit)
                && Catalog.Get(terms) is { } record
                && AppStoreRecord.SupportsMac(record, IsAppleSilicon)
                && AppStoreRecord.Text(record, "name", "trackName") is { } exactName)
            {
                logger.Log($"Resolved App Store ID {terms} in the {Storefront} storefront.");
                logger.Close(0);
                return [new Package(exactName.Trim(), terms, AppStoreRecord.Text(record, "version") ?? "", DefaultSource, this)];
            }

            IReadOnlyList<JsonObject> results = Catalog.Search(terms, IsAppleSilicon);
            List<Package> packages = [];
            foreach (JsonObject app in results)
            {
                if (AppStoreRecord.Id(app) is { } id
                    && AppStoreRecord.Text(app, "name", "trackName") is { } name)
                {
                    packages.Add(new Package(name.Trim(), id, AppStoreRecord.Text(app, "version") ?? "", DefaultSource, this));
                }
            }

            logger.Log($"Found {packages.Count} Mac App Store package(s) for '{terms}' in {Storefront}.");
            logger.Close(0);
            return packages;
        }
        catch (Exception ex)
        {
            logger.Error(ex);
            logger.Close(1);
            throw;
        }
    }

    private IReadOnlyList<MasApp> ReadInstalledApps(LoggableTaskType taskType)
    {
        var lines = RunHelperListing(taskType, "list", out int exitCode, out string errors);
        var apps = MasOutput.ParseApps(lines, outdated: false);
        if (apps.Count == 0 && exitCode != 0)
            throw new InvalidOperationException($"Mac App Store Spotlight listing failed with exit code {exitCode}: {errors.Trim()}");

        foreach (MasApp app in apps)
            if (!string.IsNullOrEmpty(app.Path))
                _installPaths[app.Id] = app.Path;
        Catalog.Want(apps.Select(app => app.Id));
        return apps;
    }

    protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe() =>
        ToPackages(ReadInstalledApps(LoggableTaskType.ListInstalledPackages));

    protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
    {
        var installed = ReadInstalledApps(LoggableTaskType.ListUpdates);
        if (installed.Count == 0)
            return [];

        IReadOnlyDictionary<string, JsonObject> catalogById = Catalog.GetMany(installed.Select(app => app.Id));
        List<Package> updates = [];
        foreach (MasApp app in installed)
        {
            if (!catalogById.TryGetValue(app.Id, out JsonObject? record))
                continue;
            if (!AppStoreRecord.IsCompatibleWithMacOS(record, CurrentMacOSVersion))
                continue;
            if (AppStoreRecord.Text(record, "version") is not { Length: > 0 } latestVersion)
                continue;
            if (CompareVersions(latestVersion, app.Version) is > 0)
                updates.Add(new Package(app.Name, app.Id, app.Version, latestVersion, DefaultSource, this));
        }

        return updates;
    }

    private Package[] ToPackages(IEnumerable<MasApp> apps) =>
        apps.Select(app => app.NewVersion is null
                ? new Package(app.Name, app.Id, app.Version, DefaultSource, this)
                : new Package(app.Name, app.Id, app.Version, app.NewVersion, DefaultSource, this))
            .ToArray();

    internal void RecordInstallPath(string id, string path) => _installPaths[id] = path;

    internal void ForgetInstallPath(string id) => _installPaths.TryRemove(id, out _);

    internal bool HasRecordedInstallPath(IPackage package) =>
        _installPaths.TryGetValue(package.Id, out string? path) && Directory.Exists(path);

    internal string? InstallPathOf(IPackage package)
    {
        if (_installPaths.TryGetValue(package.Id, out string? path) && Directory.Exists(path))
            return path;
        string guess = Path.Join("/Applications", package.Name + ".app");
        return Directory.Exists(guess) ? guess : null;
    }
}

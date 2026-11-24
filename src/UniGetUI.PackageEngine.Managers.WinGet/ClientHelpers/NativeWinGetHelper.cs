using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Management.Deployment;
using Microsoft.Win32;
using UniGetUI.Core.Classes;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Structs;
using WindowsPackageManager.Interop;

namespace UniGetUI.PackageEngine.Managers.WingetManager;

internal sealed class NativeWinGetHelper : IWinGetManagerHelper
{
    public WindowsPackageManagerFactory Factory = null!;
    public static WindowsPackageManagerFactory? ExternalFactory;
    public PackageManager WinGetManager = null!;
    public static PackageManager? ExternalWinGetManager;
    private readonly WinGet Manager;
    private readonly Func<WinGet, IWinGetManagerHelper> _systemCliHelperFactory;
    private readonly Func<IReadOnlyList<CatalogPackage>>? _localPackagesProvider;
    private readonly IPingetPackageDetailsProvider _pingetPackageDetailsProvider;
    private const string UserUninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string InstalledIndexInvalidationValueName = "UniGetUI.InstalledIndexInvalidation";
    private int _activeLocalPackageQueries;
    private ExceptionDispatchInfo? _lastActivationException;

    public string ActivationMode { get; private set; } = string.Empty;
    public string ActivationSource { get; private set; } = string.Empty;

    internal static IReadOnlyList<string> PreferredActivationModes =>
    [
        "packaged COM registration",
        "lower-trust COM registration",
    ];

    public NativeWinGetHelper(WinGet manager)
        : this(
            manager,
            systemCliHelperFactory: null,
            skipInitialization: false,
            localPackagesProvider: null
        )
    { }

    internal NativeWinGetHelper(
        WinGet manager,
        Func<WinGet, IWinGetManagerHelper>? systemCliHelperFactory,
        bool skipInitialization,
        Func<IReadOnlyList<CatalogPackage>>? localPackagesProvider,
        IPingetPackageDetailsProvider? pingetPackageDetailsProvider = null
    )
    {
        Manager = manager;
        _systemCliHelperFactory =
            systemCliHelperFactory
            ?? (static manager => manager.CreateCliHelperForSelectedCliTool());
        _localPackagesProvider = localPackagesProvider;
        _pingetPackageDetailsProvider =
            pingetPackageDetailsProvider ?? new PingetPackageDetailsProvider();
        if (CoreTools.IsAdministrator())
        {
            Logger.Info(
                "Running elevated, WinGet class registration is likely to fail unless using lower trust class registration is allowed in settings"
            );
        }

        if (skipInitialization)
        {
            return;
        }

        if (TryInitializeStandardFactory())
        {
            return;
        }

        if (TryInitializeLowerTrustFactory())
        {
            return;
        }

        _lastActivationException?.Throw();

        throw new InvalidOperationException("WinGet: Failed to initialize system COM activation.");
    }

    internal bool HasActiveLocalPackageQuery => Volatile.Read(ref _activeLocalPackageQueries) > 0;

    internal void SetLocalPackageQueryInProgressForTesting(bool value)
    {
        Interlocked.Exchange(ref _activeLocalPackageQueries, value ? 1 : 0);
    }

    private bool TryInitializeLowerTrustFactory()
    {
        try
        {
            var factory = new WindowsPackageManagerStandardFactory(allowLowerTrustRegistration: true);
            var winGetManager = factory.CreatePackageManager();
            ApplyFactory(
                factory,
                winGetManager,
                "lower-trust COM registration",
                "system COM registration (allow lower trust)",
                "Connected to WinGet API using lower-trust COM activation."
            );
            return true;
        }
        catch (WinGetComActivationException ex)
        {
            _lastActivationException = ExceptionDispatchInfo.Capture(ex);
            Logger.Warn(
                $"Lower-trust WinGet COM activation failed ({ex.HResultHex}: {ex.Reason})."
            );
            return false;
        }
        catch (Exception ex)
        {
            _lastActivationException = ExceptionDispatchInfo.Capture(ex);
            Logger.Warn(
                $"Lower-trust WinGet COM activation failed ({ex.Message})."
            );
            return false;
        }
    }

    private bool TryInitializeStandardFactory()
    {
        try
        {
            var factory = new WindowsPackageManagerStandardFactory();
            var winGetManager = factory.CreatePackageManager();
            ApplyFactory(
                factory,
                winGetManager,
                "packaged COM registration",
                "system COM registration",
                "Connected to WinGet API using packaged COM activation."
            );
            return true;
        }
        catch (WinGetComActivationException ex)
        {
            _lastActivationException = ExceptionDispatchInfo.Capture(ex);
            Logger.Warn(
                $"Packaged WinGet COM activation failed ({ex.HResultHex}: {ex.Reason}), attempting lower-trust activation..."
            );
            return false;
        }
        catch (Exception ex)
        {
            _lastActivationException = ExceptionDispatchInfo.Capture(ex);
            Logger.Warn(
                $"Packaged WinGet COM activation failed ({ex.Message}), attempting lower-trust activation..."
            );
            return false;
        }
    }

    private void ApplyFactory(
        WindowsPackageManagerFactory factory,
        PackageManager winGetManager,
        string activationMode,
        string activationSource,
        string successMessage
    )
    {
        Factory = factory;
        WinGetManager = winGetManager;
        ActivationMode = activationMode;
        ActivationSource = activationSource;
        ExternalFactory = factory;
        ExternalWinGetManager = winGetManager;

        Logger.Info(successMessage);
        Logger.Info($"WinGet activation mode selected: {ActivationMode} | Source: {ActivationSource}");
    }

    internal static readonly IReadOnlyList<PackageMatchField> SearchFields =
    [
        PackageMatchField.Name,
        PackageMatchField.Id,
        PackageMatchField.Moniker,
    ];

    internal readonly record struct NativeSearchMatchId(string CatalogName, string? Id);

    internal readonly record struct NativeSearchResult(string Name, string Version);

    internal static IReadOnlyList<int> SelectUniqueSearchMatches(
        IReadOnlyList<NativeSearchMatchId> matches
    )
    {
        List<int> selected = [];
        HashSet<(string, string)> seen = [];
        for (int index = 0; index < matches.Count; index++)
        {
            if (matches[index].Id is { } id && seen.Add((matches[index].CatalogName, id)))
            {
                selected.Add(index);
            }
        }

        return selected;
    }

    internal sealed record ResolvedSearchMatch<TPackage>(
        int FirstIndex,
        string Id,
        NativeSearchResult Result,
        TPackage Package
    );

    internal sealed record SearchMatchResolution<TPackage>(
        IReadOnlyList<ResolvedSearchMatch<TPackage>> Matches,
        IReadOnlyList<Exception> Errors,
        int ResultCount
    );

    internal static SearchMatchResolution<TPackage> ResolveSearchMatches<TPackage>(
        IReadOnlyList<(string CatalogName, TPackage Package)> matches,
        Func<TPackage, string> readId,
        Func<TPackage, NativeSearchResult> readResult,
        int parallelism,
        CancellationToken cancellationToken
    )
    {
        var ids = new NativeSearchMatchId[matches.Count];
        var resolved = new NativeSearchResult?[matches.Count];
        var errors = new Exception?[matches.Count];
        ConcurrentDictionary<(string, string), int> resolvingIndex = new();
        Parallel.For(
            0,
            matches.Count,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = cancellationToken,
            },
            index =>
            {
                var (catalogName, package) = matches[index];
                try
                {
                    string id = readId(package);
                    ids[index] = new(catalogName, id);
                    if (resolvingIndex.TryAdd((catalogName, id), index))
                    {
                        resolved[index] = readResult(package);
                    }
                }
                catch (Exception e)
                {
                    if (ids[index].Id is null)
                    {
                        ids[index] = new(catalogName, null);
                    }

                    errors[index] = e;
                }
            }
        );

        List<ResolvedSearchMatch<TPackage>> results = [];
        foreach (int firstIndex in SelectUniqueSearchMatches(ids))
        {
            NativeSearchMatchId match = ids[firstIndex];
            int index = resolvingIndex[(match.CatalogName, match.Id!)];
            if (resolved[index] is { } result)
            {
                results.Add(new(firstIndex, match.Id!, result, matches[index].Package));
            }
        }

        return new(
            results,
            [.. errors.OfType<Exception>()],
            resolvingIndex.Count + ids.Count(match => match.Id is null)
        );
    }

    public IReadOnlyList<Package> FindPackages_UnSafe(string query)
    {
        if (query.Length == 0)
        {
            return _systemCliHelperFactory(Manager).FindPackages_UnSafe(query);
        }

        List<Package> packages = [];
        INativeTaskLogger logger = Manager.TaskLogger.CreateNew(LoggableTaskType.FindPackages);
        Dictionary<
            (PackageCatalogReference, PackageMatchField),
            Task<FindPackagesResult>
        > FindPackageTasks = [];

        // Load catalogs
        logger.Log("Loading available catalogs...");
        IReadOnlyList<PackageCatalogReference> AvailableCatalogs =
            WinGetManager.GetPackageCatalogs();

        // Spawn Tasks to find packages on catalogs
        logger.Log("Spawning catalog fetching tasks...");
        foreach (PackageCatalogReference CatalogReference in NativeWinGetCollection.Copy(AvailableCatalogs))
        {
            logger.Log($"Begin search on catalog {CatalogReference.Info.Name}");
            // Connect to catalog
            CatalogReference.AcceptSourceAgreements = true;
            ConnectResult result = CatalogReference.Connect();
            if (result.Status == ConnectResultStatus.Ok)
            {
                foreach (PackageMatchField field in SearchFields)
                {
                    FindPackagesOptions PackageFilters = Factory.CreateFindPackagesOptions();

                    logger.Log("Generating filters...");
                    // Name filter
                    PackageMatchFilter FilterName = Factory.CreatePackageMatchFilter();
                    FilterName.Field = field;
                    FilterName.Value = query;
                    FilterName.Option = PackageFieldMatchOption.ContainsCaseInsensitive;
                    PackageFilters.Filters.Add(FilterName);

                    try
                    {
                        // Create task and spawn it
                        Task<FindPackagesResult> task = new(() =>
                            result.PackageCatalog.FindPackages(PackageFilters)
                        );
                        task.Start();

                        // Add task to list
                        FindPackageTasks.Add((CatalogReference, field), task);
                    }
                    catch (Exception e)
                    {
                        logger.Error(
                            "WinGet: Catalog "
                                + CatalogReference.Info.Name
                                + " failed to spawn FindPackages task."
                        );
                        logger.Error(e);
                    }
                }
            }
            else
            {
                logger.Error(
                    "WinGet: Catalog " + CatalogReference.Info.Name + " failed to connect."
                );
            }
        }

        // Wait for tasks completion
        Task.WhenAll(FindPackageTasks.Values.ToArray()).GetAwaiter().GetResult();
        logger.Log($"All catalogs fetched. Fetching results for query piece {query}");

        List<(IManagerSource Source, CatalogPackage NativePackage)> matches = [];
        foreach (var CatalogTaskPair in FindPackageTasks)
        {
            try
            {
                // Get the source for the catalog
                IManagerSource source = Manager.SourcesHelper.Factory.GetSourceOrDefault(
                    CatalogTaskPair.Key.Item1.Info.Name
                );

                FindPackagesResult FoundPackages = CatalogTaskPair.Value.Result;
                foreach (
                    MatchResult matchResult in NativeWinGetCollection.Copy(FoundPackages.Matches)
                )
                {
                    matches.Add((source, matchResult.CatalogPackage));
                }
            }
            catch (Exception e)
            {
                logger.Error(
                    "WinGet: Catalog "
                        + CatalogTaskPair.Key.Item1.Info.Name
                        + " failed to get available packages."
                );
                logger.Error(e);
            }
        }

        SearchMatchResolution<CatalogPackage> resolution;
        try
        {
            resolution = ResolveSearchMatches(
                [.. matches.Select(match => (match.Source.Name, match.NativePackage))],
                nativePackage => nativePackage.Id,
                nativePackage =>
                    new(nativePackage.Name, nativePackage.DefaultInstallVersion.Version),
                SearchVersionResolutionParallelism,
                Manager.CurrentListingCancellation
            );
        }
        catch (OperationCanceledException)
        {
            logger.Error("WinGet: The search was cancelled before its results were read.");
            logger.Close(1);
            throw;
        }

        if (resolution.Errors.Count > 0)
        {
            logger.Error(
                $"WinGet: {resolution.Errors.Count} of {resolution.ResultCount} search results could not be read. First error:"
            );
            logger.Error(resolution.Errors[0]);
        }

        foreach (var match in resolution.Matches)
        {
            IManagerSource source = matches[match.FirstIndex].Source;
            logger.Log(
                $"Found package: {match.Result.Name}|{match.Id}|{match.Result.Version} on catalog {source.Name}"
            );

            var UniGetUIPackage = new Package(
                match.Result.Name,
                match.Id,
                match.Result.Version,
                source,
                Manager,
                new OverridenInstallationOptions()
            );
            NativePackageHandler.AddSearchResult(UniGetUIPackage, match.Package);
            packages.Add(UniGetUIPackage);
        }

        logger.Close(packages.Count == 0 && resolution.Errors.Count > 0 ? 1 : 0);
        return packages;
    }

    private const int SearchVersionResolutionParallelism = 8;

    public IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
    {
        var logger = Manager.TaskLogger.CreateNew(LoggableTaskType.ListUpdates);
        IReadOnlyList<CatalogPackage> nativePackages;
        try
        {
            nativePackages = GetCachedLocalWinGetPackages(15);
        }
        catch (Exception ex) when (ShouldFallbackToCli(ex))
        {
            return GetAvailableUpdatesFromSystemCli(ex);
        }

        List<Package> packages = [];
        List<(CatalogPackage Package, PackageVersionInfo Newest)> reinstallCandidates = [];
        Lazy<IReadOnlyList<InstalledEntry>> installedEntries = new(() => ReadInstalledEntries(nativePackages));
        foreach (var nativePackage in nativePackages)
        {
            try
            {
                if (!nativePackage.IsUpdateAvailable)
                {
                    if (
                        Manager.GetLauncherGameSource(nativePackage.InstalledVersion?.Id) is null
                        && TryGetReinstallOnlyUpdate(nativePackage, logger) is { } newest
                    )
                    {
                        reinstallCandidates.Add((nativePackage, newest));
                    }

                    continue;
                }

                if (Manager.GetLauncherGameSource(nativePackage.InstalledVersion.Id) is { } launcherGameSource)
                {
                    logger.Log(
                        $"Skipping the update of {nativePackage.Id}, since it was installed by {launcherGameSource.Name}"
                    );
                    continue;
                }

                if (nativePackage.DefaultInstallVersion is null)
                {
                    Logger.Warn(
                        $"WinGet package {nativePackage.Id} has IsUpdateAvailable=true but DefaultInstallVersion is null, skipping"
                    );
                    continue;
                }

                if (nativePackage.DefaultInstallVersion.PackageCatalog?.Info is null)
                {
                    Logger.Warn(
                        $"WinGet package {nativePackage.Id} has a DefaultInstallVersion with null PackageCatalog or Info, skipping"
                    );
                    continue;
                }

                if (
                    IsSupersededByAnotherInstall(
                        nativePackage,
                        nativePackage.DefaultInstallVersion.Version,
                        installedEntries.Value,
                        logger
                    )
                )
                {
                    continue;
                }

                IManagerSource source;
                source = Manager.SourcesHelper.Factory.GetSourceOrDefault(
                    nativePackage.DefaultInstallVersion.PackageCatalog.Info.Name
                );

                string? reportedVersion = nativePackage.InstalledVersion.Version;
                string version = WinGetPkgOperationHelper.ResolveReportedInstalledVersion(
                    nativePackage.Id,
                    reportedVersion
                );

                var UniGetUIPackage = new Package(
                    nativePackage.Name,
                    nativePackage.Id,
                    version,
                    nativePackage.DefaultInstallVersion.Version,
                    source,
                    Manager
                )
                {
                    InstalledVersionIsUnverified = WinGetPkgOperationHelper.IsUnknownVersion(
                        reportedVersion
                    ),
                };

                // Suppress an update that repeatedly fails to stick (#5158); the COM path still avoids
                // the one-shot "already upgraded" cache (#5042).
                if (WinGetPkgOperationHelper.IsStuckUpgradeLoop(UniGetUIPackage))
                    continue;

                NativePackageHandler.AddPackage(UniGetUIPackage, nativePackage);
                NativePackageHandler.AddLocalIdentifier(
                    UniGetUIPackage,
                    nativePackage.InstalledVersion?.Id
                );
                packages.Add(UniGetUIPackage);
                logger.Log(
                    $"Found package {nativePackage.Name} {nativePackage.Id} on source {source.Name}, from version {version} to version {nativePackage.DefaultInstallVersion.Version}"
                );
            }
            catch (Exception ex)
            {
                LogSkippedPackage(logger, nativePackage, "listing available updates", ex);
            }
        }

        AddReinstallOnlyUpdates(
            [
                .. reinstallCandidates.Where(candidate =>
                    !IsSupersededByAnotherInstall(
                        candidate.Package,
                        candidate.Newest.Version,
                        installedEntries.Value,
                        logger
                    )
                ),
            ],
            packages,
            logger
        );

        logger.Close(0);
        return packages;
    }

    internal readonly record struct InstalledEntry(string DisplayName, string Publisher, string Version);

    private static IReadOnlyList<InstalledEntry> ReadInstalledEntries(
        IReadOnlyList<CatalogPackage> nativePackages
    )
    {
        List<InstalledEntry> entries = [];
        foreach (var nativePackage in nativePackages)
        {
            if (ReadInstalledEntry(nativePackage) is { } entry)
                entries.Add(entry);
        }

        return entries;
    }

    private static InstalledEntry? ReadInstalledEntry(CatalogPackage nativePackage)
    {
        try
        {
            return nativePackage.InstalledVersion is { } installed
                ? new InstalledEntry(
                    installed.DisplayName ?? "",
                    installed.Publisher ?? "",
                    installed.Version ?? ""
                )
                : null;
        }
        catch (Exception ex)
        {
            Logger.Debug(
                $"Could not read the installed entry of {DescribePackage(nativePackage)}: {ex.Message}"
            );
            PackageListingScope.ReportIncomplete(ex);
            return null;
        }
    }

    private bool IsSupersededByAnotherInstall(
        CatalogPackage nativePackage,
        string? newVersion,
        IReadOnlyList<InstalledEntry> installedEntries,
        INativeTaskLogger logger
    )
    {
        if (
            ReadInstalledEntry(nativePackage) is not { } candidate
            || FindInstallAtOrAbove(Manager, candidate, newVersion, installedEntries) is not { } newer
        )
        {
            return false;
        }

        logger.Log(
            $"Skipping the update of {DescribePackage(nativePackage)} from {candidate.Version} to {newVersion}, "
                + $"since {newer.DisplayName} {newer.Version} from {newer.Publisher} is already installed"
        );
        return true;
    }

    internal static InstalledEntry? FindInstallAtOrAbove(
        IPackageManager manager,
        InstalledEntry candidate,
        string? newVersion,
        IEnumerable<InstalledEntry> installedEntries
    )
    {
        if (
            string.IsNullOrWhiteSpace(candidate.DisplayName)
            || string.IsNullOrWhiteSpace(candidate.Publisher)
            || WinGetPkgOperationHelper.IsUnknownVersion(newVersion)
        )
        {
            return null;
        }

        foreach (var entry in installedEntries)
        {
            if (
                string.Equals(entry.DisplayName, candidate.DisplayName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Publisher, candidate.Publisher, StringComparison.OrdinalIgnoreCase)
                && !WinGetPkgOperationHelper.IsUnknownVersion(entry.Version)
                && manager.CompareVersions(entry.Version, newVersion!) is >= 0
            )
            {
                return entry;
            }
        }

        return null;
    }

    private PackageVersionInfo? TryGetReinstallOnlyUpdate(CatalogPackage nativePackage, INativeTaskLogger logger)
    {
        try
        {
            PackageVersionInfo? installed = nativePackage.InstalledVersion;
            var availableVersions = nativePackage.AvailableVersions;
            if (installed is null || availableVersions is null)
                return null;

            string installedChannel = installed.Channel ?? "";
            PackageVersionId? newestId = null;
            for (int i = 0; i < availableVersions.Count; i++)
            {
                if (string.Equals(availableVersions[i].Channel ?? "", installedChannel, StringComparison.OrdinalIgnoreCase))
                {
                    newestId = availableVersions[i];
                    break;
                }
            }

            if (newestId is null || !IsNewerVersion(Manager, installed.Version, newestId.Version))
                return null;

            PackageVersionInfo newest = nativePackage.GetPackageVersionInfo(newestId);
            if (newest?.PackageCatalog?.Info is null)
                return null;

            if (!IsSamePackage(nativePackage.Id, newest.Id))
            {
                logger.Log(
                    $"Not listing {DescribePackage(nativePackage)} as an update that needs a reinstall: its newest version {newest.Version} "
                        + $"belongs to {newest.Id} on {newest.PackageCatalog.Info.Name}, not to {nativePackage.Id}"
                );
                return null;
            }

            PackageInstallerInfo? installer = newest.GetApplicableInstaller(Factory.CreateInstallOptions());
            if (installer is null)
                return null;

            if (
                UpgradesInstalledProduct(
                    new InstalledProduct(
                        NativeWinGetCollection.Copy(installed.ProductCodes),
                        () => ReadInstalledScope(installed)
                    ),
                    new NewestProduct(NativeWinGetCollection.Copy(newest.ProductCodes), installer.Scope)
                )
            )
            {
                logger.Log(
                    $"Not listing {DescribePackage(nativePackage)} as an update that needs a reinstall: its newest version {newest.Version} "
                        + "installs the same Windows Installer product in the same scope, so uninstalling it first is not needed"
                );
                return null;
            }

            return newest;
        }
        catch (Exception ex) when (!PackageListingScope.IsListingOnly)
        {
            Logger.Debug(
                $"Could not check whether {DescribePackage(nativePackage)} has an update that needs a reinstall: {ex.Message}"
            );
            PackageListingScope.ReportIncomplete(ex);
            return null;
        }
    }

    private static IEnumerable<string?> GetPinIdentifiers(CatalogPackage nativePackage)
    {
        yield return nativePackage.Id;
        if (nativePackage.InstalledVersion is not { } installed)
            yield break;

        yield return installed.Id;
        foreach (string productCode in NativeWinGetCollection.Copy(installed.ProductCodes))
            yield return productCode;
        foreach (string familyName in NativeWinGetCollection.Copy(installed.PackageFamilyNames))
            yield return familyName;
    }

    internal static bool IsSamePackage(string? listedId, string? newestId) =>
        !string.IsNullOrWhiteSpace(listedId)
        && string.Equals(listedId, newestId, StringComparison.OrdinalIgnoreCase);

    internal sealed record InstalledProduct(IReadOnlyList<string> ProductCodes, Func<string?> ReadScope);

    internal sealed record NewestProduct(IReadOnlyList<string> ProductCodes, PackageInstallerScope Scope);

    internal static bool UpgradesInstalledProduct(InstalledProduct installed, NewestProduct newest)
    {
        string[] newestCodes = [.. newest.ProductCodes.Where(code => !string.IsNullOrWhiteSpace(code))];
        if (
            newestCodes.Length == 0
            || !newestCodes.All(code => installed.ProductCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
        )
            return false;

        string? expectedScope = newest.Scope switch
        {
            PackageInstallerScope.User => "User",
            PackageInstallerScope.System => "System",
            _ => null,
        };
        return expectedScope is not null
            && string.Equals(installed.ReadScope(), expectedScope, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadInstalledScope(PackageVersionInfo installed)
    {
        try
        {
            return installed.GetMetadata(PackageVersionMetadataField.InstalledScope);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not read the installed scope of {installed.Id}: {ex.Message}");
            return null;
        }
    }

    internal static bool IsNewerVersion(IPackageManager manager, string? installedVersion, string? newestVersion) =>
        !WinGetPkgOperationHelper.IsUnknownVersion(installedVersion)
        && !string.IsNullOrWhiteSpace(newestVersion)
        && manager.CompareVersions(installedVersion!, newestVersion) is < 0;

    private void AddReinstallOnlyUpdates(
        List<(CatalogPackage Package, PackageVersionInfo Newest)> candidates,
        List<Package> packages,
        INativeTaskLogger logger
    )
    {
        if (candidates.Count == 0)
            return;

        if (Settings.Get(Settings.K.UseAgentBroker))
        {
            logger.Log(
                "Updates that need a reinstall are not listed while operations go through the Devolutions Agent"
            );
            return;
        }

        IReadOnlyList<string>? pinnedIds = WinGetPins.Read(Manager);
        if (pinnedIds is null)
        {
            logger.Error(
                "Could not read the WinGet pins, so updates that need a reinstall are not listed"
            );
            return;
        }

        foreach (var (nativePackage, newest) in candidates)
        {
            try
            {
                if (WinGetPins.IsPinned(pinnedIds, GetPinIdentifiers(nativePackage)))
                    continue;

                IManagerSource source = Manager.SourcesHelper.Factory.GetSourceOrDefault(
                    newest.PackageCatalog.Info.Name
                );
                string? reportedVersion = nativePackage.InstalledVersion.Version;
                var package = new Package(
                    nativePackage.Name,
                    nativePackage.Id,
                    WinGetPkgOperationHelper.ResolveReportedInstalledVersion(
                        nativePackage.Id,
                        reportedVersion
                    ),
                    newest.Version,
                    source,
                    Manager
                )
                {
                    InstalledVersionIsUnverified = WinGetPkgOperationHelper.IsUnknownVersion(
                        reportedVersion
                    ),
                    RequiresReinstallToUpdate = true,
                };

                if (WinGetPkgOperationHelper.IsStuckUpgradeLoop(package))
                    continue;

                NativePackageHandler.AddPackage(package, nativePackage);
                NativePackageHandler.AddLocalIdentifier(package, nativePackage.InstalledVersion?.Id);
                packages.Add(package);
                logger.Log(
                    $"Found package {nativePackage.Name} {nativePackage.Id} on source {source.Name}, from version {package.VersionString} to version {newest.Version}, which needs a reinstall"
                );
            }
            catch (Exception ex)
            {
                LogSkippedPackage(logger, nativePackage, "listing updates that need a reinstall", ex);
            }
        }
    }

    public IReadOnlyList<Package> GetInstalledPackages_UnSafe()
    {
        var logger = Manager.TaskLogger.CreateNew(LoggableTaskType.ListInstalledPackages);
        IReadOnlyList<CatalogPackage> nativePackages;
        try
        {
            nativePackages = GetCachedLocalWinGetPackages(15);
        }
        catch (Exception ex) when (ShouldFallbackToCli(ex))
        {
            return GetInstalledPackagesFromSystemCli(ex);
        }

        List<Package> packages = [];
        foreach (var nativePackage in nativePackages)
        {
            try
            {
                string id = nativePackage.Id;
                string? localIdentifier = nativePackage.InstalledVersion.Id;
                IManagerSource source;
                var availableVersions =
                    nativePackage.AvailableVersions is { } versions
                        ? NativeWinGetCollection.Copy(versions)
                        : [];
                if (Manager.GetLauncherGameSource(localIdentifier) is { } launcherGameSource)
                {
                    id = localIdentifier!;
                    source = launcherGameSource;
                }
                else if (availableVersions.Length > 0)
                {
                    var installPackage = nativePackage.GetPackageVersionInfo(availableVersions[0]);
                    source = Manager.SourcesHelper.Factory.GetSourceOrDefault(
                        installPackage.PackageCatalog.Info.Name
                    );
                }
                else
                {
                    source = Manager.GetLocalSource(nativePackage.Id);
                }

                string? reportedVersion = nativePackage.InstalledVersion.Version;
                string version = WinGetPkgOperationHelper.ResolveReportedInstalledVersion(
                    id,
                    reportedVersion
                );

                logger.Log(
                    $"Found package {nativePackage.Name} {id} on source {source.Name}"
                );
                var UniGetUIPackage = new Package(
                    nativePackage.Name,
                    id,
                    version,
                    source,
                    Manager
                )
                {
                    InstalledVersionIsUnverified = WinGetPkgOperationHelper.IsUnknownVersion(
                        reportedVersion
                    ),
                };
                NativePackageHandler.AddPackage(UniGetUIPackage, nativePackage);
                NativePackageHandler.AddLocalIdentifier(
                    UniGetUIPackage,
                    nativePackage.InstalledVersion?.Id
                );
                packages.Add(UniGetUIPackage);
            }
            catch (Exception ex)
            {
                LogSkippedPackage(logger, nativePackage, "listing installed packages", ex);
            }
        }
        logger.Close(0);
        return packages;
    }

    private IReadOnlyList<CatalogPackage> GetCachedLocalWinGetPackages(int? cacheSeconds = null)
    {
        (long, long) generations = (
            WinGet.SourceIndexGeneration,
            Manager.InstalledPackagesGeneration
        );

        LocalPackageSnapshot snapshot;
        try
        {
            // Remote scans must see changes made outside this process before the UI cache expires.
            if (PackageListingScope.IsListingOnly)
            {
                snapshot = EnumerateLocalWinGetPackages(generations);
            }
            else
            {
                snapshot = cacheSeconds is null
                    ? TaskRecycler<LocalPackageSnapshot>.RunOrAttach(
                        EnumerateLocalWinGetPackages,
                        generations
                    )
                    : TaskRecycler<LocalPackageSnapshot>.RunOrAttach(
                        EnumerateLocalWinGetPackages,
                        generations,
                        cacheSeconds.Value
                    );
            }
        }
        catch (LocalPackageEnumerationException ex)
        {
            foreach (var failure in ex.Failures)
                PackageListingScope.ReportIncomplete(failure);
            ex.Error.Throw();
            throw;
        }

        foreach (var failure in snapshot.Failures)
            PackageListingScope.ReportIncomplete(failure);
        return snapshot.Packages;
    }

    private sealed record LocalPackageSnapshot(
        IReadOnlyList<CatalogPackage> Packages,
        IReadOnlyList<Exception> Failures
    );

    private sealed class LocalPackageEnumerationException(
        Exception error,
        IReadOnlyList<Exception> failures
    ) : Exception("WinGet local package enumeration failed after omitting catalogs.", error)
    {
        public ExceptionDispatchInfo Error { get; } = ExceptionDispatchInfo.Capture(error);
        public IReadOnlyList<Exception> Failures { get; } = failures;
    }

    private LocalPackageSnapshot EnumerateLocalWinGetPackages((long, long) generations)
    {
        List<Exception> failures = [];
        try
        {
            // Cache completeness with the rows, not with the first caller's tracking callback.
            using var tracking = PackageListingScope.TrackFailures(failures.Add);
            var packages = _localPackagesProvider is not null
                ? _localPackagesProvider()
                : GetLocalWinGetPackages();
            return new(packages, [.. failures]);
        }
        catch (Exception ex) when (failures.Count > 0)
        {
            throw new LocalPackageEnumerationException(ex, [.. failures]);
        }
    }

    private IReadOnlyList<Package> GetAvailableUpdatesFromSystemCli(Exception ex)
    {
        var unwrappedException = UnwrapException(ex);
        Logger.Warn(
            $"Native WinGet update enumeration failed with {unwrappedException.GetType().Name}: {unwrappedException.Message}. Falling back to system WinGet CLI."
        );
        return _systemCliHelperFactory(Manager).GetAvailableUpdates_UnSafe();
    }

    private IReadOnlyList<Package> GetInstalledPackagesFromSystemCli(Exception ex)
    {
        var unwrappedException = UnwrapException(ex);
        Logger.Warn(
            $"Native WinGet installed-package enumeration failed with {unwrappedException.GetType().Name}: {unwrappedException.Message}. Falling back to system WinGet CLI."
        );
        return _systemCliHelperFactory(Manager).GetInstalledPackages_UnSafe();
    }

    private static bool ShouldFallbackToCli(Exception ex)
    {
        var unwrappedException = UnwrapException(ex);

        // Native COM/SEH exceptions from the WinGet COM server or its interop layer
        if (unwrappedException is SEHException or COMException or AccessViolationException)
        {
            return true;
        }

        return
            unwrappedException is InvalidOperationException
            && string.Equals(
                unwrappedException.Message,
                "WinGet: Failed to connect to composite catalog.",
                StringComparison.Ordinal
            );
    }

    private static Exception UnwrapException(Exception ex)
    {
        while (ex is AggregateException aggregateException && aggregateException.InnerException is not null)
        {
            ex = aggregateException.InnerException;
        }

        return ex;
    }

    private static void LogSkippedPackage(
        INativeTaskLogger logger,
        CatalogPackage nativePackage,
        string operation,
        Exception ex
    )
    {
        string packageDescription = DescribePackage(nativePackage);
        logger.Error($"Skipping WinGet package while {operation}: {packageDescription}");
        logger.Error(ex);
        Logger.Warn(
            $"Skipping WinGet package while {operation}: {packageDescription}. {ex.GetType().Name}: {ex.Message}"
        );
        PackageListingScope.ReportIncomplete(ex);
    }

    private static string DescribePackage(CatalogPackage nativePackage)
    {
        try
        {
            string id = nativePackage.Id;
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }
        catch
        {
        }

        try
        {
            string name = nativePackage.Name;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        catch
        {
        }

        return "<unknown package>";
    }

    private IReadOnlyList<CatalogPackage> GetLocalWinGetPackages()
    {
        Interlocked.Increment(ref _activeLocalPackageQueries);
        var logger = Manager.TaskLogger.CreateNew(LoggableTaskType.OtherTask);
        try
        {
            logger.Log("OtherTask: GetWinGetLocalPackages");
            InvalidateInstalledIndexCache(logger);
            var availableCatalogs = GetReachableLocalCatalogReferences(logger);
            FindPackagesResult TaskResult = FindWithFallbacks(
                availableCatalogs,
                IsOnlineOnlyCatalog,
                catalogs => FindInstalledPackages(catalogs, logger),
                static result => result.Status == FindPackagesResultStatus.Ok,
                (failed, nextAttempt) =>
                {
                    string message =
                        $"WinGet could not search its catalogs for installed packages ({DescribeFailure(failed)}); "
                        + $"listing them again {nextAttempt}";
                    logger.Error(message);
                    Logger.Warn(message);
                }
            );

            if (TaskResult.Status != FindPackagesResultStatus.Ok)
            {
                logger.Error($"WinGet could not list installed packages: {DescribeFailure(TaskResult)}");
                logger.Close(1);
                throw new InvalidOperationException(
                    $"WinGet could not list installed packages: {DescribeFailure(TaskResult)}",
                    TaskResult.ExtendedErrorCode
                );
            }

            List<CatalogPackage> foundPackages = [];
            foreach (var match in NativeWinGetCollection.Copy(TaskResult.Matches))
            {
                foundPackages.Add(match.CatalogPackage);
            }

            logger.Close(0);
            return foundPackages;
        }
        finally
        {
            Interlocked.Decrement(ref _activeLocalPackageQueries);
        }
    }

    private FindPackagesResult FindInstalledPackages(
        IReadOnlyList<PackageCatalogReference> catalogs,
        INativeTaskLogger logger
    )
    {
        PackageCatalogReference installedSearchCatalogRef =
            catalogs.Count > 0
                ? CreateLocalCompositeCatalog(catalogs, logger)
                : WinGetManager.GetLocalPackageCatalog(LocalPackageCatalog.InstalledPackages);

        var ConnectResult = installedSearchCatalogRef.Connect();
        if (ConnectResult.Status != ConnectResultStatus.Ok)
        {
            logger.Error("Failed to connect to installedSearchCatalogRef. Aborting.");
            logger.Close(1);
            throw new InvalidOperationException("WinGet: Failed to connect to composite catalog.");
        }

        FindPackagesOptions findPackagesOptions = Factory.CreateFindPackagesOptions();
        PackageMatchFilter filter = Factory.CreatePackageMatchFilter();
        filter.Field = PackageMatchField.Id;
        filter.Option = PackageFieldMatchOption.StartsWithCaseInsensitive;
        filter.Value = "";
        findPackagesOptions.Filters.Add(filter);

        try
        {
            return ConnectResult.PackageCatalog.FindPackages(findPackagesOptions);
        }
        catch (Exception ex)
        {
            logger.Error($"FindPackages native call failed: {ex.GetType().Name}: {ex.Message}");
            logger.Close(1);
            throw new InvalidOperationException("WinGet: Failed to connect to composite catalog.", ex);
        }
    }

    internal static TResult FindWithFallbacks<TCatalog, TResult>(
        IReadOnlyList<TCatalog> catalogs,
        Func<TCatalog, bool> isOnlineOnly,
        Func<IReadOnlyList<TCatalog>, TResult> find,
        Func<TResult, bool> succeeded,
        Action<TResult, string>? onRetry = null
    )
    {
        TResult result = find(catalogs);
        if (succeeded(result))
            return result;

        TCatalog[] offlineCatalogs = [.. catalogs.Where(catalog => !isOnlineOnly(catalog))];
        if (offlineCatalogs.Length < catalogs.Count)
        {
            onRetry?.Invoke(
                result,
                offlineCatalogs.Length > 0
                    ? "without its online-only catalogs"
                    : "from the installed-packages catalog alone"
            );
            PackageListingScope.ReportIncomplete(new InvalidOperationException(
                "WinGet local package enumeration omitted online-only catalogs after a failed search."
            ));
            result = find(offlineCatalogs);
            if (succeeded(result))
                return result;
        }

        if (offlineCatalogs.Length > 0)
        {
            onRetry?.Invoke(result, "from the installed-packages catalog alone");
            PackageListingScope.ReportIncomplete(new InvalidOperationException(
                "WinGet local package enumeration omitted its source catalogs after a failed search."
            ));
            result = find([]);
        }

        return result;
    }

    internal static bool IsOnlineOnlyCatalog(PackageCatalogReference catalog)
    {
        try
        {
            return IsOnlineOnlyCatalogType(catalog.Info.Type);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsOnlineOnlyCatalogType(string? type) =>
        string.Equals(type, "Microsoft.Rest", StringComparison.OrdinalIgnoreCase);

    private static string DescribeFailure(FindPackagesResult result) =>
        result.ExtendedErrorCode is { } error
            ? $"{result.Status}, 0x{error.HResult:X8}"
            : result.Status.ToString();

    private static void InvalidateInstalledIndexCache(INativeTaskLogger logger)
    {
        try
        {
            using RegistryKey? uninstallKey = Registry.CurrentUser.OpenSubKey(
                UserUninstallKeyPath,
                writable: true
            );
            if (uninstallKey is null)
            {
                logger.Log("No per-user uninstall key found; skipping WinGet installed index invalidation");
                return;
            }

            uninstallKey.SetValue(InstalledIndexInvalidationValueName, Environment.ProcessId);
            uninstallKey.DeleteValue(InstalledIndexInvalidationValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            logger.Error($"Could not invalidate the WinGet installed index: {ex.Message}");
            Logger.Warn($"Could not invalidate the WinGet installed index: {ex.Message}");
        }
    }

    private IReadOnlyList<PackageCatalogReference> GetReachableLocalCatalogReferences(
        INativeTaskLogger logger
    )
    {
        return SelectReachableCatalogs(
            NativeWinGetCollection.Copy(WinGetManager.GetPackageCatalogs()),
            static catalogRef => catalogRef.Info.Name,
            catalogRef => TryConnectLocalCatalog(catalogRef, logger),
            catalogName =>
            {
                logger.Log($"Catalog {catalogName} is reachable for local package enumeration");
            },
            catalogName =>
            {
                string message =
                    $"Skipping unavailable WinGet catalog {catalogName} while loading local packages";
                logger.Error(message);
                Logger.Warn(message);
            }
        );
    }

    internal static IReadOnlyList<TCatalog> SelectReachableCatalogs<TCatalog>(
        IEnumerable<TCatalog> catalogs,
        Func<TCatalog, string> describeCatalog,
        Func<TCatalog, bool> isReachable,
        Action<string>? onReachable = null,
        Action<string>? onSkipped = null
    )
    {
        List<TCatalog> reachableCatalogs = [];
        foreach (var catalog in catalogs)
        {
            string catalogName = describeCatalog(catalog);
            if (isReachable(catalog))
            {
                onReachable?.Invoke(catalogName);
                reachableCatalogs.Add(catalog);
            }
            else
            {
                onSkipped?.Invoke(catalogName);
                PackageListingScope.ReportIncomplete(new InvalidOperationException(
                    $"Skipping unavailable WinGet catalog {catalogName} while loading local packages"
                ));
            }
        }

        if (reachableCatalogs.Count == 0)
        {
            throw new InvalidOperationException("WinGet: Failed to connect to composite catalog.");
        }

        return reachableCatalogs;
    }

    private bool TryConnectLocalCatalog(PackageCatalogReference catalogRef, INativeTaskLogger logger)
    {
        var localCatalogRef = CreateLocalCompositeCatalog([catalogRef], logger);
        var connectResult = localCatalogRef.Connect();
        if (connectResult.Status == ConnectResultStatus.Ok)
        {
            return true;
        }

        logger.Error(
            $"WinGet: Catalog {catalogRef.Info.Name} failed local catalog connection with status {connectResult.Status}."
        );
        return false;
    }

    private static void DisableBackgroundUpdate(
        PackageCatalogReference catalogRef,
        INativeTaskLogger logger
    )
    {
        try
        {
            catalogRef.PackageCatalogBackgroundUpdateInterval = TimeSpan.Zero;
        }
        catch (Exception ex)
        {
            logger.Log(
                $"Catalog {catalogRef.Info.Name} keeps WinGet's background update: {ex.GetType().Name}: {ex.Message}"
            );
        }
    }

    private PackageCatalogReference CreateLocalCompositeCatalog(
        IEnumerable<PackageCatalogReference> catalogs,
        INativeTaskLogger logger
    )
    {
        CreateCompositePackageCatalogOptions createCompositePackageCatalogOptions =
            Factory.CreateCreateCompositePackageCatalogOptions();
        foreach (var catalogRef in catalogs)
        {
            catalogRef.AcceptSourceAgreements = true;
            DisableBackgroundUpdate(catalogRef, logger);
            logger.Log($"Adding catalog {catalogRef.Info.Name} to composite catalog");
            createCompositePackageCatalogOptions.Catalogs.Add(catalogRef);
        }

        createCompositePackageCatalogOptions.CompositeSearchBehavior =
            CompositeSearchBehavior.LocalCatalogs;
        return WinGetManager.CreateCompositePackageCatalog(createCompositePackageCatalogOptions);
    }

    public IReadOnlyList<IManagerSource> GetSources_UnSafe()
    {
        List<ManagerSource> sources = [];
        INativeTaskLogger logger = Manager.TaskLogger.CreateNew(LoggableTaskType.ListSources);

        foreach (
            PackageCatalogReference catalog in NativeWinGetCollection.Copy(
                WinGetManager.GetPackageCatalogs()
            )
        )
        {
            try
            {
                logger.Log(
                    $"Found source {catalog.Info.Name} with argument {catalog.Info.Argument}"
                );
                sources.Add(
                    new ManagerSource(
                        Manager,
                        catalog.Info.Name,
                        new Uri(catalog.Info.Argument),
                        updateDate: (
                            catalog.Info.LastUpdateTime.Second != 0
                                ? catalog.Info.LastUpdateTime
                                : DateTime.Now
                        ).ToString()
                    )
                );
            }
            catch (Exception e)
            {
                logger.Error(e);
            }
        }

        logger.Close(0);
        return sources;
    }

    public IReadOnlyList<string> GetInstallableVersions_Unsafe(IPackage package)
    {
        INativeTaskLogger logger = Manager.TaskLogger.CreateNew(
            LoggableTaskType.LoadPackageVersions
        );

        var nativePackage = NativePackageHandler.GetPackage(package);
        if (nativePackage is null)
            return [];

        var nativeVersions = NativeWinGetCollection.Copy(nativePackage.AvailableVersions);
        string[] versions = new string[nativeVersions.Length];
        for (int index = 0; index < nativeVersions.Length; index++)
        {
            versions[index] = nativeVersions[index].Version;
        }
        foreach (string? version in versions)
        {
            logger.Log(version);
        }

        logger.Close(0);
        return versions ?? [];
    }

    public void GetPackageDetails_UnSafe(IPackageDetails details)
    {
        INativeTaskLogger logger = Manager.TaskLogger.CreateNew(
            LoggableTaskType.LoadPackageDetails
        );

        if (details.Package.Source.Name == "winget")
        {
            details.ManifestUrl = new Uri(
                "https://github.com/microsoft/winget-pkgs/tree/master/manifests/"
                    + details.Package.Id[0].ToString().ToLowerInvariant()
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

        CatalogPackageMetadata? NativeDetails = NativePackageHandler.GetDetails(details.Package);
        if (NativeDetails is null)
        {
            logger.Close(1);
            return;
        }

        // Extract data from NativeDetails
        if (NativeDetails.Author != "")
            details.Author = NativeDetails.Author;

        if (NativeDetails.Description != "")
            details.Description = NativeDetails.Description;

        if (NativeDetails.PackageUrl != "")
            details.HomepageUrl = new Uri(NativeDetails.PackageUrl);

        if (NativeDetails.License != "")
            details.License = NativeDetails.License;

        if (NativeDetails.LicenseUrl != "")
            details.LicenseUrl = new Uri(NativeDetails.LicenseUrl);

        if (NativeDetails.Publisher != "")
            details.Publisher = NativeDetails.Publisher;

        if (NativeDetails.ReleaseNotes != "")
            details.ReleaseNotes = NativeDetails.ReleaseNotes;

        if (NativeDetails.ReleaseNotesUrl != "")
            details.ReleaseNotesUrl = new Uri(NativeDetails.ReleaseNotesUrl);

        if (NativeDetails.Tags is not null)
            details.Tags = NativeWinGetCollection.Copy(NativeDetails.Tags);

        bool metadataLoaded = _pingetPackageDetailsProvider.LoadPackageDetails(details, logger);

        logger.Close(metadataLoaded ? 0 : 1);
    }
}

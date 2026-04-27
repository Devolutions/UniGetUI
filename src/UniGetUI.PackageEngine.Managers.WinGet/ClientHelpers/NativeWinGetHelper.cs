using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Management.Deployment;
using UniGetUI.Core.Classes;
using UniGetUI.Core.Logging;
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

    public IReadOnlyList<Package> FindPackages_UnSafe(string query)
    {
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
        foreach (PackageCatalogReference CatalogReference in AvailableCatalogs.ToArray())
        {
            logger.Log($"Begin search on catalog {CatalogReference.Info.Name}");
            // Connect to catalog
            CatalogReference.AcceptSourceAgreements = true;
            ConnectResult result = CatalogReference.Connect();
            if (result.Status == ConnectResultStatus.Ok)
            {
                foreach (
                    var filter_type in new[]
                    {
                        PackageMatchField.Name,
                        PackageMatchField.Id,
                        PackageMatchField.Moniker,
                    }
                )
                {
                    FindPackagesOptions PackageFilters = Factory.CreateFindPackagesOptions();

                    logger.Log("Generating filters...");
                    // Name filter
                    PackageMatchFilter FilterName = Factory.CreatePackageMatchFilter();
                    FilterName.Field = filter_type;
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
                        FindPackageTasks.Add((CatalogReference, filter_type), task);
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

        foreach (var CatalogTaskPair in FindPackageTasks)
        {
            try
            {
                // Get the source for the catalog
                IManagerSource source = Manager.SourcesHelper.Factory.GetSourceOrDefault(
                    CatalogTaskPair.Key.Item1.Info.Name
                );

                FindPackagesResult FoundPackages = CatalogTaskPair.Value.Result;
                foreach (MatchResult matchResult in FoundPackages.Matches.ToArray())
                {
                    CatalogPackage nativePackage = matchResult.CatalogPackage;
                    // Create the Package item and add it to the list
                    logger.Log(
                        $"Found package: {nativePackage.Name}|{nativePackage.Id}|{nativePackage.DefaultInstallVersion.Version} on catalog {source.Name}"
                    );

                    var overriden_options = new OverridenInstallationOptions();

                    var UniGetUIPackage = new Package(
                        nativePackage.Name,
                        nativePackage.Id,
                        nativePackage.DefaultInstallVersion.Version,
                        source,
                        Manager,
                        overriden_options
                    );
                    NativePackageHandler.AddPackage(UniGetUIPackage, nativePackage);
                    packages.Add(UniGetUIPackage);
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

        logger.Close(0);
        return packages;
    }

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
        foreach (var nativePackage in nativePackages)
        {
            try
            {
                if (!nativePackage.IsUpdateAvailable)
                {
                    continue;
                }

                if (nativePackage.DefaultInstallVersion is null)
                {
                    Logger.Warn(
                        $"WinGet package {nativePackage.Id} has IsUpdateAvailable=true but DefaultInstallVersion is null, skipping"
                    );
                    continue;
                }

                IManagerSource source;
                source = Manager.SourcesHelper.Factory.GetSourceOrDefault(
                    nativePackage.DefaultInstallVersion.PackageCatalog.Info.Name
                );

                string version = nativePackage.InstalledVersion.Version;
                if (version == "Unknown")
                    version = WinGetPkgOperationHelper.GetLastInstalledVersion(nativePackage.Id);

                var UniGetUIPackage = new Package(
                    nativePackage.Name,
                    nativePackage.Id,
                    version,
                    nativePackage.DefaultInstallVersion.Version,
                    source,
                    Manager
                );

                if (!WinGetPkgOperationHelper.UpdateAlreadyInstalled(UniGetUIPackage))
                {
                    NativePackageHandler.AddPackage(UniGetUIPackage, nativePackage);
                    packages.Add(UniGetUIPackage);
                    logger.Log(
                        $"Found package {nativePackage.Name} {nativePackage.Id} on source {source.Name}, from version {version} to version {nativePackage.DefaultInstallVersion.Version}"
                    );
                }
                else
                {
                    Logger.Warn(
                        $"WinGet package {nativePackage.Id} not being shown as an updated as this version has already been marked as installed"
                    );
                }
            }
            catch (Exception ex)
            {
                LogSkippedPackage(logger, nativePackage, "listing available updates", ex);
            }
        }

        logger.Close(0);
        return packages;
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
                IManagerSource source;
                var availableVersions = nativePackage.AvailableVersions?.ToArray() ?? [];
                if (availableVersions.Length > 0)
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

                string version = nativePackage.InstalledVersion.Version;
                if (version == "Unknown")
                    version = WinGetPkgOperationHelper.GetLastInstalledVersion(nativePackage.Id);

                logger.Log(
                    $"Found package {nativePackage.Name} {nativePackage.Id} on source {source.Name}"
                );
                var UniGetUIPackage = new Package(
                    nativePackage.Name,
                    nativePackage.Id,
                    version,
                    source,
                    Manager
                );
                NativePackageHandler.AddPackage(UniGetUIPackage, nativePackage);
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
        if (_localPackagesProvider is not null)
        {
            return _localPackagesProvider();
        }

        return cacheSeconds is null
            ? TaskRecycler<IReadOnlyList<CatalogPackage>>.RunOrAttach(GetLocalWinGetPackages)
            : TaskRecycler<IReadOnlyList<CatalogPackage>>.RunOrAttach(
                GetLocalWinGetPackages,
                cacheSeconds.Value
            );
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
            var availableCatalogs = GetReachableLocalCatalogReferences(logger);
            PackageCatalogReference installedSearchCatalogRef = CreateLocalCompositeCatalog(
                availableCatalogs,
                logger
            );

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

            FindPackagesResult TaskResult;
            try
            {
                TaskResult = ConnectResult.PackageCatalog.FindPackages(findPackagesOptions);
            }
            catch (Exception ex)
            {
                logger.Error($"FindPackages native call failed: {ex.GetType().Name}: {ex.Message}");
                logger.Close(1);
                throw new InvalidOperationException("WinGet: Failed to connect to composite catalog.", ex);
            }

            List<CatalogPackage> foundPackages = [];
            foreach (var match in TaskResult.Matches.ToArray())
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

    private IReadOnlyList<PackageCatalogReference> GetReachableLocalCatalogReferences(
        INativeTaskLogger logger
    )
    {
        return SelectReachableCatalogs(
            WinGetManager.GetPackageCatalogs().ToArray(),
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

        foreach (PackageCatalogReference catalog in WinGetManager.GetPackageCatalogs().ToList())
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

        string[] versions = nativePackage.AvailableVersions.Select(x => x.Version).ToArray();
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
                    + details.Package.Id[0].ToString().ToLower()
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
            details.Tags = NativeDetails.Tags.ToArray();

        bool metadataLoaded = _pingetPackageDetailsProvider.LoadPackageDetails(details, logger);

        logger.Close(metadataLoaded ? 0 : 1);
    }
}

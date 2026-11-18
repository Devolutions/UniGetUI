using System.Collections.Concurrent;
using Microsoft.Management.Deployment;
using UniGetUI.Core.Classes;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using InstallOptions = UniGetUI.PackageEngine.Serializable.InstallOptions;

namespace UniGetUI.PackageEngine.Managers.WingetManager;

public static class NativePackageHandler
{
    private static readonly ConcurrentDictionary<long, CatalogPackage> __nativePackages = new();
    private static readonly ConcurrentDictionary<long, CatalogPackageMetadata> __nativeDetails =
        new();
    private static ConcurrentDictionary<long, PackageInstallerInfo> __nativeInstallers_Install =
        new();
    private static ConcurrentDictionary<long, PackageInstallerInfo> __nativeInstallers_Uninstall =
        new();
    private static readonly ConcurrentDictionary<long, string> __localIdentifiers = new();

    // The caches above hold WinGet COM objects, which also keep memory alive in WinGet's own
    // out-of-process server. They used to keep an entry for every package ever listed, so each
    // Discover search added its results for the rest of the session. Installed and upgradable
    // packages are pinned: the machine bounds them, and a locally installed package cannot be
    // found again on a catalog by name. Everything else (search results, imported packages) is
    // kept only while it is among the most recently used, and found again on its catalog by
    // GetPackage if it is needed after being dropped. Typed searches have no result limit, so
    // the cap leaves room for a broad search to be cached whole; past it, a result simply costs
    // one catalog lookup the next time it is used.
    internal const int MAX_UNPINNED_PACKAGES = 2_500;
    private static readonly PinnedLruKeyTracker<long> __retention = new(MAX_UNPINNED_PACKAGES);

    /// <summary>
    /// Get (cache or load) the native package for the given package, if any;
    /// </summary>
    /// <returns></returns>
    public static CatalogPackage? GetPackage(IPackage package)
    {
        if (
            NativeWinGetHelper.ExternalFactory is null
            || NativeWinGetHelper.ExternalWinGetManager is null
        )
            return null;

        __nativePackages.TryGetValue(package.GetHash(), out CatalogPackage? catalogPackage);
        if (catalogPackage is not null)
        {
            __retention.Refresh(package.GetHash());
            return catalogPackage;
        }

        // Rarely a package will not be available in cache (native WinGet helper should always call AddPackage)
        // so it makes no sense to add TaskRecycler.RunOrAttach() here. (Only imported packages may arrive at this point)
        catalogPackage = _findPackageOnCatalog(package);
        if (catalogPackage is not null)
            AddSearchResult(package, (CatalogPackage)catalogPackage);

        return catalogPackage;
    }

    /// <summary>
    /// Adds the CatalogPackage of an installed or upgradable package to the internal database.
    /// It is kept until <see cref="Clear"/>.
    /// </summary>
    public static void AddPackage(IPackage package, CatalogPackage catalogPackage)
    {
        long hash = package.GetHash();
        __retention.Pin(hash, () => __nativePackages[hash] = catalogPackage);
    }

    /// <summary>
    /// Adds the CatalogPackage of a search result to the internal database. Only the most
    /// recently used ones are kept, unless the package is also installed or upgradable.
    /// </summary>
    public static void AddSearchResult(IPackage package, CatalogPackage catalogPackage)
    {
        long hash = package.GetHash();
        Track(hash, () => __nativePackages[hash] = catalogPackage);
    }

    // Caches a value for a hash (store), records the use, and drops every cached object of the
    // packages that fall out of retention as a result. The tracker runs all of it as one step,
    // so an installed listing cannot pin and store a package between its being chosen for
    // eviction and its objects being removed.
    private static void Track(long hash, Action store) => __retention.Touch(hash, store, Evict);

    private static void Evict(long evicted)
    {
        __nativePackages.TryRemove(evicted, out _);
        __nativeDetails.TryRemove(evicted, out _);
        __nativeInstallers_Install.TryRemove(evicted, out _);
        __nativeInstallers_Uninstall.TryRemove(evicted, out _);
        __localIdentifiers.TryRemove(evicted, out _);
    }

    internal static void AddLocalIdentifier(IPackage package, string? localIdentifier)
    {
        if (string.IsNullOrWhiteSpace(localIdentifier))
        {
            __localIdentifiers.TryRemove(package.GetHash(), out _);
            return;
        }

        long hash = package.GetHash();
        Track(hash, () => __localIdentifiers[hash] = localIdentifier);
    }

    internal static string? GetLocalIdentifier(IPackage package)
    {
        if (__localIdentifiers.TryGetValue(package.GetHash(), out string? cached))
        {
            __retention.Refresh(package.GetHash());
            return cached;
        }

        string? localIdentifier;
        try
        {
            localIdentifier = GetPackage(package)?.InstalledVersion?.Id;
        }
        catch (Exception ex)
        {
            Logger.Warn(
                $"Could not read the local identifier of package {package.Id}: {ex.GetType().Name}: {ex.Message}"
            );
            return null;
        }

        if (string.IsNullOrWhiteSpace(localIdentifier))
            return null;

        AddLocalIdentifier(package, localIdentifier);
        return localIdentifier;
    }

    /// <summary>
    /// Get (cached or load) the native package details for the given package, if any;
    /// </summary>
    public static CatalogPackageMetadata? GetDetails(IPackage package)
    //    => TaskRecycler<CatalogPackageMetadata?>.RunOrAttach(_getDetails, package);
    //
    //private static CatalogPackageMetadata? _getDetails(IPackage package)
    {
        try
        {
            if (
                __nativeDetails.TryGetValue(package.GetHash(), out CatalogPackageMetadata? metadata)
            )
            {
                __retention.Refresh(package.GetHash());
                return metadata;
            }

            CatalogPackage? catalogPackage = GetPackage(package);
            if (catalogPackage is null)
                return null;

            var availableVersions =
                catalogPackage.AvailableVersions is { } versions
                    ? NativeWinGetCollection.Copy(versions)
                    : [];
            if (availableVersions.Length > 0)
            {
                metadata = catalogPackage
                    .GetPackageVersionInfo(availableVersions[0])
                    .GetCatalogPackageMetadata();
            }

            if (metadata is not null)
            {
                long hash = package.GetHash();
                CatalogPackageMetadata found = metadata;
                Track(hash, () => __nativeDetails[hash] = found);
            }

            return metadata;
        }
        catch
        {
            throw;
        }
    }

    /// <summary>
    /// Get (cached or load) the native installer for the given package, if any. The operation type determines wether
    /// </summary>
    public static PackageInstallerInfo? GetInstallationOptions(
        IPackage package,
        InstallOptions unigetuiOptions,
        OperationType operation
    )
    //    =>  TaskRecycler<PackageInstallerInfo?>.RunOrAttach(_getInstallationOptions, package, operation);
    //
    //private static PackageInstallerInfo? _getInstallationOptions(IPackage package, OperationType operation)
    {
        if (NativeWinGetHelper.ExternalFactory is null)
            return null;

        PackageInstallerInfo? installerInfo;
        if (operation is OperationType.Uninstall)
            installerInfo = _getInstallationOptionsOnDict(
                package,
                ref __nativeInstallers_Uninstall,
                true,
                unigetuiOptions
            );
        else
            installerInfo = _getInstallationOptionsOnDict(
                package,
                ref __nativeInstallers_Install,
                false,
                unigetuiOptions
            );

        return installerInfo;
    }

    internal static bool CanCheckInstallTargets =>
        WinGetHelper.Instance is NativeWinGetHelper
        && NativeWinGetHelper.ExternalFactory is not null
        && NativeWinGetHelper.ExternalWinGetManager is not null;

    internal static bool? CanInstallExactVersion(
        IPackage package,
        string version,
        string? scope,
        string? architecture
    )
    {
        if (
            NativeWinGetHelper.ExternalFactory is not { } factory
            || NativeWinGetHelper.ExternalWinGetManager is null
        )
            return null;

        try
        {
            CatalogPackage? fresh = _findPackageOnCatalog(package);
            if (fresh?.AvailableVersions is not { } versions)
                return false;

            PackageVersionInfo? target = null;
            foreach (var available in NativeWinGetCollection.Copy(versions))
            {
                if (version.Length == 0 || string.Equals(available.Version, version, StringComparison.Ordinal))
                {
                    target = fresh.GetPackageVersionInfo(available);
                    break;
                }
            }

            if (target is null || !IsExactTarget(package.Id, package.Source.Name, target.Id, target.PackageCatalog?.Info?.Name))
                return false;

            var installOptions = factory.CreateInstallOptions();
            installOptions.PackageInstallScope = ToInstallScope(scope);
            if (ToProcessorArchitecture(architecture) is { } allowedArchitecture)
                installOptions.AllowedArchitectures.Add(allowedArchitecture);

            return target.GetApplicableInstaller(installOptions) is not null;
        }
        catch (Exception ex)
        {
            Logger.Warn(
                $"Could not confirm that {package.Id} {version} can be installed from {package.Source.Name}: {ex.GetType().Name}: {ex.Message}"
            );
            return false;
        }
    }

    internal static bool IsExactTarget(
        string listedId,
        string sourceName,
        string? targetId,
        string? targetCatalogName
    ) =>
        NativeWinGetHelper.IsSamePackage(listedId, targetId)
        && string.Equals(targetCatalogName, sourceName, StringComparison.OrdinalIgnoreCase);

    internal static PackageInstallScope ToInstallScope(string? scope) =>
        scope switch
        {
            PackageScope.Machine => PackageInstallScope.System,
            PackageScope.User => PackageInstallScope.User,
            _ => PackageInstallScope.Any,
        };

    internal static global::Windows.System.ProcessorArchitecture? ToProcessorArchitecture(string? architecture) =>
        architecture switch
        {
            UniGetUI.PackageEngine.Enums.Architecture.x86 => global::Windows.System.ProcessorArchitecture.X86,
            UniGetUI.PackageEngine.Enums.Architecture.x64 => global::Windows.System.ProcessorArchitecture.X64,
            UniGetUI.PackageEngine.Enums.Architecture.arm64 => global::Windows.System.ProcessorArchitecture.Arm64,
            _ => null,
        };

    public static void Clear()
    {
        __retention.Clear(() =>
        {
            __nativePackages.Clear();
            __localIdentifiers.Clear();
            __nativeDetails.Clear();
            __nativeInstallers_Install.Clear();
            __nativeInstallers_Uninstall.Clear();
        });
    }

    private static PackageInstallerInfo? _getInstallationOptionsOnDict(
        IPackage package,
        ref ConcurrentDictionary<long, PackageInstallerInfo> source,
        bool installed,
        InstallOptions unigetuiOptions
    )
    {
        if (source.TryGetValue(package.GetHash(), out PackageInstallerInfo? installerInfo))
        {
            __retention.Refresh(package.GetHash());
            return installerInfo;
        }

        PackageVersionInfo? catalogPackage;
        if (installed)
            catalogPackage = GetPackage(package)?.InstalledVersion;
        else
            catalogPackage = GetPackage(package)?.DefaultInstallVersion;

        Microsoft.Management.Deployment.InstallOptions? wingetOptions =
            NativeWinGetHelper.ExternalFactory?.CreateInstallOptions();
        Microsoft.Management.Deployment.InstallOptions? wingetDefaultOptions =
            NativeWinGetHelper.ExternalFactory?.CreateInstallOptions();

        if (wingetOptions is null)
        {
            Logger.Error("WinGetFactory.CreateInstallOptions returned null!");
            return installerInfo;
        }

        if (ToInstallScope(unigetuiOptions.InstallationScope) is not PackageInstallScope.Any and var scope)
            wingetOptions.PackageInstallScope = scope;

        installerInfo = catalogPackage?.GetApplicableInstaller(wingetOptions);
        installerInfo ??= catalogPackage?.GetApplicableInstaller(wingetDefaultOptions);

        if (installerInfo is not null)
        {
            long hash = package.GetHash();
            PackageInstallerInfo found = installerInfo;
            ConcurrentDictionary<long, PackageInstallerInfo> cache = source;
            Track(hash, () => cache[hash] = found);
        }

        return installerInfo;
    }

    private static CatalogPackage? _findPackageOnCatalog(IPackage package)
    {
        if (
            NativeWinGetHelper.ExternalWinGetManager is null
            || NativeWinGetHelper.ExternalFactory is null
        )
            return null;

        PackageCatalogReference Catalog =
            NativeWinGetHelper.ExternalWinGetManager.GetPackageCatalogByName(package.Source.Name);
        if (Catalog is null)
        {
            Logger.Error(
                "Failed to get catalog " + package.Source.Name + ". Is the package local?"
            );
            return null;
        }

        // Connect to catalog
        Catalog.AcceptSourceAgreements = true;
        ConnectResult ConnectResult = Catalog.Connect();
        if (ConnectResult.Status != ConnectResultStatus.Ok)
        {
            Logger.Error("Failed to connect to catalog " + package.Source.Name);
            return null;
        }

        // Match only the exact same Id
        FindPackagesOptions packageMatchFilter =
            NativeWinGetHelper.ExternalFactory.CreateFindPackagesOptions();
        PackageMatchFilter filters = NativeWinGetHelper.ExternalFactory.CreatePackageMatchFilter();
        filters.Field = PackageMatchField.Id;
        filters.Value = package.Id;
        filters.Option = PackageFieldMatchOption.Equals;
        packageMatchFilter.Filters.Add(filters);
        packageMatchFilter.ResultLimit = 1;
        var SearchResult = Task.Run(() =>
            ConnectResult.PackageCatalog.FindPackages(packageMatchFilter)
        );

        if (SearchResult?.Result?.Matches is null || SearchResult.Result.Matches.Count == 0)
        {
            Logger.Error(
                "Failed to find package " + package.Id + " in catalog " + package.Source.Name
            );
            return null;
        }

        // Return the Native Package
        return SearchResult.Result.Matches[0].CatalogPackage;
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.PackageLoader
{
    public class UpgradablePackagesLoader : AbstractPackageLoader
    {
        public static UpgradablePackagesLoader Instance = null!;

        /// <summary>
        /// The collection of packages with updates ignored
        /// </summary>
        public ConcurrentDictionary<string, IPackage> IgnoredPackages = new();

        public UpgradablePackagesLoader(IReadOnlyList<IPackageManager> managers)
            : base(
                managers,
                identifier: "UPGRADABLE_PACKAGES",
                AllowMultiplePackageVersions: false,
                DisableReload: false,
                CheckedBydefault: !Settings.Get(Settings.K.DisableSelectingUpdatesByDefault),
                RequiresInternet: true
            )
        {
            Instance = this;
        }

        protected override async Task<bool> IsPackageValid(IPackage package)
        {
            if (package.VersionString == package.NewVersionString)
                return false;

            if (
                package.NormalizedNewVersion != default
                && package.Manager.CompareVersions(package.VersionString, package.NewVersionString)
                    is >= 0
            )
            {
                Logger.Info(
                    $"Ignoring package {package.Id} because the offered version {package.NewVersionString} is not newer than the installed version {package.VersionString}."
                );
                return false;
            }

            if (package.NewerVersionIsInstalled())
            {
                Logger.Info(
                    $"Ignoring package {package.Id} because a newer or equal version than {package.NewVersionString} is already installed."
                );
                return false;
            }

            var installOptions = await package.GetInstallOptions();
            if (installOptions.SkipMinorUpdates && package.IsUpdateMinor(installOptions.SkipMinorUpdatesLevel))
            {
                Logger.Info(
                    $"Ignoring package {package.Id} because it is a minor update ({package.VersionString} -> {package.NewVersionString}) below skip level {installOptions.SkipMinorUpdatesLevel} and SkipMinorUpdates is set to true."
                );
                return false;
            }

            if (installOptions.SkipMajorUpdates && package.IsUpdateMajor())
            {
                Logger.Info(
                    $"Ignoring package {package.Id} because it is a major update ({package.VersionString} -> {package.NewVersionString}) and SkipMajorUpdates is set to true."
                );
                return false;
            }

            if (await package.HasUpdatesIgnoredAsync(package.NewVersionString))
            {
                IgnoredPackages[package.Id] = package;
                return false;
            }

            string? perManagerVal = Settings.GetDictionaryItem<string, string>(
                Settings.K.PerManagerMinimumUpdateAge, package.Manager.Name);

            int minimumAge;
            if (perManagerVal is { Length: > 0 })
            {
                if (perManagerVal == "custom")
                    int.TryParse(
                        Settings.GetDictionaryItem<string, string>(
                            Settings.K.PerManagerMinimumUpdateAgeCustom, package.Manager.Name),
                        out minimumAge);
                else
                    int.TryParse(perManagerVal, out minimumAge);
            }
            else
            {
                string globalVal = Settings.GetValue(Settings.K.MinimumUpdateAge);
                if (globalVal == "custom")
                    int.TryParse(
                        Settings.GetValue(Settings.K.MinimumUpdateAgeCustom),
                        out minimumAge);
                else
                    int.TryParse(globalVal, out minimumAge);
            }

            if (minimumAge > 0)
            {
                await package.Details.Load();
                string? dateStr = package.Details.UpdateDate;
                if (dateStr is { Length: > 0 } &&
                    DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime releaseDate) &&
                    (DateTime.UtcNow - releaseDate.ToUniversalTime()).TotalDays < minimumAge)
                {
                    Logger.Info(
                        $"Suppressing update for {package.Id}: released {releaseDate:yyyy-MM-dd}, minimum age is {minimumAge} days"
                    );
                    return false;
                }
            }

            return true;
        }

        protected override bool DidManagerReportFailure(IPackageManager manager)
            => manager.LastUpdatesListingFailed;

        protected virtual TimeSpan InstalledListingWaitLimit => TimeSpan.FromMinutes(2);

        protected override async Task WaitBeforeLoadingFromManagerAsync(IPackageManager manager)
        {
            Task installedListing =
                InstalledPackagesLoader.Instance?.WaitForManagerAsync(manager) ?? Task.CompletedTask;
            if (installedListing.IsCompleted)
                return;

            Logger.Info($"Waiting for the installed packages of {manager.Name} before checking its updates");
            try
            {
                await installedListing.WaitAsync(InstalledListingWaitLimit);
            }
            catch (TimeoutException)
            {
                Logger.Warn(
                    $"The installed packages of {manager.Name} were still loading after {InstalledListingWaitLimit.TotalSeconds:0} seconds, checking its updates anyway"
                );
            }
        }

        protected override IReadOnlyList<IPackage> LoadPackagesFromManager(IPackageManager manager)
        {
            return manager.GetAvailableUpdates();
        }

        private readonly ConcurrentDictionary<long, SemaphoreSlim> _resolutionGates = new();

        public async Task<IPackage?> ResolveSkippedMajorUpdateAsync(IPackage package, InstallOptions? options = null)
        {
            if (!package.IsUpgradable)
                return package;

            var gate = _resolutionGates.GetOrAdd(HashPackage(package), _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                return await ResolveSkippedMajorUpdateExclusivelyAsync(package, options);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<IPackage?> ResolveSkippedMajorUpdateExclusivelyAsync(IPackage package, InstallOptions? options)
        {
            if (PackageReference.TryGetValue(HashPackage(package), out var current))
                package = current;

            if (IsPending(package))
                return package;

            bool pinned = package.OverridenOptions.PinUpdateToNewVersion;
            options ??= await package.GetInstallOptions();
            if (pinned && !options.SkipMajorUpdates)
            {
                Remove(package);
                return package;
            }

            if (!options.SkipMajorUpdates || !(pinned || package.IsUpdateMajor()))
                return package;

            var resolved = await Task.Run(() => package.Manager.ResolveSkippedMajorUpdate(package, options));
            if (IsPending(package) || (pinned && ReferenceEquals(resolved, package)))
                return package;

            if (resolved is null || ReferenceEquals(resolved, package) || !await IsPackageValid(resolved))
            {
                if (!IsPending(package))
                    Remove(package);
                return null;
            }

            if (IsPending(package))
                return package;

            bool wasChecked = package.IsChecked;
            Remove(package);
            await AddForeign(resolved);
            resolved.IsChecked = wasChecked;
            return resolved;
        }

        private static bool IsPending(IPackage package) =>
            package.Tag is PackageTag.OnQueue or PackageTag.BeingProcessed;

        protected override Task WhenAddingPackage(IPackage package)
        {
            package.GetAvailablePackage()?.SetTag(PackageTag.IsUpgradable);

            foreach (var p in package.GetInstalledPackages())
                p.SetTag(PackageTag.IsUpgradable);

            return Task.CompletedTask;
        }
    }
}

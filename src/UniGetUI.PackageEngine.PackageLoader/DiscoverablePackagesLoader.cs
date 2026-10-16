using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.PackageLoader
{
    public class DiscoverablePackagesLoader : AbstractPackageLoader
    {
        public static DiscoverablePackagesLoader Instance = null!;

        // Cap results per manager; a broad query across every enabled manager otherwise wraps,
        // tags and renders thousands of packages, freezing the UI and spiking RAM.
        private const int MAX_RESULTS_PER_MANAGER = 100;

        private string? QUERY_TEXT;
        private volatile bool _pendingReload;

        public DiscoverablePackagesLoader(IReadOnlyList<IPackageManager> managers)
            : base(
                managers,
                identifier: "DISCOVERABLE_PACKAGES",
                AllowMultiplePackageVersions: false,
                DisableReload: false,
                CheckedBydefault: false,
                RequiresInternet: true
            )
        {
            Instance = this;
            FinishedLoading += OnFinishedLoading;
        }

        private void OnFinishedLoading(object? sender, EventArgs e)
        {
            if (_pendingReload)
            {
                _pendingReload = false;
                _ = ReloadPackages();
            }
        }

        public async Task ReloadPackages(string query)
        {
            QUERY_TEXT = query;
            await ReloadPackages();
        }

        public void ResetSearch()
        {
            QUERY_TEXT = null;
            ClearPackages(emitFinishSignal: false);
        }

        public override async Task ReloadPackages()
        {
            if (QUERY_TEXT is null)
            {
                return;
            }

            if (IsLoading)
            {
                _pendingReload = true;
                return;
            }

            await base.ReloadPackages();
        }

        protected override async Task<bool> IsPackageValid(IPackage package)
        {
            return await Task.FromResult(true);
        }

        public IReadOnlyList<IPackageManager> GetSearchableManagers()
            => Managers.Where(manager => manager.IsReady()).ToArray();

        public bool IsManagerSearched(IPackageManager manager)
            => !Settings.GetDictionaryItem<string, bool>(Settings.K.ExcludedSearchManagers, manager.Name);

        public void SetManagerSearched(IPackageManager manager, bool searched)
            => Settings.SetDictionaryItem(Settings.K.ExcludedSearchManagers, manager.Name, !searched);

        protected override bool WillQueryAnyManager()
        {
            string? query = QUERY_TEXT;
            return Managers.Any(manager =>
                manager.IsReady() && IsManagerSearched(manager) && CanSearch(manager, query));
        }

        private static bool CanSearch(IPackageManager manager, string? query)
            => query != string.Empty || manager.Capabilities.SupportsEmptySearchQuery;

        protected override IReadOnlyList<IPackage> LoadPackagesFromManager(IPackageManager manager)
        {
            string? query = QUERY_TEXT;
            if (query is null || !IsManagerSearched(manager) || !CanSearch(manager, query))
            {
                return [];
            }

            string text = CoreTools.EnsureSafeQueryString(query);
            if (text.Length == 0 && query.Length > 0)
            {
                return [];
            }

            IReadOnlyList<IPackage> found = manager.FindPackages(text);
            if (text.Length == 0)
            {
                return found
                    .GroupBy(package => package.Source.Name)
                    .SelectMany(group => group.Take(SearchLimits.MaxEmptyQueryResultsPerSource))
                    .ToArray();
            }

            return found.Count > MAX_RESULTS_PER_MANAGER
                ? found.Take(MAX_RESULTS_PER_MANAGER).ToArray()
                : found;
        }

        protected override Task WhenAddingPackage(IPackage package)
        {
            if (package.GetUpgradablePackage() is not null)
            {
                package.SetTag(PackageTag.IsUpgradable);
            }
            else if (package.GetInstalledPackages().Any())
            {
                package.SetTag(PackageTag.AlreadyInstalled);
            }
            return Task.CompletedTask;
        }

        public (IPackage?, string?) GetPackageFromIdAndManager(
            string id,
            string managerName,
            string sourceName
        )
        {
            IPackageManager? manager = null;

            foreach (var candidate in Managers)
            {
                if (candidate.Name == managerName || candidate.DisplayName == managerName)
                {
                    manager = candidate;
                    break;
                }
            }

            if (manager is null)
                return (
                    null,
                    CoreTools.Translate("The package manager \"{0}\" was not found", managerName)
                );

            if (!manager.IsEnabled())
                return (
                    null,
                    CoreTools.Translate(
                        "The package manager \"{0}\" is disabled",
                        manager.DisplayName
                    )
                );

            if (!manager.Status.Found)
                return (
                    null,
                    CoreTools.Translate(
                        "There is an error with the configuration of the package manager \"{0}\"",
                        manager.DisplayName
                    )
                );

            var results = manager.FindPackages(id);
            var candidates = results.Where(p => p.Id == id).ToArray();

            if (candidates.Length == 0)
                return (
                    null,
                    CoreTools.Translate(
                        "The package \"{0}\" was not found on the package manager \"{1}\"",
                        id,
                        manager.DisplayName
                    )
                );

            IPackage package = candidates[0];

            // Get package from best source
            if (candidates.Length >= 1 && manager.Capabilities.SupportsCustomSources)
                foreach (var candidate in candidates)
                {
                    if (candidate.Source.Name == sourceName)
                        package = candidate;
                }

            return (package, null);
        }
    }
}

using UniGetUI.Core.Logging;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.PackageLoader
{
    public class InstalledPackagesLoader : AbstractPackageLoader
    {
        public static InstalledPackagesLoader Instance = null!;

        public InstalledPackagesLoader(IReadOnlyList<IPackageManager> managers)
            : base(
                managers,
                identifier: "INSTALLED_PACKAGES",
                AllowMultiplePackageVersions: true,
                DisableReload: false,
                CheckedBydefault: false,
                RequiresInternet: true
            )
        {
            Instance = this;
        }

        protected override Task<bool> IsPackageValid(IPackage package)
        {
            return Task.FromResult(true);
        }

        protected override bool DidManagerReportFailure(IPackageManager manager)
            => manager.LastInstalledListingFailed;

        protected override IReadOnlyList<IPackage> LoadPackagesFromManager(IPackageManager manager)
        {
            return manager.GetInstalledPackages();
        }

        protected override async Task WhenAddingPackage(IPackage package)
        {
            if (await package.HasUpdatesIgnoredAsync(version: "*"))
            {
                package.Tag = PackageTag.Pinned;
            }
            else if (package.GetUpgradablePackage() is not null)
            {
                package.Tag = PackageTag.IsUpgradable;
            }

            package.GetAvailablePackage()?.SetTag(PackageTag.AlreadyInstalled);
        }

        public async Task ReloadPackagesSilently()
        {
            if (IsLoading)
            {
                Logger.Debug($"[{this.GetType()}] Packages are already being loaded!!!");
                return;
            }

            try
            {
                IsLoading = true;
                InvokeStartedLoadingEvent();

                List<Task<IReadOnlyList<IPackage>>> tasks = [];

                foreach (IPackageManager manager in Managers)
                {
                    if (manager.IsEnabled() && manager.Status.Found)
                    {
                        Task<IReadOnlyList<IPackage>> task = Task.Run(() =>
                            LoadPackagesFromManager(manager)
                        );
                        tasks.Add(task);
                    }
                }

                while (tasks.Count > 0)
                {
                    foreach (Task<IReadOnlyList<IPackage>> task in tasks.ToArray())
                    {
                        if (!task.IsCompleted)
                        {
                            await Task.Delay(100);
                        }

                        if (task.IsCompleted)
                        {
                            if (task.IsCompletedSuccessfully)
                            {
                                var toAdd = new List<IPackage>();
                                foreach (IPackage package in task.Result)
                                {
                                    if (Contains(package) || !await IsPackageValid(package))
                                    {
                                        continue;
                                    }

                                    Logger.ImportantInfo(
                                        $"Adding missing package {package.Id} to installed packages list"
                                    );
                                    toAdd.Add(package);
                                    await AddPackage(package);
                                }

                                InvokePackagesChangedEvent(true, toAdd, []);
                            }

                            tasks.Remove(task);
                        }
                    }
                }

                InvokeFinishedLoadingEvent();
                IsLoading = false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                IsLoading = false;
            }
        }
    }
}

using UniGetUI.Core.IconEngine;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;

namespace UniGetUI.PackageEngine.Classes.Manager.BaseProviders
{
    public abstract class BasePkgDetailsHelper : IPackageDetailsHelper
    {
        protected IPackageManager Manager;

        public BasePkgDetailsHelper(IPackageManager manager)
        {
            Manager = manager;
        }

        public void GetDetails(IPackageDetails details)
        {
            if (!Manager.IsReady())
            {
                Logger.Warn(
                    $"Manager {Manager.Name} is disabled but yet GetPackageDetails was called"
                );
                return;
            }
            try
            {
                GetDetails_UnSafe(details);
                Logger.Info(
                    $"Loaded details for package {details.Package.Id} on manager {Manager.Name}"
                );
            }
            catch (Exception e)
            {
                Logger.Error("Error finding installed packages on manager " + Manager.Name);
                Logger.Error(e);
            }
        }

        public IReadOnlyList<string> GetVersions(IPackage package)
        {
            if (!Manager.IsReady())
            {
                Logger.Warn(
                    $"Manager {Manager.Name} is disabled but yet GetPackageVersions was called"
                );
                return [];
            }
            try
            {
                if (Manager.Capabilities.SupportsCustomVersions)
                {
                    var result = GetInstallableVersions_UnSafe(package);
                    Logger.Debug(
                        $"Found {result.Count} versions for package Id={package.Id} on manager {Manager.Name}"
                    );
                    return result;
                }

                Logger.Warn(
                    $"Manager {Manager.Name} does not support version retrieving, this method should have not been called"
                );
                return [];
            }
            catch (Exception e)
            {
                Logger.Error(
                    $"Error finding available package versions for package {package.Id} on manager "
                        + Manager.Name
                );
                Logger.Error(e);
                return [];
            }
        }

        public CacheableIcon? GetIcon(IPackage package)
        {
            try
            {
                // Load native icon
                if (Manager.Capabilities.SupportsCustomPackageIcons)
                {
                    var nativeIcon = GetIcon_UnSafe(package);
                    if (nativeIcon is not null)
                    {
                        return nativeIcon;
                    }
                }

                foreach (string lookupId in GetIconDatabaseLookupIds(package))
                {
                    string? iconUrl = IconDatabase.Instance.GetIconUrlForId(lookupId);
                    if (iconUrl is not null)
                        return new CacheableIcon(new Uri(iconUrl));
                }

                return null;
            }
            catch (Exception e)
            {
                Logger.Error(
                    $"Error when loading the package icon for the package {package.Id} on manager "
                        + Manager.Name
                );
                Logger.Error(e);
                return null;
            }
        }

        public IReadOnlyList<Uri> GetScreenshots(IPackage package)
        {
            try
            {
                IReadOnlyList<Uri> URIs = [];

                // Load native screenshots
                if (Manager.Capabilities.SupportsCustomPackageScreenshots)
                {
                    URIs = GetScreenshots_UnSafe(package);
                }
                else
                {
                    Logger.Debug($"Manager {Manager.Name} does not support native screenshots");
                }

                // Try to get exact screenshots for this package
                if (!URIs.Any())
                {
                    foreach (string lookupId in GetIconDatabaseLookupIds(package))
                    {
                        string[] UrlArray = IconDatabase.Instance.GetScreenshotsUrlForId(
                            lookupId
                        );
                        List<Uri> UriList = [];
                        foreach (string url in UrlArray)
                        {
                            if (url != "")
                                UriList.Add(new Uri(url));
                        }

                        if (UriList.Count > 0)
                        {
                            URIs = UriList;
                            break;
                        }
                    }
                }

                Logger.Info($"Found {URIs.Count} screenshots for package Id={package.Id}");
                return URIs;
            }
            catch (Exception e)
            {
                Logger.Error(
                    $"Error when loading the package icon for the package {package.Id} on manager "
                        + Manager.Name
                );
                Logger.Error(e);
                return [];
            }
        }

        private IEnumerable<string> GetIconDatabaseLookupIds(IPackage package)
        {
            yield return Manager.Name + "." + package.Id;

            if (Manager.Name == "Winget")
            {
                yield return package.Id;
            }

            yield return package.GetIconId();
        }

        protected abstract void GetDetails_UnSafe(IPackageDetails details);
        protected abstract IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package);
        protected abstract CacheableIcon? GetIcon_UnSafe(IPackage package);
        protected abstract IReadOnlyList<Uri> GetScreenshots_UnSafe(IPackage package);
        protected abstract string? GetInstallLocation_UnSafe(IPackage package);

        public string? GetInstallLocation(IPackage package)
        {
            try
            {
                string? path = GetInstallLocation_UnSafe(package);
                if (path is not null && !Directory.Exists(path))
                {
                    Logger.Warn(
                        $"Path returned by the package manager \"{path}\" did not exist while loading package install location for package Id={package.Id} with Manager={package.Manager.Name}"
                    );
                    return null;
                }

                return path;
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"An error occurred while loading package install location for package Id={package.Id} with Manager={package.Manager.Name}"
                );
                Logger.Error(ex);
                return null;
            }
        }
    }
}

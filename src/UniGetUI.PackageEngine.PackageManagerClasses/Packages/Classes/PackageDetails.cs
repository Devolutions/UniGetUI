using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.PackageClasses
{
    /// <summary>
    /// Holds the details of a Package.
    /// </summary>
    public class PackageDetails : IPackageDetails
    {
        /// <summary>
        /// The package to which this details instance corresponds
        /// </summary>
        public IPackage Package { get; }

        /// <summary>
        /// Whether this PackageDetails instance has valid data or not.
        /// To load valid data, make use of the `Load()` method
        /// </summary>
        public bool IsPopulated { get; private set; }

        /// <summary>
        /// The description of the package
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// The publisher of the package. The one(s) in charge of maintaining the package published on the package manager.
        /// </summary>
        public string? Publisher { get; set; }

        /// <summary>
        /// The author of the package. Who has created the package. Usually the developer of the package.
        /// </summary>
        public string? Author { get; set; }

        /// <summary>
        /// A link to the homepage of the package
        /// </summary>
        public Uri? HomepageUrl { get; set; }

        /// <summary>
        /// The license name (not the URL) of the package
        /// </summary>
        public string? License { get; set; }

        /// <summary>
        /// A URL pointing to the license of the package.
        /// </summary>
        public Uri? LicenseUrl { get; set; }

        /// <summary>
        /// A URL pointing to the installer of the package
        /// </summary>
        public Uri? InstallerUrl { get; set; }

        /// <summary>
        /// A string representing the hash of the installer.
        /// </summary>
        public string? InstallerHash { get; set; }

        /// <summary>
        /// A string representing the type of the installer (.zip, .exe, .msi, .appx, tarball, etc.)
        /// </summary>
        public string? InstallerType { get; set; }

        /// <summary>
        /// The size, in **BYTES**, of the installer
        /// </summary>
        public long InstallerSize { get; set; }

        /// <summary>
        /// A URL pointing to the Manifest File of the package
        /// </summary>
        public Uri? ManifestUrl { get; set; }

        /// <summary>
        /// The update date (aka the publication date for the latest version) of the package
        /// </summary>
        public string? UpdateDate { get; set; }

        /// <summary>
        /// The release notes (not the URL) for the package.
        /// </summary>
        public string? ReleaseNotes { get; set; }

        /// <summary>
        /// A URL to the package release notes.
        /// </summary>
        public Uri? ReleaseNotesUrl { get; set; }

        /// <summary>
        /// A list of tags that (in theory) represent the package
        /// </summary>
        public string[] Tags { get; set; } = [];

        public List<IPackageDetails.Dependency> Dependencies { get; set; } = [];

        private readonly object _loadLock = new();
        private Task? _loadTask;

        public PackageDetails(IPackage package)
        {
            Package = package;
        }

        public Task Load()
        {
            lock (_loadLock)
            {
                if (_loadTask is { IsCompleted: false })
                    return _loadTask;

                return _loadTask = LoadCore();
            }
        }

        private async Task LoadCore()
        {
            try
            {
                await Task.Run(() => Package.Manager.DetailsHelper.GetDetails(this));
                IsPopulated = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"PackageDetails.Load failed for package {Package.Name}");
                Logger.Error(ex);
            }
        }
    }
}

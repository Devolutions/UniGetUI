using System.Text;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Interfaces
{
    public interface IPackageManager
    {
        public ManagerProperties Properties { get; }
        public ManagerCapabilities Capabilities { get; }
        public ManagerStatus Status { get; }
        public string Id { get; }
        public string Name { get; }
        public string DisplayName { get; }
        public IManagerSource DefaultSource { get; }
        public IManagerLogger TaskLogger { get; }
        public IMultiSourceHelper SourcesHelper { get; }
        public IPackageDetailsHelper DetailsHelper { get; }
        public IPackageOperationHelper OperationHelper { get; }
        public IReadOnlyList<ManagerDependency> Dependencies { get; }

        /// <summary>
        /// The text encoding this manager's CLI emits its output in. Defaults to UTF-8; managers
        /// whose CLI emits in the system console code page (e.g. Chocolatey) must override this.
        /// </summary>
        public Encoding OutputEncoding { get; }

        public bool InstallerUrlFollowsPackageVersion { get; }

        public bool CommandLineIsShellInterpreted { get; }

        /// <summary>
        /// Whether this manager quotes the package identifier when it places it on the command
        /// line. Only such a manager may carry an identifier containing whitespace.
        /// </summary>
        public bool IdentifiersAreQuotedOnCommandLine { get; }

        /// <summary>
        /// Compares two version strings using this manager's ecosystem semantics, returning a
        /// negative number, zero or a positive number in the usual way, or null when the two
        /// versions cannot be meaningfully compared.
        /// A dash means opposite things across ecosystems - a Debian or Scoop revision is newer
        /// than its base version, while a SemVer pre-release is older than its release - so the
        /// shared numeric comparison cannot be right for every manager at once.
        /// </summary>
        public int? CompareVersions(string versionA, string versionB);

        public bool CanUpdateToExactVersion { get; }

        public IPackage? ResolveSkippedMajorUpdate(IPackage update, InstallOptions? options = null);

        /// <summary>
        /// Initializes the Package Manager (asynchronously). Must be run before using any other method of the manager.
        /// </summary>
        public void Initialize();

        /// <summary>
        /// Returns true if the manager is enabled, false otherwise
        /// </summary>
        public bool IsEnabled();

        /// <summary>
        /// Returns true if the manager is enabled and available (the required executable files were found). Returns false otherwise
        /// </summary>
        public bool IsReady();

        public bool IsInitializing { get; }

        /// <summary>
        /// Returns an array of Package objects that the manager lists for the given query. Depending on the manager, the list may
        /// also include similar results. This method is fail-safe and will return an empty array if an error occurs.
        /// </summary>
        public IReadOnlyList<IPackage> FindPackages(string query);

        public IReadOnlyList<IPackage> FindPackages(string query, out bool failed);

        public ManagerHealthRecord GetHealthRecord();

        public bool ExpectsInstalledPackages { get; }

        public IReadOnlyList<ManagerMaintenanceAction> GetMaintenanceActions();

        /// <summary>
        /// Returns an array of UpgradablePackage objects that represent the available updates reported by the manager.
        /// This method is fail-safe and will return an empty array if an error occurs.
        /// </summary>
        public IReadOnlyList<IPackage> GetAvailableUpdates();

        /// <summary>
        /// Lists the available updates and reports, for this call only, whether the listing failed
        /// or did not run. Unlike <see cref="LastUpdatesListingFailed"/>, another listing running
        /// at the same time cannot change the result.
        /// </summary>
        public IReadOnlyList<IPackage> GetAvailableUpdates(out bool failed)
        {
            var packages = GetAvailableUpdates();
            failed = LastUpdatesListingFailed;
            return packages;
        }

        public bool LastUpdatesListingFailed { get; }

        public bool LastInstalledListingFailed { get; }

        /// <summary>
        /// Returns an array of Package objects that represent the installed reported by the manager.
        /// This method is fail-safe and will return an empty array if an error occurs.
        /// </summary>
        public IReadOnlyList<IPackage> GetInstalledPackages();

        /// <summary>
        /// Lists the installed packages and reports, for this call only, whether the listing failed
        /// or did not run; see <see cref="GetAvailableUpdates(out bool)"/>.
        /// </summary>
        public IReadOnlyList<IPackage> GetInstalledPackages(out bool failed)
        {
            var packages = GetInstalledPackages();
            failed = LastInstalledListingFailed;
            return packages;
        }

        public long InstalledPackagesGeneration { get; }

        public void InvalidateInstalledPackages();

        /// <summary>
        /// Refreshes the Package Manager sources/indexes
        /// Each manager MUST implement this method.
        /// </summary>
        public void RefreshPackageIndexes();

        /// <summary>
        /// This method should attempt to resolve issues
        /// such as COM API disconnect or similar issues that
        /// can easily be resolved by reconnecting to a client and/or
        /// clearing some internal caches. See the WinGet implementation for
        /// an example
        /// </summary>
        public void AttemptFastRepair();

        /// <summary>
        /// Re-detects and reloads the manager when its cached executable path no longer exists on
        /// disk (for example after an in-place upgrade switched install locations, such as
        /// PowerShell 7 MSI to MSIX). Returns true when the manager is ready with an existing
        /// executable afterwards.
        /// </summary>
        public bool TryReloadMissingExecutable() => false;

        /// <summary>
        /// Find all available executable files that apply to this package manager
        /// </summary>
        /// <returns></returns>
        public IReadOnlyList<string> FindCandidateExecutableFiles();
        public Tuple<bool, string> GetExecutableFile();
        public bool HasExecutable() => GetExecutableFile().Item1;
    }
}

using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;

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

        /// <summary>
        /// Returns an array of Package objects that the manager lists for the given query. Depending on the manager, the list may
        /// also include similar results. This method is fail-safe and will return an empty array if an error occurs.
        /// </summary>
        public IReadOnlyList<IPackage> FindPackages(string query);

        /// <summary>
        /// Returns an array of UpgradablePackage objects that represent the available updates reported by the manager.
        /// This method is fail-safe and will return an empty array if an error occurs.
        /// </summary>
        public IReadOnlyList<IPackage> GetAvailableUpdates();

        /// <summary>
        /// Returns an array of Package objects that represent the installed reported by the manager.
        /// This method is fail-safe and will return an empty array if an error occurs.
        /// </summary>
        public IReadOnlyList<IPackage> GetInstalledPackages();

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
        /// Find all available executable files that apply to this package manager
        /// </summary>
        /// <returns></returns>
        public IReadOnlyList<string> FindCandidateExecutableFiles();
        public Tuple<bool, string> GetExecutableFile();
    }
}

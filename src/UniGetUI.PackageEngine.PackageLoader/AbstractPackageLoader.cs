using System.Collections.Concurrent;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.PackageLoader
{
    public class PackagesChangedEvent
    {
        public PackagesChangedEvent(
            bool proceduralChange,
            IReadOnlyList<IPackage> addedPackages,
            IReadOnlyList<IPackage> removedPackages
        )
        {
            ProceduralChange = proceduralChange;
            AddedPackages = addedPackages;
            RemovedPackages = removedPackages;
        }

        public readonly bool ProceduralChange;
        public readonly IReadOnlyList<IPackage> AddedPackages;
        public readonly IReadOnlyList<IPackage> RemovedPackages;
    }

    public abstract class AbstractPackageLoader
    {
        /// <summary>
        /// Checks if the loader has loaded packages
        /// </summary>
        public bool IsLoaded { get; private set; }

        /// <summary>
        /// Checks if the loader is fetching new packages right now
        /// </summary>
        public bool IsLoading { get; protected set; }

        public bool Any()
        {
            return !PackageReference.IsEmpty;
        }

        /// <summary>
        /// The collection of currently available packages
        /// </summary>
        public List<IPackage> Packages
        {
            get => PackageReference.Values.ToList();
        }

        protected readonly ConcurrentDictionary<long, IPackage> PackageReference;

        /// <summary>
        /// Fires when a block of packages (one package or more) is added or removed to the loader
        /// </summary>
        public event EventHandler<PackagesChangedEvent>? PackagesChanged;

        /// <summary>
        /// Fires when the loader finishes fetching packages
        /// </summary>
        public event EventHandler<EventArgs>? FinishedLoading;

        /// <summary>
        /// Fires when the manager starts fetching packages
        /// </summary>
        public event EventHandler<EventArgs>? StartedLoading;

        private readonly bool ALLOW_MULTIPLE_PACKAGE_VERSIONS;
        private readonly bool DISABLE_RELOAD;
        private readonly bool PACKAGES_CHECKED_BY_DEFAULT;
        private readonly bool REQUIRES_INTERNET;
        protected string LOADER_IDENTIFIER;
        private int LoadOperationIdentifier;
        protected IReadOnlyList<IPackageManager> Managers { get; private set; }

        public AbstractPackageLoader(
            IReadOnlyList<IPackageManager> managers,
            string identifier,
            bool AllowMultiplePackageVersions,
            bool DisableReload,
            bool CheckedBydefault,
            bool RequiresInternet
        )
        {
            Managers = managers;
            PackageReference = new ConcurrentDictionary<long, IPackage>();
            IsLoaded = false;
            IsLoading = false;
            PACKAGES_CHECKED_BY_DEFAULT = CheckedBydefault;
            DISABLE_RELOAD = DisableReload;
            ALLOW_MULTIPLE_PACKAGE_VERSIONS = AllowMultiplePackageVersions;
            LOADER_IDENTIFIER = identifier;
            ALLOW_MULTIPLE_PACKAGE_VERSIONS = AllowMultiplePackageVersions;
            REQUIRES_INTERNET = RequiresInternet;
        }

        /// <summary>
        /// Stops the current loading process
        /// </summary>
        public void StopLoading(bool emitFinishSignal = true)
        {
            LoadOperationIdentifier = -1;
            IsLoaded = false;
            IsLoading = false;
            if (emitFinishSignal)
                InvokeFinishedLoadingEvent();
        }

        protected void InvokePackagesChangedEvent(
            bool proceduralChange,
            IReadOnlyList<IPackage> toAdd,
            IReadOnlyList<IPackage> toRemove
        )
        {
            PackagesChanged?.Invoke(this, new(proceduralChange, toAdd, toRemove));
        }

        protected void InvokeStartedLoadingEvent()
        {
            StartedLoading?.Invoke(this, EventArgs.Empty);
        }

        protected void InvokeFinishedLoadingEvent()
        {
            FinishedLoading?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Will trigger a forceful reload of the packages
        /// </summary>
        public virtual async Task ReloadPackages()
        {
            try
            {
                if (DISABLE_RELOAD)
                {
                    InvokePackagesChangedEvent(false, [], []);
                    return;
                }

                if (IsLoading)
                {
                    Logger.Debug($"[{this.GetType()}] Packages are already being loaded!!!");
                    return;
                }

                LoadOperationIdentifier = new Random().Next();
                int current_identifier = LoadOperationIdentifier;
                IsLoading = true;
                StartedLoading?.Invoke(this, EventArgs.Empty);

                // Clear packages only after signaling the load started, so the UI shows the
                // loading state instead of briefly flashing the "no packages found" message.
                PackageReference.Clear();
                IsLoaded = false;
                InvokePackagesChangedEvent(false, [], []);

                if (REQUIRES_INTERNET)
                {
                    await CoreTools.WaitForInternetConnection();
                }

                List<Task<IReadOnlyList<IPackage>>> tasks = [];

                foreach (IPackageManager manager in Managers)
                {
                    if (manager.IsReady())
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
                            await Task.Delay(100).ConfigureAwait(false);
                        }

                        if (task.IsCompleted)
                        {
                            if (
                                LoadOperationIdentifier == current_identifier
                                && task.IsCompletedSuccessfully
                            )
                            {
                                var toAdd = new List<IPackage>();
                                foreach (IPackage package in task.Result)
                                {
                                    if (Contains(package) || !await IsPackageValid(package))
                                    {
                                        continue;
                                    }

                                    toAdd.Add(package);
                                    await AddPackage(package);
                                }

                                InvokePackagesChangedEvent(true, toAdd, []);
                            }

                            tasks.Remove(task);
                        }
                    }
                }

                if (LoadOperationIdentifier == current_identifier)
                {
                    InvokeFinishedLoadingEvent();
                    IsLoaded = true;
                }

                IsLoading = false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                IsLoading = false;
            }
        }

        /// <summary>
        /// Resets the packages available on the loader
        /// </summary>
        public void ClearPackages(bool emitFinishSignal = true)
        {
            StopLoading(emitFinishSignal);
            PackageReference.Clear();
            IsLoaded = false;
            IsLoading = false;
            InvokePackagesChangedEvent(false, [], []);
        }

        /// <summary>
        /// Loads the packages from the given manager
        /// </summary>
        /// <param name="manager">The manager from which to load packages</param>
        /// <returns>A task that will load the packages</returns>
        protected abstract IReadOnlyList<IPackage> LoadPackagesFromManager(IPackageManager manager);

        /// <summary>
        /// Checks whether the package is valid or must be skipped
        /// </summary>
        /// <param name="package">The package to check</param>
        /// <returns>True if the package can be added, false otherwise</returns>
        protected abstract Task<bool> IsPackageValid(IPackage package);

        /// <summary>
        /// A method to post-process packages after they have been added.
        /// </summary>
        /// <param name="package">The package to process</param>
        protected abstract Task WhenAddingPackage(IPackage package);

        /// <summary>
        /// Checks whether a package is contained on the current Loader
        /// </summary>
        /// <param name="package">The package to check against</param>
        public bool Contains(IPackage package)
        {
            return PackageReference.ContainsKey(HashPackage(package));
        }

        /// <summary>
        /// Returns the appropriate hash of the package, according to the current loader configuration
        /// </summary>
        /// <param name="package">The package to hash</param>
        /// <returns>A long int containing the hash</returns>
        protected long HashPackage(IPackage package)
        {
            return ALLOW_MULTIPLE_PACKAGE_VERSIONS ? package.GetVersionedHash() : package.GetHash();
        }

        protected async Task AddPackage(IPackage package)
        {
            if (Contains(package))
                return;

            package.IsChecked = PACKAGES_CHECKED_BY_DEFAULT;
            await WhenAddingPackage(package);
            PackageReference.TryAdd(HashPackage(package), package);
        }

        /// <summary>
        /// Adds a foreign package to the current loader. Perhaps a package has been recently installed and it needs to be added to the installed packages loader
        /// </summary>
        /// <param name="package">The package to add</param>
        public async Task AddForeign(IPackage? package)
        {
            if (package is null)
            {
                return;
            }

            await AddPackage(package);
            InvokePackagesChangedEvent(true, [package], []);
        }

        /// <summary>
        /// Removes the given package from the list.
        /// </summary>
        public void Remove(IPackage? package)
        {
            if (package is null)
            {
                return;
            }

            if (!Contains(package))
            {
                return;
            }

            PackageReference.Remove(HashPackage(package), out _);
            InvokePackagesChangedEvent(true, [], [package]);
        }

        /// <summary>
        /// Gets the corresponding package on the current loader.
        /// This method follows the equivalence settings for this loader
        /// </summary>
        /// <returns>A Package? object</returns>
        public IPackage? GetEquivalentPackage(IPackage? package)
        {
            if (package is null)
            {
                return null;
            }

            PackageReference.TryGetValue(HashPackage(package), out IPackage? eq);
            return eq;
        }

        /// <summary>
        /// Gets ALL of the equivalent packages on this loader.
        /// This method does NOT follow the equivalence settings for this loader
        /// </summary>
        /// <param name="package">The package for which to find the equivalent packages</param>
        /// <returns>A IReadOnlyList<Package> object</returns>
        public IReadOnlyList<IPackage> GetEquivalentPackages(IPackage? package)
        {
            if (package is null)
            {
                return [];
            }

            return Packages.Where(p => p.IsEquivalentTo(package)).ToArray();
        }

        public IPackage? GetPackageForId(string id, string? sourceName = null)
        {
            foreach (IPackage package in Packages)
            {
                if (package.Id == id && (sourceName is null || package.Source.Name == sourceName))
                {
                    return package;
                }
            }

            return null;
        }

        public int Count()
        {
            return PackageReference.Count;
        }
    }
}

using System.Collections.Concurrent;
using UniGetUI.Core.Classes;
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

    public class PackageLoadFinishedEventArgs : EventArgs
    {
        public static readonly PackageLoadFinishedEventArgs Normal = new(false);
        public static readonly PackageLoadFinishedEventArgs ListingOnlyLoad = new(true);

        private PackageLoadFinishedEventArgs(bool listingOnly) => ListingOnly = listingOnly;

        /// <summary>
        /// Whether the load that finished only listed packages (a remote rescan or the periodic
        /// inventory refresh). Handlers that act on a finished load, such as installing updates
        /// automatically, skip such loads.
        /// </summary>
        public bool ListingOnly { get; }

        public static PackageLoadFinishedEventArgs For(bool listingOnly) =>
            listingOnly ? ListingOnlyLoad : Normal;
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

        public bool LastLoadReportedFailures { get; private set; }

        private long _loadGeneration;
        public long LoadGeneration => Interlocked.Read(ref _loadGeneration);
        protected long AdvanceLoadGeneration() => Interlocked.Increment(ref _loadGeneration);

        public bool HasPendingInitialLoad => !IsLoaded && !LastLoadReportedFailures;

        public DateTime? LastLoadFinishedUtc { get; private set; }

        /// <summary>
        /// Whether the running load, or the last one when none runs, only lists packages; see
        /// <see cref="ReloadPackagesListingOnly"/>. Handlers of a finished load read
        /// <see cref="PackageLoadFinishedEventArgs.ListingOnly"/> instead, since another load may
        /// have started by the time they run.
        /// </summary>
        public bool LoadIsListingOnly { get; private set; }

        private CompletedPackageScan? _lastCompletedScan;

        /// <summary>
        /// A copy of the packages of the last full load that finished, with the managers whose
        /// listing succeeded in it. Later incremental changes to <see cref="Packages"/> do not
        /// alter it, so readers never observe a load in progress.
        /// </summary>
        public CompletedPackageScan? LastCompletedScan => Volatile.Read(ref _lastCompletedScan);

        private TaskCompletionSource? _loadCompletion;
        private readonly object _loadStateLock = new();

        private IReadOnlyDictionary<IPackageManager, TaskCompletionSource> _managerLoads =
            new Dictionary<IPackageManager, TaskCompletionSource>();

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

        private readonly ConcurrentDictionary<long, (bool Checked, bool RequiresReinstall)> _rememberedSelection = new();

        /// <summary>
        /// Fires when a block of packages (one package or more) is added or removed to the loader
        /// </summary>
        public event EventHandler<PackagesChangedEvent>? PackagesChanged;

        /// <summary>
        /// Fires when the loader finishes fetching packages
        /// </summary>
        public event EventHandler<PackageLoadFinishedEventArgs>? FinishedLoading;

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

        /// <summary>
        /// True when at least one of this loader's managers is ready. When none is, a reload runs nothing
        /// and an empty list says nothing about the packages on this device.
        /// </summary>
        public bool AnyManagerReady => Managers.Any(manager => manager.IsReady());

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
        public Task WaitForCurrentLoadAsync()
        {
            lock (_loadStateLock)
                return IsLoading && _loadCompletion is not null
                    ? _loadCompletion.Task
                    : Task.CompletedTask;
        }

        public Task WaitForManagerAsync(IPackageManager manager)
        {
            lock (_loadStateLock)
                return IsLoading && _managerLoads.TryGetValue(manager, out var load)
                    ? load.Task
                    : Task.CompletedTask;
        }

        private static void CompleteManagerLoads(
            IReadOnlyDictionary<IPackageManager, TaskCompletionSource> managerLoads
        )
        {
            foreach (TaskCompletionSource load in managerLoads.Values)
                load.TrySetResult();
        }

        private void CompleteCurrentLoad()
        {
            Interlocked.Exchange(ref _loadCompletion, null)?.TrySetResult();
            CompleteManagerLoads(Volatile.Read(ref _managerLoads));
        }

        private void CompleteLoad(TaskCompletionSource completion)
        {
            Interlocked.CompareExchange(ref _loadCompletion, null, completion);
            completion.TrySetResult();
        }

        public void StopLoading(bool emitFinishSignal = true)
        {
            bool listingOnly;
            lock (_loadStateLock)
            {
                listingOnly = IsLoading && LoadIsListingOnly;
                LoadOperationIdentifier = -1;
                IsLoaded = false;
                IsLoading = false;
                CompleteCurrentLoad();
            }
            if (emitFinishSignal)
                InvokeFinishedLoadingEvent(listingOnly);
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

        protected void InvokeFinishedLoadingEvent(bool listingOnly = false)
        {
            FinishedLoading?.Invoke(this, PackageLoadFinishedEventArgs.For(listingOnly));

            // A load leaves the managers' output and the list it replaced behind as garbage.
            IdleMemoryTrimmer.Request();
        }

        /// <summary>
        /// Marks the start of a load that a subclass runs itself, so that waiters and reloads
        /// requested meanwhile see a normal load with a completion to wait for.
        /// </summary>
        protected TaskCompletionSource? BeginExternalLoad()
        {
            lock (_loadStateLock)
            {
                if (IsLoading)
                    return null;
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _loadCompletion, completion);
                AdvanceLoadGeneration();
                LoadIsListingOnly = false;
                IsLoading = true;
                return completion;
            }
        }

        protected void EndExternalLoad(TaskCompletionSource completion)
        {
            lock (_loadStateLock)
            {
                if (ReferenceEquals(_loadCompletion, completion))
                    IsLoading = false;
                CompleteLoad(completion);
            }
        }

        /// <summary>
        /// Lists the packages of one manager and reports whether that call failed or did not run.
        /// </summary>
        protected virtual IReadOnlyList<IPackage> LoadPackagesFromManager(
            IPackageManager manager,
            out bool failed
        )
        {
            failed = false;
            return LoadPackagesFromManager(manager);
        }

        /// <summary>
        /// Will trigger a forceful reload of the packages
        /// </summary>
        public virtual Task ReloadPackages() => ReloadPackagesCore(listingOnly: false);

        /// <summary>
        /// Reloads the packages in listing-only mode (see <see cref="PackageListingScope"/>): the
        /// managers neither repair themselves nor change anything but their catalogues. Handlers of
        /// <see cref="FinishedLoading"/> are told so through its arguments.
        /// </summary>
        public Task ReloadPackagesListingOnly() => ReloadPackagesCore(listingOnly: true);

        private async Task ReloadPackagesCore(bool listingOnly)
        {
            TaskCompletionSource? completion = null;
            Dictionary<IPackageManager, TaskCompletionSource>? managerLoads = null;
            int current_identifier = 0;
            long startGeneration = 0;
            bool finishWasAnnounced = false;
            try
            {
                if (DISABLE_RELOAD)
                {
                    InvokePackagesChangedEvent(false, [], []);
                    return;
                }

                // A listing-only load skips what a normal load triggers (automatic updates,
                // maintenance records), so a normal reload must not settle for it.
                while (true)
                {
                    Task running;
                    lock (_loadStateLock)
                    {
                        if (!IsLoading)
                        {
                            LoadOperationIdentifier = new Random().Next();
                            current_identifier = LoadOperationIdentifier;
                            completion = new TaskCompletionSource(
                                TaskCreationOptions.RunContinuationsAsynchronously
                            );
                            managerLoads = Managers.Distinct().ToDictionary(
                                manager => manager,
                                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                            );
                            Volatile.Write(ref _loadCompletion, completion);
                            Volatile.Write(ref _managerLoads, managerLoads);
                            startGeneration = AdvanceLoadGeneration();
                            LoadIsListingOnly = listingOnly;
                            LastLoadReportedFailures = false;
                            IsLoading = true;
                            break;
                        }
                        if (!LoadIsListingOnly || listingOnly)
                        {
                            Logger.Debug($"[{this.GetType()}] Packages are already being loaded!!!");
                            return;
                        }
                        running = _loadCompletion!.Task;
                    }
                    await running;
                }
                StartedLoading?.Invoke(this, EventArgs.Empty);

                RememberSelectionState();

                // Clear packages only after signaling the load started, so the UI shows the
                // loading state instead of briefly flashing the "no packages found" message.
                PackageReference.Clear();
                IsLoaded = false;
                InvokePackagesChangedEvent(false, [], []);

                if (REQUIRES_INTERNET && WillQueryAnyManager())
                {
                    await CoreTools.WaitForInternetConnection();
                }

                List<Task<(IReadOnlyList<IPackage> Packages, bool Failed)>> tasks = [];
                Dictionary<Task<(IReadOnlyList<IPackage> Packages, bool Failed)>, IPackageManager> taskManagers = [];
                HashSet<IPackageManager> succeededManagers = [];

                foreach (IPackageManager manager in Managers)
                {
                    if (manager.IsReady())
                    {
                        var task = Task.Run(async () =>
                        {
                            await WaitBeforeLoadingFromManagerAsync(manager);
                            using IDisposable? scope = listingOnly
                                ? PackageListingScope.EnterListingOnly()
                                : null;
                            var packages = LoadPackagesFromManager(manager, out bool failed);
                            return (packages, failed);
                        });
                        tasks.Add(task);
                        taskManagers[task] = manager;
                    }
                    else
                    {
                        managerLoads[manager].TrySetResult();
                    }
                }

                while (tasks.Count > 0)
                {
                    foreach (var task in tasks.ToArray())
                    {
                        if (!task.IsCompleted)
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }

                        if (task.IsCompleted)
                        {
                            // Each call reports its own result: a manager's shared failure flag
                            // can be reset meanwhile by another listing of the same manager.
                            if (
                                task.IsFaulted
                                || task.IsCanceled
                                || (task.Result.Failed && taskManagers[task].IsReady())
                            )
                                LastLoadReportedFailures = true;

                            if (
                                LoadOperationIdentifier == current_identifier
                                && task.IsCompletedSuccessfully
                            )
                            {
                                var toAdd = new List<IPackage>();
                                foreach (IPackage package in task.Result.Packages)
                                {
                                    if (Contains(package) || !await IsPackageValid(package))
                                    {
                                        continue;
                                    }

                                    toAdd.Add(package);
                                    await AddPackage(package, restoreSelection: true);
                                }

                                InvokePackagesChangedEvent(true, toAdd, []);
                                if (!task.Result.Failed)
                                    succeededManagers.Add(taskManagers[task]);
                            }

                            managerLoads[taskManagers[task]].TrySetResult();
                            tasks.Remove(task);
                        }
                    }
                }

                bool finishedCurrentLoad;
                lock (_loadStateLock)
                {
                    finishedCurrentLoad = LoadOperationIdentifier == current_identifier;
                    if (finishedCurrentLoad)
                    {
                        Volatile.Write(
                            ref _lastCompletedScan,
                            new CompletedPackageScan(
                                PackageReference.Values.ToArray(),
                                DateTime.UtcNow,
                                succeededManagers,
                                startGeneration
                            )
                        );
                        IsLoading = false;
                        LastLoadFinishedUtc = DateTime.UtcNow;
                        IsLoaded = true;
                    }
                }
                if (finishedCurrentLoad)
                {
                    finishWasAnnounced = true;
                    InvokeFinishedLoadingEvent(listingOnly);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);

                bool announceFailure;
                lock (_loadStateLock)
                {
                    announceFailure = !finishWasAnnounced
                        && completion is not null
                        && LoadOperationIdentifier == current_identifier;
                    if (announceFailure)
                    {
                        LastLoadReportedFailures = true;
                        IsLoading = false;
                    }
                }

                if (announceFailure)
                {
                    try
                    {
                        InvokeFinishedLoadingEvent(listingOnly);
                    }
                    catch (Exception announceEx)
                    {
                        Logger.Error(announceEx);
                    }
                }
            }
            finally
            {
                if (managerLoads is not null)
                    CompleteManagerLoads(managerLoads);
                if (completion is not null)
                    CompleteLoad(completion);
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

        protected virtual bool WillQueryAnyManager() => true;

        protected virtual Task WaitBeforeLoadingFromManagerAsync(IPackageManager manager) =>
            Task.CompletedTask;

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
            if (!ALLOW_MULTIPLE_PACKAGE_VERSIONS)
                return package.GetHash();

            return DistinguishReportedScopes ? package.GetInstalledIdentityHash() : package.GetVersionedHash();
        }

        /// <summary>
        /// When true, installations that differ only by reported scope are separate entries.
        /// </summary>
        protected virtual bool DistinguishReportedScopes => false;

        /// <summary>
        /// Finds the entry a probe refers to when its exact key misses: same versioned identity, and either side
        /// not reporting a scope. Ambiguity returns null rather than guessing between scopes.
        /// </summary>
        private IPackage? FindScopeTolerant(IPackage package)
        {
            if (!DistinguishReportedScopes || !package.Manager.Capabilities.InstalledPackagesKeepTheirSource)
                return null;

            var matches = Packages
                .Where(c => c.Manager.Id == package.Manager.Id
                    && c.Source.Name == package.Source.Name
                    && c.Id == package.Id
                    && c.VersionString == package.VersionString
                    && (c.NormalizedReportedScope is null || package.NormalizedReportedScope is null))
                .Take(2)
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private void RememberSelectionState()
        {
            _rememberedSelection.Clear();
            foreach (var entry in PackageReference)
            {
                _rememberedSelection[entry.Key] = (
                    entry.Value.IsChecked,
                    entry.Value.RequiresReinstallToUpdate
                );
            }
        }

        protected async Task AddPackage(IPackage package, bool restoreSelection = false)
        {
            if (Contains(package))
                return;

            package.IsChecked =
                restoreSelection
                && _rememberedSelection.TryGetValue(HashPackage(package), out var remembered)
                && (remembered.RequiresReinstall || !package.RequiresReinstallToUpdate)
                    ? remembered.Checked
                    : PACKAGES_CHECKED_BY_DEFAULT && !package.RequiresReinstallToUpdate;
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

            if (Contains(package))
            {
                PackageReference.Remove(HashPackage(package), out _);
                InvokePackagesChangedEvent(true, [], [package]);
                return;
            }

            if ((FindScopeTolerant(package) ?? FindFromAnySource(package)) is { } stored)
            {
                PackageReference.Remove(HashPackage(stored), out _);
                InvokePackagesChangedEvent(true, [], [stored]);
            }
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

            if (PackageReference.TryGetValue(HashPackage(package), out IPackage? eq))
                return eq;

            return FindScopeTolerant(package) ?? FindFromAnySource(package);
        }

        private IPackage? FindFromAnySource(IPackage package)
        {
            if (package.Manager.Capabilities.InstalledPackagesKeepTheirSource)
                return null;

            var matches = Packages.Where(candidate =>
                PackageIdentity.IsSamePackageFromAnySource(candidate, package)
                && (!ALLOW_MULTIPLE_PACKAGE_VERSIONS || candidate.VersionString == package.VersionString)
                && (!DistinguishReportedScopes
                    || candidate.NormalizedReportedScope is null
                    || package.NormalizedReportedScope is null
                    || candidate.NormalizedReportedScope == package.NormalizedReportedScope)
            );

            if (!DistinguishReportedScopes)
                return matches.FirstOrDefault();

            // Several candidates mean the probe cannot say which installation it refers to.
            var found = matches.Take(2).ToArray();
            return found.Length == 1 ? found[0] : null;
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

            return Packages.Where(p => p.IsSamePackageAs(package)).ToArray();
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

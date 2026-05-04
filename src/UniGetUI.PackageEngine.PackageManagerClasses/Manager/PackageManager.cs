using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Classes.Packages;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.ManagerClasses.Manager
{
    public abstract class PackageManager : IPackageManager
    {
        private const int PackageListingTaskTimeout = 60;

        public ManagerProperties Properties { get; set; } = new(IsDummy: true);
        public ManagerCapabilities Capabilities { get; set; } = new(IsDummy: true);
        public ManagerStatus Status { get; set; } = new() { Found = false };
        public string Id
        {
            get => string.IsNullOrWhiteSpace(Properties.Id) ? Name : Properties.Id;
        }
        public string Name
        {
            get => Properties.Name;
        }
        public string DisplayName
        {
            get => Properties.DisplayName ?? Name;
        }
        public IManagerSource DefaultSource
        {
            get => Properties.DefaultSource;
        }
        public IManagerLogger TaskLogger { get; }
        public IReadOnlyList<ManagerDependency> Dependencies { get; protected set; } = [];
        public IMultiSourceHelper SourcesHelper { get; protected set; } = new NullSourceHelper();
        public IPackageDetailsHelper DetailsHelper { get; protected set; } = null!;
        public IPackageOperationHelper OperationHelper { get; protected set; } = null!;

        private readonly bool _baseConstructorCalled;
        private bool _ready;

        public PackageManager()
        {
            _baseConstructorCalled = true;
            TaskLogger = new ManagerLogger(this);
        }

        private static void Throw(string message)
        {
            throw new InvalidDataException(message);
        }

        protected abstract void _loadManagerExecutableFile(
            out bool found,
            out string path,
            out string callArguments
        );
        protected abstract void _loadManagerVersion(out string version);

        protected virtual void _performExtraLoadingSteps() { }

        public virtual void Initialize()
        {
            try
            {
                _ready = false;
                _ensurePropertlyConstructed();

                if (!IsEnabled())
                { // Do NOT initialise disabled package managers
                    Status = new() { Version = CoreTools.Translate("{0} is disabled", DisplayName) };
                    Logger.ImportantInfo($"{Name} is not enabled");
                    return;
                }

                // Find package manager executable
                _loadManagerExecutableFile(
                    out bool found,
                    out string path,
                    out string callArguments
                );
                Status = new ManagerStatus();
                Status.Found = found;
                Status.ExecutablePath = path;
                Status.ExecutableCallArgs = callArguments;

                if (!Status.Found)
                { // Do not load version of managers that were not found
                    Status.Version = CoreTools
                        .Translate("{pm} was not found!")
                        .Replace("{pm}", DisplayName)
                        .Trim('!');
                    Logger.Error($"{Name} is enabled but was not found on the system!");
                    return;
                }

                Logger.ImportantInfo($"{Name} is enabled and was found on {path}");

                // Load manager version
                _loadManagerVersion(out string version);
                Status.Version = version;
                _logManagerInfo();

                // Finish initialization
                _performExtraLoadingSteps();
                _ready = true;
                _initializeSources();
            }
            catch (Exception ex)
            {
                Logger.Error($"An error occurred while initialising package manager {Name}");
                Logger.Error(ex);
            }
        }

        /// <summary>
        /// This serves to ensure that the implemented hasn't messed up any crucial
        /// details on the implementation
        /// </summary>
        private void _ensurePropertlyConstructed()
        {
            if (!_baseConstructorCalled)
                Throw($"The Manager {Properties.Name} has not called the base constructor.");
            if (Capabilities.IsDummy)
                Throw(
                    $"The current instance of PackageManager with name ${Properties.Name} does not have a valid Capabilities object"
                );
            if (Properties.IsDummy)
                Throw(
                    $"The current instance of PackageManager with name ${Properties.Name} does not have a valid Properties object"
                );

            if (OperationHelper is NullPkgOperationHelper)
                Throw($"Manager {Name} does not have an OperationProvider");
            if (DetailsHelper is NullPkgDetailsHelper)
                Throw($"Manager {Name} does not have a valid DetailsHelper");

            if (Capabilities.SupportsCustomSources && SourcesHelper is NullSourceHelper)
                Throw(
                    $"Manager {Name} has been declared as SupportsCustomSources but has no helper associated with it"
                );
        }

        /// <summary>
        /// Load and prepare manager sources sources
        /// </summary>
        private void _initializeSources()
        {
            // Sources that are instantiated before Manager's DisplayName property will have invalid DisplayName
            // To prevent loading it on runtime, the string is cached internally and will only be reloaded when needed to
            Properties.DefaultSource.RefreshSourceNames();
            foreach (var source in Properties.KnownSources)
            {
                source.RefreshSourceNames();
            }

            if (Capabilities.SupportsCustomSources)
            {
                // SourcesHelper.GetSources() will handle saving the found sources to the internal registry
                SourcesHelper.GetSources();
            }
        }

        private void _logManagerInfo()
        {
            string LogData = $"""
                ▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄
                █▀▀▀▀▀▀▀▀▀▀▀▀▀ MANAGER LOADED ▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀
                █ Name: {DisplayName} (aka {Name})
                █ Executable name: "{Properties.ExecutableFriendlyName}"
                █ Executable path: "{Status.ExecutablePath}"
                █ Call arguments: "{Status.ExecutableCallArgs}"
                █ Version: {Status.Version.Trim().Replace("\n", "\n█          ")}
                █          
                █ {DisplayName} is enabled and ready to go.
                ▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀
                """;
            Logger.Info(LogData);
        }

        /// <summary>
        /// Returns a list of paths that could be used for this package manager.
        /// For example, if you have three Pythons installed on your system, this would return those three Pythons.
        /// </summary>
        /// <returns>A tuple containing: a boolean that represents whether the path was found or not; the path to the file if found.</returns>
        public abstract IReadOnlyList<string> FindCandidateExecutableFiles();

        public Tuple<bool, string> GetExecutableFile()
        {
            var candidates = FindCandidateExecutableFiles();
            if (candidates.Count == 0)
            {
                // No paths were found
                return new(false, "");
            }

            // If custom package manager paths are DISABLED, get the first one (as old UniGetUI did) and return it.
            if (!SecureSettings.Get(SecureSettings.K.AllowCustomManagerPaths))
            {
                return new(true, candidates[0]);
            }
            else
            {
                string? exeSelection = Settings.GetDictionaryItem<string, string>(
                    Settings.K.ManagerPaths,
                    Name
                );
                // If there is no executable selection for this package manager
                if (string.IsNullOrEmpty(exeSelection))
                {
                    return new(true, candidates[0]);
                }
                else if (!File.Exists(exeSelection))
                {
                    Logger.Error(
                        $"The selected executable path {exeSelection} for manager {Name} does not exist, the default one will be used..."
                    );
                    return new(true, candidates[0]);
                }

                // While technically executables that are not in the path should work,
                // since detection of executables will be performed on the found paths, it is more consistent
                // to throw an error when a non-found executable is used. Furthermore, doing this we can filter out
                // any invalid paths or files.
                if (candidates.Select(x => x.ToLower()).Contains(exeSelection.ToLower()))
                {
                    return new(true, exeSelection);
                }
                else
                {
                    Logger.Error(
                        $"The selected executable path {exeSelection} for manager {Name} was not found among the candidates "
                            + $"(executables found are [{string.Join(',', candidates)}]), the default will be used..."
                    );
                    return new(true, candidates[0]);
                }
            }
        }

        /// <summary>
        /// Returns true if the manager is enabled, false otherwise
        /// </summary>
        public bool IsEnabled()
        {
            return !Settings.GetDictionaryItem<string, bool>(Settings.K.DisabledManagers, Name);
        }

        /// <summary>
        /// Returns true if the manager is enabled and available (the required executable files were found). Returns false otherwise
        /// </summary>
        public bool IsReady()
        {
            return _ready && IsEnabled() && Status.Found;
        }

        /// <summary>
        /// Returns an array of Package objects that the manager lists for the given query. Depending on the manager, the list may
        /// also include similar results. This method is fail-safe and will return an empty array if an error occurs.
        /// </summary>
        public IReadOnlyList<IPackage> FindPackages(string query) => _findPackages(query, false);

        private IReadOnlyList<IPackage> _findPackages(string query, bool SecondAttempt)
        {
            if (!IsReady())
            {
                Logger.Warn($"Manager {Name} is disabled but yet FindPackages was called");
                return [];
            }
            try
            {
                var task = Task.Run(() => FindPackages_UnSafe(query));
                if (!task.Wait(TimeSpan.FromSeconds(PackageListingTaskTimeout)))
                {
                    if (!Settings.Get(Settings.K.DisableTimeoutOnPackageListingTasks))
                    {
                        CoreTools.FinalizeDangerousTask(task);
                        throw new TimeoutException(
                            $"Task _getInstalledPackages for manager {Name} did not finish after "
                                + $"{PackageListingTaskTimeout} seconds, aborting.  You may disable "
                                + $"timeouts from UniGetUI Advanced Settings"
                        );
                    }

                    task.Wait();
                }

                var packages = task.GetAwaiter().GetResult();
                Logger.Info(
                    $"Found {packages.Count} available packages from {Name} with the query {query}"
                );
                return packages;
            }
            catch (Exception e)
            {
                if (!SecondAttempt)
                {
                    while (e is AggregateException)
                        e =
                            e.InnerException
                            ?? new InvalidOperationException("How did we get here?");
                    Logger.Warn(
                        $"Manager {DisplayName} failed to find packages with exception {e.GetType().Name}: {e.Message}"
                    );
                    Logger.Warn(
                        $"Since this was the first attempt, {Name}.AttemptFastRepair() will be called and the procedure will be restarted"
                    );
                    AttemptFastRepair();
                    return _findPackages(query, true);
                }

                Logger.Error("Error finding packages on manager " + Name + " with query " + query);
                Logger.Error(e);
                return [];
            }
        }

        /// <summary>
        /// Returns an array of UpgradablePackage objects that represent the available updates reported by the manager.
        /// This method is fail-safe and will return an empty array if an error occurs.
        /// </summary>
        public IReadOnlyList<IPackage> GetAvailableUpdates() => _getAvailableUpdates(false);

        private IReadOnlyList<IPackage> _getAvailableUpdates(bool SecondAttempt)
        {
            if (!IsReady())
            {
                Logger.Warn($"Manager {Name} is disabled but yet GetAvailableUpdates was called");
                return [];
            }
            try
            {
                Task.Run(RefreshPackageIndexes)
                    .Wait(TimeSpan.FromSeconds(PackageListingTaskTimeout));

                var task = Task.Run(GetAvailableUpdates_UnSafe);
                if (!task.Wait(TimeSpan.FromSeconds(PackageListingTaskTimeout)))
                {
                    if (!Settings.Get(Settings.K.DisableTimeoutOnPackageListingTasks))
                    {
                        CoreTools.FinalizeDangerousTask(task);
                        throw new TimeoutException(
                            $"Task _getInstalledPackages for manager {Name} did not finish after "
                                + $"{PackageListingTaskTimeout} seconds, aborting.  You may disable "
                                + $"timeouts from UniGetUI Advanced Settings"
                        );
                    }

                    task.Wait();
                }

                var packages = task.GetAwaiter().GetResult();
                Logger.Info($"Found {packages.Count} available updates from {Name}");
                return packages;
            }
            catch (Exception e)
            {
                if (!SecondAttempt)
                {
                    while (e is AggregateException)
                        e =
                            e.InnerException
                            ?? new InvalidOperationException("How did we get here?");
                    Logger.Warn(
                        $"Manager {DisplayName} failed to list available updates with exception {e.GetType().Name}: {e.Message}"
                    );
                    Logger.Warn(
                        $"Since this was the first attempt, {Name}.AttemptFastRepair() will be called and the procedure will be restarted"
                    );
                    AttemptFastRepair();
                    return _getAvailableUpdates(true);
                }

                Logger.Error("Error finding updates on manager " + Name);
                Logger.Error(e);
                return [];
            }
        }

        /// <summary>
        /// Returns an array of Package objects that represent the installed reported by the manager.
        /// This method is fail-safe and will return an empty array if an error occurs.
        /// </summary>
        public IReadOnlyList<IPackage> GetInstalledPackages() => _getInstalledPackages(false);

        private IReadOnlyList<IPackage> _getInstalledPackages(bool SecondAttempt)
        {
            if (!IsReady())
            {
                Logger.Warn($"Manager {Name} is disabled but yet GetInstalledPackages was called");
                return [];
            }
            try
            {
                var task = Task.Run(GetInstalledPackages_UnSafe);
                if (!task.Wait(TimeSpan.FromSeconds(PackageListingTaskTimeout)))
                {
                    if (!Settings.Get(Settings.K.DisableTimeoutOnPackageListingTasks))
                    {
                        CoreTools.FinalizeDangerousTask(task);
                        throw new TimeoutException(
                            $"Task _getInstalledPackages for manager {Name} did not finish after "
                                + $"{PackageListingTaskTimeout} seconds, aborting.  You may disable "
                                + $"timeouts from UniGetUI Advanced Settings"
                        );
                    }

                    task.Wait();
                }

                var packages = task.GetAwaiter().GetResult();
                Logger.Info($"Found {packages.Count} installed packages from {Name}");
                return packages;
            }
            catch (Exception e)
            {
                if (!SecondAttempt)
                {
                    while (e is AggregateException)
                        e =
                            e.InnerException
                            ?? new InvalidOperationException("How did we get here?");
                    Logger.Warn(
                        $"Manager {DisplayName} failed to list installed packages with exception {e.GetType().Name}: {e.Message}"
                    );
                    Logger.Warn(
                        $"Since this was the first attempt, {Name}.AttemptFastRepair() will be called and the procedure will be restarted"
                    );
                    AttemptFastRepair();
                    return _getInstalledPackages(true);
                }

                Logger.Error("Error finding installed packages on manager " + Name);
                Logger.Error(e);
                return [];
            }
        }

        /// <summary>
        /// Returns the available packages to install for the given query.
        /// Each manager MUST implement this method.
        /// </summary>
        /// <param name="query">The query string to search for</param>
        /// <returns>An array of Package objects</returns>
        protected abstract IReadOnlyList<Package> FindPackages_UnSafe(string query);

        /// <summary>
        /// Returns the available updates reported by the manager.
        /// Each manager MUST implement this method.
        /// </summary>
        /// <returns>An array of UpgradablePackage objects</returns>
        protected abstract IReadOnlyList<Package> GetAvailableUpdates_UnSafe();

        /// <summary>
        /// Returns an array of Package objects containing the installed packages reported by the manager.
        /// Each manager MUST implement this method.
        /// </summary>
        /// <returns>An array of Package objects</returns>
        protected abstract IReadOnlyList<Package> GetInstalledPackages_UnSafe();

        /// <summary>
        /// Refreshes the Package Manager sources/indexes
        /// Each manager MUST implement this method.
        /// </summary>
        public virtual void RefreshPackageIndexes()
        {
            Logger.Debug($"Manager {Name} has not implemented RefreshPackageIndexes");
        }

        /// <summary>
        /// Attempt a live, fast, repair method when an exception occurs (for example, reconnect to COM Server)
        /// </summary>
        public virtual void AttemptFastRepair()
        {
            Logger.Debug($"Manager {Name} has not implemented AttemptFastRepair");
        }
    }
}

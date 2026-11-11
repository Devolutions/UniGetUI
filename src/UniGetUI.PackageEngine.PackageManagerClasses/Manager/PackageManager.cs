using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Classes.Packages;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.ManagerClasses.Manager
{
    public abstract class PackageManager : IPackageManager
    {
        private const int PackageListingTaskTimeout = 60;

        protected virtual TimeSpan ListingTaskTimeout => TimeSpan.FromSeconds(PackageListingTaskTimeout);

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
        public virtual Encoding OutputEncoding => Encoding.UTF8;
        public virtual bool InstallerUrlFollowsPackageVersion => false;

        public virtual bool CommandLineIsShellInterpreted => false;

        public virtual bool IdentifiersAreQuotedOnCommandLine => false;

        public virtual bool CanUpdateToExactVersion =>
            Capabilities.SupportsCustomVersions
            && Capabilities.SupportsCustomVersionsOnUpdate
            && Capabilities.CanListVersions;

        protected virtual bool? IsEligibleUpdateTarget(IPackage update, string version) => true;

        protected virtual IReadOnlyList<string> ListVersionsForUpdateSelection(IPackage update, bool includePreReleases) =>
            DetailsHelper.GetVersions(update);

        private IReadOnlyList<Package> OfferUpdatesWithinSkippedMajors(IReadOnlyList<Package> updates)
        {
            if (!CanUpdateToExactVersion)
                return updates;

            Package[]? resolved = null;
            for (int i = 0; i < updates.Count; i++)
            {
                if (!updates[i].IsUpdateMajor())
                    continue;
                if (UpdateWithinSkippedMajor(updates[i]) is { } withinMajor)
                    (resolved ??= [.. updates])[i] = withinMajor;
            }

            return resolved ?? updates;
        }

        public IPackage? ResolveSkippedMajorUpdate(IPackage update, InstallOptions? options = null)
        {
            if (
                !CanUpdateToExactVersion
                || update is not Package package
                || !(package.IsUpdateMajor() || package.OverridenOptions.PinUpdateToNewVersion)
            )
                return update;

            return UpdateWithinSkippedMajor(package, options);
        }

        private Package? UpdateWithinSkippedMajor(Package update, InstallOptions? options = null)
        {
            try
            {
                options ??= InstallOptionsFactory.LoadApplicable(update);
                if (!options.SkipMajorUpdates)
                    return null;

                if (
                    VersionsForSkippedMajor(update, options.PreRelease) is not { } versions
                    || NewestVersionWithinMajor(update, versions, options.PreRelease) is not { } target
                )
                    return null;

                Logger.Info(
                    $"Offering {update.Id} {target} instead of {update.NewVersionString}: major updates are skipped for this package."
                );
                var overrides = update.OverridenOptions;
                overrides.PinUpdateToNewVersion = true;
                return new Package(update.Name, update.Id, update.VersionString, target, update.Source, this, overrides)
                {
                    RequiresReinstallToUpdate = update.RequiresReinstallToUpdate,
                };
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not look for an update of {update.Id} within its installed major version");
                Logger.Warn(ex);
                return null;
            }
        }

        internal static TimeSpan VersionLookupTimeout { get; set; } = TimeSpan.FromSeconds(35);

        private static readonly TimeSpan VersionListLifetime = TimeSpan.FromMinutes(30);

        private static readonly TimeSpan VersionLookupBackoff = TimeSpan.FromMinutes(5);

        private readonly ConcurrentDictionary<long, (string ReportedVersion, bool PreReleases, DateTime FetchedAt, IReadOnlyList<string> Versions)> _skippedMajorVersionLists = new();

        private readonly ConcurrentDictionary<long, DateTime> _skippedMajorLookupTimeouts = new();

        private readonly ConcurrentDictionary<(long Package, string Version), (DateTime CheckedAt, bool Eligible)> _skippedMajorEligibility = new();

        private IReadOnlyList<string>? VersionsForSkippedMajor(Package update, bool includePreReleases)
        {
            long key = update.GetHash();
            if (
                _skippedMajorVersionLists.TryGetValue(key, out var cached)
                && cached.PreReleases == includePreReleases
                && (cached.ReportedVersion == update.NewVersionString || update.OverridenOptions.PinUpdateToNewVersion)
                && DateTime.UtcNow - cached.FetchedAt < VersionListLifetime
            )
                return cached.Versions;

            if (
                _skippedMajorLookupTimeouts.TryGetValue(key, out DateTime timedOutAt)
                && DateTime.UtcNow - timedOutAt < VersionLookupBackoff
            )
                return null;

            var lookup = Task.Run(() => ListVersionsForUpdateSelection(update, includePreReleases));
            if (!lookup.Wait(VersionLookupTimeout))
            {
                _skippedMajorLookupTimeouts[key] = DateTime.UtcNow;
                Logger.Warn(
                    $"Listing the versions of {update.Id} took longer than {VersionLookupTimeout.TotalSeconds:0} seconds; its major update stays skipped"
                );
                return null;
            }

            if (lookup.Result.Count is 0)
            {
                _skippedMajorLookupTimeouts[key] = DateTime.UtcNow;
                return lookup.Result;
            }

            _skippedMajorLookupTimeouts.TryRemove(key, out _);
            _skippedMajorVersionLists[key] = (update.NewVersionString, includePreReleases, DateTime.UtcNow, lookup.Result);
            return lookup.Result;
        }

        internal string? NewestVersionWithinMajor(IPackage update, IEnumerable<string> versions, bool includePreReleases)
        {
            if ((CoreTools.GetMajorVersion(update.VersionString) ?? CoreTools.MajorVersionOf(update.VersionString)) is not { } installedMajor)
                return null;

            List<string> candidates = [];
            foreach (string version in versions)
            {
                if (!includePreReleases && PreReleaseVersion.IsPreRelease(version))
                    continue;
                if (CoreTools.GetMajorVersion(version) != installedMajor)
                    continue;
                if (CompareVersions(version, update.VersionString) is not > 0)
                    continue;
                candidates.Add(version);
            }

            candidates.Sort((left, right) => CompareVersions(right, left) ?? 0);
            foreach (string candidate in candidates)
            {
                switch (CachedEligibility(update, candidate))
                {
                    case true:
                        return candidate;
                    case false:
                        Logger.Debug($"Skipping {update.Id} {candidate} as an update within the installed major: the feed has withdrawn it");
                        continue;
                    default:
                        Logger.Warn($"Could not tell whether {update.Id} {candidate} is still published; offering no update within the major this time");
                        return null;
                }
            }

            return null;
        }

        private bool? CachedEligibility(IPackage update, string version)
        {
            var key = (update.GetHash(), version);
            if (
                _skippedMajorEligibility.TryGetValue(key, out var cached)
                && DateTime.UtcNow - cached.CheckedAt < VersionListLifetime
            )
                return cached.Eligible;

            bool? eligible = IsEligibleUpdateTarget(update, version);
            if (eligible is { } known)
                _skippedMajorEligibility[key] = (DateTime.UtcNow, known);
            return eligible;
        }

        public virtual int? CompareVersions(string versionA, string versionB)
        {
            var parsedA = CoreTools.VersionStringToStruct(versionA);
            var parsedB = CoreTools.VersionStringToStruct(versionB);

            if (parsedA == CoreTools.Version.Null || parsedB == CoreTools.Version.Null)
                return null;

            return parsedA.CompareTo(parsedB);
        }

        private readonly bool _baseConstructorCalled;
        private bool _ready;

        public PackageManager()
        {
            _baseConstructorCalled = true;
            TaskLogger = new ManagerLogger(
                this,
                (type, process) =>
                {
                    if (
                        type
                        is LoggableTaskType.FindPackages
                            or LoggableTaskType.ListUpdates
                            or LoggableTaskType.ListInstalledPackages
                            or LoggableTaskType.RefreshIndexes
                    )
                        RegisterListingProcess(process);
                }
            );
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

        /// <summary>
        /// The argument vector that precedes the operation parameters, for managers whose command
        /// line must be built with <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
        /// instead of a single concatenated string. An empty vector selects the concatenated
        /// <see cref="ManagerStatus.ExecutableCallArgs"/> path.
        /// </summary>
        protected virtual IReadOnlyList<string> _getOperationCallArgs(
            string executablePath,
            string callArguments
        ) => [];

        protected virtual void _performPreInitializationSteps() { }

        protected virtual void _performExtraLoadingSteps() { }

        public bool IsInitializing { get; private set; } = true;

        public virtual void Initialize()
        {
            IsInitializing = true;
            try
            {
                _ready = false;
                _ensurePropertlyConstructed();
                _performPreInitializationSteps();

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

                if (_recordDefaultEnabledState(found))
                {
                    Status = new() { Version = CoreTools.Translate("{0} is disabled", DisplayName) };
                    Logger.ImportantInfo(
                        $"{Name} was not found on the system and has been disabled by default"
                    );
                    return;
                }

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

                Status.OperationCallArgs = _getOperationCallArgs(path, callArguments);

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
            finally
            {
                IsInitializing = false;
                ManagerHealthStore.NotifyChanged(Name);
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

        protected virtual bool CanSelectAutomatically(string candidate) => true;

        protected virtual bool CanUseSelectedExecutable(string path) => true;

        public Tuple<bool, string> GetExecutableFile()
        {
            var candidates = FindCandidateExecutableFiles()
                .Where(CanSelectAutomatically)
                .ToList();

            // If custom package manager paths are DISABLED, get the first one (as old UniGetUI did) and return it.
            if (!SecureSettings.Get(SecureSettings.K.AllowCustomManagerPaths))
            {
                if (candidates.Count == 0)
                {
                    // No paths were found
                    return new(false, "");
                }

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
                    if (candidates.Count == 0)
                    {
                        // No paths were found
                        return new(false, "");
                    }

                    return new(true, candidates[0]);
                }
                else if (!File.Exists(exeSelection))
                {
                    Logger.Error(
                        $"The selected executable path {exeSelection} for manager {Name} does not exist, the default one will be used if available..."
                    );
                }
                else if (!CanUseSelectedExecutable(exeSelection))
                {
                    Logger.Warn(
                        $"The selected executable path {exeSelection} for manager {Name} has been excluded, the default one will be used if available..."
                    );
                }

                else
                {
                    return new(true, exeSelection);
                }

                if (candidates.Count == 0)
                {
                    // No paths were found
                    return new(false, "");
                }

                return new(true, candidates[0]);
            }
        }

        public virtual bool HasExecutable() => GetExecutableFile().Item1;

        protected bool IsUserSelectedExecutablePath(string path) =>
            GetUserSelectedExecutablePath()?.Equals(path, StringComparison.OrdinalIgnoreCase) == true;

        protected string? GetUserSelectedExecutablePath()
        {
            if (!SecureSettings.Get(SecureSettings.K.AllowCustomManagerPaths))
            {
                return null;
            }

            string? exeSelection = Settings.GetDictionaryItem<string, string>(
                Settings.K.ManagerPaths,
                Name
            );

            return !string.IsNullOrEmpty(exeSelection) && File.Exists(exeSelection)
                ? exeSelection
                : null;
        }

        /// <summary>
        /// Returns true if the manager is enabled, false otherwise
        /// </summary>
        public bool IsEnabled()
        {
            return !Settings.GetDictionaryItem<string, bool>(Settings.K.DisabledManagers, Name);
        }

        private static readonly object _defaultEnabledStateLock = new();

        private bool _recordDefaultEnabledState(bool found)
        {
            lock (_defaultEnabledStateLock)
            {
                if (Settings.DictionaryContainsKey<string, bool>(Settings.K.DisabledManagers, Name))
                    return false;

                Settings.SetDictionaryItem(Settings.K.DisabledManagers, Name, !found);
                return !found;
            }
        }

        /// <summary>
        /// Returns true if the manager is enabled and available (the required executable files were found). Returns false otherwise
        /// </summary>
        public bool IsReady()
        {
            return _ready && IsEnabled() && Status.Found;
        }

        // Opt out (override => false) when AttemptFastRepair can't recover a timed-out query (issue #4974).
        protected virtual bool RetryListingTasksOnTimeout => true;

        // Processes started by the current listing task, so a timeout can kill them instead of orphaning them.
        private readonly AsyncLocal<List<Process>?> _listingProcesses = new();

        private readonly HashSet<List<Process>> _activeListingProcesses = [];

        private readonly AsyncLocal<CancellationToken> _listingCancellation = new();

        private readonly HashSet<CancellationTokenSource> _activeListingCancellations = [];

        protected CancellationToken ListingCancellationToken => _listingCancellation.Value;

        // Lets a listing task register a process for kill-on-timeout. No-op when outside a listing task.
        protected void RegisterListingProcess(Process process)
        {
            List<Process>? processes = _listingProcesses.Value;
            if (processes is null)
                return;
            lock (processes)
                if (!processes.Contains(process))
                    processes.Add(process);
        }

        private static int KillListingProcesses(List<Process> processes)
        {
            int killed = 0;
            lock (processes)
                foreach (Process p in processes)
                {
                    try
                    {
                        if (!p.HasExited)
                        {
                            p.Kill(entireProcessTree: true);
                            killed++;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Could not kill a timed-out process tree: {ex.Message}");
                    }
                }
            return killed;
        }

        public int StopRunningListings()
        {
            List<Process>[] active;
            lock (_activeListingProcesses)
                active = [.. _activeListingProcesses];
            CancellationTokenSource[] cancellations;
            lock (_activeListingCancellations)
                cancellations = [.. _activeListingCancellations];

            Interlocked.Exchange(ref _lastStopTimestamp, Stopwatch.GetTimestamp());
            int killed = active.Sum(KillListingProcesses);
            foreach (CancellationTokenSource cancellation in cancellations)
                CancelListing(cancellation);
            Logger.Warn($"Stopped {killed} running process(es) of manager {Name}");
            return killed;
        }

        private void CancelListing(CancellationTokenSource cancellation)
        {
            lock (_activeListingCancellations)
            {
                if (!_activeListingCancellations.Contains(cancellation))
                    return;
                try
                {
                    cancellation.Cancel();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"A cancelled listing task of manager {Name} failed to stop: {ex.Message}");
                }
            }
        }

        private void RefreshPackageIndexesSafely()
        {
            try
            {
                RunListingTaskWithTimeout<object?>(
                    () =>
                    {
                        RefreshPackageIndexes();
                        return null;
                    },
                    "RefreshPackageIndexes",
                    allowDisablingTimeout: false
                );
            }
            catch (Exception e)
            {
                while (e is AggregateException)
                    e = e.InnerException ?? new InvalidOperationException("How did we get here?");

                Logger.Warn(
                    $"Manager {DisplayName} could not refresh its package indexes "
                        + $"({e.GetType().Name}: {e.Message}). The available updates will be listed "
                        + "with the indexes as they are, which may result in an incomplete list."
                );
            }
        }

        private T RunListingTaskWithTimeout<T>(
            Func<T> method,
            string taskName,
            bool allowDisablingTimeout = true
        )
        {
            List<Process> processes = [];
            lock (_activeListingProcesses)
                _activeListingProcesses.Add(processes);
            CancellationTokenSource cancellation = new();
            lock (_activeListingCancellations)
                _activeListingCancellations.Add(cancellation);

            bool abandoned = false;
            try
            {
                var task = Task.Run(() =>
                {
                    _listingProcesses.Value = processes;
                    _listingCancellation.Value = cancellation.Token;
                    return method();
                });

                if (!task.Wait(ListingTaskTimeout))
                {
                    if (
                        !allowDisablingTimeout
                        || !Settings.Get(Settings.K.DisableTimeoutOnPackageListingTasks)
                    )
                    {
                        KillListingProcesses(processes);
                        CancelListing(cancellation);
                        abandoned = true;
                        task.ContinueWith(
                            _ => ForgetListing(processes, cancellation),
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default
                        );
                        CoreTools.FinalizeDangerousTask(task);
                        throw new TimeoutException(
                            $"Task {taskName} for manager {Name} did not finish after "
                                + $"{ListingTaskTimeout.TotalSeconds} seconds, aborting."
                                + (
                                    allowDisablingTimeout
                                        ? "  You may disable timeouts from UniGetUI Advanced Settings"
                                        : ""
                                )
                        );
                    }

                    task.Wait();
                }

                return task.GetAwaiter().GetResult();
            }
            finally
            {
                if (!abandoned)
                    ForgetListing(processes, cancellation);
            }
        }

        private void ForgetListingProcesses(List<Process> processes)
        {
            lock (_activeListingProcesses)
                _activeListingProcesses.Remove(processes);
        }

        private void ForgetListing(List<Process> processes, CancellationTokenSource cancellation)
        {
            ForgetListingProcesses(processes);
            lock (_activeListingCancellations)
            {
                _activeListingCancellations.Remove(cancellation);
                cancellation.Dispose();
            }
        }

        private long _lastStopTimestamp;

        private bool WasStoppedSince(long timestamp) =>
            Interlocked.Read(ref _lastStopTimestamp) >= timestamp;

        private sealed class ListingOutcome
        {
            public long Started;
            public bool Skipped;
            public Exception? FastRepairCause;
            public Exception? Failure;
        }

        private IReadOnlyList<IPackage> TrackListing(
            ManagerListingKind kind,
            Func<ListingOutcome, IReadOnlyList<IPackage>> listing,
            out bool failed
        )
        {
            ListingOutcome outcome = new() { Started = Stopwatch.GetTimestamp() };
            long start = Stopwatch.GetTimestamp();
            IReadOnlyList<IPackage> packages = listing(outcome);
            if (!outcome.Skipped && outcome.Failure is null && WasStoppedSince(outcome.Started))
            {
                outcome.Failure = new OperationCanceledException(
                    "The listing was stopped from Stop stuck processes."
                );
                packages = [];
                if (kind is ManagerListingKind.Installed)
                    LastInstalledListingFailed = true;
                else if (kind is ManagerListingKind.Updates)
                    LastUpdatesListingFailed = true;
            }
            failed = outcome.Failure is not null;

            if (!outcome.Skipped)
            {
                ManagerHealthStore.RecordListing(
                    Name,
                    kind,
                    Stopwatch.GetElapsedTime(start),
                    packages.Count,
                    outcome.Failure,
                    outcome.FastRepairCause
                );
            }

            return packages;
        }

        public ManagerHealthRecord GetHealthRecord() => ManagerHealthStore.Get(Name);

        public virtual bool ExpectsInstalledPackages => false;

        public IReadOnlyList<ManagerMaintenanceAction> GetMaintenanceActions() =>
            [
                new ManagerMaintenanceAction
                {
                    Id = "reload",
                    Title = CoreTools.Translate("Reload"),
                    Description = CoreTools.Translate(
                        "Look for {0} again and reload its configuration",
                        DisplayName
                    ),
                    RequiresConfirmation = false,
                    ReloadsManager = false,
                    RunAsync = () => Task.Run(Initialize),
                },
                new ManagerMaintenanceAction
                {
                    Id = "stop-stuck-listings",
                    Title = CoreTools.Translate("Stop stuck processes"),
                    Description = CoreTools.Translate(
                        "Kill the processes of any search, package listing or maintenance command {0} is still running",
                        DisplayName
                    ),
                    ReloadsManager = false,
                    RunAsync = () => Task.Run(StopRunningListings),
                },
                .. GetExtraMaintenanceActions()
                    .Where(action => action.AvailableWhenNotReady || IsReady()),
            ];

        protected virtual IEnumerable<ManagerMaintenanceAction> GetExtraMaintenanceActions() => [];

        protected virtual TimeSpan MaintenanceCommandTimeout => TimeSpan.FromMinutes(15);

        protected async Task RunMaintenanceCommandAsync(
            string fileName,
            string arguments,
            bool elevated = false
        )
        {
            int exitCode = await RunMaintenanceProcessAsync(fileName, arguments, elevated);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The maintenance command exited with code {exitCode}."
                );
            }
        }

        protected async Task<int> RunMaintenanceProcessAsync(
            string fileName,
            string arguments,
            bool elevated = false
        )
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new InvalidOperationException(
                    CoreTools.Translate(
                        "{0} is enabled but could not be found on this system.",
                        DisplayName
                    )
                );
            }

            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = elevated,
                    Verb = elevated ? "runas" : string.Empty,
                    CreateNoWindow = !elevated,
                    RedirectStandardOutput = !elevated,
                    RedirectStandardError = !elevated,
                },
            };

            if (!elevated)
            {
                process.StartInfo.StandardOutputEncoding = OutputEncoding;
                process.StartInfo.StandardErrorEncoding = OutputEncoding;
            }

            IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.OtherTask, process);
            List<Process> tracked = [process];
            lock (_activeListingProcesses)
                _activeListingProcesses.Add(tracked);

            try
            {
                process.Start();
                Task<string> stdout = elevated
                    ? Task.FromResult("")
                    : process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = elevated
                    ? Task.FromResult("")
                    : process.StandardError.ReadToEndAsync();

                using var timeout = new CancellationTokenSource(MaintenanceCommandTimeout);
                try
                {
                    await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr)
                        .WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    KillListingProcesses(tracked);
                    logger.Close(-1);
                    throw new TimeoutException(
                        $"The maintenance command did not finish after {MaintenanceCommandTimeout.TotalMinutes} minutes and was stopped."
                    );
                }

                logger.AddToStdOut(await stdout);
                logger.AddToStdErr(await stderr);
            }
            finally
            {
                ForgetListingProcesses(tracked);
            }

            logger.Close(process.ExitCode);
            return process.ExitCode;
        }

        protected string WithCallArguments(string arguments) =>
            $"{Status.ExecutableCallArgs.Trim()} {arguments}".Trim();

        /// <summary>
        /// Returns an array of Package objects that the manager lists for the given query. Depending on the manager, the list may
        /// also include similar results. This method is fail-safe and will return an empty array if an error occurs.
        /// </summary>
        public IReadOnlyList<IPackage> FindPackages(string query) =>
            FindPackages(query, out _);

        public IReadOnlyList<IPackage> FindPackages(string query, out bool failed) =>
            TrackListing(
                ManagerListingKind.Search,
                outcome => _findPackages(query, false, outcome),
                out failed
            );

        private IReadOnlyList<IPackage> _findPackages(
            string query,
            bool SecondAttempt,
            ListingOutcome outcome
        )
        {
            if (!IsReady())
            {
                Logger.Warn($"Manager {Name} is disabled but yet FindPackages was called");
                if (SecondAttempt)
                    outcome.Failure = outcome.FastRepairCause;
                else
                    outcome.Skipped = true;
                return [];
            }
            try
            {
                var packages = RunListingTaskWithTimeout(
                    () => FindPackages_UnSafe(query),
                    "_findPackages"
                );
                Logger.Info(
                    $"Found {packages.Count} available packages from {Name} with the query {query}"
                );
                return packages;
            }
            catch (Exception e)
            {
                while (e is AggregateException)
                    e = e.InnerException ?? new InvalidOperationException("How did we get here?");

                if (
                    !SecondAttempt
                    && !WasStoppedSince(outcome.Started)
                    && (RetryListingTasksOnTimeout || e is not TimeoutException)
                )
                {
                    Logger.Warn(
                        $"Manager {DisplayName} failed to find packages with exception {e.GetType().Name}: {e.Message}"
                    );
                    Logger.Warn(
                        $"Since this was the first attempt, {Name}.AttemptFastRepair() will be called and the procedure will be restarted"
                    );
                    outcome.FastRepairCause = e;
                    AttemptFastRepair();
                    return _findPackages(query, true, outcome);
                }

                Logger.Error("Error finding packages on manager " + Name + " with query " + query);
                Logger.Error(e);
                outcome.Failure = e;
                return [];
            }
        }

        /// <summary>
        /// Returns an array of UpgradablePackage objects that represent the available updates reported by the manager.
        /// This method is fail-safe and will return an empty array if an error occurs.
        /// </summary>
        public bool LastUpdatesListingFailed { get; private set; }

        public IReadOnlyList<IPackage> GetAvailableUpdates()
        {
            LastUpdatesListingFailed = false;
            return TrackListing(
                ManagerListingKind.Updates,
                outcome => _getAvailableUpdates(false, outcome),
                out _
            );
        }

        private IReadOnlyList<IPackage> _getAvailableUpdates(
            bool SecondAttempt,
            ListingOutcome outcome
        )
        {
            if (!IsReady())
            {
                Logger.Warn($"Manager {Name} is disabled but yet GetAvailableUpdates was called");
                if (SecondAttempt)
                {
                    outcome.Failure = outcome.FastRepairCause;
                    LastUpdatesListingFailed = true;
                }
                else
                    outcome.Skipped = true;
                return [];
            }
            try
            {
                if (!SecondAttempt)
                {
                    RefreshPackageIndexesSafely();
                }

                var packages = RunListingTaskWithTimeout(
                    GetAvailableUpdates_UnSafe,
                    "_getAvailableUpdates"
                );
                Logger.Info($"Found {packages.Count} available updates from {Name}");
                return OfferUpdatesWithinSkippedMajors(packages);
            }
            catch (Exception e)
            {
                while (e is AggregateException)
                    e = e.InnerException ?? new InvalidOperationException("How did we get here?");

                if (
                    !SecondAttempt
                    && !WasStoppedSince(outcome.Started)
                    && (RetryListingTasksOnTimeout || e is not TimeoutException)
                )
                {
                    Logger.Warn(
                        $"Manager {DisplayName} failed to list available updates with exception {e.GetType().Name}: {e.Message}"
                    );
                    Logger.Warn(
                        $"Since this was the first attempt, {Name}.AttemptFastRepair() will be called and the procedure will be restarted"
                    );
                    outcome.FastRepairCause = e;
                    AttemptFastRepair();
                    return _getAvailableUpdates(true, outcome);
                }

                Logger.Error("Error finding updates on manager " + Name);
                Logger.Error(e);
                LastUpdatesListingFailed = true;
                outcome.Failure = e;
                return [];
            }
        }

        /// <summary>
        /// Returns an array of Package objects that represent the installed reported by the manager.
        /// This method is fail-safe and will return an empty array if an error occurs.
        /// </summary>
        public bool LastInstalledListingFailed { get; private set; }

        private long _installedPackagesGeneration;

        public long InstalledPackagesGeneration => Volatile.Read(ref _installedPackagesGeneration);

        public void InvalidateInstalledPackages() =>
            Interlocked.Increment(ref _installedPackagesGeneration);

        public IReadOnlyList<IPackage> GetInstalledPackages()
        {
            LastInstalledListingFailed = false;
            return TrackListing(
                ManagerListingKind.Installed,
                outcome => _getInstalledPackages(false, outcome),
                out _
            );
        }

        private IReadOnlyList<IPackage> _getInstalledPackages(
            bool SecondAttempt,
            ListingOutcome outcome
        )
        {
            if (!IsReady())
            {
                Logger.Warn($"Manager {Name} is disabled but yet GetInstalledPackages was called");
                if (SecondAttempt)
                {
                    outcome.Failure = outcome.FastRepairCause;
                    LastInstalledListingFailed = true;
                }
                else
                    outcome.Skipped = true;
                return [];
            }
            try
            {
                var packages = RunListingTaskWithTimeout(
                    GetInstalledPackages_UnSafe,
                    "_getInstalledPackages"
                );
                Logger.Info($"Found {packages.Count} installed packages from {Name}");
                return packages;
            }
            catch (Exception e)
            {
                while (e is AggregateException)
                    e = e.InnerException ?? new InvalidOperationException("How did we get here?");

                if (
                    !SecondAttempt
                    && !WasStoppedSince(outcome.Started)
                    && (RetryListingTasksOnTimeout || e is not TimeoutException)
                )
                {
                    Logger.Warn(
                        $"Manager {DisplayName} failed to list installed packages with exception {e.GetType().Name}: {e.Message}"
                    );
                    Logger.Warn(
                        $"Since this was the first attempt, {Name}.AttemptFastRepair() will be called and the procedure will be restarted"
                    );
                    outcome.FastRepairCause = e;
                    AttemptFastRepair();
                    return _getInstalledPackages(true, outcome);
                }

                Logger.Error("Error finding installed packages on manager " + Name);
                Logger.Error(e);
                LastInstalledListingFailed = true;
                outcome.Failure = e;
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

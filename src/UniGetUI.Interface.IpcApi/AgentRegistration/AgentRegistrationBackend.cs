using Devolutions.Now.Policy.Api;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.AgentBroker;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Interface.AgentRegistration;

/// <summary>
/// The local state remote registration requests may use. Reads only copy finished state, and the
/// only way to act is <see cref="SubmitGrantAsync"/>, which hands an Agent-authorized request to
/// the Broker; nothing here executes a package operation locally.
/// </summary>
internal interface IAgentRegistrationBackend
{
    /// <summary>The last completed scans, or null when inventory is unavailable.</summary>
    RemoteInventorySnapshot? TryGetInventorySnapshot();

    /// <summary>The public id of the UniGetUI manager the Broker knows by this name, or null.</summary>
    string? FindManagerId(ManagerName manager);

    RemoteSourceResolution ResolveSource(string managerId, string sourceName);

    /// <summary>
    /// Whether the current installed list is complete and holds exactly one installation with this
    /// manager and package ID, and that installation reports exactly this scope.
    /// </summary>
    bool IsOnlyInstalledRow(string managerId, string packageId, string reportedScope);

    Task<RemoteGrantDelivery> SubmitGrantAsync(
        RemoteGrantPlan plan,
        string grant,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Reloads the installed and available-update lists in a scan that starts after this call,
    /// waiting out a load already running, and returns whether both published a new completed scan
    /// without failures. Loads started here only list: see <see cref="PackageListingScope"/>.
    /// </summary>
    Task<bool> RescanInventoryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts a listing-only reload of the installed packages, unless a load or a remote rescan is
    /// already under way; returns whether it started one.
    /// </summary>
    bool RefreshInstalledInventory();
}

internal sealed class IpcRemoteInventoryBackend : IAgentRegistrationBackend
{
    private readonly IRemoteGrantSubmitter _submitter;
    private readonly Func<CancellationToken, Task> _managersLoaded;

    public IpcRemoteInventoryBackend(
        IRemoteGrantSubmitter? submitter = null,
        Func<CancellationToken, Task>? managersLoaded = null
    )
    {
        _submitter = submitter ?? new BrokerRemoteGrantSubmitter();
        _managersLoaded = managersLoaded ?? WaitForManagersLoadedAsync;
    }

    public RemoteInventorySnapshot? TryGetInventorySnapshot() =>
        IpcRemoteInventory.TryGetInventorySnapshot();

    public string? FindManagerId(ManagerName manager) =>
        PEInterface.Managers
            .Where(candidate => RemoteGrantRequestBuilder.IsManager(candidate.Name, manager))
            .Select(IpcManagerSettingsApi.GetPublicManagerId)
            .SingleOrDefault();

    public RemoteSourceResolution ResolveSource(string managerId, string sourceName) =>
        IpcRemoteInventory.ResolveSource(managerId, sourceName);

    public bool IsOnlyInstalledRow(string managerId, string packageId, string reportedScope)
    {
        var loader = InstalledPackagesLoader.Instance;
        if (loader is null || !loader.IsLoaded || loader.IsLoading)
        {
            return false;
        }

        long loadGeneration = loader.LoadGeneration;
        var scan = loader.LastCompletedScan;
        if (scan is null || loader.LastLoadReportedFailures
            || !scan.SucceededManagers.Any(manager =>
                IpcManagerSettingsApi.GetPublicManagerId(manager) == managerId))
        {
            return false;
        }

        var packages = loader.Packages;
        // A reload that finishes while copying also makes the copied list unreliable.
        if (!loader.IsLoaded || loader.IsLoading || loader.LastLoadReportedFailures
            || loadGeneration != loader.LoadGeneration
            || !ReferenceEquals(scan, loader.LastCompletedScan))
        {
            return false;
        }

        return IpcRemoteInventory.IsOnlyInstalledRow(packages, managerId, packageId, reportedScope);
    }

    public Task<RemoteGrantDelivery> SubmitGrantAsync(
        RemoteGrantPlan plan,
        string grant,
        CancellationToken cancellationToken
    ) => _submitter.SubmitAsync(plan, grant, cancellationToken);

    public async Task<bool> RescanInventoryAsync(CancellationToken cancellationToken)
    {
        // Before managers load, unloaded ones would be left out of both the scan and the
        // completeness check, so a partial inventory would pass as complete.
        await _managersLoaded(cancellationToken).ConfigureAwait(false);
        if (InstalledPackagesLoader.Instance is not { } installed
            || UpgradablePackagesLoader.Instance is not { } updates)
        {
            return false;
        }

        bool rescanned = await RemoteRescan.Shared
            .RequestAsync(installed, updates, cancellationToken)
            .ConfigureAwait(false);
        // Complete means every expected manager succeeded in both the installed and the update scan.
        return rescanned && TryGetInventorySnapshot() is { UpdateCompletedUtc: not null };
    }

    public bool RefreshInstalledInventory() =>
        PEInterface.AreManagersLoaded
        && InstalledPackagesLoader.Instance is { } installed
        && RemoteRescan.Shared.TryStartInstalledReload(installed);

    private static async Task WaitForManagersLoadedAsync(CancellationToken cancellationToken)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onLoaded = (_, _) => loaded.TrySetResult();
        PEInterface.ManagersLoaded += onLoaded;
        try
        {
            if (PEInterface.AreManagersLoaded)
            {
                return;
            }

            await loaded.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            PEInterface.ManagersLoaded -= onLoaded;
        }
    }
}

/// <summary>
/// Remote rescans of both inventory lists. The scan that answers a request always starts after the
/// request arrived: a load already running is waited out, never joined, because it may have begun
/// before the change the administrator wants to see. Requests that arrive while a scan is queued
/// share it, so at most one is queued.
/// </summary>
internal sealed class RemoteRescan
{
    public static RemoteRescan Shared { get; } = new();

    private readonly object _gate = new();
    private Task<bool>? _queued;

    public Task<bool> RequestAsync(
        AbstractPackageLoader installed,
        AbstractPackageLoader updates,
        CancellationToken cancellationToken
    )
    {
        Task<bool> scan;
        lock (_gate)
        {
            if (_queued is null)
            {
                // Read inside the lock so a request sharing this scan arrived before it starts.
                long installedArrival = installed.LoadGeneration;
                long updatesArrival = updates.LoadGeneration;
                _queued = Task.Run(() => RunAsync(installed, updates, installedArrival, updatesArrival));
            }

            scan = _queued;
        }

        return scan.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// The periodic installed-list reload while registered. It is skipped while a load is running
    /// or a rescan is queued, since either already brings the list up to date.
    /// </summary>
    public bool TryStartInstalledReload(AbstractPackageLoader installed)
    {
        lock (_gate)
        {
            if (_queued is not null || installed.IsLoading)
            {
                Logger.Debug("Periodic inventory reload skipped: a package load is already running.");
                return false;
            }

            using (PackageListingScope.EnterListingOnly())
            {
                _ = installed.ReloadPackages();
            }

            return true;
        }
    }

    private async Task<bool> RunAsync(
        AbstractPackageLoader installed,
        AbstractPackageLoader updates,
        long installedArrival,
        long updatesArrival
    )
    {
        bool dequeued = false;
        try
        {
            while (installed.IsLoading || updates.IsLoading)
            {
                await Task.WhenAll(installed.WaitForCurrentLoadAsync(), updates.WaitForCurrentLoadAsync())
                    .ConfigureAwait(false);
            }

            Task both;
            lock (_gate)
            {
                // From here a new request queues the next scan, which waits for this one.
                _queued = null;
                dequeued = true;
                using (PackageListingScope.EnterListingOnly())
                {
                    // The installed load starts first: the update load waits for each manager's
                    // installed listing only while that one is running.
                    both = Task.WhenAll(StartOrJoinAsync(installed), StartOrJoinAsync(updates));
                }
            }

            await both.ConfigureAwait(false);
            return StartedAfter(installed, installedArrival) && StartedAfter(updates, updatesArrival);
        }
        finally
        {
            // A failure before the scan started must not leave later requests sharing it.
            if (!dequeued)
            {
                lock (_gate)
                    _queued = null;
            }
        }
    }

    private static async Task StartOrJoinAsync(AbstractPackageLoader loader)
    {
        // A load that started after the wait above also started after the request, so joining it is fine.
        if (!loader.IsLoading)
        {
            await loader.ReloadPackages().ConfigureAwait(false);
        }

        await loader.WaitForCurrentLoadAsync().ConfigureAwait(false);
    }

    private static bool StartedAfter(AbstractPackageLoader loader, long arrival) =>
        loader.LastCompletedScan is { } scan && scan.StartGeneration > arrival;
}
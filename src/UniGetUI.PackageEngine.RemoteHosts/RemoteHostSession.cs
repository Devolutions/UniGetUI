using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.RemoteHosts;

public sealed class RemoteHostSession
{
    public RemoteHost Host { get; }
    public bool IsLoading { get; private set; }
    public string? Error { get; private set; }
    public string? HostDescription { get; private set; }
    public bool? CanElevate { get; private set; }
    public string? SystemPackageManager { get; private set; }
    public RemoteBackendKind Backend { get; private set; }
    public RemoteHostOsKind OsKind { get; private set; }
    public IReadOnlyList<IPackage> Installed { get; private set; } = [];
    public IReadOnlyList<IPackage> Updates { get; private set; } = [];
    public IReadOnlyList<IPackage> Discover { get; private set; } = [];

    private readonly RemoteSshClient _client;

    public RemoteHostSession(RemoteHost host, RemoteSshClient? client = null)
    {
        Host = host;
        _client = client ?? RemoteSshClient.ForHost(host);
    }

    public bool CanMutate(IPackage package)
    {
        if (package.Manager is RemotePackageManager remote && remote.IsSystemPackageManager)
            return CanElevate == true;
        return true;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        Error = null;
        try
        {
            RemoteControlResponse response = await _client.InventoryAsync(Host, cancellationToken)
                .ConfigureAwait(false);
            Apply(response);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Logger.Error($"Remote inventory failed for {Host.Destination}");
            Logger.Error(ex);
            Installed = [];
            Updates = [];
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        RemoteControlResponse response = await _client.ProbeAsync(Host, cancellationToken)
            .ConfigureAwait(false);
        Apply(response);
        return response.HostDescription ?? Host.DisplayName;
    }

    public async Task SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        Error = null;
        try
        {
            RemoteControlResponse response = await _client.SearchAsync(Host, query, cancellationToken)
                .ConfigureAwait(false);
            Discover = response.Packages
                .Select(dto => RemoteInventoryPackageFactory.Create(dto, Host.Id))
                .ToList();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Discover = [];
            Logger.Error($"Remote search failed for {Host.Destination}");
            Logger.Error(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Apply(RemoteControlResponse response)
    {
        Backend = response.BackendKind;
        HostDescription = response.HostDescription;
        CanElevate = response.CanElevate;
        SystemPackageManager = response.SystemPackageManager;
        OsKind = response.Os?.ToLowerInvariant() switch
        {
            "linux" => RemoteHostOsKind.Linux,
            "darwin" or "macos" => RemoteHostOsKind.MacOs,
            "windows" => RemoteHostOsKind.Windows,
            _ => Backend == RemoteBackendKind.LinuxAgentless ? RemoteHostOsKind.Linux : RemoteHostOsKind.Unknown,
        };

        List<IPackage> installed = [];
        List<IPackage> updates = [];
        foreach (RemoteInventoryPackageDto dto in response.Packages)
        {
            IPackage package = RemoteInventoryPackageFactory.Create(dto, Host.Id);
            installed.Add(package);
            if (package.IsUpgradable)
                updates.Add(package);
        }

        Installed = installed;
        Updates = updates;
        if (response.Errors.Count > 0 && string.IsNullOrEmpty(Error))
            Error = string.Join('\n', response.Errors);
    }
}

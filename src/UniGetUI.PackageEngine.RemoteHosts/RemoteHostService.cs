using System.Collections.ObjectModel;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.RemoteHosts;

public sealed class RemoteHostPickerItem : IEquatable<RemoteHostPickerItem>
{
    public Guid? HostId { get; init; }
    public string DisplayName { get; init; } = "";
    public bool IsLocal => HostId is null;

    public override string ToString() => DisplayName;

    public bool Equals(RemoteHostPickerItem? other) => other is not null && HostId == other.HostId;
    public override bool Equals(object? obj) => Equals(obj as RemoteHostPickerItem);
    public override int GetHashCode() => HostId?.GetHashCode() ?? 0;
}

public sealed class RemoteHostService
{
    public static RemoteHostService Instance { get; } = new();

    private readonly Dictionary<Guid, RemoteHostSession> _sessions = [];
    private readonly List<RemoteHost> _wslHosts = [];

    public ObservableCollection<RemoteHost> Hosts { get; } = [];
    public RemoteHost? SelectedHost { get; private set; }
    public RemoteHostSession? ActiveSession =>
        SelectedHost is null ? null : GetSession(SelectedHost);

    public event EventHandler? HostsChanged;
    public event EventHandler? SelectedHostChanged;

    private RemoteHostService()
    {
        ReloadFromStore();
    }

    public void ReloadFromStore()
    {
        Hosts.Clear();
        foreach (RemoteHost host in RemoteHostStore.Load())
            Hosts.Add(host);

        _wslHosts.Clear();
        _wslHosts.AddRange(WslDistroCatalog.GetEnabledHosts());

        if (SelectedHost is not null && !TryGetHost(SelectedHost.Id, out _))
            SelectHost(null);

        HostsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<RemoteHostPickerItem> GetPickerItems(string thisPcLabel)
    {
        List<RemoteHostPickerItem> items =
        [
            new() { HostId = null, DisplayName = thisPcLabel },
        ];
        items.AddRange(Hosts.Select(host => new RemoteHostPickerItem
        {
            HostId = host.Id,
            DisplayName = host.DisplayName,
        }));
        items.AddRange(_wslHosts.Select(host => new RemoteHostPickerItem
        {
            HostId = host.Id,
            DisplayName = host.DisplayName,
        }));
        return items;
    }

    public void SelectHost(Guid? hostId)
    {
        RemoteHost? next = null;
        if (hostId is Guid id && TryGetHost(id, out RemoteHost found))
            next = found;
        if (ReferenceEquals(SelectedHost, next) || (SelectedHost?.Id == next?.Id))
            return;
        SelectedHost = next;
        SelectedHostChanged?.Invoke(this, EventArgs.Empty);
    }

    public RemoteHostSession GetSession(RemoteHost host)
    {
        if (_sessions.TryGetValue(host.Id, out RemoteHostSession? existing)
            && existing.Host.Destination == host.Destination
            && existing.Host.Kind == host.Kind)
        {
            return existing;
        }

        var session = new RemoteHostSession(host, RemoteSshClient.ForHost(host));
        _sessions[host.Id] = session;
        return session;
    }

    public RemoteHost SaveHost(RemoteHost host)
    {
        if (host.Kind == RemoteHostKind.Wsl)
            throw new RemoteHostException(RemoteHostErrorKind.InvalidDestination);

        RemoteHost saved = RemoteHostStore.AddOrUpdate(host);
        ReloadFromStore();
        return Hosts.First(item => item.Id == saved.Id);
    }

    public void RemoveHost(Guid id)
    {
        RemoteHostStore.Remove(id);
        _sessions.Remove(id);
        ReloadFromStore();
    }

    public bool TryGetHost(Guid id, out RemoteHost host)
    {
        host = Hosts.FirstOrDefault(item => item.Id == id)
            ?? _wslHosts.FirstOrDefault(item => item.Id == id)!;
        return host is not null;
    }

    public bool CanMutate(IPackage package)
    {
        if (package.RemoteHostId is not Guid id || !TryGetHost(id, out RemoteHost host))
            return true;
        return GetSession(host).CanMutate(package);
    }
}

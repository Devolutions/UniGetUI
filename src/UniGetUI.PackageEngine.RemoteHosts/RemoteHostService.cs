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
    private readonly RemoteSshClient _client = new();

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

        if (SelectedHost is not null && Hosts.All(host => host.Id != SelectedHost.Id))
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
        return items;
    }

    public void SelectHost(Guid? hostId)
    {
        RemoteHost? next = hostId is null ? null : Hosts.FirstOrDefault(host => host.Id == hostId);
        if (ReferenceEquals(SelectedHost, next) || (SelectedHost?.Id == next?.Id))
            return;
        SelectedHost = next;
        SelectedHostChanged?.Invoke(this, EventArgs.Empty);
    }

    public RemoteHostSession GetSession(RemoteHost host)
    {
        if (_sessions.TryGetValue(host.Id, out RemoteHostSession? existing)
            && existing.Host.Destination == host.Destination)
        {
            return existing;
        }

        var session = new RemoteHostSession(host, _client);
        _sessions[host.Id] = session;
        return session;
    }

    public RemoteHost SaveHost(RemoteHost host)
    {
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
        host = Hosts.FirstOrDefault(item => item.Id == id)!;
        return host is not null;
    }

    public bool CanMutate(IPackage package)
    {
        if (package.RemoteHostId is not Guid id || !TryGetHost(id, out RemoteHost host))
            return true;
        return GetSession(host).CanMutate(package);
    }
}

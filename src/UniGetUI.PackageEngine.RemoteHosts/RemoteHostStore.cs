using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.PackageEngine.RemoteHosts;

public static class RemoteHostStore
{
    public static IReadOnlyList<RemoteHost> Load()
    {
        try
        {
            string json = Settings.GetValue(Settings.K.RemoteHosts);
            if (string.IsNullOrWhiteSpace(json))
                return [];
            return RemoteHostsJson.DeserializeHosts(json);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not load remote hosts");
            Logger.Error(ex);
            return [];
        }
    }

    public static void Save(IReadOnlyList<RemoteHost> hosts)
    {
        try
        {
            if (hosts.Count == 0)
                Settings.SetValue(Settings.K.RemoteHosts, "");
            else
                Settings.SetValue(Settings.K.RemoteHosts, RemoteHostsJson.SerializeHosts(hosts));
        }
        catch (Exception ex)
        {
            Logger.Error("Could not save remote hosts");
            Logger.Error(ex);
        }
    }

    public static RemoteHost AddOrUpdate(RemoteHost host)
    {
        List<RemoteHost> hosts = [.. Load()];
        if (hosts.Any(existing =>
            existing.Id != host.Id
            && existing.Destination.Equals(host.Destination, StringComparison.OrdinalIgnoreCase)))
        {
            throw new RemoteHostException(RemoteHostErrorKind.DuplicateDestination);
        }

        int index = hosts.FindIndex(existing => existing.Id == host.Id);
        if (index >= 0)
            hosts[index] = host;
        else
            hosts.Add(host);

        Save(hosts);
        return host;
    }

    public static void Remove(Guid id)
    {
        List<RemoteHost> hosts = [.. Load().Where(host => host.Id != id)];
        Save(hosts);
    }
}

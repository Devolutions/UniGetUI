using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.AgentBroker;

public static class BrokerClientFactory
{
    public static BrokerClient Create(
        Elevation requestedElevation,
        IBrokerTransport? transport = null)
    {
        return new BrokerClient(
            new BrokerClientOptions
            {
                Transport = transport,
                RequestedElevation = requestedElevation,
                EffectiveUser = GetEffectiveUser(),
                ClientExecutablePath = Environment.ProcessPath,
                ClientVersion = CoreData.VersionName,
            })
        {
            Trace = message => Logger.Info($"[AgentBroker] {message}"),
        };
    }

    private static string GetEffectiveUser()
    {
        return string.IsNullOrWhiteSpace(Environment.UserDomainName)
            ? Environment.UserName
            : $"{Environment.UserDomainName}\\{Environment.UserName}";
    }
}

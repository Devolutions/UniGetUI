using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

namespace UniGetUI.PackageEngine.AgentBroker;

public enum BrokerUnreachableCause
{
    Unknown,
    NotListening,
    ConnectionClosed,
}

public static class BrokerReachability
{
    private const int DiagnosticResponseBodyBytes = 65536;

    public static bool BrokerPipeExists()
    {
        if (!OperatingSystem.IsWindows())
            return true;

        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\", BrokerApi.DefaultPipeName).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static BrokerUnreachableCause Diagnose(BrokerClientException exception, Func<bool> brokerPipeExists)
    {
        if (!brokerPipeExists())
            return BrokerUnreachableCause.NotListening;

        return exception.InnerException is BrokerConnectionClosedException
            ? BrokerUnreachableCause.ConnectionClosed
            : BrokerUnreachableCause.Unknown;
    }

    public static Task<BrokerUnreachableCause> DiagnoseAsync(CancellationToken cancellationToken) =>
        OperatingSystem.IsWindows()
            ? DiagnoseAsync(BrokerPipeExists, CreateDiagnosticClient, cancellationToken)
            : Task.FromResult(BrokerUnreachableCause.Unknown);

    internal static async Task<BrokerUnreachableCause> DiagnoseAsync(
        Func<bool> brokerPipeExists,
        Func<BrokerClient> clientFactory,
        CancellationToken cancellationToken)
    {
        if (!brokerPipeExists())
            return BrokerUnreachableCause.NotListening;

        try
        {
            using BrokerClient client = clientFactory();
            await client.GetHealth(cancellationToken).ConfigureAwait(false);
            return BrokerUnreachableCause.Unknown;
        }
        catch (BrokerClientException ex)
        {
            return Diagnose(ex, brokerPipeExists);
        }
    }

    private static BrokerClient CreateDiagnosticClient() =>
        BrokerClientFactory.Create(
            Elevation.Standard,
            new BoundedNamedPipeBrokerTransport(DiagnosticResponseBodyBytes));
}

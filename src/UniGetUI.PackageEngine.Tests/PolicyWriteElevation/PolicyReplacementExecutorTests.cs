#if WINDOWS
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using UniGetUI.AgentPolicy.ElevatedHelper;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

public class PolicyReplacementExecutorTests
{
    [Fact]
    public void ElevatedBrokerTransportLimitsImpersonationAndAuthenticatesBeforeWriting()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UniGetUI.AgentPolicy.ElevatedHelper",
            "AuthenticatedBrokerTransport.cs"));

        Assert.Contains("TokenImpersonationLevel.Identification", source);
        int authenticate = source.IndexOf(
            "AuthenticatedBrokerServer.Authenticate",
            StringComparison.Ordinal);
        int write = source.IndexOf("WriteRequestAsync", StringComparison.Ordinal);
        Assert.True(authenticate >= 0 && write > authenticate);
        Assert.Contains("GetNamedPipeServerProcessId", source);
        Assert.Contains("VerifyExecutable(imagePath)", source);
        Assert.Contains("PolicyElevationSignerBinding.Bind", source);
    }

    [Fact]
    public void UnreadableBrokerError_IsUnknownBecausePersistenceCannotBeRuledOut()
    {
        var exception = new BrokerClientException(
            BrokerClientErrorKind.BrokerError,
            "HTTP 500 body could not be parsed",
            statusCode: 500);

        PolicyElevationDisposition disposition =
            PolicyReplacementExecutor.GetFailureDisposition(exception);

        Assert.Equal(PolicyElevationDisposition.Unknown, disposition);
    }

    [Fact]
    public void StructuredBrokerError_IsDefinitiveRejection()
    {
        var exception = new BrokerClientException(
            BrokerClientErrorKind.BrokerError,
            "structured rejection",
            statusCode: 403,
            brokerError: new ErrorResponse
            {
                Code = ErrorCode.Forbidden,
                Message = "forbidden",
            });

        PolicyElevationDisposition disposition =
            PolicyReplacementExecutor.GetFailureDisposition(exception);

        Assert.Equal(PolicyElevationDisposition.Rejected, disposition);
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "UniGetUI.Windows.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
#endif

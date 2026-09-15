#if WINDOWS
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using UniGetUI.AgentPolicy.ElevatedHelper;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

public class PolicyReplacementExecutorTests
{
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
}
#endif

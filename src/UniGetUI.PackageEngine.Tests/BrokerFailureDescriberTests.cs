using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.AgentBroker;

namespace UniGetUI.PackageEngine.Tests;

public class BrokerFailureDescriberTests
{
    private static BrokerClientException BrokerError(int statusCode, ErrorCode code, string message = "", params string[] details) =>
        new(
            BrokerClientErrorKind.BrokerError,
            $"Broker returned HTTP {statusCode} ({code})",
            "/v1/package-operations/execute",
            statusCode,
            new ErrorResponse
            {
                Code = code,
                Message = message,
                Details = [.. details.Select(detail => new ErrorDetail { Message = detail })],
            });

    [Fact]
    public void DescribeDenial_IncludesReasonAndRule()
    {
        var description = BrokerFailureDescriber.DescribeDenial(new DecisionInfo
        {
            Decision = Decision.Deny,
            RuleId = "block-beta",
            Reason = "Pre-release versions are blocked",
        });

        Assert.Equal(CoreTools.Translate("Operation denied by policy"), description.Title);
        Assert.Contains("block-beta", description.Message);
        Assert.Contains("Pre-release versions are blocked", description.Message);
    }

    [Fact]
    public void DescribeDenial_WithoutDetailsStillExplainsThePolicy()
    {
        var description = BrokerFailureDescriber.DescribeDenial(new DecisionInfo { Decision = Decision.Deny });

        Assert.Contains(CoreTools.Translate("Your organization's package policy does not allow this operation."), description.Message);
        Assert.DoesNotContain(CoreTools.Translate("Policy rule: {0}", ""), description.Message);
    }

    [Fact]
    public void Describe_ValidationFailureShowsTheBrokerMessage()
    {
        var description = BrokerFailureDescriber.Describe(
            BrokerError(400, ErrorCode.ValidationFailed, "custom install location is not a plain local drive path", "path detail"));

        Assert.Equal(CoreTools.Translate("The package broker rejected the request"), description.Title);
        Assert.Contains("custom install location is not a plain local drive path", description.Message);
        Assert.Contains("path detail", description.Message);
    }

    [Fact]
    public void Describe_BusyBrokerAfterRetries()
    {
        var description = BrokerFailureDescriber.Describe(
            BrokerError(503, ErrorCode.BrokerPaused, "package broker is busy; retry later"));

        Assert.Equal(CoreTools.Translate("The Devolutions Agent is busy"), description.Title);
    }

    [Fact]
    public void Describe_UnstructuredServiceUnavailableIsBusy()
    {
        var description = BrokerFailureDescriber.Describe(new BrokerClientException(
            BrokerClientErrorKind.BrokerError, "Broker returned HTTP 503", "/v1/capabilities", 503));

        Assert.Equal(CoreTools.Translate("The Devolutions Agent is busy"), description.Title);
    }

    [Fact]
    public void Describe_PausedBrokerWithoutPolicy()
    {
        var description = BrokerFailureDescriber.Describe(
            BrokerError(503, ErrorCode.BrokerPaused, "active policy is unavailable"));

        Assert.Equal(CoreTools.Translate("Package operations are paused"), description.Title);
    }

    [Theory]
    [InlineData(401, ErrorCode.Unauthorized, "UniGetUI is not authorized to use the Devolutions Agent")]
    [InlineData(401, ErrorCode.Unauthenticated, "UniGetUI is not authorized to use the Devolutions Agent")]
    [InlineData(403, ErrorCode.AdministratorRequired, "Administrator rights are required")]
    [InlineData(403, ErrorCode.Forbidden, "Administrator rights are required")]
    public void Describe_AuthorizationFailures(int statusCode, ErrorCode code, string expectedTitle)
    {
        var exception = BrokerError(statusCode, code);

        Assert.NotNull(BrokerFailureDescriber.DescribeAccessFailure(exception));
        Assert.Equal(CoreTools.Translate(expectedTitle), BrokerFailureDescriber.Describe(exception).Title);
    }

    [Fact]
    public void DescribeAccessFailure_IgnoresOtherFailures()
    {
        Assert.Null(BrokerFailureDescriber.DescribeAccessFailure(BrokerError(400, ErrorCode.ValidationFailed)));
        Assert.Null(BrokerFailureDescriber.DescribeAccessFailure(
            new BrokerClientException(BrokerClientErrorKind.BrokerUnavailable, "pipe not found")));
    }

    [Theory]
    [InlineData(BrokerClientErrorKind.UnsupportedCapability, "Operation unsupported by broker")]
    [InlineData(BrokerClientErrorKind.Timeout, "Broker communication error")]
    [InlineData(BrokerClientErrorKind.RequestTooLarge, "The request is too large")]
    [InlineData(BrokerClientErrorKind.BrokerUnavailable, "Agent broker unavailable")]
    [InlineData(BrokerClientErrorKind.InvalidResponse, "Broker communication error")]
    public void Describe_ClientFailures(BrokerClientErrorKind kind, string expectedTitle)
    {
        var description = BrokerFailureDescriber.Describe(new BrokerClientException(kind, "client failure"));

        Assert.Equal(CoreTools.Translate(expectedTitle), description.Title);
        Assert.False(string.IsNullOrWhiteSpace(description.Message));
    }

    [Fact]
    public void DescribeValidation_ListsEveryIssue()
    {
        var description = BrokerFailureDescriber.DescribeValidation(["first issue", "second issue"]);

        Assert.Contains("first issue", description.Message);
        Assert.Contains("second issue", description.Message);
    }
}

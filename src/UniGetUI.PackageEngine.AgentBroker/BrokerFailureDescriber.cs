using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.AgentBroker;

/// <summary>A localized title and explanation for a package operation the broker did not run.</summary>
public sealed record BrokerFailureDescription(string Title, string Message);

/// <summary>
/// Turns package broker outcomes (policy denials, structured errors and client failures)
/// into specific, localized explanations for the operation failure dialog.
/// </summary>
public static class BrokerFailureDescriber
{
    /// <summary>Explains a request that the organization's policy denied.</summary>
    public static BrokerFailureDescription DescribeDenial(DecisionInfo decision)
    {
        List<string> lines =
        [
            CoreTools.Translate("Your organization's package policy does not allow this operation."),
        ];

        if (!string.IsNullOrWhiteSpace(decision.Reason))
            lines.Add(CoreTools.Translate("Reason: {0}", decision.Reason.Trim()));

        if (!string.IsNullOrWhiteSpace(decision.RuleId))
            lines.Add(CoreTools.Translate("Policy rule: {0}", decision.RuleId.Trim()));

        lines.Add(CoreTools.Translate("Contact your administrator if you need this operation to be allowed."));

        return new(CoreTools.Translate("Operation denied by policy"), string.Join(Environment.NewLine, lines));
    }

    /// <summary>Explains a request that UniGetUI did not send because the broker would reject it.</summary>
    public static BrokerFailureDescription DescribeValidation(IReadOnlyList<string> issues) =>
        new(
            CoreTools.Translate("The package broker cannot accept this request"),
            string.Join(
                Environment.NewLine,
                [
                    CoreTools.Translate("The Devolutions Agent would reject the following options of this operation:"),
                    .. issues.Select(issue => "• " + issue),
                    CoreTools.Translate("Change the installation options of this package, then try again."),
                ]));

    /// <summary>
    /// Explains why the broker did not accept the client's identity. Returns null when the
    /// failure is not an authentication or authorization failure.
    /// </summary>
    public static BrokerFailureDescription? DescribeAccessFailure(BrokerClientException exception)
    {
        ErrorCode? code = exception.BrokerError?.Code;
        if (exception.StatusCode is 401 || code is ErrorCode.Unauthorized or ErrorCode.Unauthenticated)
        {
            return new(
                CoreTools.Translate("UniGetUI is not authorized to use the Devolutions Agent"),
                CoreTools.Translate(
                    "The Devolutions Agent only accepts requests from signed, unmodified copies of UniGetUI. If you are running a development or self-built version, install an official release of UniGetUI and try again."));
        }

        if (exception.StatusCode is 403 || code is ErrorCode.Forbidden or ErrorCode.AdministratorRequired)
        {
            return new(
                CoreTools.Translate("Administrator rights are required"),
                CoreTools.Translate(
                    "The Devolutions Agent only accepts this request from an administrator running with elevated rights."));
        }

        return null;
    }

    /// <summary>Explains a failed broker request.</summary>
    public static BrokerFailureDescription Describe(BrokerClientException exception)
    {
        if (DescribeAccessFailure(exception) is { } accessFailure)
            return accessFailure;

        ErrorResponse? error = exception.BrokerError;
        string? brokerMessage = string.IsNullOrWhiteSpace(error?.Message) ? null : error.Message.Trim();

        // The broker answers both "busy" (connection limit, retried by the client) and "no valid
        // policy" with BrokerPaused over HTTP 503; only the busy reply says so in its message.
        bool busy = error is null
            ? exception.StatusCode is 503
            : error.Code is ErrorCode.BrokerPaused
                && brokerMessage?.Contains("busy", StringComparison.OrdinalIgnoreCase) is true;
        if (busy)
        {
            return new(
                CoreTools.Translate("The Devolutions Agent is busy"),
                CoreTools.Translate(
                    "The Devolutions Agent is handling too many requests right now. Wait a moment, then try again."));
        }

        switch (error?.Code)
        {
            case ErrorCode.BrokerPaused:
                return new(
                    CoreTools.Translate("Package operations are paused"),
                    WithDetails(
                        CoreTools.Translate(
                            "The Devolutions Agent is not accepting package operations, usually because no valid package policy is installed. Contact your administrator."),
                        brokerMessage));

            case ErrorCode.ValidationFailed or ErrorCode.BadRequest:
                return new(
                    CoreTools.Translate("The package broker rejected the request"),
                    WithDetails(
                        CoreTools.Translate("The Devolutions Agent did not accept one of the options of this operation."),
                        DescribeErrorDetails(error, brokerMessage)));

            case ErrorCode.PayloadTooLarge:
                return new(
                    CoreTools.Translate("The request is too large"),
                    CoreTools.Translate(
                        "The options of this operation exceed the size the Devolutions Agent accepts. Shorten the custom arguments or commands, then try again."));
        }

        return exception.Kind switch
        {
            BrokerClientErrorKind.PolicyDenied => new(
                CoreTools.Translate("Operation denied by policy"),
                CoreTools.Translate("Your organization's package policy does not allow this operation.")),
            BrokerClientErrorKind.UnsupportedCapability => new(
                CoreTools.Translate("Operation unsupported by broker"),
                WithDetails(
                    CoreTools.Translate("The Devolutions Agent on this computer does not support this operation or one of its options."),
                    exception.Message)),
            BrokerClientErrorKind.RequestTooLarge => new(
                CoreTools.Translate("The request is too large"),
                CoreTools.Translate(
                    "The options of this operation exceed the size the Devolutions Agent accepts. Shorten the custom arguments or commands, then try again.")),
            BrokerClientErrorKind.Timeout => new(
                CoreTools.Translate("Broker communication error"),
                CoreTools.Translate("The Devolutions Agent did not respond in time. Try again in a moment.")),
            BrokerClientErrorKind.BrokerUnavailable => new(
                CoreTools.Translate("Agent broker unavailable"),
                CoreTools.Translate(
                    "The Devolutions Agent broker is not available. The operation cannot be performed. Please ensure the Devolutions Agent is installed and running.")),
            BrokerClientErrorKind.EmptyResponse or BrokerClientErrorKind.InvalidResponse => new(
                CoreTools.Translate("Broker communication error"),
                CoreTools.Translate("The Devolutions Agent returned an unexpected response.")),
            _ => new(
                CoreTools.Translate("Operation failed via broker"),
                brokerMessage ?? exception.Message),
        };
    }

    private static string? DescribeErrorDetails(ErrorResponse error, string? brokerMessage)
    {
        List<string> lines = [];
        if (brokerMessage is not null)
            lines.Add(brokerMessage);

        foreach (ErrorDetail detail in error.Details ?? [])
        {
            if (!string.IsNullOrWhiteSpace(detail.Message) && detail.Message.Trim() != brokerMessage)
                lines.Add("• " + detail.Message.Trim());
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static string WithDetails(string summary, string? details) =>
        string.IsNullOrWhiteSpace(details)
            ? summary
            : summary + Environment.NewLine + CoreTools.Translate("Details: {0}", details);
}

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using UniGetUI.Core.Logging;
using ApiElevation = Devolutions.Now.Policy.Api.Elevation;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyManagement;

/// <summary>
/// Read-only adapter over the Phase 2 Agent policy management/validation endpoints
/// (GET /v1/policy/management, POST /v1/policy/validate). This is independent of the
/// Phase 1 <c>IBrokerPolicyInspector</c> (GET /v1/policy) and does not read the
/// UseAgentBroker setting: callers decide when to invoke it.
/// </summary>
public interface IBrokerPolicyManagementService
{
    /// <summary>Retrieves the current policy management snapshot (Active/Missing/Invalid, write capability, etc.).</summary>
    Task<BrokerPolicyManagementResult> GetManagementAsync(CancellationToken cancellationToken);

    /// <summary>Validates a raw policy draft against the Agent, returning a canonical draft/receipt/findings on success.</summary>
    Task<BrokerPolicyValidationOutcome> ValidateAsync(JsonElement draft, CancellationToken cancellationToken);
}

public sealed partial class BrokerPolicyManagementService : IBrokerPolicyManagementService
{
    private readonly Func<BrokerClient> _clientFactory;
    private readonly Func<bool> _isWindows;

    public BrokerPolicyManagementService()
        : this(CreateStandardClient, OperatingSystem.IsWindows)
    {
    }

    private static BrokerClient CreateStandardClient() =>
        BrokerClientFactory.Create(ApiElevation.Standard);

    public BrokerPolicyManagementService(Func<BrokerClient> clientFactory, Func<bool>? isWindows = null)
    {
        _clientFactory = clientFactory;
        _isWindows = isWindows ?? OperatingSystem.IsWindows;
    }

    public async Task<BrokerPolicyManagementResult> GetManagementAsync(CancellationToken cancellationToken)
    {
        if (!_isWindows())
        {
            return new(BrokerPolicyManagementStatus.UnsupportedPlatform);
        }

        try
        {
            using BrokerClient client = _clientFactory();
            PolicyManagementResponse response = await client.GetPolicyManagement(cancellationToken).ConfigureAwait(false);
            if (!HasRequiredManagementData(response))
            {
                Logger.Warn("[AgentBroker] Policy management response contained invalid required data.");
                return new(
                    BrokerPolicyManagementStatus.InvalidResponse,
                    ErrorMessage: "The broker response contained invalid policy management data.");
            }

            return new(
                BrokerPolicyManagementStatus.Retrieved,
                response.Management,
                BuildManagementDiagnostics(response.Management));
        }
        catch (BrokerClientException ex)
        {
            Logger.Warn($"[AgentBroker] Policy management retrieval failed: {ex}");
            return new(MapManagementFailure(ex), ErrorMessage: ex.BrokerError?.Message ?? ex.Message);
        }
    }

    public async Task<BrokerPolicyValidationOutcome> ValidateAsync(JsonElement draft, CancellationToken cancellationToken)
    {
        if (!_isWindows())
        {
            return new(BrokerPolicyValidationStatus.UnsupportedPlatform);
        }

        try
        {
            using BrokerClient client = _clientFactory();
            PolicyValidationResponse response = await client.ValidatePolicy(draft, cancellationToken).ConfigureAwait(false);
            if (!HasRequiredValidationData(response))
            {
                Logger.Warn("[AgentBroker] Policy validation response contained invalid required data.");
                return new(
                    BrokerPolicyValidationStatus.InvalidResponse,
                    ErrorMessage: "The broker response contained invalid policy validation data.");
            }

            return new(
                BrokerPolicyValidationStatus.Completed,
                response.Validation,
                BuildFindingsDiagnostics(response.Validation.Findings));
        }
        catch (BrokerClientException ex)
        {
            Logger.Warn($"[AgentBroker] Policy validation failed: {ex}");
            return new(MapValidationFailure(ex), ErrorMessage: ex.BrokerError?.Message ?? ex.Message);
        }
    }

    // The package's own BrokerJson deserializer already enforces (for both response types below):
    // canonical-cased enum values, non-null required properties, no unknown/extra properties, and -
    // specifically for PolicyManagementSnapshot/PolicyValidationResult - the State<->Policy/InvalidDiagnostics,
    // WriteCapability<->ReadOnlyReason, and IsValid<->CanonicalDraft/ValidationReceipt/Findings cross-field
    // invariants (violations throw JsonException, surfaced by BrokerClient as
    // BrokerClientException(Kind = InvalidResponse) before ever reaching this adapter). What is *not*
    // enforced by the package - and is therefore checked here - is the envelope's ResponseVersion format
    // and ServerVersion bound (mirroring the Phase 1 BrokerPolicyInspector checks for the sibling GET
    // /v1/policy endpoint), plus defensive Enum.IsDefined checks for forward-compatibility.
    private static bool HasRequiredManagementData(PolicyManagementResponse response)
    {
        return response.ResponseKind == BrokerApi.PolicyManagementResponseKind
            && IsResponseVersion(response.ResponseVersion)
            && response.Server is not null
            && IsRequiredString(response.Server.ServerVersion, 128)
            && Enum.IsDefined(response.Server.Transport)
            && response.Management is not null
            && Enum.IsDefined(response.Management.State)
            && Enum.IsDefined(response.Management.Source)
            && Enum.IsDefined(response.Management.WriteCapability)
            && (response.Management.ReadOnlyReason is null || Enum.IsDefined(response.Management.ReadOnlyReason.Value));
    }

    private static bool HasRequiredValidationData(PolicyValidationResponse response)
    {
        return response.ResponseKind == BrokerApi.PolicyValidationResponseKind
            && IsResponseVersion(response.ResponseVersion)
            && response.Server is not null
            && IsRequiredString(response.Server.ServerVersion, 128)
            && Enum.IsDefined(response.Server.Transport)
            && response.Validation is not null
            && response.Validation.Findings is not null
            && response.Validation.Findings.All(finding =>
                finding is not null
                && Enum.IsDefined(finding.Severity)
                && Enum.IsDefined(finding.Code)
                && finding.Message is not null);
    }

    private static bool IsRequiredString(string? value, int maxLength)
    {
        if (value is null)
        {
            return false;
        }

        int length = value.EnumerateRunes().Take(maxLength + 1).Count();
        return length is > 0 && length <= maxLength;
    }

    private static bool IsResponseVersion(string? value)
    {
        return !string.IsNullOrEmpty(value)
            && ResponseVersionRegex().IsMatch(value);
    }

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ResponseVersionRegex();

    // Keeps 404/NotFound/UnsupportedEndpoint mapped as "older unsupported Agent", and the three
    // policy-path/format/filesystem error codes distinct from each other and from every other outcome,
    // per the Phase 2 contract. Mirrors (without modifying) the Phase 1 BrokerPolicyInspector.MapFailure
    // precedent: BrokerClientErrorKind.BrokerError collapses every structured broker error into one kind,
    // so disambiguation must happen via StatusCode/BrokerError.Code first.
    private static BrokerPolicyManagementStatus MapManagementFailure(BrokerClientException ex)
    {
        if (ex.StatusCode == 404
            || ex.BrokerError?.Code is ErrorCode.NotFound or ErrorCode.UnsupportedEndpoint)
        {
            return BrokerPolicyManagementStatus.Unsupported;
        }

        if (ex.StatusCode is 401 or 403
            || ex.BrokerError?.Code is ErrorCode.Unauthorized or ErrorCode.Forbidden
                or ErrorCode.Unauthenticated or ErrorCode.AdministratorRequired)
        {
            return BrokerPolicyManagementStatus.AccessDenied;
        }

        switch (ex.BrokerError?.Code)
        {
            case ErrorCode.UnsafePolicyPath:
                return BrokerPolicyManagementStatus.UnsafePolicyPath;
            case ErrorCode.UnsupportedPolicyFormat:
                return BrokerPolicyManagementStatus.UnsupportedPolicyFormat;
            case ErrorCode.UnsupportedPolicyFilesystem:
                return BrokerPolicyManagementStatus.UnsupportedPolicyFilesystem;
        }

        return ex.Kind switch
        {
            BrokerClientErrorKind.BrokerUnavailable or BrokerClientErrorKind.Timeout =>
                BrokerPolicyManagementStatus.AgentUnavailable,
            BrokerClientErrorKind.EmptyResponse or BrokerClientErrorKind.InvalidResponse =>
                BrokerPolicyManagementStatus.InvalidResponse,
            BrokerClientErrorKind.BrokerError =>
                BrokerPolicyManagementStatus.PolicyUnavailable,
            _ => BrokerPolicyManagementStatus.InvalidResponse,
        };
    }

    private static BrokerPolicyValidationStatus MapValidationFailure(BrokerClientException ex)
    {
        if (ex.StatusCode == 404
            || ex.BrokerError?.Code is ErrorCode.NotFound or ErrorCode.UnsupportedEndpoint)
        {
            return BrokerPolicyValidationStatus.Unsupported;
        }

        if (ex.StatusCode is 401 or 403
            || ex.BrokerError?.Code is ErrorCode.Unauthorized or ErrorCode.Forbidden
                or ErrorCode.Unauthenticated or ErrorCode.AdministratorRequired)
        {
            return BrokerPolicyValidationStatus.AccessDenied;
        }

        if (ex.Kind == BrokerClientErrorKind.RequestTooLarge
            || ex.BrokerError?.Code == ErrorCode.PayloadTooLarge)
        {
            return BrokerPolicyValidationStatus.RequestTooLarge;
        }

        if (ex.BrokerError?.Code == ErrorCode.MalformedDraft)
        {
            return BrokerPolicyValidationStatus.MalformedDraft;
        }

        return ex.Kind switch
        {
            BrokerClientErrorKind.BrokerUnavailable or BrokerClientErrorKind.Timeout =>
                BrokerPolicyValidationStatus.AgentUnavailable,
            BrokerClientErrorKind.EmptyResponse or BrokerClientErrorKind.InvalidResponse =>
                BrokerPolicyValidationStatus.InvalidResponse,
            BrokerClientErrorKind.BrokerError =>
                BrokerPolicyValidationStatus.ValidationUnavailable,
            _ => BrokerPolicyValidationStatus.InvalidResponse,
        };
    }

    // --- Sanitized diagnostics -------------------------------------------------------------------
    // Neither ConfiguredPath nor PolicyFinding.Message/Path/RuleId/Arguments are bounded by the
    // package (verified empirically: multi-hundred-KB strings and thousands of Arguments entries
    // round-trip successfully). The projections below make Agent-supplied diagnostics safe to render:
    // control characters are stripped, free text is bounded in length, and Arguments are capped in
    // both entry count and per-value length.

    private static readonly IReadOnlyDictionary<string, string> EmptyArguments = new Dictionary<string, string>();

    private static BrokerPolicyDiagnosticsView? BuildManagementDiagnostics(PolicyManagementSnapshot snapshot)
    {
        IReadOnlyList<PolicyFinding>? findings = snapshot.InvalidDiagnostics?.Findings;
        return findings is null ? null : BuildFindingsDiagnostics(findings);
    }

    private static BrokerPolicyDiagnosticsView BuildFindingsDiagnostics(IReadOnlyList<PolicyFinding> findings)
    {
        var sanitized = new List<BrokerPolicySanitizedFinding>(
            Math.Min(findings.Count, BrokerPolicyManagementLimits.MaxSanitizedFindings));
        foreach (PolicyFinding finding in findings.Take(BrokerPolicyManagementLimits.MaxSanitizedFindings))
        {
            sanitized.Add(SanitizeFinding(finding));
        }

        return new BrokerPolicyDiagnosticsView(
            sanitized,
            findings.Count > BrokerPolicyManagementLimits.MaxSanitizedFindings);
    }

    private static BrokerPolicySanitizedFinding SanitizeFinding(PolicyFinding finding)
    {
        (string? path, bool pathTruncated) =
            SanitizeOptionalText(finding.Path, BrokerPolicyManagementLimits.MaxSanitizedTextLength);
        (string? ruleId, bool ruleIdTruncated) =
            SanitizeOptionalText(finding.RuleId, BrokerPolicyManagementLimits.MaxSanitizedTextLength);
        (string message, bool messageTruncated) =
            SanitizeRequiredText(finding.Message ?? string.Empty, BrokerPolicyManagementLimits.MaxSanitizedTextLength);
        (IReadOnlyDictionary<string, string> arguments, bool argumentsTruncated) =
            SanitizeArguments(finding.Arguments);

        return new BrokerPolicySanitizedFinding(
            finding.Severity,
            finding.Code,
            path,
            ruleId,
            message,
            arguments,
            pathTruncated,
            ruleIdTruncated,
            messageTruncated,
            argumentsTruncated);
    }

    private static (IReadOnlyDictionary<string, string> Arguments, bool Truncated) SanitizeArguments(
        IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return (EmptyArguments, false);
        }

        var sanitized = new Dictionary<string, string>(
            Math.Min(arguments.Count, BrokerPolicyManagementLimits.MaxSanitizedArgumentEntries));
        bool truncated = arguments.Count > BrokerPolicyManagementLimits.MaxSanitizedArgumentEntries;
        foreach (KeyValuePair<string, JsonElement> entry in
            arguments.Take(BrokerPolicyManagementLimits.MaxSanitizedArgumentEntries))
        {
            (string key, bool keyTruncated) = SanitizeRequiredText(
                entry.Key,
                BrokerPolicyManagementLimits.MaxSanitizedArgumentValueLength);
            (string value, bool valueTruncated) = SerializeSanitizedArgumentValue(entry.Value);
            sanitized[key] = value;
            truncated |= keyTruncated || valueTruncated;
        }

        return (sanitized, truncated);
    }

    private static (string? Value, bool Truncated) SanitizeOptionalText(string? value, int maxLength)
    {
        if (value is null)
        {
            return (null, false);
        }

        (string sanitized, bool truncated) = SanitizeRequiredText(value, maxLength);
        return (sanitized, truncated);
    }

    private static (string Value, bool Truncated) SanitizeRequiredText(string value, int maxLength)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        int count = 0;
        bool truncated = false;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
                continue;

            if (count >= maxLength)
            {
                truncated = true;
                break;
            }

            builder.Append(rune);
            count++;
        }

        return (builder.ToString(), truncated);
    }

    private static (string Value, bool Truncated) SerializeSanitizedArgumentValue(JsonElement value)
    {
        const int bytesPerEscapedScalar = 12;
        int capacity =
            (BrokerPolicyManagementLimits.MaxSanitizedArgumentValueLength + 1)
            * bytesPerEscapedScalar
            + 64;
        var buffer = new FixedCapacityBufferWriter(capacity);
        try
        {
            using var writer = new Utf8JsonWriter(buffer);
            value.WriteTo(writer);
            writer.Flush();
        }
        catch (BoundedBufferExceededException)
        {
            return (string.Empty, true);
        }
        catch (InvalidOperationException)
        {
            return (string.Empty, true);
        }

        string rawValue = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return SanitizeRequiredText(
            rawValue,
            BrokerPolicyManagementLimits.MaxSanitizedArgumentValueLength);
    }

    private sealed class FixedCapacityBufferWriter(int capacity) : IBufferWriter<byte>
    {
        private readonly byte[] _buffer = new byte[capacity];

        public int WrittenCount { get; private set; }

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, WrittenCount);

        public void Advance(int count)
        {
            if (count < 0 || count > _buffer.Length - WrittenCount)
                throw new BoundedBufferExceededException();

            WrittenCount += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureAvailable(sizeHint);
            return _buffer.AsMemory(WrittenCount);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureAvailable(sizeHint);
            return _buffer.AsSpan(WrittenCount);
        }

        private void EnsureAvailable(int sizeHint)
        {
            int required = Math.Max(sizeHint, 1);
            if (required > _buffer.Length - WrittenCount)
                throw new BoundedBufferExceededException();
        }
    }

    private sealed class BoundedBufferExceededException : Exception
    {
    }
}

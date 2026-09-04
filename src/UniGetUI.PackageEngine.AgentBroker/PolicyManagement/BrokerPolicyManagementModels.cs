using Devolutions.Now.Policy.Api;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyManagement;

/// <summary>
/// Outcome classification for a GET /v1/policy/management call.
/// </summary>
public enum BrokerPolicyManagementStatus
{
    /// <summary>The management snapshot was retrieved and passed validation.</summary>
    Retrieved,
    AgentUnavailable,
    /// <summary>Ordinary 404 (or an explicit UnsupportedEndpoint error code): the Agent is older and does not support this endpoint.</summary>
    Unsupported,
    AccessDenied,
    InvalidResponse,
    UnsupportedPlatform,
    /// <summary>The Agent reported the configured policy path as unsafe (e.g. traversal, reparse point). Distinct from UnsupportedPolicyFormat/UnsupportedPolicyFilesystem.</summary>
    UnsafePolicyPath,
    /// <summary>The Agent reported the policy file format as unsupported. Distinct from UnsafePolicyPath/UnsupportedPolicyFilesystem.</summary>
    UnsupportedPolicyFormat,
    /// <summary>The Agent reported the filesystem hosting the policy path as unsupported. Distinct from UnsafePolicyPath/UnsupportedPolicyFormat.</summary>
    UnsupportedPolicyFilesystem,
    PolicyUnavailable,
}

/// <summary>
/// Outcome classification for a POST /v1/policy/validate call.
/// </summary>
public enum BrokerPolicyValidationStatus
{
    /// <summary>The validate call completed and returned a validation result (which may itself report IsValid == false with findings).</summary>
    Completed,
    /// <summary>The submitted draft could not be parsed/understood by the Agent as a policy draft.</summary>
    MalformedDraft,
    /// <summary>The draft exceeded the shared policy management body size limit (see <see cref="BrokerPolicyManagementLimits.MaxRequestBodyBytes"/>) and was rejected before being sent, or the Agent rejected it as too large.</summary>
    RequestTooLarge,
    AgentUnavailable,
    /// <summary>Ordinary 404 (or an explicit UnsupportedEndpoint error code): the Agent is older and does not support this endpoint.</summary>
    Unsupported,
    AccessDenied,
    InvalidResponse,
    UnsupportedPlatform,
    ValidationUnavailable,
}

/// <summary>
/// Shared constants for the Phase 2 policy management/validation surface.
/// </summary>
public static class BrokerPolicyManagementLimits
{
    /// <summary>
    /// Mirrors <c>Devolutions.Now.Policy.Api.BrokerApi.MaxPolicyManagementBodyBytes</c>, the authoritative
    /// shared constant enforced by <see cref="Devolutions.Now.Policy.Client.BrokerClient"/> on outbound
    /// policy-validate/replace request bodies. Exposed here so callers can pre-flight-check draft size
    /// without constructing a client, and to document that this limit governs request bodies (not the
    /// unbounded GET /v1/policy/management or validate response bodies - the package does not cap those).
    /// </summary>
    public const int MaxRequestBodyBytes = BrokerApi.MaxPolicyManagementBodyBytes;

    /// <summary>Maximum length (in Unicode scalar values) kept for the sanitized configured-path diagnostic field.</summary>
    public const int MaxSanitizedPathLength = 4096;

    /// <summary>Maximum length (in Unicode scalar values) kept for sanitized finding Message/Path/RuleId text.</summary>
    public const int MaxSanitizedTextLength = 2048;

    /// <summary>Maximum number of findings copied into a sanitized diagnostics view.</summary>
    public const int MaxSanitizedFindings = 200;

    /// <summary>Maximum number of Arguments entries copied per sanitized finding.</summary>
    public const int MaxSanitizedArgumentEntries = 32;

    /// <summary>Maximum length (in Unicode scalar values) kept for each sanitized Arguments raw-JSON value.</summary>
    public const int MaxSanitizedArgumentValueLength = 256;
}

/// <summary>
/// A single policy finding (from either an Invalid management snapshot's diagnostics, or a validation
/// result), sanitized for safe display: free-text fields are control-character-stripped and bounded in
/// length, and the Arguments dictionary is capped in both entry count and per-value length. The package
/// itself does not bound any of these (see <see cref="BrokerPolicyManagementLimits"/> remarks), so this
/// projection exists purely to make Agent-supplied diagnostics safe for UI consumption.
/// </summary>
public sealed record BrokerPolicySanitizedFinding(
    PolicyFindingSeverity Severity,
    PolicyFindingCode Code,
    string? Path,
    string? RuleId,
    string Message,
    IReadOnlyDictionary<string, string> Arguments,
    bool PathTruncated,
    bool RuleIdTruncated,
    bool MessageTruncated,
    bool ArgumentsTruncated);

/// <summary>
/// Sanitized diagnostics for a policy management snapshot or validation result: a bounded, safe-to-render
/// projection of the raw findings/paths the Agent returned.
/// </summary>
public sealed record BrokerPolicyDiagnosticsView(
    IReadOnlyList<BrokerPolicySanitizedFinding> Findings,
    bool FindingsTruncated);

/// <summary>
/// Result of <see cref="IBrokerPolicyManagementService.GetManagementAsync"/>. <see cref="Snapshot"/> exposes
/// the package's own contract type directly (mirroring the Phase 1 <c>BrokerPolicyInspectionResult</c>
/// pattern) so callers retain full fidelity (state, write capability/reason, configured path, store token,
/// and - when Active - the policy document). <see cref="Diagnostics"/> additionally provides a sanitized,
/// bounded view of Invalid-state findings suitable for direct UI rendering.
/// </summary>
public sealed record BrokerPolicyManagementResult(
    BrokerPolicyManagementStatus Status,
    PolicyManagementSnapshot? Snapshot = null,
    BrokerPolicyDiagnosticsView? Diagnostics = null,
    string? ErrorMessage = null);

/// <summary>
/// Result of <see cref="IBrokerPolicyManagementService.ValidateAsync"/>. <see cref="Validation"/> exposes the
/// package's own contract type directly, including the canonical draft and validation receipt when the
/// submitted draft is valid, and the full (unsanitized) findings list. <see cref="Diagnostics"/> additionally
/// provides a sanitized, bounded view of the same findings suitable for direct UI rendering.
/// </summary>
public sealed record BrokerPolicyValidationOutcome(
    BrokerPolicyValidationStatus Status,
    PolicyValidationResult? Validation = null,
    BrokerPolicyDiagnosticsView? Diagnostics = null,
    string? ErrorMessage = null);

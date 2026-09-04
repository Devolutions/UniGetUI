using Devolutions.Now.Policy.Api;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

/// <summary>
/// Wire-level constants shared by the non-elevated UniGetUI process and the elevated
/// policy-write helper. This file is compiled into both binaries; any change here is a
/// protocol break and must bump <see cref="Version"/>.
/// </summary>
public static class PolicyElevationProtocol
{
    /// <summary>Protocol revision. Both peers reject a mismatching value before any payload is exchanged.</summary>
    public const string Version = "2.0";

    /// <summary>File name of the elevated helper as staged inside the packaged install tree.</summary>
    public const string HelperFileName = "UniGetUI.PolicyElevator.exe";

    /// <summary>File name of the non-elevated host that is allowed to drive the helper.</summary>
    public const string HostFileName = "UniGetUI.exe";

    /// <summary>First directory segment, relative to the install root, the helper is staged into.</summary>
    public const string HelperRelativeDirectory = "Assets";

    /// <summary>Second directory segment, relative to the install root, the helper is staged into.</summary>
    public const string HelperRelativeSubDirectory = "Utilities";

    /// <summary>Prefix of the per-request single-use named pipe.</summary>
    public const string PipeNamePrefix = "UniGetUI.PolicyElevation.";

    /// <summary>Number of hexadecimal characters of entropy appended to <see cref="PipeNamePrefix"/>.</summary>
    public const int PipeNameEntropyCharacters = 32;

    /// <summary>Number of hexadecimal characters in a request identifier.</summary>
    public const int RequestIdCharacters = 32;

    // ---- Command line contract -------------------------------------------------------------
    // The command line carries routing information only. It never carries the policy draft,
    // a store token, a validation receipt, or any other secret: those travel exclusively over
    // the authenticated pipe, and never through a temporary file.

    public const string ProtocolArgument = "--protocol";
    public const string PipeArgument = "--pipe";
    public const string ParentProcessIdArgument = "--parent-pid";
    public const string ParentCreationTimeArgument = "--parent-created";
    public const string SessionArgument = "--session";

    // ---- Frame limits ----------------------------------------------------------------------

    /// <summary>
    /// Size of the length prefix that precedes every UTF-8 JSON frame body (big-endian uint32).
    /// </summary>
    public const int FrameLengthPrefixBytes = 4;

    /// <summary>
    /// The single authoritative policy-management body budget, taken verbatim from the shared
    /// broker contract. Both the request draft and the relayed broker response payload are
    /// bounded by this value.
    /// </summary>
    public const int MaxPolicyManagementBodyBytes = BrokerApi.MaxPolicyManagementBodyBytes;

    // Bounded scalar fields. These are enforced by validation, and the frame budgets below are
    // derived from them, so the derived budget is an exact upper bound rather than a guess.
    public const int MaxProtocolVersionCharacters = 16;
    public const int MaxStoreTokenCharacters = 512;
    public const int MaxValidationReceiptCharacters = 2048;
    public const int MaxBrokerErrorCodeCharacters = 64;
    public const int MaxConflictPolicyIdCharacters = 2048;

    // Exact JSON byte accounting.
    //
    //  * a UTF-16 code unit never needs more than 6 UTF-8 bytes once JSON-escaped
    //    (\uXXXX); a non-BMP character is two code units and costs 12 bytes, i.e. still
    //    6 bytes per counted character.
    //  * a quoted string therefore costs 2 (quotes) + 6 * maxCharacters.
    //  * a 32-bit integer never exceeds 11 bytes ("-2147483648").
    //  * a boolean never exceeds 5 bytes ("false").
    //  * a property name costs 2 (quotes) + name length + 1 (colon).
    private const int QuoteBytes = 2;
    private const int MaxEscapedBytesPerCharacter = 6;
    private const int MaxInt32Bytes = 11;
    private const int MaxBooleanBytes = 5;
    private const int MaxOperationBytes = QuoteBytes + 15;
    private const int MaxConflictHandlingBytes = QuoteBytes + 16;
    private const int ActiveManagementStateBytes = QuoteBytes + 6;
    private const int ObjectBraceBytes = 2;
    private const int PropertyNameOverheadBytes = 3;

    private const int ProtocolVersionValueBytes =
        QuoteBytes + (MaxProtocolVersionCharacters * MaxEscapedBytesPerCharacter);

    private const int RequestIdValueBytes =
        QuoteBytes + (RequestIdCharacters * MaxEscapedBytesPerCharacter);

    private const int StoreTokenValueBytes =
        QuoteBytes + (MaxStoreTokenCharacters * MaxEscapedBytesPerCharacter);

    private const int ValidationReceiptValueBytes =
        QuoteBytes + (MaxValidationReceiptCharacters * MaxEscapedBytesPerCharacter);

    // System.Text.Json's default encoder emits HTML-sensitive safe-ASCII characters such as
    // quotation marks as six-byte \uXXXX escapes.
    private const int MaxSafeAsciiJsonBytesPerCharacter = 6;
    private const int MaxSafeAsciiStoreTokenValueBytes =
        QuoteBytes + 1
        + ((MaxStoreTokenCharacters - 1) * MaxSafeAsciiJsonBytesPerCharacter);
    private const int MaxSafeAsciiConflictPolicyIdValueBytes =
        QuoteBytes + 1
        + ((MaxConflictPolicyIdCharacters - 1) * MaxSafeAsciiJsonBytesPerCharacter);
    private const int StaleErrorCodeValueBytes = QuoteBytes + 21;
    private const int RejectedDispositionBytes = QuoteBytes + 8;

    // {"protocolVersion":…,"requestId":…,"operation":…,"conflictHandling":…,
    //  "expectedStoreToken":…,"validationReceipt":…,"warningsAcknowledged":…,"draft":…}
    private const int RequestPropertyCount = 8;
    // protocolVersion(15) requestId(9) operation(9) conflictHandling(16) expectedStoreToken(18)
    // validationReceipt(17) warningsAcknowledged(20) draft(5) = 109 name characters, plus three
    // bytes of quoting and colon per property. Asserted against the real contract by
    // PolicyElevationProtocolTests.
    public const int RequestPropertyNameCharacters = 109;

    private const int RequestPropertyNameBytes =
        RequestPropertyNameCharacters + (RequestPropertyCount * PropertyNameOverheadBytes);

    /// <summary>
    /// Every request byte that is not the draft itself.
    /// </summary>
    public const int RequestEnvelopeOverheadBytes =
        ObjectBraceBytes
        + (RequestPropertyCount - 1)
        + RequestPropertyNameBytes
        + ProtocolVersionValueBytes
        + RequestIdValueBytes
        + MaxOperationBytes
        + MaxConflictHandlingBytes
        + StoreTokenValueBytes
        + ValidationReceiptValueBytes
        + MaxBooleanBytes; // warningsAcknowledged

    // {"protocolVersion":…,"requestId":…,"disposition":…,"brokerStatusCode":…,
    //  "brokerErrorCode":…,"committedStoreToken":…,"conflictStoreToken":…,
    //  "conflictState":…,"conflictPolicyId":…}
    private const int ResponsePropertyCount = 9;

    public const int ResponsePropertyNameCharacters = 132;

    private const int ResponsePropertyNameBytes =
        ResponsePropertyNameCharacters + (ResponsePropertyCount * PropertyNameOverheadBytes);

    /// <summary>
    /// Exact upper bound of the stale-conflict acknowledgement, the largest valid response shape.
    /// </summary>
    public const int ResponseEnvelopeOverheadBytes =
        ObjectBraceBytes
        + (ResponsePropertyCount - 1)
        + ResponsePropertyNameBytes
        + (QuoteBytes + 3) // "2.0"
        + (QuoteBytes + RequestIdCharacters)
        + RejectedDispositionBytes
        + MaxInt32Bytes // brokerStatusCode
        + StaleErrorCodeValueBytes
        + 4 // committedStoreToken is null in a stale response
        + MaxSafeAsciiStoreTokenValueBytes
        + ActiveManagementStateBytes
        + MaxSafeAsciiConflictPolicyIdValueBytes;

    /// <summary>
    /// Maximum accepted request frame body: the full shared policy-management budget plus the
    /// exact envelope overhead. Deliberately not a smaller round number — a maximum-size draft
    /// must survive the hop.
    /// </summary>
    public const int MaxRequestFrameBytes =
        MaxPolicyManagementBodyBytes + RequestEnvelopeOverheadBytes;

    /// <summary>
    /// Maximum accepted response frame body. The helper response is a compact acknowledgement and
    /// never carries a policy document, validation result, findings list, or broker error payload.
    /// </summary>
    public const int MaxResponseFrameBytes = ResponseEnvelopeOverheadBytes;

    // ---- Timeouts --------------------------------------------------------------------------

    /// <summary>How long the host waits for the elevated helper to connect after consent was granted.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);

    /// <summary>How long each peer waits for the single request/response exchange to complete.</summary>
    public static readonly TimeSpan ExchangeTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Fresh post-broker deadline used only to deliver the compact acknowledgement.</summary>
    public static readonly TimeSpan ResponseWriteTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long the host waits for the helper to exit after the response was received.</summary>
    public static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    // ---- Helper process exit codes ---------------------------------------------------------
    // A helper that produced a response frame always exits with Success; failure detail lives in
    // the frame. These codes only describe failures that happen before or after the exchange.

    public const int ExitSuccess = 0;
    public const int ExitInvalidArguments = 10;
    public const int ExitConnectFailed = 11;
    public const int ExitPeerAuthenticationFailed = 12;
    public const int ExitProtocolError = 13;
    public const int ExitUnexpectedFailure = 14;

    /// <summary>ERROR_CANCELLED — the user dismissed the consent prompt.</summary>
    public const int ErrorCancelled = 1223;
}

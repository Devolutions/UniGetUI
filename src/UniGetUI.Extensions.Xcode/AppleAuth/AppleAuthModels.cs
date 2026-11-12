// Request and response shapes ported from xcodes (Sources/AppleAPI), MIT License,
// Copyright (c) 2019-2021 Robots and Pencils. See THIRD_PARTY_NOTICES.md in this project.
using System.Text.Json.Serialization;

namespace UniGetUI.Extensions.Xcode.AppleAuth;

public enum AppleAuthError
{
    InvalidCredentials,
    PrivacyAcknowledgementRequired,
    ServiceUnavailable,
    NoTrustedPhoneNumbers,
    SecurityCodeLocked,
    TwoStepNotSupported,
    SecurityKeyRequired,
    SecurityKeyFailed,
    FederatedAccount,
    NotDeveloper,
    InvalidResponse,
    Unexpected,
}

public sealed class AppleAuthException(AppleAuthError error, string message, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public AppleAuthError Error { get; } = error;
    public int? StatusCode { get; } = statusCode;
}

/// <summary>What Apple's developer session says about the signed-in account. Missing fields stay null.</summary>
public sealed record AppleSession(string? FullName, string? EmailAddress, string? TeamName);

public sealed record AppleTrustedPhone(int Id, string Number);

public enum AppleCodeDelivery
{
    TrustedDevice,
    Sms,
}

/// <param name="Error">Why the previous answer was refused, already user-facing, or null.</param>
public sealed record AppleCodeRequest(
    AppleCodeDelivery Delivery, int Length, AppleTrustedPhone? Phone, bool CanRequestSms, string? Error);

/// <summary>Either a code, or a request to have one sent by text message instead.</summary>
public sealed record AppleCodeAnswer(string? Code, bool UseSms = false);

/// <summary>The account to sign in with. The password is kept only for the duration of one attempt.</summary>
public sealed record AppleCredentials(string AppleId, string Password)
{
    public override string ToString() => $"AppleCredentials {{ AppleId = {AppleId} }}";
}

/// <summary>Questions the sign-in asks. Returning null cancels it.</summary>
public interface IAppleSignInPrompts
{
    Task<AppleCredentials?> AskCredentialsAsync(string? appleId, string? error, CancellationToken cancellationToken);

    Task<AppleCodeAnswer?> AskCodeAsync(AppleCodeRequest request, CancellationToken cancellationToken);

    Task<AppleTrustedPhone?> ChoosePhoneAsync(IReadOnlyList<AppleTrustedPhone> phones, CancellationToken cancellationToken);

    /// <summary>Asks for a security key's PIN when it has one. Null cancels.</summary>
    Task<string?> AskSecurityKeyPinAsync(string? error, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    /// <summary>Shown while waiting for the user to touch their security key. The returned task is not awaited.</summary>
    void ShowSecurityKeyTouch(bool waiting) { }

    /// <summary>
    /// Opens the organization's sign-in page and returns the address the browser ended on after signing in, or null.
    /// </summary>
    Task<Uri?> AskFederatedCallbackAsync(Uri signInPage, CancellationToken cancellationToken)
        => Task.FromResult<Uri?>(null);
}

internal sealed class AppleServiceKeyResponse
{
    public string? AuthServiceKey { get; set; }
}

internal sealed class AppleSrpInitRequest
{
    public required string A { get; set; }
    public required string AccountName { get; set; }
    public required string[] Protocols { get; set; }
}

internal sealed class AppleSrpInitResponse
{
    public int Iteration { get; set; }
    public string? Salt { get; set; }
    public string? B { get; set; }
    public string? C { get; set; }
    public string? Protocol { get; set; }
}

internal sealed class AppleSrpCompleteRequest
{
    public required string AccountName { get; set; }
    public required string C { get; set; }
    public required string M1 { get; set; }
    public required string M2 { get; set; }
    public bool RememberMe { get; set; }
}

internal sealed class AppleSignInResponse
{
    public string? AuthType { get; set; }
    public AppleServiceError[]? ServiceErrors { get; set; }
}

internal sealed class AppleServiceError
{
    public string? Code { get; set; }
    public string? Message { get; set; }
}

internal sealed class AppleAuthOptions
{
    public AppleAuthPhone[]? TrustedPhoneNumbers { get; set; }
    public System.Text.Json.JsonElement? TrustedDevices { get; set; }
    public AppleSecurityCodeInfo? SecurityCode { get; set; }
    public bool? NoTrustedDevices { get; set; }
    public AppleFsaChallenge? FsaChallenge { get; set; }
    public AppleAuthDirectOptions? Direct { get; set; }
    public AppleServiceError[]? ServiceErrors { get; set; }
}

internal sealed class AppleAuthDirectOptions
{
    [JsonPropertyName("twoSV")]
    public AppleAuthTwoStepOptions? TwoStep { get; set; }
}

internal sealed class AppleAuthTwoStepOptions
{
    public AppleFsaChallenge? FsaChallenge { get; set; }
}

internal sealed class AppleAuthPhone
{
    public int Id { get; set; }
    public string? NumberWithDialCode { get; set; }
}

internal sealed class AppleSecurityCodeInfo
{
    public int Length { get; set; }
    public bool TooManyCodesSent { get; set; }
    public bool TooManyCodesValidated { get; set; }
    public bool SecurityCodeLocked { get; set; }
    public bool SecurityCodeCooldown { get; set; }
}

internal sealed class AppleFsaChallenge
{
    public string? Challenge { get; set; }
    public string[]? KeyHandles { get; set; }
    public string? RpId { get; set; }
    public string? AllowedCredentials { get; set; }
}

internal sealed class ApplePhoneRequest
{
    public required ApplePhoneId PhoneNumber { get; set; }
    public string Mode { get; set; } = "sms";
}

internal sealed class ApplePhoneId
{
    public int Id { get; set; }
}

internal sealed class AppleCodeValue
{
    public required string Code { get; set; }
}

internal sealed class AppleDeviceCodeRequest
{
    public required AppleCodeValue SecurityCode { get; set; }
}

internal sealed class AppleSmsCodeRequest
{
    public required AppleCodeValue SecurityCode { get; set; }
    public required ApplePhoneId PhoneNumber { get; set; }
    public string Mode { get; set; } = "sms";
}

internal sealed class AppleOlympusSession
{
    public AppleOlympusUser? User { get; set; }
    public AppleOlympusProvider? Provider { get; set; }
}

internal sealed class AppleOlympusUser
{
    public string? FullName { get; set; }
    public string? EmailAddress { get; set; }
}

internal sealed class AppleOlympusProvider
{
    public string? Name { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppleServiceKeyResponse))]
[JsonSerializable(typeof(AppleSrpInitRequest))]
[JsonSerializable(typeof(AppleSrpInitResponse))]
[JsonSerializable(typeof(AppleSrpCompleteRequest))]
[JsonSerializable(typeof(AppleSignInResponse))]
[JsonSerializable(typeof(AppleAuthOptions))]
[JsonSerializable(typeof(ApplePhoneRequest))]
[JsonSerializable(typeof(AppleDeviceCodeRequest))]
[JsonSerializable(typeof(AppleSmsCodeRequest))]
[JsonSerializable(typeof(AppleOlympusSession))]
internal partial class AppleAuthJsonContext : JsonSerializerContext;

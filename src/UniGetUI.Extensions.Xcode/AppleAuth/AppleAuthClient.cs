// The sign-in flow is ported from xcodes (Sources/AppleAPI/Client.swift and URLRequest+Apple.swift), MIT License,
// Copyright (c) 2019-2021 Robots and Pencils. See THIRD_PARTY_NOTICES.md in this project.
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using UniGetUI.Core.Logging;

namespace UniGetUI.Extensions.Xcode.AppleAuth;

/// <summary>
/// Signs in to Apple's developer services and keeps the resulting cookies. Passwords and codes go only to Apple's
/// sign-in endpoints; nothing here logs a request or response body.
/// </summary>
public sealed class AppleAuthClient
{
    internal const int MaximumResponseBytes = 1024 * 1024;
    internal const int MaximumCodeAttempts = 5;
    internal const int MaximumPasswordAttempts = 3;

    internal static readonly Uri ServiceKeyRedirectUri = new("https://appstoreconnect.apple.com/logout");
    internal static readonly Uri ServiceKeyUri = new("https://appstoreconnect.apple.com/olympus/v1/app/config?hostname=itunesconnect.apple.com");
    internal static readonly Uri SignInUri = new("https://idmsa.apple.com/appleauth/auth/signin");
    internal static readonly Uri SrpInitUri = new("https://idmsa.apple.com/appleauth/auth/signin/init");
    internal static readonly Uri SrpCompleteUri = new("https://idmsa.apple.com/appleauth/auth/signin/complete?isRememberMeEnabled=false");
    internal static readonly Uri AuthOptionsUri = new("https://idmsa.apple.com/appleauth/auth");
    internal static readonly Uri RequestSmsUri = new("https://idmsa.apple.com/appleauth/auth/verify/phone");
    internal static readonly Uri DeviceCodeUri = new("https://idmsa.apple.com/appleauth/auth/verify/trusteddevice/securitycode");
    internal static readonly Uri SmsCodeUri = new("https://idmsa.apple.com/appleauth/auth/verify/phone/securitycode");
    internal static readonly Uri TrustUri = new("https://idmsa.apple.com/appleauth/auth/2sv/trust");
    internal static readonly Uri OlympusSessionUri = new("https://appstoreconnect.apple.com/olympus/v1/session");

    private static readonly string[] KnownAuthTypes = ["sa", "hsa", "non-sa", "hsa2"];

    // The redirect points to a real sign-out; it must never receive the account's cookies or be followed.
    internal static readonly AppleCurlTransportOptions ServiceKeyTransportOptions = new()
    {
        UseCookies = false,
        FollowRedirects = false,
    };
    private static readonly HttpClient PublicServiceKeyClient = new(new AppleCurlTransport(new CookieContainer(), ServiceKeyTransportOptions))
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly HttpClient _client;
    private readonly HttpClient _serviceKeyClient;
    private readonly Func<DateTime> _utcNow;

    public AppleAuthClient(HttpClient client) : this(client, PublicServiceKeyClient, () => DateTime.UtcNow) { }

    internal AppleAuthClient(HttpClient client, Func<DateTime> utcNow) : this(client, client, utcNow) { }

    internal AppleAuthClient(HttpClient client, HttpClient serviceKeyClient, Func<DateTime> utcNow)
    {
        _client = client;
        _serviceKeyClient = serviceKeyClient;
        _utcNow = utcNow;
    }

    /// <summary>Answers a security-key challenge. Without one, such accounts get <see cref="AppleAuthError.SecurityKeyRequired"/>.</summary>
    public IAppleSecurityKeyAuthenticator? SecurityKeys { get; init; }

    /// <summary>The current session, or null when Apple does not consider it signed in.</summary>
    public async Task<AppleSession?> ValidateSessionAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(Get(OlympusSessionUri), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;
        if (!response.IsSuccessStatusCode)
            throw await UnexpectedAsync(response, cancellationToken).ConfigureAwait(false);
        AppleOlympusSession session = await TryReadAsync(response, AppleAuthJsonContext.Default.AppleOlympusSession, cancellationToken).ConfigureAwait(false)
            ?? throw InvalidResponse();
        if (session.Provider is null)
            return null;
        return new AppleSession(Clean(session.User?.FullName), Clean(session.User?.EmailAddress), Clean(session.Provider.Name));
    }

    /// <summary>
    /// Signs in, asking for the password again when Apple refuses it. <paramref name="appleId"/> is only a suggestion
    /// shown in the first question.
    /// </summary>
    public async Task<(string AppleId, AppleSession Session)> SignInAsync(
        IAppleSignInPrompts prompts, string? appleId, CancellationToken cancellationToken)
    {
        string? error = null;
        for (int attempt = 0; ; attempt++)
        {
            AppleCredentials credentials = await prompts.AskCredentialsAsync(appleId, error, cancellationToken).ConfigureAwait(false)
                ?? throw new OperationCanceledException(cancellationToken);
            string account = credentials.AppleId.Trim();
            if (account.Length == 0 || credentials.Password.Length == 0)
            {
                error = "Enter your Apple ID and password.";
                continue;
            }
            appleId = account;
            try
            {
                AppleSession session = await SignInOnceAsync(prompts, account, credentials.Password, cancellationToken).ConfigureAwait(false);
                return (account, session);
            }
            catch (AppleAuthException ex) when (ex.Error == AppleAuthError.InvalidCredentials && attempt + 1 < MaximumPasswordAttempts)
            {
                error = ex.Message;
            }
        }
    }

    private async Task<AppleSession> SignInOnceAsync(
        IAppleSignInPrompts prompts, string accountName, string password, CancellationToken cancellationToken)
    {
        string serviceKey = await GetServiceKeyAsync(cancellationToken).ConfigureAwait(false);
        string hashcash = await GetHashcashAsync(cancellationToken).ConfigureAwait(false);

        using var srp = new AppleSrpClient();
        AppleSrpInitResponse init;
        using (HttpRequestMessage request = Json(HttpMethod.Post, SrpInitUri, new AppleSrpInitRequest
        {
            A = Convert.ToBase64String(srp.PublicEphemeral),
            AccountName = accountName,
            Protocols = ["s2k", "s2k_fo"],
        }, AppleAuthJsonContext.Default.AppleSrpInitRequest))
        {
            AddWidgetHeaders(request, serviceKey);
            using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw InvalidCredentials();
            if (!response.IsSuccessStatusCode)
                throw await UnexpectedAsync(response, cancellationToken).ConfigureAwait(false);
            init = await TryReadAsync(response, AppleAuthJsonContext.Default.AppleSrpInitResponse, cancellationToken).ConfigureAwait(false)
                ?? throw InvalidResponse();
        }

        byte[] salt, serverPublic;
        try
        {
            salt = Convert.FromBase64String(init.Salt ?? "");
            serverPublic = Convert.FromBase64String(init.B ?? "");
        }
        catch (FormatException)
        {
            throw InvalidResponse();
        }
        string protocol = init.Protocol ?? "s2k";
        if (string.IsNullOrEmpty(init.C) || !AppleSrpClient.IsSupportedProtocol(protocol))
            throw InvalidResponse();

        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        (byte[] M1, byte[] M2) proofs;
        try
        {
            proofs = srp.ComputeProofs(protocol, accountName, passwordBytes, salt, init.Iteration, serverPublic);
        }
        catch (ArgumentException ex)
        {
            throw new AppleAuthException(AppleAuthError.InvalidResponse, "Apple sent an invalid sign-in challenge.", inner: ex);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(passwordBytes);
        }

        using HttpRequestMessage complete = Json(HttpMethod.Post, SrpCompleteUri, new AppleSrpCompleteRequest
        {
            AccountName = accountName,
            C = init.C,
            M1 = Convert.ToBase64String(proofs.M1),
            M2 = Convert.ToBase64String(proofs.M2),
            RememberMe = false,
        }, AppleAuthJsonContext.Default.AppleSrpCompleteRequest);
        AddWidgetHeaders(complete, serviceKey);
        complete.Headers.TryAddWithoutValidation("X-Apple-HC", hashcash);
        using HttpResponseMessage completed = await SendAsync(complete, cancellationToken).ConfigureAwait(false);
        byte[] body = await ReadBoundedAsync(completed, cancellationToken).ConfigureAwait(false);
        AppleSignInResponse? signIn = TryParse(body, AppleAuthJsonContext.Default.AppleSignInResponse);
        switch ((int)completed.StatusCode)
        {
            case 200:
                break;
            case 401:
                throw InvalidCredentials();
            case 409:
                string sessionId = Header(completed, "X-Apple-ID-Session-Id") ?? throw InvalidResponse();
                string scnt = Header(completed, "scnt") ?? throw InvalidResponse();
                await HandleSecondFactorAsync(prompts, serviceKey, sessionId, scnt, cancellationToken).ConfigureAwait(false);
                break;
            case 412 when KnownAuthTypes.Contains(signIn?.AuthType ?? ""):
                throw new AppleAuthException(AppleAuthError.PrivacyAcknowledgementRequired,
                    "Sign in to https://appstoreconnect.apple.com and accept the Apple ID & Privacy notice, then try again.", 412);
            case 503 when signIn is null:
                throw new AppleAuthException(AppleAuthError.ServiceUnavailable,
                    "Apple's sign-in service is temporarily unavailable. Try again later.", 503);
            default:
                throw Unexpected((int)completed.StatusCode);
        }

        return await ValidateSessionAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new AppleAuthException(AppleAuthError.NotDeveloper,
                "Apple accepted the sign-in, but this Apple ID has no access to Apple's developer services. "
                + "Sign in to https://developer.apple.com once to accept the agreement, then try again.");
    }

    private async Task HandleSecondFactorAsync(
        IAppleSignInPrompts prompts, string serviceKey, string sessionId, string scnt, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = Get(AuthOptionsUri);
        AddSessionHeaders(request, serviceKey, sessionId, scnt);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await UnexpectedAsync(response, cancellationToken).ConfigureAwait(false);
        AppleAuthOptions options = await TryReadAsync(response, AppleAuthJsonContext.Default.AppleAuthOptions, cancellationToken).ConfigureAwait(false)
            ?? throw InvalidResponse();

        AppleFsaChallenge? challenge = options.FsaChallenge ?? options.Direct?.TwoStep?.FsaChallenge;
        if (challenge is not null && options.TrustedPhoneNumbers is not { Length: > 0 })
        {
            if (SecurityKeys is null)
                throw new AppleAuthException(AppleAuthError.SecurityKeyRequired,
                    "This Apple ID signs in with a security key, which is not supported on this system.");
            await SecurityKeys.AuthenticateAsync(new AppleSecurityKeyContext(this, prompts, serviceKey, sessionId, scnt, challenge),
                cancellationToken).ConfigureAwait(false);
            await TrustAsync(serviceKey, sessionId, scnt, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (options.TrustedDevices is { ValueKind: not JsonValueKind.Null })
            throw new AppleAuthException(AppleAuthError.TwoStepNotSupported,
                "This Apple ID uses the older two-step verification. Upgrade it to two-factor authentication at https://appleid.apple.com, then try again.");
        if (options.TrustedPhoneNumbers is not null)
        {
            await HandleTwoFactorAsync(prompts, serviceKey, sessionId, scnt, options, cancellationToken).ConfigureAwait(false);
            return;
        }
        throw new AppleAuthException(AppleAuthError.InvalidResponse,
            "Apple asked for a verification method that is not recognized.");
    }

    private async Task HandleTwoFactorAsync(
        IAppleSignInPrompts prompts, string serviceKey, string sessionId, string scnt,
        AppleAuthOptions options, CancellationToken cancellationToken)
    {
        AppleTrustedPhone[] phones = [.. (options.TrustedPhoneNumbers ?? [])
            .Where(phone => !string.IsNullOrWhiteSpace(phone.NumberWithDialCode))
            .Select(phone => new AppleTrustedPhone(phone.Id, phone.NumberWithDialCode!.Trim()))];
        AppleSecurityCodeInfo? info = options.SecurityCode;
        if (info is { SecurityCodeLocked: true } or { TooManyCodesValidated: true })
            throw new AppleAuthException(AppleAuthError.SecurityCodeLocked,
                "Apple has temporarily locked verification codes for this Apple ID. Try again later.");
        int length = info?.Length is > 0 and <= 12 ? info.Length : 6;
        bool canUseSms = options.NoTrustedDevices == true;

        AppleTrustedPhone? phone = null;
        if (canUseSms)
        {
            if (phones.Length == 0)
                throw NoTrustedPhones();
            // Apple sends the text by itself when there is only one number to send it to.
            phone = phones.Length == 1 ? phones[0] : await ChoosePhoneAndSendAsync(prompts, phones, serviceKey, sessionId, scnt, cancellationToken).ConfigureAwait(false);
        }

        string? error = null;
        for (int attempt = 0; attempt < MaximumCodeAttempts; attempt++)
        {
            var codeRequest = new AppleCodeRequest(
                phone is null ? AppleCodeDelivery.TrustedDevice : AppleCodeDelivery.Sms,
                length, phone, phone is null && phones.Length > 0, error);
            AppleCodeAnswer answer = await prompts.AskCodeAsync(codeRequest, cancellationToken).ConfigureAwait(false)
                ?? throw new OperationCanceledException(cancellationToken);
            if (answer.UseSms && phone is null)
            {
                if (phones.Length == 0)
                    throw NoTrustedPhones();
                phone = await ChoosePhoneAndSendAsync(prompts, phones, serviceKey, sessionId, scnt, cancellationToken).ConfigureAwait(false);
                error = null;
                attempt--;
                continue;
            }

            string code = new([.. (answer.Code ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-')]);
            if (code.Length != length || !code.All(char.IsAsciiDigit))
            {
                error = $"Enter the {length}-digit code.";
                continue;
            }

            using HttpRequestMessage submit = phone is null
                ? Json(HttpMethod.Post, DeviceCodeUri, new AppleDeviceCodeRequest { SecurityCode = new() { Code = code } },
                    AppleAuthJsonContext.Default.AppleDeviceCodeRequest)
                : Json(HttpMethod.Post, SmsCodeUri, new AppleSmsCodeRequest
                {
                    SecurityCode = new() { Code = code },
                    PhoneNumber = new() { Id = phone.Id },
                }, AppleAuthJsonContext.Default.AppleSmsCodeRequest);
            AddSessionHeaders(submit, serviceKey, sessionId, scnt);
            using HttpResponseMessage response = await SendAsync(submit, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.BadRequest)
            {
                error = "That code is incorrect. Try again.";
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw await UnexpectedAsync(response, cancellationToken).ConfigureAwait(false);
            await TrustAsync(serviceKey, sessionId, scnt, cancellationToken).ConfigureAwait(false);
            return;
        }
        throw new AppleAuthException(AppleAuthError.SecurityCodeLocked, "Too many incorrect codes were entered. Try signing in again.");
    }

    private async Task<AppleTrustedPhone> ChoosePhoneAndSendAsync(
        IAppleSignInPrompts prompts, AppleTrustedPhone[] phones, string serviceKey, string sessionId, string scnt,
        CancellationToken cancellationToken)
    {
        AppleTrustedPhone chosen = phones.Length == 1
            ? phones[0]
            : await prompts.ChoosePhoneAsync(phones, cancellationToken).ConfigureAwait(false)
                ?? throw new OperationCanceledException(cancellationToken);
        if (!phones.Contains(chosen))
            throw new ArgumentException("The chosen phone number is not one of the trusted numbers.");

        using HttpRequestMessage request = Json(HttpMethod.Put, RequestSmsUri,
            new ApplePhoneRequest { PhoneNumber = new() { Id = chosen.Id } }, AppleAuthJsonContext.Default.ApplePhoneRequest);
        AddSessionHeaders(request, serviceKey, sessionId, scnt);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await UnexpectedAsync(response, cancellationToken).ConfigureAwait(false);
        return chosen;
    }

    internal async Task TrustAsync(string serviceKey, string sessionId, string scnt, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = Get(TrustUri);
        AddSessionHeaders(request, serviceKey, sessionId, scnt);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await UnexpectedAsync(response, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<string> GetServiceKeyAsync(CancellationToken cancellationToken)
    {
        string redirectFailure;
        try
        {
            using HttpRequestMessage request = Get(ServiceKeyRedirectUri);
            using HttpResponseMessage redirect = await SendAsync(_serviceKeyClient, request, cancellationToken).ConfigureAwait(false);
            string? redirectKey = IsRedirect(redirect.StatusCode) ? WidgetKey(redirect.Headers.Location) : null;
            if (redirectKey is not null)
                return redirectKey;
            redirectFailure = $"the public redirect returned HTTP {(int)redirect.StatusCode} without a valid key";
        }
        catch (AppleAuthException ex) when (ex.Error == AppleAuthError.Unexpected && ex.StatusCode is null)
        {
            redirectFailure = "the public redirect could not be reached";
        }

        Logger.Warn("Apple's public sign-in configuration was unavailable; trying the legacy endpoint.");
        using HttpRequestMessage legacy = Get(ServiceKeyUri);
        using HttpResponseMessage response = await SendAsync(_serviceKeyClient, legacy, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException(AppleAuthError.Unexpected,
                $"Could not load Apple's sign-in configuration: {redirectFailure}; the legacy endpoint returned HTTP {(int)response.StatusCode}.",
                (int)response.StatusCode);
        AppleServiceKeyResponse? key = await TryReadAsync(response, AppleAuthJsonContext.Default.AppleServiceKeyResponse, cancellationToken).ConfigureAwait(false);
        string? value = key?.AuthServiceKey?.Trim();
        if (!ValidWidgetKey(value))
            throw new AppleAuthException(AppleAuthError.InvalidResponse,
                $"Could not load Apple's sign-in configuration: {redirectFailure}; the legacy endpoint returned an invalid key.");
        return value!;
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static string? WidgetKey(Uri? location)
    {
        if (location is not { IsAbsoluteUri: true, IsDefaultPort: true, Scheme: "https", UserInfo.Length: 0, Fragment.Length: 0 }
            || !location.IdnHost.Equals("idmsa.apple.com", StringComparison.OrdinalIgnoreCase)
            || !location.AbsolutePath.Equals("/appleauth/signout", StringComparison.Ordinal)
            || location.Query.Length < 2)
            return null;

        string? value = null;
        bool found = false;
        foreach (string part in location.Query[1..].Split('&'))
        {
            int equals = part.IndexOf('=');
            if (equals < 0 || !part.AsSpan(0, equals).SequenceEqual("widgetKey"))
                continue;
            if (found)
                return null;
            found = true;
            value = Uri.UnescapeDataString(part[(equals + 1)..]);
        }
        if (!ValidWidgetKey(value))
            return null;
        return value;
    }

    private static bool ValidWidgetKey(string? value)
        => value is { Length: > 0 and <= 256 } && value.All(c => c is > ' ' and < '\u007f');

    private async Task<string> GetHashcashAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = Get(SignInUri);
        request.Headers.Accept.ParseAdd("application/json");
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await UnexpectedAsync(response, cancellationToken).ConfigureAwait(false);
        if (!int.TryParse(Header(response, "X-Apple-HC-Bits"), out int bits) || Header(response, "X-Apple-HC-Challenge") is not { } challenge)
            throw new AppleAuthException(AppleAuthError.InvalidResponse, "Apple did not send the sign-in proof-of-work challenge.");
        try
        {
            return await Task.Run(() => AppleHashcash.Make(bits, challenge, _utcNow(), cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new AppleAuthException(AppleAuthError.InvalidResponse, "Apple sent an invalid proof-of-work challenge.", inner: ex);
        }
    }

    internal Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => SendAsync(_client, request, cancellationToken);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                response.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return response;
        }
        catch (HttpRequestException ex)
        {
            throw new AppleAuthException(AppleAuthError.Unexpected, $"Could not connect to Apple: {ex.Message}", inner: ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AppleAuthException(AppleAuthError.Unexpected, "Apple did not respond in time.", inner: ex);
        }
    }

    internal static HttpRequestMessage Get(Uri uri) => new(HttpMethod.Get, uri);

    internal static HttpRequestMessage Json<T>(HttpMethod method, Uri uri, T body, JsonTypeInfo<T> type)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, type)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private static void AddWidgetHeaders(HttpRequestMessage request, string serviceKey)
    {
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("X-Apple-Widget-Key", serviceKey);
    }

    internal static void AddSessionHeaders(HttpRequestMessage request, string serviceKey, string sessionId, string scnt)
    {
        if (request.Headers.Accept.Count == 0)
            request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("X-Apple-ID-Session-Id", sessionId);
        request.Headers.TryAddWithoutValidation("X-Apple-Widget-Key", serviceKey);
        request.Headers.TryAddWithoutValidation("scnt", scnt);
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out IEnumerable<string>? values)
            && values.FirstOrDefault()?.Trim() is { Length: > 0 and <= 4096 } value
            && !value.Any(char.IsControl) ? value : null;

    internal static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response, CancellationToken cancellationToken, int maximumBytes = MaximumResponseBytes)
    {
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new AppleAuthException(AppleAuthError.InvalidResponse, "Apple sent an unexpectedly large response.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maximumBytes)
                throw new AppleAuthException(AppleAuthError.InvalidResponse, "Apple sent an unexpectedly large response.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    internal static async Task<T?> TryReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken cancellationToken)
        => TryParse(await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false), type);

    internal static T? TryParse<T>(byte[] body, JsonTypeInfo<T> type)
    {
        if (body.Length == 0)
            return default;
        try
        {
            return JsonSerializer.Deserialize(body, type);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static async Task<AppleAuthException> UnexpectedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        AppleSignInResponse? body = null;
        try
        {
            body = await TryReadAsync(response, AppleAuthJsonContext.Default.AppleSignInResponse, cancellationToken).ConfigureAwait(false);
        }
        catch (AppleAuthException)
        {
        }
        if ((int)response.StatusCode == 503 && body is null)
            return new AppleAuthException(AppleAuthError.ServiceUnavailable, "Apple's sign-in service is temporarily unavailable. Try again later.", 503);
        return Unexpected((int)response.StatusCode);
    }

    private static AppleAuthException Unexpected(int status)
        => new(AppleAuthError.Unexpected, $"Apple's sign-in service returned HTTP {status}.", status);

    private static AppleAuthException InvalidCredentials()
        => new(AppleAuthError.InvalidCredentials, "The Apple ID or password is incorrect.", 401);

    private static AppleAuthException InvalidResponse()
        => new(AppleAuthError.InvalidResponse, "Apple's sign-in service sent a response that could not be read.");

    private static AppleAuthException NoTrustedPhones()
        => new(AppleAuthError.NoTrustedPhoneNumbers,
            "This Apple ID has no trusted phone number to send a code to. Add one at https://appleid.apple.com, then try again.");

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string text = new([.. value.Trim().Where(c => !char.IsControl(c))]);
        return text.Length > 200 ? text[..200] : text;
    }
}

/// <summary>Everything a security-key exchange needs from the surrounding sign-in.</summary>
public sealed class AppleSecurityKeyContext
{
    internal AppleSecurityKeyContext(
        AppleAuthClient client, IAppleSignInPrompts prompts, string serviceKey, string sessionId, string scnt, AppleFsaChallenge challenge)
    {
        Client = client;
        Prompts = prompts;
        ServiceKey = serviceKey;
        SessionId = sessionId;
        Scnt = scnt;
        Challenge = challenge.Challenge;
        KeyHandles = challenge.KeyHandles ?? [];
        RelyingPartyId = challenge.RpId;
        AllowedCredentials = challenge.AllowedCredentials;
    }

    internal AppleAuthClient Client { get; }
    public IAppleSignInPrompts Prompts { get; }
    internal string ServiceKey { get; }
    internal string SessionId { get; }
    internal string Scnt { get; }
    public string? Challenge { get; }
    public IReadOnlyList<string> KeyHandles { get; }
    public string? RelyingPartyId { get; }
    public string? AllowedCredentials { get; }
}

public interface IAppleSecurityKeyAuthenticator
{
    Task AuthenticateAsync(AppleSecurityKeyContext context, CancellationToken cancellationToken);
}

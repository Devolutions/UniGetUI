using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace UniGetUI.Extensions.Xcode.AppleAuth;

/// <summary>
/// The Apple ID UniGetUI downloads Xcode with. Its password and cookies live only in UniGetUI's own Keychain item and
/// cookie jar; other tools' accounts are never read.
/// </summary>
public sealed class AppleAccount : IDisposable
{
    internal const string StoreItem = "account";
    private const int MaximumCookies = 200;

    private readonly ISecretStore? _store;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly AppleAuthClient _auth;
    private bool _loaded;
    private bool _secretsLoaded;
    private bool _hasStoredItem;
    private bool _hasSavedPasswordFromMetadata;
    private bool _hasWrittenMetadata;
    private long? _storedModificationStamp;
    private byte[]? _storedDigest;
    private string? _appleId;
    private string? _password;

    public AppleAccount(ISecretStore? store = null) : this(store, null) { }

    internal AppleAccount(ISecretStore? store, HttpMessageHandler? handler, IAppleSecurityKeyAuthenticator? securityKeys = null)
    {
        _store = store;
        Cookies = new CookieContainer { PerDomainCapacity = 100 };
        bool useInjectedTransport = handler is not null;
        handler ??= new AppleCurlTransport(Cookies);
        Http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("UniGetUI (Macintosh; macOS)");
        _auth = useInjectedTransport
            ? new AppleAuthClient(Http, () => DateTime.UtcNow) { SecurityKeys = securityKeys }
            : new AppleAuthClient(Http) { SecurityKeys = securityKeys };
    }

    public static AppleAccount CreateDefault()
        => new(OperatingSystem.IsMacOS() ? new KeychainSecretStore() : null);

    /// <summary>The jar that holds this account's Apple cookies, shared with <see cref="Http"/>.</summary>
    public CookieContainer Cookies { get; }

    /// <summary>A client carrying this account's cookies, for Apple's developer download hosts only.</summary>
    public HttpClient Http { get; }

    public string? AppleId => _appleId;
    public bool HasSavedPassword => _secretsLoaded ? _password is not null : _hasSavedPasswordFromMetadata;
    public bool HasStoredItem => _hasStoredItem;
    public AppleSession? Session { get; private set; }
    public bool IsSignedIn => Session is not null;

    public event EventHandler? Changed;

    /// <summary>Reads the saved Apple ID metadata, without asking Apple anything or unlocking Keychain secrets.</summary>
    public void LoadSaved()
    {
        _lock.Wait();
        try
        {
            LoadSavedCore(refresh: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Loads the saved account and asks Apple whether its session still holds. Never prompts.</summary>
    public async Task<AppleSession?> RefreshAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LoadSecretsRequired();
            if (_appleId is null)
                return SetSession(null);
            AppleSession? session = await TryValidateAsync(cancellationToken).ConfigureAwait(false);
            EnsureStoredAccountUnchanged();
            return SetSession(session);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns a valid session, signing in again with the saved password when the old one expired and asking the user
    /// only for what Apple still needs.
    /// </summary>
    public async Task<AppleSession> EnsureSignedInAsync(IAppleSignInPrompts prompts, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LoadSecretsRequired();
            if (_appleId is not null)
            {
                AppleSession? session = await TryValidateAsync(cancellationToken).ConfigureAwait(false);
                EnsureStoredAccountUnchanged();
                if (session is not null)
                    return SetSession(session)!;
            }
            return await SignInLockedAsync(prompts, _appleId, useSavedPassword: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Signs in from scratch, replacing any saved account once Apple accepts the new one.</summary>
    public async Task<AppleSession> SignInAsync(IAppleSignInPrompts prompts, string? suggestedAppleId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
                EnsureStoredAccountUnchanged();
            else
                LoadSavedCore(refresh: false);
            return await SignInLockedAsync(prompts, suggestedAppleId, useSavedPassword: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Refreshes all session cookies by signing in again with the saved credentials when Apple's developer download
    /// service considers its session expired even though App Store Connect still accepted the previous session.
    /// </summary>
    internal async Task<AppleSession> ReauthenticateAsync(IAppleSignInPrompts prompts, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LoadSecretsRequired();
            return await SignInLockedAsync(prompts, _appleId, useSavedPassword: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Forgets the Apple ID, its password and every cookie. Apple's own session simply expires.</summary>
    public void SignOut()
    {
        _lock.Wait();
        try
        {
            SignOutLocked();
        }
        finally
        {
            _lock.Release();
        }
    }

    internal async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SignOutLocked();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void SignOutLocked()
    {
        if (_loaded)
            EnsureStoredAccountUnchanged();
        else
            LoadSavedCore(refresh: false);
        _store?.Delete(StoreItem);
        ClearCookies();
        _appleId = null;
        _password = null;
        _hasSavedPasswordFromMetadata = false;
        _hasStoredItem = false;
        _hasWrittenMetadata = false;
        _storedModificationStamp = null;
        _storedDigest = null;
        _loaded = true;
        _secretsLoaded = true;
        SetSession(null);
    }

    /// <summary>Writes the current cookies back, for example after a download handed out a new authorization cookie.</summary>
    public void SaveCookies()
    {
        _lock.Wait();
        try
        {
            if (_appleId is not null)
                Save();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Keeps account changes from replacing the cookie jar during an authenticated request.</summary>
    internal async Task<SessionLease?> AcquireSessionAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool leased = false;
        try
        {
            if (_appleId is null || Session is null)
                return null;
            EnsureStoredAccountUnchanged();
            var lease = new SessionLease(this, _appleId, Session);
            leased = true;
            return lease;
        }
        finally
        {
            if (!leased)
                _lock.Release();
        }
    }

    internal sealed class SessionLease(AppleAccount owner, string appleId, AppleSession session) : IDisposable
    {
        private AppleAccount? _owner = owner;

        public string AppleId { get; } = appleId;
        public AppleSession Session { get; } = session;

        public void SaveCookies()
            => (_owner ?? throw new ObjectDisposedException(nameof(SessionLease))).Save();

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?._lock.Release();
    }

    private async Task<AppleSession> SignInLockedAsync(
        IAppleSignInPrompts prompts, string? appleId, bool useSavedPassword, CancellationToken cancellationToken)
    {
        string? previousId = _appleId;
        string? previousPassword = _password;
        bool previousSecretsLoaded = _secretsLoaded;
        bool previousHasStoredItem = _hasStoredItem;
        bool previousHasSavedPassword = _hasSavedPasswordFromMetadata;
        Cookie[] previousCookies = [.. Cookies.GetAllCookies().Cast<Cookie>().Where(c => !c.Expired).Select(Clone)];
        ClearCookies();
        var wrapped = new SavedPasswordPrompts(prompts, useSavedPassword && _secretsLoaded && _appleId is not null && _password is not null
            ? new AppleCredentials(_appleId, _password) : null);
        try
        {
            (string signedIn, AppleSession session) = await _auth.SignInAsync(wrapped, appleId, cancellationToken).ConfigureAwait(false);
            _appleId = signedIn;
            _password = wrapped.Password;
            Save();
            return SetSession(session)!;
        }
        catch
        {
            // A refused or abandoned sign-in leaves the previous account as it was.
            ClearCookies();
            foreach (Cookie cookie in previousCookies)
                Cookies.Add(cookie);
            _appleId = previousId;
            _password = previousPassword;
            _secretsLoaded = previousSecretsLoaded;
            _hasStoredItem = previousHasStoredItem;
            _hasSavedPasswordFromMetadata = previousHasSavedPassword;
            throw;
        }
    }

    private async Task<AppleSession?> TryValidateAsync(CancellationToken cancellationToken)
        => await _auth.ValidateSessionAsync(cancellationToken).ConfigureAwait(false);

    private AppleSession? SetSession(AppleSession? session)
    {
        bool changed = Session != session;
        Session = session;
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
        return session;
    }

    private void LoadSavedCore(bool refresh)
    {
        if (_loaded && !refresh)
            return;
        if (_store is null)
        {
            _loaded = true;
            _secretsLoaded = true;
            return;
        }
        if (!_store.SupportsDeferredMetadata)
        {
            LoadSecretsFromStore(refresh);
            return;
        }

        SecretMetadata metadata = _store.ReadMetadata(StoreItem);
        if (!metadata.Exists)
        {
            if (_loaded && (_hasStoredItem || _appleId is not null))
            {
                ClearCookies();
                _appleId = null;
                _password = null;
                _hasSavedPasswordFromMetadata = false;
                SetSession(null);
            }
            _hasStoredItem = false;
            _hasWrittenMetadata = false;
            _storedModificationStamp = null;
            _storedDigest = null;
            _loaded = true;
            _secretsLoaded = true;
            return;
        }

        if (metadata.Metadata is { Length: > 0 } rawMeta)
        {
            if (!TryParseMetadata(rawMeta, out string? appleId, out bool hasSavedPassword, out byte[]? digest))
                throw new InvalidDataException("The saved Apple account metadata could not be read.");
            if (_secretsLoaded && _storedDigest is not null && CryptographicOperations.FixedTimeEquals(digest, _storedDigest))
            {
                _storedModificationStamp = metadata.ModificationStamp;
                _hasWrittenMetadata = true;
                _loaded = true;
                return;
            }
            ClearCookies();
            _password = null;
            _secretsLoaded = false;
            _appleId = appleId;
            _hasSavedPasswordFromMetadata = hasSavedPassword;
            _hasStoredItem = true;
            _hasWrittenMetadata = true;
            _storedModificationStamp = metadata.ModificationStamp;
            _storedDigest = digest;
            if (_loaded)
                SetSession(null);
            _loaded = true;
            return;
        }

        if (_secretsLoaded && _storedModificationStamp == metadata.ModificationStamp)
        {
            _loaded = true;
            return;
        }
        ClearCookies();
        _appleId = null;
        _password = null;
        _hasSavedPasswordFromMetadata = false;
        _secretsLoaded = false;
        _hasStoredItem = true;
        _hasWrittenMetadata = false;
        _storedModificationStamp = metadata.ModificationStamp;
        _storedDigest = null;
        if (_loaded)
            SetSession(null);
        _loaded = true;
    }

    private void LoadSecretsRequired()
    {
        if (_store is null)
        {
            _loaded = true;
            _secretsLoaded = true;
            return;
        }
        if (!_store.SupportsDeferredMetadata)
        {
            LoadSecretsFromStore(refresh: false);
            EnsureStoredAccountUnchanged();
            return;
        }

        if (_loaded)
            EnsureStoredAccountUnchanged();
        else
            LoadSavedCore(refresh: false);

        if (!_hasStoredItem || _secretsLoaded)
            return;

        byte[]? expectedDigest = _storedDigest;
        string? previousAppleId = _appleId;
        LoadSecretsFromStore(refresh: true);
        if (expectedDigest is not null && (_storedDigest is null || !CryptographicOperations.FixedTimeEquals(expectedDigest, _storedDigest)))
            throw new IOException("The saved Apple account changed outside UniGetUI. Refresh it before trying again.");
        if (!string.Equals(previousAppleId, _appleId, StringComparison.Ordinal))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void LoadSecretsFromStore(bool refresh)
    {
        if (_secretsLoaded && _loaded && !refresh)
            return;
        if (_store is null)
        {
            _loaded = true;
            _secretsLoaded = true;
            return;
        }

        byte[]? bytes = _store.Read(StoreItem);
        if (bytes is null)
        {
            if (_loaded && (_storedDigest is not null || _hasStoredItem))
            {
                ClearCookies();
                _appleId = null;
                _password = null;
                _hasSavedPasswordFromMetadata = false;
                SetSession(null);
            }
            _hasStoredItem = false;
            _hasWrittenMetadata = false;
            _storedModificationStamp = null;
            _storedDigest = null;
            _loaded = true;
            _secretsLoaded = true;
            return;
        }
        try
        {
            byte[] digest = SHA256.HashData(bytes);
            if (_secretsLoaded && _loaded && _storedDigest is not null
                && CryptographicOperations.FixedTimeEquals(digest, _storedDigest))
                return;

            StoredAccount? stored = DeserializeJson(bytes, AppleAccountJsonContext.Default.StoredAccount);
            if (string.IsNullOrWhiteSpace(stored?.AppleId))
                throw new InvalidDataException("The saved Apple account is invalid.");
            Cookie[] cookies = [.. (stored.Cookies ?? []).Take(MaximumCookies)
                .OfType<StoredCookie>().Select(ToCookie).OfType<Cookie>()];
            bool preserveLoadedMetadataState = _store.SupportsDeferredMetadata && !_secretsLoaded;
            ClearCookies();
            _appleId = stored.AppleId;
            _password = string.IsNullOrEmpty(stored.Password) ? null : stored.Password;
            _hasSavedPasswordFromMetadata = _password is not null;
            foreach (Cookie cookie in cookies)
                Cookies.Add(cookie);
            _storedDigest = digest;
            _hasStoredItem = true;
            if (_loaded && !preserveLoadedMetadataState)
                SetSession(null);
            _loaded = true;
            _secretsLoaded = true;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The saved Apple account could not be read.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void Save()
    {
        if (_store is null || _appleId is null)
            return;
        EnsureStoredAccountUnchanged();
        var stored = new StoredAccount
        {
            AppleId = _appleId,
            Password = _password,
            Cookies = [.. Cookies.GetAllCookies().Cast<Cookie>()
                .Where(c => !c.Expired && IsAppleDomain(c.Domain))
                .Take(MaximumCookies)
                .Select(c => new StoredCookie
                {
                    Name = c.Name,
                    Value = c.Value,
                    Domain = c.Domain,
                    Path = c.Path,
                    Expires = c.Expires == DateTime.MinValue ? null : new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeSeconds(),
                    Secure = c.Secure,
                    HttpOnly = c.HttpOnly,
                })],
        };
        byte[] bytes = SerializeJson(stored, AppleAccountJsonContext.Default.StoredAccount);
        try
        {
            byte[] digest = SHA256.HashData(bytes);
            if (_store.SupportsDeferredMetadata && _hasWrittenMetadata && _storedDigest is not null
                && CryptographicOperations.FixedTimeEquals(digest, _storedDigest))
            {
                _secretsLoaded = true;
                _hasStoredItem = true;
                _hasSavedPasswordFromMetadata = _password is not null;
                return;
            }

            if (_store.SupportsDeferredMetadata)
            {
                byte[] metaBytes = SerializeJson(new StoredAccountMetadata
                {
                    AppleId = _appleId,
                    HasSavedPassword = _password is not null,
                    Digest = Convert.ToHexStringLower(digest),
                }, AppleAccountJsonContext.Default.StoredAccountMetadata);
                _store.Write(StoreItem, bytes, metaBytes);
                _storedModificationStamp = _store.ReadMetadata(StoreItem).ModificationStamp;
                _hasWrittenMetadata = true;
            }
            else
            {
                _store.Write(StoreItem, bytes);
            }
            _storedDigest = digest;
            _hasStoredItem = true;
            _hasSavedPasswordFromMetadata = _password is not null;
            _loaded = true;
            _secretsLoaded = true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void EnsureStoredAccountUnchanged()
    {
        if (_store is null)
            return;
        if (_store.SupportsDeferredMetadata)
        {
            SecretMetadata metadata = _store.ReadMetadata(StoreItem);
            bool unchanged = !_hasStoredItem
                ? !metadata.Exists
                : metadata.Exists && (metadata.Metadata is { Length: > 0 } rawMeta
                    ? TryParseMetadata(rawMeta, out _, out _, out byte[]? digest)
                      && _storedDigest is not null
                      && CryptographicOperations.FixedTimeEquals(digest, _storedDigest)
                    : !_hasWrittenMetadata && _storedModificationStamp == metadata.ModificationStamp);
            if (!unchanged)
                throw new IOException("The saved Apple account changed outside UniGetUI. Refresh it before trying again.");
            return;
        }

        byte[]? bytes = _store.Read(StoreItem);
        try
        {
            bool unchanged = bytes is null
                ? _storedDigest is null
                : _storedDigest is not null
                  && CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), _storedDigest);
            if (!unchanged)
                throw new IOException("The saved Apple account changed outside UniGetUI. Refresh it before trying again.");
        }
        finally
        {
            if (bytes is not null)
                CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool TryParseMetadata(ReadOnlySpan<byte> rawMeta, out string? appleId, out bool hasSavedPassword, out byte[]? digest)
    {
        appleId = null;
        hasSavedPassword = false;
        digest = null;
        try
        {
            StoredAccountMetadata? parsed = DeserializeJson(rawMeta, AppleAccountJsonContext.Default.StoredAccountMetadata);
            if (string.IsNullOrWhiteSpace(parsed?.AppleId) || string.IsNullOrEmpty(parsed.Digest) || parsed.Digest.Length != 64)
                return false;
            digest = Convert.FromHexString(parsed.Digest);
            appleId = parsed.AppleId;
            hasSavedPassword = parsed.HasSavedPassword;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return false;
        }
    }

    private static byte[] SerializeJson<T>(T value, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

    private static T? DeserializeJson<T>(ReadOnlySpan<byte> bytes, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Deserialize(bytes, typeInfo);

    private void ClearCookies()
    {
        foreach (Cookie cookie in Cookies.GetAllCookies().Cast<Cookie>())
            cookie.Expired = true;
    }

    internal static bool IsAppleDomain(string domain)
    {
        string host = domain.TrimStart('.').ToLowerInvariant();
        return host == "apple.com" || host.EndsWith(".apple.com", StringComparison.Ordinal);
    }

    private static Cookie Clone(Cookie cookie) => new(cookie.Name, cookie.Value, cookie.Path, cookie.Domain)
    {
        Secure = cookie.Secure,
        HttpOnly = cookie.HttpOnly,
        Expires = cookie.Expires,
    };

    private static Cookie? ToCookie(StoredCookie stored)
    {
        if (string.IsNullOrEmpty(stored.Name) || stored.Domain is null || !IsAppleDomain(stored.Domain))
            return null;
        try
        {
            var cookie = new Cookie(stored.Name, stored.Value ?? "", string.IsNullOrEmpty(stored.Path) ? "/" : stored.Path, stored.Domain)
            {
                Secure = stored.Secure,
                HttpOnly = stored.HttpOnly,
            };
            if (stored.Expires is { } expires)
            {
                DateTimeOffset at = DateTimeOffset.FromUnixTimeSeconds(expires);
                if (at <= DateTimeOffset.UtcNow)
                    return null;
                cookie.Expires = at.UtcDateTime;
            }
            return cookie;
        }
        catch (Exception ex) when (ex is CookieException or ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        Http.Dispose();
        _lock.Dispose();
    }

    /// <summary>Answers the first password question with the saved one, then hands over to the user.</summary>
    private sealed class SavedPasswordPrompts(IAppleSignInPrompts inner, AppleCredentials? saved) : IAppleSignInPrompts
    {
        private AppleCredentials? _saved = saved;

        internal string? Password { get; private set; }

        public async Task<AppleCredentials?> AskCredentialsAsync(string? appleId, string? error, CancellationToken cancellationToken)
        {
            AppleCredentials? answer = _saved;
            _saved = null;
            answer ??= await inner.AskCredentialsAsync(appleId, error, cancellationToken).ConfigureAwait(false);
            Password = answer?.Password;
            return answer;
        }

        public Task<AppleCodeAnswer?> AskCodeAsync(AppleCodeRequest request, CancellationToken cancellationToken)
            => inner.AskCodeAsync(request, cancellationToken);

        public Task<AppleTrustedPhone?> ChoosePhoneAsync(IReadOnlyList<AppleTrustedPhone> phones, CancellationToken cancellationToken)
            => inner.ChoosePhoneAsync(phones, cancellationToken);

        public Task<string?> AskSecurityKeyPinAsync(string? error, CancellationToken cancellationToken)
            => inner.AskSecurityKeyPinAsync(error, cancellationToken);

        public void ShowSecurityKeyTouch(bool waiting) => inner.ShowSecurityKeyTouch(waiting);

        public Task<Uri?> AskFederatedCallbackAsync(Uri signInPage, CancellationToken cancellationToken)
            => inner.AskFederatedCallbackAsync(signInPage, cancellationToken);
    }
}

internal sealed class StoredAccount
{
    public string? AppleId { get; set; }
    public string? Password { get; set; }
    public StoredCookie[]? Cookies { get; set; }
}

internal sealed class StoredCookie
{
    public string? Name { get; set; }
    public string? Value { get; set; }
    public string? Domain { get; set; }
    public string? Path { get; set; }
    public long? Expires { get; set; }
    public bool Secure { get; set; }
    public bool HttpOnly { get; set; }
}

internal sealed class StoredAccountMetadata
{
    public string? AppleId { get; set; }
    public bool HasSavedPassword { get; set; }
    public string? Digest { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StoredAccount))]
[JsonSerializable(typeof(StoredAccountMetadata))]
internal partial class AppleAccountJsonContext : JsonSerializerContext;

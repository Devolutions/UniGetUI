using System.Net;
using System.Text;
using UniGetUI.Extensions.Xcode.AppleAuth;
using static UniGetUI.Extensions.Xcode.Tests.AppleAuth.AppleAuthClientTests;

namespace UniGetUI.Extensions.Xcode.Tests.AppleAuth;

public sealed class AppleAccountTests
{
    private readonly MemoryStore _store = new();

    [Fact]
    public async Task SignInSavesTheAccountAndItsAppleCookiesOnly()
    {
        var apple = new FakeApple();
        using var account = new AppleAccount(_store, apple);
        account.Cookies.Add(new Cookie("myacinfo", "token", "/", ".apple.com") { Secure = true, HttpOnly = true });
        account.Cookies.Add(new Cookie("other", "x", "/", "example.com"));

        AppleSession session = await account.SignInAsync(new Prompts(), null, CancellationToken.None);
        account.Cookies.Add(new Cookie("myacinfo", "token", "/", ".apple.com") { Secure = true, HttpOnly = true });
        account.Cookies.Add(new Cookie("other", "x", "/", "example.com"));
        account.SaveCookies();

        Assert.Equal("Test Team", session.TeamName);
        Assert.Equal("test@example.com", account.AppleId);
        Assert.True(account.HasSavedPassword);
        string saved = Encoding.UTF8.GetString(_store.Items[AppleAccount.StoreItem]);
        Assert.Contains("\"appleId\":\"test@example.com\"", saved);
        Assert.Contains("\"name\":\"myacinfo\"", saved);
        Assert.DoesNotContain("example.com\"", saved.Replace("test@example.com", ""));
    }

    [Fact]
    public async Task SignInClearsTheCookiesOfThePreviousAccount()
    {
        var apple = new FakeApple();
        using var account = new AppleAccount(_store, apple);
        account.Cookies.Add(new Cookie("myacinfo", "old", "/", ".apple.com"));

        await account.SignInAsync(new Prompts(), null, CancellationToken.None);

        Assert.DoesNotContain(account.Cookies.GetAllCookies().Cast<Cookie>(), c => !c.Expired);
    }

    [Fact]
    public async Task SavedAccountIsRestoredWithItsCookies()
    {
        using (var first = new AppleAccount(_store, new FakeApple()))
        {
            await first.SignInAsync(new Prompts(), null, CancellationToken.None);
            first.Cookies.Add(new Cookie("myacinfo", "token", "/", ".apple.com") { Expires = DateTime.UtcNow.AddDays(1) });
            first.Cookies.Add(new Cookie("DSESSIONID", "session", "/", ".apple.com"));
            first.SaveCookies();
        }

        using var second = new AppleAccount(_store, new FakeApple());
        AppleSession? session = await second.RefreshAsync(CancellationToken.None);

        Assert.NotNull(session);
        Assert.True(second.IsSignedIn);
        Assert.Equal("test@example.com", second.AppleId);
        Cookie[] cookies = [.. second.Cookies.GetAllCookies().Cast<Cookie>()];
        Assert.Equal(["DSESSIONID", "myacinfo"], cookies.Select(c => c.Name).Order());
    }

    [Fact]
    public async Task RefreshWithoutSavedAccountDoesNotCallApple()
    {
        var apple = new FakeApple();
        using var account = new AppleAccount(_store, apple);

        Assert.Null(await account.RefreshAsync(CancellationToken.None));
        Assert.Empty(apple.Requests);
    }

    [Fact]
    public async Task ExpiredSessionSignsInAgainWithTheSavedPassword()
    {
        using (var first = new AppleAccount(_store, new FakeApple()))
            await first.SignInAsync(new Prompts(), null, CancellationToken.None);

        var apple = new FakeApple { RequireSignIn = true };
        using var account = new AppleAccount(_store, apple);
        var prompts = new Prompts();
        int changes = 0;
        account.Changed += (_, _) => changes++;

        Assert.Null(await account.RefreshAsync(CancellationToken.None));
        AppleSession session = await account.EnsureSignedInAsync(prompts, CancellationToken.None);

        Assert.Equal("Test User", session.FullName);
        Assert.Empty(prompts.CredentialRequests);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task ValidSessionNeedsNoSignIn()
    {
        using (var first = new AppleAccount(_store, new FakeApple()))
            await first.SignInAsync(new Prompts(), null, CancellationToken.None);
        var apple = new FakeApple();
        using var account = new AppleAccount(_store, apple);

        await account.EnsureSignedInAsync(new Prompts { Passwords = 0 }, CancellationToken.None);

        Assert.DoesNotContain("/appleauth/auth/signin/complete", apple.Paths);
    }

    [Fact]
    public async Task SessionServiceFailureDoesNotTriggerAutomaticSignIn()
    {
        using (var first = new AppleAccount(_store, new FakeApple()))
            await first.SignInAsync(new Prompts(), null, CancellationToken.None);
        var apple = new FakeApple { SessionStatus = 403 };
        using var account = new AppleAccount(_store, apple);
        var prompts = new Prompts();

        AppleAuthException error = await Assert.ThrowsAsync<AppleAuthException>(() =>
            account.EnsureSignedInAsync(prompts, CancellationToken.None));

        Assert.Equal(403, error.StatusCode);
        Assert.Empty(prompts.CredentialRequests);
        Assert.DoesNotContain("/appleauth/auth/signin/complete", apple.Paths);
    }

    [Fact]
    public async Task RefusedSavedPasswordAsksTheUserWithTheReason()
    {
        using (var first = new AppleAccount(_store, new FakeApple()))
            await first.SignInAsync(new Prompts(), null, CancellationToken.None);
        var apple = new FakeApple { RequireSignIn = true };
        apple.CompleteStatuses.Enqueue(401);
        using var account = new AppleAccount(_store, apple);
        var prompts = new Prompts();

        await account.EnsureSignedInAsync(prompts, CancellationToken.None);

        Assert.Equal([("test@example.com", "The Apple ID or password is incorrect.")], prompts.CredentialRequests);
    }

    [Fact]
    public async Task AbandonedSignInKeepsThePreviousAccount()
    {
        using var account = new AppleAccount(_store, new FakeApple());
        await account.SignInAsync(new Prompts(), null, CancellationToken.None);
        account.Cookies.Add(new Cookie("myacinfo", "token", "/", ".apple.com"));
        byte[] before = _store.Items[AppleAccount.StoreItem];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => account.SignInAsync(new Prompts { Passwords = 0 }, null, CancellationToken.None));

        Assert.Equal("test@example.com", account.AppleId);
        Assert.True(account.IsSignedIn);
        Assert.Same(before, _store.Items[AppleAccount.StoreItem]);
        Assert.Contains(account.Cookies.GetAllCookies().Cast<Cookie>(), c => c.Name == "myacinfo" && !c.Expired);
    }

    [Fact]
    public async Task SignOutForgetsEverything()
    {
        using var account = new AppleAccount(_store, new FakeApple());
        await account.SignInAsync(new Prompts(), null, CancellationToken.None);
        account.Cookies.Add(new Cookie("myacinfo", "token", "/", ".apple.com"));

        account.SignOut();

        Assert.Null(account.AppleId);
        Assert.False(account.IsSignedIn);
        Assert.False(account.HasSavedPassword);
        Assert.Empty(_store.Items);
        Assert.Empty(account.Cookies.GetCookies(new Uri("https://developer.apple.com/")));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"appleId":""}""")]
    public async Task UnreadableSavedAccountReportsAnErrorAndCanBeReadAfterRepair(string json)
    {
        _store.Items[AppleAccount.StoreItem] = Encoding.UTF8.GetBytes(json);
        using var account = new AppleAccount(_store, new FakeApple());

        await Assert.ThrowsAsync<InvalidDataException>(() => account.RefreshAsync(CancellationToken.None));
        _store.Items[AppleAccount.StoreItem] = Encoding.UTF8.GetBytes("""{"appleId":"repaired@example.com"}""");

        Assert.NotNull(await account.RefreshAsync(CancellationToken.None));
        Assert.Equal("repaired@example.com", account.AppleId);
    }

    [Fact]
    public void AFailedKeychainReadCanBeRetried()
    {
        _store.Items[AppleAccount.StoreItem] = Encoding.UTF8.GetBytes("""{"appleId":"test@example.com"}""");
        _store.FailRead = true;
        using var account = new AppleAccount(_store, new FakeApple());

        Assert.Throws<IOException>(account.LoadSaved);
        _store.FailRead = false;
        account.LoadSaved();

        Assert.Equal("test@example.com", account.AppleId);
    }

    [Fact]
    public async Task RefreshingSeesAnExternallyReplacedOrDeletedAccount()
    {
        using var account = new AppleAccount(_store, new FakeApple());
        await account.SignInAsync(new Prompts(), null, CancellationToken.None);
        account.Cookies.Add(new Cookie("old", "old", "/", ".apple.com"));
        account.SaveCookies();
        _store.Items[AppleAccount.StoreItem] = Encoding.UTF8.GetBytes(
            """{"appleId":"other@example.com","password":"different","cookies":[{"name":"new","value":"new","domain":".apple.com"}]}""");

        account.LoadSaved();

        Assert.Equal("other@example.com", account.AppleId);
        Assert.True(account.HasSavedPassword);
        Assert.False(account.IsSignedIn);
        Assert.Equal(["new"], account.Cookies.GetAllCookies().Cast<Cookie>().Where(c => !c.Expired).Select(c => c.Name));

        _store.Items.Remove(AppleAccount.StoreItem);
        account.LoadSaved();
        Assert.Null(account.AppleId);
        Assert.False(account.HasSavedPassword);
        Assert.DoesNotContain(account.Cookies.GetAllCookies().Cast<Cookie>(), c => !c.Expired);
    }

    [Theory]
    [InlineData("verify", false)]
    [InlineData("verify", true)]
    [InlineData("ensure", false)]
    [InlineData("ensure", true)]
    [InlineData("sign-in", false)]
    [InlineData("sign-in", true)]
    [InlineData("lease", false)]
    [InlineData("lease", true)]
    public async Task AccountOperationsRejectAChangedOrDeletedSavedIdentityBeforeUsingIt(string operation, bool deleted)
    {
        var apple = new FakeApple();
        using var account = new AppleAccount(_store, apple);
        await account.SignInAsync(new Prompts(), null, CancellationToken.None);
        apple.Requests.Clear();
        if (deleted)
            _store.Items.Remove(AppleAccount.StoreItem);
        else
            _store.Items[AppleAccount.StoreItem] = Encoding.UTF8.GetBytes("""{"appleId":"other@example.com"}""");
        var prompts = new Prompts();

        IOException error = await Assert.ThrowsAsync<IOException>(async () =>
        {
            switch (operation)
            {
                case "verify":
                    await account.RefreshAsync(CancellationToken.None);
                    break;
                case "ensure":
                    await account.EnsureSignedInAsync(prompts, CancellationToken.None);
                    break;
                case "sign-in":
                    await account.SignInAsync(prompts, null, CancellationToken.None);
                    break;
                case "lease":
                    using (await account.AcquireSessionAsync(CancellationToken.None)) { }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        });

        Assert.Contains("changed outside UniGetUI", error.Message);
        Assert.Empty(apple.Requests);
        Assert.Empty(prompts.CredentialRequests);
        account.LoadSaved();
        Assert.Equal(deleted ? null : "other@example.com", account.AppleId);
        Assert.False(account.IsSignedIn);
    }

    [Fact]
    public async Task AFailedKeychainReadReleasesTheSessionLeaseLock()
    {
        using var account = new AppleAccount(_store, new FakeApple());
        await account.SignInAsync(new Prompts(), null, CancellationToken.None);
        _store.FailRead = true;

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            using (await account.AcquireSessionAsync(CancellationToken.None)) { }
        });

        _store.FailRead = false;
        using AppleAccount.SessionLease? lease = await account.AcquireSessionAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(lease);
    }

    [Theory]
    [InlineData(true, 200)]
    [InlineData(false, 200)]
    [InlineData(false, 401)]
    public async Task AccountChangedDuringValidationIsNotReturnedOrUsedToSignInAgain(bool refresh, int status)
    {
        var apple = new FakeApple();
        using var account = new AppleAccount(_store, apple);
        await account.SignInAsync(new Prompts(), null, CancellationToken.None);
        apple.Requests.Clear();
        apple.SessionStatus = status;
        apple.SessionResponded = () =>
            _store.Items[AppleAccount.StoreItem] = Encoding.UTF8.GetBytes("""{"appleId":"other@example.com"}""");
        var prompts = new Prompts();

        IOException error = await Assert.ThrowsAsync<IOException>(async () =>
        {
            if (refresh)
                await account.RefreshAsync(CancellationToken.None);
            else
                await account.EnsureSignedInAsync(prompts, CancellationToken.None);
        });

        Assert.Contains("changed outside UniGetUI", error.Message);
        Assert.Equal(["/olympus/v1/session"], apple.Paths);
        Assert.Empty(prompts.CredentialRequests);
        account.LoadSaved();
        Assert.Equal("other@example.com", account.AppleId);
        Assert.False(account.IsSignedIn);
    }

    [Fact]
    public async Task AFailedSignOutKeepsTheAccountAndSession()
    {
        using var account = new AppleAccount(_store, new FakeApple());
        await account.SignInAsync(new Prompts(), null, CancellationToken.None);
        account.Cookies.Add(new Cookie("session", "token", "/", ".apple.com"));
        _store.FailDelete = true;

        Assert.Throws<IOException>(account.SignOut);

        Assert.Equal("test@example.com", account.AppleId);
        Assert.True(account.HasSavedPassword);
        Assert.True(account.IsSignedIn);
        Assert.Contains(account.Cookies.GetAllCookies().Cast<Cookie>(), c => c.Name == "session" && !c.Expired);
        Assert.Contains(AppleAccount.StoreItem, _store.Items.Keys);
    }

    [Fact]
    public async Task SignOutDoesNotDeleteAnAccountChangedByAnotherProcess()
    {
        using var account = new AppleAccount(_store, new FakeApple());
        await account.SignInAsync(new Prompts(), null, CancellationToken.None);
        _store.Items[AppleAccount.StoreItem] = Encoding.UTF8.GetBytes("""{"appleId":"other@example.com"}""");

        Assert.Throws<IOException>(account.SignOut);

        Assert.Equal("test@example.com", account.AppleId);
        account.LoadSaved();
        Assert.Equal("other@example.com", account.AppleId);
        Assert.Contains(AppleAccount.StoreItem, _store.Items.Keys);
    }

    [Fact]
    public async Task FailedAccountReplacementPreservesThePreviousAccount()
    {
        using var account = new AppleAccount(_store, new FakeApple());
        await account.SignInAsync(new Prompts(), null, CancellationToken.None);
        account.Cookies.Add(new Cookie("session", "old-token", "/", ".apple.com"));
        byte[] before = _store.Items[AppleAccount.StoreItem];
        _store.FailWrite = true;

        await Assert.ThrowsAsync<IOException>(() => account.SignInAsync(
            new Prompts { AppleId = "other@example.com", Password = "different" }, null, CancellationToken.None));

        Assert.Equal("test@example.com", account.AppleId);
        Assert.True(account.HasSavedPassword);
        Assert.True(account.IsSignedIn);
        Assert.Contains(account.Cookies.GetAllCookies().Cast<Cookie>(), c => c.Name == "session" && !c.Expired);
        Assert.Same(before, _store.Items[AppleAccount.StoreItem]);
    }

    [Fact]
    public async Task ForeignCookiesInTheSavedItemAreIgnored()
    {
        _store.Items[AppleAccount.StoreItem] = Encoding.UTF8.GetBytes(
            """{"appleId":"a@b.c","cookies":[{"name":"x","value":"1","domain":".evil.com","path":"/"},{"name":"y","value":"2","domain":"notapple.com"},{"name":"z","value":"3","domain":"idmsa.apple.com"},{"name":"old","value":"4","domain":"apple.com","expires":1}]}""");
        using var account = new AppleAccount(_store, new FakeApple());

        await account.RefreshAsync(CancellationToken.None);

        Assert.Equal(["z"], account.Cookies.GetAllCookies().Cast<Cookie>().Select(c => c.Name));
    }

    [Theory]
    [InlineData("apple.com", true)]
    [InlineData(".developer.apple.com", true)]
    [InlineData("notapple.com", false)]
    [InlineData("apple.com.evil.net", false)]
    public void AppleDomainsAreRecognized(string domain, bool expected)
        => Assert.Equal(expected, AppleAccount.IsAppleDomain(domain));

    [Fact]
    public async Task DeferredKeychainStoreNeverReadsSecretsOnStartupAndReadsAtMostOnceWhenNeeded()
    {
        var keychain = new DeferredMemoryStore();
        using (var initial = new AppleAccount(keychain, new FakeApple()))
        {
            await initial.SignInAsync(new Prompts(), null, CancellationToken.None);
            initial.Cookies.Add(new Cookie("myacinfo", "token", "/", ".apple.com") { Secure = true });
            initial.SaveCookies();
        }
        keychain.SecretReadCount = 0;

        using var restarted = new AppleAccount(keychain, new FakeApple());
        restarted.LoadSaved();
        restarted.LoadSaved();

        Assert.Equal(0, keychain.SecretReadCount);
        Assert.Equal("test@example.com", restarted.AppleId);
        Assert.True(restarted.HasSavedPassword);
        Assert.True(restarted.HasStoredItem);
        Assert.False(restarted.IsSignedIn);

        await restarted.EnsureSignedInAsync(new Prompts { Passwords = 0 }, CancellationToken.None);
        using (AppleAccount.SessionLease? lease = await restarted.AcquireSessionAsync(CancellationToken.None))
        {
            Assert.NotNull(lease);
            lease.SaveCookies();
        }
        restarted.LoadSaved();

        Assert.Equal(1, keychain.SecretReadCount);
        Assert.True(restarted.IsSignedIn);
    }

    [Fact]
    public async Task LegacyKeychainItemWithoutMetadataDelaysSecretReadUntilAnAppleOperationRuns()
    {
        var keychain = new DeferredMemoryStore();
        keychain.Items[AppleAccount.StoreItem] = Encoding.UTF8.GetBytes(
            """{"appleId":"legacy@example.com","password":"saved","cookies":[{"name":"myacinfo","value":"tok","domain":".apple.com"}]}""");

        using var account = new AppleAccount(keychain, new FakeApple());
        account.LoadSaved();

        Assert.Equal(0, keychain.SecretReadCount);
        Assert.Null(account.AppleId);
        Assert.True(account.HasStoredItem);

        AppleSession? session = await account.RefreshAsync(CancellationToken.None);
        account.LoadSaved();

        Assert.NotNull(session);
        Assert.Equal(1, keychain.SecretReadCount);
        Assert.Equal("legacy@example.com", account.AppleId);
    }

    [Fact]
    public async Task DeferredKeychainStoreDetectsExternalChangesViaMetadataWithoutReadingSecrets()
    {
        var keychain = new DeferredMemoryStore();
        using var account = new AppleAccount(keychain, new FakeApple());
        await account.SignInAsync(new Prompts(), null, CancellationToken.None);
        keychain.SecretReadCount = 0;

        byte[] replacement = Encoding.UTF8.GetBytes("""{"appleId":"other@example.com","password":"pw"}""");
        string digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(replacement));
        keychain.Write(AppleAccount.StoreItem, replacement,
            Encoding.UTF8.GetBytes($$"""{"appleId":"other@example.com","hasSavedPassword":true,"digest":"{{digest}}"}"""));

        IOException error = await Assert.ThrowsAsync<IOException>(() => account.RefreshAsync(CancellationToken.None));
        Assert.Contains("changed outside UniGetUI", error.Message);
        Assert.Equal(0, keychain.SecretReadCount);

        account.LoadSaved();
        Assert.Equal(0, keychain.SecretReadCount);
        Assert.Equal("other@example.com", account.AppleId);
    }

    internal sealed class DeferredMemoryStore : ISecretStore
    {
        public Dictionary<string, byte[]> Items { get; } = [];
        public Dictionary<string, byte[]> Metadata { get; } = [];
        public Dictionary<string, long> Stamps { get; } = [];
        public int SecretReadCount { get; set; }
        private long _clock = 1;

        public bool SupportsDeferredMetadata => true;

        public SecretMetadata ReadMetadata(string account)
            => Items.ContainsKey(account)
                ? new SecretMetadata(
                    Exists: true,
                    Metadata: Metadata.TryGetValue(account, out byte[]? meta) ? [.. meta] : null,
                    ModificationStamp: Stamps.TryGetValue(account, out long stamp) ? stamp : 1)
                : new SecretMetadata(Exists: false, Metadata: null, ModificationStamp: null);

        public byte[]? Read(string account)
        {
            SecretReadCount++;
            return Items.TryGetValue(account, out byte[]? value) ? [.. value] : null;
        }

        public void Write(string account, ReadOnlySpan<byte> secret)
            => Write(account, secret, default);

        public void Write(string account, ReadOnlySpan<byte> secret, ReadOnlySpan<byte> metadata)
        {
            Items[account] = secret.ToArray();
            if (metadata.IsEmpty)
                Metadata.Remove(account);
            else
                Metadata[account] = metadata.ToArray();
            Stamps[account] = ++_clock;
        }

        public void Delete(string account)
        {
            Items.Remove(account);
            Metadata.Remove(account);
            Stamps.Remove(account);
        }
    }

    internal sealed class MemoryStore : ISecretStore
    {
        public Dictionary<string, byte[]> Items { get; } = [];
        public bool FailRead { get; set; }
        public bool FailWrite { get; set; }
        public bool FailDelete { get; set; }

        public byte[]? Read(string account)
            => FailRead ? throw new IOException("Keychain read failed.")
                : Items.TryGetValue(account, out byte[]? value) ? [.. value] : null;

        public void Write(string account, ReadOnlySpan<byte> secret)
        {
            if (FailWrite) throw new IOException("Keychain write failed.");
            Items[account] = secret.ToArray();
        }

        public void Delete(string account)
        {
            if (FailDelete) throw new IOException("Keychain delete failed.");
            Items.Remove(account);
        }
    }
}

// Scenarios mirror the AppleAPI tests of xcodes (MIT License, Copyright (c) 2019-2021 Robots and Pencils).
// See THIRD_PARTY_NOTICES.md in UniGetUI.Extensions.Xcode.
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using UniGetUI.Extensions.Xcode.AppleAuth;

namespace UniGetUI.Extensions.Xcode.Tests.AppleAuth;

public sealed class AppleAuthClientTests
{
    private const string Session = """{"user":{"fullName":"Test User","emailAddress":"test@example.com"},"provider":{"providerId":1,"name":"Test Team"}}""";
    private const string OnePhone = """[{"numberWithDialCode":"+1 (•••) •••-••00","id":1}]""";
    private const string TwoPhones = """[{"numberWithDialCode":"+1 (•••) •••-••00","id":1},{"numberWithDialCode":"+1 (•••) •••-••01","id":2}]""";
    private const string Code = "\"securityCode\":{\"length\":6,\"securityCodeLocked\":false}";

    [Fact]
    public async Task TrustedDeviceCodeSignsIn()
    {
        var apple = new FakeApple(completeStatus: 409, authOptions: $$"""{"trustedPhoneNumbers":{{OnePhone}},{{Code}}}""");
        var prompts = new Prompts { Codes = { new AppleCodeAnswer("123 456") } };

        (string appleId, AppleSession session) = await apple.Client.SignInAsync(prompts, null, CancellationToken.None);

        Assert.Equal("test@example.com", appleId);
        Assert.Equal(new AppleSession("Test User", "test@example.com", "Test Team"), session);
        Assert.Equal(AppleCodeDelivery.TrustedDevice, prompts.CodeRequests.Single().Delivery);
        Assert.True(prompts.CodeRequests.Single().CanRequestSms);
        Assert.Equal("""{"securityCode":{"code":"123456"}}""", apple.Body("/appleauth/auth/verify/trusteddevice/securitycode"));
        Assert.Contains("/appleauth/auth/2sv/trust", apple.Paths);
        Assert.DoesNotContain("/appleauth/auth/verify/phone", apple.Paths);
        HttpRequestMessage trust = apple.Requests.Single(r => r.RequestUri!.AbsolutePath == "/appleauth/auth/2sv/trust");
        Assert.Equal("session-id", trust.Headers.GetValues("X-Apple-ID-Session-Id").Single());
        Assert.Equal("scnt-value", trust.Headers.GetValues("scnt").Single());
        Assert.Equal("NNNNN", trust.Headers.GetValues("X-Apple-Widget-Key").Single());
    }

    [Fact]
    public async Task SmsIsNotRequestedAgainWhenAppleSentItAutomatically()
    {
        var apple = new FakeApple(completeStatus: 409, authOptions: $$"""{"trustedPhoneNumbers":{{OnePhone}},"noTrustedDevices":true,{{Code}}}""");
        var prompts = new Prompts { Codes = { new AppleCodeAnswer("123456") } };

        await apple.Client.SignInAsync(prompts, null, CancellationToken.None);

        AppleCodeRequest request = prompts.CodeRequests.Single();
        Assert.Equal(AppleCodeDelivery.Sms, request.Delivery);
        Assert.Equal(1, request.Phone!.Id);
        Assert.DoesNotContain("/appleauth/auth/verify/phone", apple.Paths);
        Assert.Equal("""{"securityCode":{"code":"123456"},"phoneNumber":{"id":1},"mode":"sms"}""", apple.Body("/appleauth/auth/verify/phone/securitycode"));
        Assert.Equal(0, prompts.PhoneChoices);
    }

    [Fact]
    public async Task ChosenNumberReceivesTheSms()
    {
        var apple = new FakeApple(completeStatus: 409, authOptions: $$"""{"trustedPhoneNumbers":{{TwoPhones}},"noTrustedDevices":true,{{Code}}}""");
        var prompts = new Prompts { Phone = 2, Codes = { new AppleCodeAnswer("654321") } };

        await apple.Client.SignInAsync(prompts, null, CancellationToken.None);

        Assert.Equal(1, prompts.PhoneChoices);
        Assert.Equal("""{"phoneNumber":{"id":2},"mode":"sms"}""", apple.Body("/appleauth/auth/verify/phone"));
        Assert.Equal(HttpMethod.Put, apple.Requests.Single(r => r.RequestUri!.AbsolutePath == "/appleauth/auth/verify/phone").Method);
        Assert.Contains("\"phoneNumber\":{\"id\":2}", apple.Body("/appleauth/auth/verify/phone/securitycode"));
    }

    [Fact]
    public async Task UserCanSwitchFromDeviceToSms()
    {
        var apple = new FakeApple(completeStatus: 409, authOptions: $$"""{"trustedPhoneNumbers":{{OnePhone}},{{Code}}}""");
        var prompts = new Prompts { Codes = { new AppleCodeAnswer(null, UseSms: true), new AppleCodeAnswer("111111") } };

        await apple.Client.SignInAsync(prompts, null, CancellationToken.None);

        Assert.Equal([AppleCodeDelivery.TrustedDevice, AppleCodeDelivery.Sms], prompts.CodeRequests.Select(r => r.Delivery));
        Assert.Equal("""{"phoneNumber":{"id":1},"mode":"sms"}""", apple.Body("/appleauth/auth/verify/phone"));
        Assert.Contains("/appleauth/auth/verify/phone/securitycode", apple.Paths);
    }

    [Fact]
    public async Task IncorrectAndMalformedCodesAreAskedAgain()
    {
        var apple = new FakeApple(completeStatus: 409, authOptions: $$"""{"trustedPhoneNumbers":{{OnePhone}},{{Code}}}""") { WrongCodes = 1 };
        var prompts = new Prompts { Codes = { new AppleCodeAnswer("12"), new AppleCodeAnswer("000000"), new AppleCodeAnswer("123456") } };

        await apple.Client.SignInAsync(prompts, null, CancellationToken.None);

        Assert.Equal([null, "Enter the 6-digit code.", "That code is incorrect. Try again."], prompts.CodeRequests.Select(r => r.Error));
        Assert.Equal(2, apple.Paths.Count(p => p == "/appleauth/auth/verify/trusteddevice/securitycode"));
    }

    [Fact]
    public async Task TooManyIncorrectCodesStop()
    {
        var apple = new FakeApple(completeStatus: 409, authOptions: $$"""{"trustedPhoneNumbers":{{OnePhone}},{{Code}}}""") { WrongCodes = 99 };
        var prompts = new Prompts();
        for (int i = 0; i < 10; i++)
            prompts.Codes.Add(new AppleCodeAnswer("000000"));

        AppleAuthException ex = await Assert.ThrowsAsync<AppleAuthException>(() => apple.Client.SignInAsync(prompts, null, CancellationToken.None));

        Assert.Equal(AppleAuthError.SecurityCodeLocked, ex.Error);
        Assert.Equal(AppleAuthClient.MaximumCodeAttempts, prompts.CodeRequests.Count);
    }

    [Fact]
    public async Task LockedCodesAreReported()
    {
        var apple = new FakeApple(completeStatus: 409, authOptions: $$$"""{"trustedPhoneNumbers":{{{OnePhone}}},"securityCode":{"length":6,"securityCodeLocked":true}}""");

        AppleAuthException ex = await Assert.ThrowsAsync<AppleAuthException>(() => apple.Client.SignInAsync(new Prompts(), null, CancellationToken.None));

        Assert.Equal(AppleAuthError.SecurityCodeLocked, ex.Error);
    }

    [Fact]
    public async Task WrongPasswordIsAskedAgainThenGivesUp()
    {
        var apple = new FakeApple(completeStatus: 401);
        var prompts = new Prompts { Passwords = 5 };

        AppleAuthException ex = await Assert.ThrowsAsync<AppleAuthException>(() => apple.Client.SignInAsync(prompts, "test@example.com", CancellationToken.None));

        Assert.Equal(AppleAuthError.InvalidCredentials, ex.Error);
        Assert.Equal(AppleAuthClient.MaximumPasswordAttempts, prompts.CredentialRequests.Count);
        Assert.Equal([null, "The Apple ID or password is incorrect.", "The Apple ID or password is incorrect."], prompts.CredentialRequests.Select(r => r.Error));
        Assert.Equal("test@example.com", prompts.CredentialRequests[0].AppleId);
    }

    [Theory]
    [InlineData(412, """{"authType":"hsa2"}""", AppleAuthError.PrivacyAcknowledgementRequired)]
    [InlineData(503, "<html>down</html>", AppleAuthError.ServiceUnavailable)]
    [InlineData(500, """{"serviceErrors":[{"code":"-20101","message":"Something failed."}]}""", AppleAuthError.Unexpected)]
    public async Task CompletionFailuresAreTyped(int status, string body, AppleAuthError expected)
    {
        var apple = new FakeApple(completeStatus: status, completeBody: body);

        AppleAuthException ex = await Assert.ThrowsAsync<AppleAuthException>(() => apple.Client.SignInAsync(new Prompts(), null, CancellationToken.None));

        Assert.Equal(expected, ex.Error);
        Assert.Equal(status, ex.StatusCode);
        if (expected == AppleAuthError.Unexpected)
        {
            Assert.Equal("Apple's sign-in service returned HTTP 500.", ex.Message);
            Assert.DoesNotContain("Something failed.", ex.Message);
        }
    }

    [Fact]
    public async Task DirectSignInRequestsProofAndValidatesSession()
    {
        var apple = new FakeApple(completeStatus: 200);

        (_, AppleSession session) = await apple.Client.SignInAsync(new Prompts(), null, CancellationToken.None);

        Assert.Equal("Test Team", session.TeamName);
        Assert.Contains("/logout", apple.Paths);
        Assert.DoesNotContain("/olympus/v1/app/config", apple.Paths);
        HttpRequestMessage complete = apple.Requests.Single(r => r.RequestUri!.AbsolutePath == "/appleauth/auth/signin/complete");
        Assert.StartsWith("1:1:240102030405:challenge::", complete.Headers.GetValues("X-Apple-HC").Single());
        using JsonDocument init = JsonDocument.Parse(apple.Body("/appleauth/auth/signin/init"));
        Assert.Equal("test@example.com", init.RootElement.GetProperty("accountName").GetString());
        Assert.Equal(["s2k", "s2k_fo"], init.RootElement.GetProperty("protocols").EnumerateArray().Select(e => e.GetString()));
        using JsonDocument body = JsonDocument.Parse(apple.Body("/appleauth/auth/signin/complete"));
        Assert.Equal(32, Convert.FromBase64String(body.RootElement.GetProperty("m1").GetString()!).Length);
        Assert.False(body.RootElement.GetProperty("rememberMe").GetBoolean());
        Assert.All(apple.RawBodies, b => Assert.DoesNotContain("secret-password", b));
    }

    [Fact]
    public async Task SignInUsesASeparateClientForThePublicWidgetKey()
    {
        var apple = new FakeApple();
        var publicApple = new FakeApple();
        using var authClient = new HttpClient(apple);
        using var publicClient = new HttpClient(publicApple);
        var client = new AppleAuthClient(authClient, publicClient, () => new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        await client.SignInAsync(new Prompts(), null, CancellationToken.None);

        Assert.Equal(["/logout"], publicApple.Paths);
        Assert.DoesNotContain("/logout", apple.Paths);
        Assert.Contains("/appleauth/auth/signin/complete", apple.Paths);
    }

    [Fact]
    public async Task WidgetKeyIsDecodedFromThePublicRedirect()
    {
        var apple = new FakeApple
        {
            WidgetLocation = "https://idmsa.apple.com/appleauth/signout?other=value&widgetKey=N%4eNNN&last=value",
        };

        Assert.Equal("NNNNN", await apple.Client.GetServiceKeyAsync(CancellationToken.None));
        Assert.DoesNotContain("/olympus/v1/app/config", apple.Paths);
    }

    [Theory]
    [InlineData("http://idmsa.apple.com/appleauth/signout?widgetKey=NNNNN")]
    [InlineData("https://other.example/appleauth/signout?widgetKey=NNNNN")]
    [InlineData("https://idmsa.apple.com:444/appleauth/signout?widgetKey=NNNNN")]
    [InlineData("https://user@idmsa.apple.com/appleauth/signout?widgetKey=NNNNN")]
    [InlineData("https://idmsa.apple.com/appleauth/auth?widgetKey=NNNNN")]
    [InlineData("https://idmsa.apple.com/appleauth/signout?widgetKey=NNNNN#fragment")]
    [InlineData("https://idmsa.apple.com/appleauth/signout?widgetKey=one&widgetKey=two")]
    [InlineData("https://idmsa.apple.com/appleauth/signout?widgetKey=bad%0d%0avalue")]
    [InlineData("https://idmsa.apple.com/appleauth/signout?widgetKey=bad%20value")]
    [InlineData("https://idmsa.apple.com/appleauth/signout?widgetKey=")]
    [InlineData("/appleauth/signout?widgetKey=NNNNN")]
    public async Task UntrustedWidgetRedirectFallsBackToTheLegacyConfiguration(string location)
    {
        var apple = new FakeApple { WidgetLocation = location, LegacyBody = """{"authServiceKey":"legacy-key"}""" };

        Assert.Equal("legacy-key", await apple.Client.GetServiceKeyAsync(CancellationToken.None));
        Assert.Equal(["/logout", "/olympus/v1/app/config"], apple.Paths);
    }

    [Fact]
    public async Task MissingWidgetKeyReportsBothFailedSources()
    {
        var apple = new FakeApple { WidgetLocation = null, LegacyStatus = 404 };

        AppleAuthException error = await Assert.ThrowsAsync<AppleAuthException>(
            () => apple.Client.GetServiceKeyAsync(CancellationToken.None));

        Assert.Equal(AppleAuthError.Unexpected, error.Error);
        Assert.Equal(404, error.StatusCode);
        Assert.Contains("public redirect", error.Message);
        Assert.Contains("legacy endpoint returned HTTP 404", error.Message);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(503)]
    public async Task AWidgetKeyRequiresARedirectResponse(int status)
    {
        var apple = new FakeApple { WidgetStatus = status, LegacyBody = """{"authServiceKey":"legacy-key"}""" };

        Assert.Equal("legacy-key", await apple.Client.GetServiceKeyAsync(CancellationToken.None));
        Assert.Contains("/olympus/v1/app/config", apple.Paths);
    }

    [Fact]
    public async Task AnUnreachablePublicRedirectFallsBackToTheLegacyConfiguration()
    {
        var apple = new FakeApple
        {
            WidgetConnectionFails = true,
            LegacyBody = """{"authServiceKey":"legacy-key"}""",
        };

        Assert.Equal("legacy-key", await apple.Client.GetServiceKeyAsync(CancellationToken.None));
        Assert.Equal(["/logout", "/olympus/v1/app/config"], apple.Paths);
    }

    [Fact]
    public async Task InvalidLegacyConfigurationIsReportedAfterTheRedirectFails()
    {
        var apple = new FakeApple { WidgetLocation = null, LegacyBody = """{"authServiceKey":"bad key"}""" };

        AppleAuthException error = await Assert.ThrowsAsync<AppleAuthException>(
            () => apple.Client.GetServiceKeyAsync(CancellationToken.None));

        Assert.Equal(AppleAuthError.InvalidResponse, error.Error);
        Assert.Contains("public redirect", error.Message);
        Assert.Contains("legacy endpoint returned an invalid key", error.Message);
    }

    [Fact]
    public async Task CancellingWidgetLookupDoesNotTryTheLegacyEndpoint()
    {
        var apple = new FakeApple();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apple.Client.GetServiceKeyAsync(cancellation.Token));

        Assert.Empty(apple.Paths);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellingAsTheWidgetRedirectArrivesStopsLookup(bool validRedirect)
    {
        using var cancellation = new CancellationTokenSource();
        var apple = new FakeApple
        {
            WidgetLocation = validRedirect ? "https://idmsa.apple.com/appleauth/signout?widgetKey=NNNNN" : null,
            WidgetResponded = cancellation.Cancel,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apple.Client.GetServiceKeyAsync(cancellation.Token));

        Assert.Equal(["/logout"], apple.Paths);
    }

    [Fact]
    public async Task AFailedChallengeRequestIsNotUsedEvenIfItIncludesProofHeaders()
    {
        var apple = new FakeApple { HashcashStatus = 503 };

        AppleAuthException error = await Assert.ThrowsAsync<AppleAuthException>(
            () => apple.Client.SignInAsync(new Prompts(), null, CancellationToken.None));

        Assert.Equal(503, error.StatusCode);
        Assert.DoesNotContain("/appleauth/auth/signin/init", apple.Paths);
    }

    [Fact]
    public async Task AccountWithoutDeveloperAccessIsReported()
    {
        var apple = new FakeApple(completeStatus: 200) { SessionBody = """{"user":{"fullName":"Test User"}}""" };

        AppleAuthException ex = await Assert.ThrowsAsync<AppleAuthException>(() => apple.Client.SignInAsync(new Prompts(), null, CancellationToken.None));

        Assert.Equal(AppleAuthError.NotDeveloper, ex.Error);
    }

    [Theory]
    [InlineData("""{"trustedDevices":[{"id":1}]}""", AppleAuthError.TwoStepNotSupported)]
    [InlineData("""{"fsaChallenge":{"challenge":"abc","keyHandles":["k"],"rpId":"apple.com"}}""", AppleAuthError.SecurityKeyRequired)]
    [InlineData("""{"trustedPhoneNumbers":[],"noTrustedDevices":true,"fsaChallenge":{"challenge":"abc","keyHandles":["k"],"rpId":"apple.com"}}""", AppleAuthError.SecurityKeyRequired)]
    [InlineData("""{"trustedDevices":[],"fsaChallenge":{"challenge":"abc","keyHandles":["k"],"rpId":"apple.com"}}""", AppleAuthError.SecurityKeyRequired)]
    [InlineData("""{"direct":{"twoSV":{"fsaChallenge":{"challenge":"abc","keyHandles":["k"],"rpId":"apple.com"}}}}""", AppleAuthError.SecurityKeyRequired)]
    [InlineData("""{"trustedPhoneNumbers":[],"noTrustedDevices":true}""", AppleAuthError.NoTrustedPhoneNumbers)]
    [InlineData("""{}""", AppleAuthError.InvalidResponse)]
    public async Task UnsupportedSecondFactorsAreTyped(string options, AppleAuthError expected)
    {
        var apple = new FakeApple(completeStatus: 409, authOptions: options);

        AppleAuthException ex = await Assert.ThrowsAsync<AppleAuthException>(() => apple.Client.SignInAsync(new Prompts(), null, CancellationToken.None));

        Assert.Equal(expected, ex.Error);
    }

    [Fact]
    public async Task SecurityKeyChallengeWithEmptyPhoneListReachesAuthenticator()
    {
        var key = new FakeSecurityKey();
        var apple = new FakeApple(completeStatus: 409, securityKey: key,
            authOptions: """{"trustedPhoneNumbers":[],"fsaChallenge":{"challenge":"abc","keyHandles":["key"],"rpId":"apple.com"}}""");
        var prompts = new Prompts();

        await apple.Client.SignInAsync(prompts, null, CancellationToken.None);

        Assert.Equal("abc", key.Context!.Challenge);
        Assert.Equal(["key"], key.Context.KeyHandles);
        Assert.Empty(prompts.CodeRequests);
        Assert.Contains("/appleauth/auth/2sv/trust", apple.Paths);
    }

    [Fact]
    public async Task NestedSecurityKeyChallengeReachesAuthenticator()
    {
        var key = new FakeSecurityKey();
        var apple = new FakeApple(completeStatus: 409, securityKey: key,
            authOptions: """{"direct":{"twoSV":{"fsaChallenge":{"challenge":"abc","keyHandles":["key"],"rpId":"apple.com"}}}}""");

        await apple.Client.SignInAsync(new Prompts(), null, CancellationToken.None);

        Assert.Equal("abc", key.Context!.Challenge);
        Assert.Equal(["key"], key.Context.KeyHandles);
        Assert.Contains("/appleauth/auth/2sv/trust", apple.Paths);
    }

    [Fact]
    public async Task PhoneVerificationStillWorksWhenAKeyChallengeIsAlsoPresent()
    {
        var apple = new FakeApple(completeStatus: 409,
            authOptions: $$$"""{"trustedPhoneNumbers":{{{OnePhone}}},{{{Code}}},"fsaChallenge":{"challenge":"abc"}}""");
        var prompts = new Prompts { Codes = { new AppleCodeAnswer("123456") } };

        await apple.Client.SignInAsync(prompts, null, CancellationToken.None);

        Assert.Single(prompts.CodeRequests);
        Assert.Contains("/appleauth/auth/verify/trusteddevice/securitycode", apple.Paths);
    }

    [Fact]
    public async Task PhoneVerificationRemainsPreferredWithANestedKeyChallenge()
    {
        var apple = new FakeApple(completeStatus: 409,
            authOptions: $$$$$"""{"trustedPhoneNumbers":{{{{{OnePhone}}}}},{{{{{Code}}}}},"direct":{"twoSV":{"fsaChallenge":{"challenge":"abc"}}}}""");
        var prompts = new Prompts { Codes = { new AppleCodeAnswer("123456") } };

        await apple.Client.SignInAsync(prompts, null, CancellationToken.None);

        Assert.Single(prompts.CodeRequests);
        Assert.Contains("/appleauth/auth/verify/trusteddevice/securitycode", apple.Paths);
    }

    [Fact]
    public async Task CancellingTheQuestionCancelsSignIn()
    {
        var apple = new FakeApple(completeStatus: 409, authOptions: $$"""{"trustedPhoneNumbers":{{OnePhone}},{{Code}}}""");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apple.Client.SignInAsync(new Prompts(), null, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateDistinguishesAnExpiredSessionFromServiceErrors()
    {
        Assert.NotNull(await new FakeApple(200).Client.ValidateSessionAsync(CancellationToken.None));
        Assert.Null(await new FakeApple(200) { SessionBody = "{}" }.Client.ValidateSessionAsync(CancellationToken.None));
        Assert.Null(await new FakeApple(200) { SessionStatus = 401 }.Client.ValidateSessionAsync(CancellationToken.None));

        AppleAuthException invalid = await Assert.ThrowsAsync<AppleAuthException>(() =>
            new FakeApple(200) { SessionBody = "not json" }.Client.ValidateSessionAsync(CancellationToken.None));
        Assert.Equal(AppleAuthError.InvalidResponse, invalid.Error);
        AppleAuthException forbidden = await Assert.ThrowsAsync<AppleAuthException>(() =>
            new FakeApple(200) { SessionStatus = 403 }.Client.ValidateSessionAsync(CancellationToken.None));
        Assert.Equal(403, forbidden.StatusCode);
        AppleAuthException unavailable = await Assert.ThrowsAsync<AppleAuthException>(() =>
            new FakeApple(200) { SessionStatus = 503, SessionBody = "unavailable" }.Client.ValidateSessionAsync(CancellationToken.None));
        Assert.Equal(AppleAuthError.ServiceUnavailable, unavailable.Error);
    }

    [Fact]
    public async Task OversizedResponsesAreRefused()
    {
        var apple = new FakeApple(200) { SessionBody = "{\"x\":\"" + new string('a', AppleAuthClient.MaximumResponseBytes) + "\"}" };

        AppleAuthException ex = await Assert.ThrowsAsync<AppleAuthException>(() => apple.Client.ValidateSessionAsync(CancellationToken.None));

        Assert.Equal(AppleAuthError.InvalidResponse, ex.Error);
    }

    internal sealed class Prompts : IAppleSignInPrompts
    {
        public int Passwords { get; set; } = 1;
        public string AppleId { get; set; } = " test@example.com ";
        public string Password { get; set; } = "secret-password";
        public int? Phone { get; set; }
        public List<AppleCodeAnswer> Codes { get; } = [];
        public List<AppleCodeRequest> CodeRequests { get; } = [];
        public List<(string? AppleId, string? Error)> CredentialRequests { get; } = [];
        public int PhoneChoices { get; private set; }

        public Task<AppleCredentials?> AskCredentialsAsync(string? appleId, string? error, CancellationToken cancellationToken)
        {
            CredentialRequests.Add((appleId, error));
            return Task.FromResult<AppleCredentials?>(CredentialRequests.Count <= Passwords
                ? new AppleCredentials(AppleId, Password)
                : null);
        }

        public Task<AppleCodeAnswer?> AskCodeAsync(AppleCodeRequest request, CancellationToken cancellationToken)
        {
            CodeRequests.Add(request);
            AppleCodeAnswer? answer = Codes.Count >= CodeRequests.Count ? Codes[CodeRequests.Count - 1] : null;
            return Task.FromResult(answer);
        }

        public Task<AppleTrustedPhone?> ChoosePhoneAsync(IReadOnlyList<AppleTrustedPhone> phones, CancellationToken cancellationToken)
        {
            PhoneChoices++;
            return Task.FromResult(phones.FirstOrDefault(p => p.Id == Phone));
        }
    }

    private sealed class FakeSecurityKey : IAppleSecurityKeyAuthenticator
    {
        public AppleSecurityKeyContext? Context { get; private set; }

        public Task AuthenticateAsync(AppleSecurityKeyContext context, CancellationToken cancellationToken)
        {
            Context = context;
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeApple : HttpMessageHandler
    {
        private static readonly BigInteger N = BigInteger.Parse(
            "00AC6BDB41324A9A9BF166DE5E1389582FAF72B6651987EE07FC3192943DB56050A37329CBB4A099ED8193E0757767A13DD52312AB4B03310DCD7F48A9DA04FD50E8083969EDB767B0CF6095179A163AB3661A05FBD5FAAAE82918A9962F0B93B855F97993EC975EEAA80D740ADBF4FF747359D041D5C33EA71D281E446B14773BCA97B43A23FB801676BD207A436C6481F1D2B9078717461A5B9D32E688F87748544523B524B0D57D5EA77A2775D2ECFA032CFBDBF52FB3786160279004E57AE6AF874E7303CE53299CCC041C7BC308D82A5698F3A8D0C38271AE35F8E9DBFBB694B5C803D89F7AE435DE236D525F54759B65E372FCD68EF20FA7111F9E4AFF73",
            System.Globalization.NumberStyles.HexNumber);

        private readonly int _completeStatus;
        private readonly string _completeBody;
        private readonly string? _authOptions;

        public FakeApple(int completeStatus = 200, string completeBody = """{"authType":"hsa2"}""", string? authOptions = null,
            IAppleSecurityKeyAuthenticator? securityKey = null)
        {
            _completeStatus = completeStatus;
            _completeBody = completeBody;
            _authOptions = authOptions;
            Client = new AppleAuthClient(new HttpClient(this), () => new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc))
            {
                SecurityKeys = securityKey,
            };
        }

        public AppleAuthClient Client { get; }
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RawBodies { get; } = [];
        private readonly Dictionary<HttpRequestMessage, string> _bodies = [];
        public IEnumerable<string> Paths => Requests.Select(r => r.RequestUri!.AbsolutePath);
        public int WrongCodes { get; set; }
        public string SessionBody { get; set; } = Session;
        public int SessionStatus { get; set; } = 200;
        public int WidgetStatus { get; set; } = 302;
        public string? WidgetLocation { get; set; } = "https://idmsa.apple.com/appleauth/signout?widgetKey=NNNNN";
        public Action? WidgetResponded { get; set; }
        public Action? SessionResponded { get; set; }
        public bool WidgetConnectionFails { get; set; }
        public int LegacyStatus { get; set; } = 200;
        public string LegacyBody { get; set; } = """{"authServiceKey":"NNNNN"}""";
        public int HashcashStatus { get; set; } = 200;
        public bool RequireSignIn { get; set; }
        public Queue<int> CompleteStatuses { get; } = [];
        public byte[]? ArchiveBytes { get; set; }
        public Func<string>? DownloadListResponse { get; set; }
        public bool ArchiveRefuseOnce { get; set; }
        private bool _signedIn;
        private bool _archiveRefused;

        public string Body(string path) => _bodies[Requests.Last(r => r.RequestUri!.AbsolutePath == path)];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            _bodies[request] = body;
            RawBodies.Add(body);
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/logout" && WidgetConnectionFails)
                throw new HttpRequestException("Synthetic network failure.");
            if (path == "/logout")
                WidgetResponded?.Invoke();
            if (path == "/olympus/v1/session")
                SessionResponded?.Invoke();
            return path switch
            {
                "/logout" => WidgetLocation is null ? Respond(WidgetStatus, "") : WithHeaders(Respond(WidgetStatus, ""), ("Location", WidgetLocation)),
                "/olympus/v1/app/config" => Respond(LegacyStatus, LegacyBody),
                "/olympus/v1/session" => Respond(RequireSignIn && !_signedIn ? 401 : SessionStatus, SessionBody),
                "/appleauth/auth/signin" => WithHeaders(Respond(HashcashStatus, "{}"), ("X-Apple-HC-Bits", "1"), ("X-Apple-HC-Challenge", "challenge")),
                "/appleauth/auth/signin/init" => Respond(200, InitBody()),
                "/appleauth/auth/signin/complete" => WithHeaders(Complete(),
                    ("X-Apple-ID-Session-Id", "session-id"), ("scnt", "scnt-value")),
                "/appleauth/auth" => Respond(200, _authOptions ?? "{}"),
                "/appleauth/auth/verify/trusteddevice/securitycode" or "/appleauth/auth/verify/phone/securitycode"
                    => Respond(WrongCodes-- > 0 ? 401 : 204, ""),
                "/appleauth/auth/verify/phone" => Respond(200, "{}"),
                "/appleauth/auth/2sv/trust" => Respond(204, ""),
                "/services-account/QH65B2/downloadws/listDownloads.action" when request.RequestUri.Host == "developer.apple.com"
                    => Respond(200, DownloadListResponse?.Invoke() ?? """{"resultCode":0,"downloads":[]}"""),
                _ when request.RequestUri.Host == "download.developer.apple.com" && ArchiveBytes is { } bytes
                    => ArchiveRefuseOnce && (_archiveRefused = !_archiveRefused)
                        ? new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            RequestMessage = new HttpRequestMessage(HttpMethod.Get, new Uri("https://developer.apple.com/unauthorized/")),
                            Content = new StringContent("<html/>", Encoding.UTF8, "text/html"),
                        }
                        : new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            RequestMessage = request,
                            Content = new ByteArrayContent(bytes)
                            {
                                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream") },
                            },
                        },
                _ => Respond(404, ""),
            };
        }

        private HttpResponseMessage Complete()
        {
            int status = CompleteStatuses.Count > 0 ? CompleteStatuses.Dequeue() : _completeStatus;
            _signedIn = status == 200;
            return Respond(status, _completeBody);
        }

        private static string InitBody()
        {
            byte[] b = BigInteger.ModPow(2, 12345, N).ToByteArray(isUnsigned: true, isBigEndian: true);
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["iteration"] = 10,
                ["salt"] = Convert.ToBase64String(Encoding.ASCII.GetBytes("saltsaltsaltsalt")),
                ["b"] = Convert.ToBase64String(b),
                ["c"] = "c-token",
                ["protocol"] = "s2k",
            });
        }

        private static HttpResponseMessage Respond(int status, string body)
            => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage WithHeaders(HttpResponseMessage response, params (string Name, string Value)[] headers)
        {
            foreach ((string name, string value) in headers)
                response.Headers.TryAddWithoutValidation(name, value);
            return response;
        }
    }
}

using UniGetUI.Extensions.Xcode.AppleAuth;

namespace UniGetUI.Extensions.Xcode.Tests.AppleAuth;

public sealed class AppleSignInPromptAdapterTests
{
    private readonly List<XcodesPrompt> _asked = [];

    private AppleSignInPromptAdapter Answering(params string?[] answers)
    {
        var queue = new Queue<string?>(answers);
        return new AppleSignInPromptAdapter((prompt, _) =>
        {
            _asked.Add(prompt);
            return Task.FromResult(queue.Count > 0 ? queue.Dequeue() : null);
        });
    }

    [Fact]
    public async Task AsksForTheAppleIdOnlyWhenNoneIsKnown()
    {
        AppleCredentials? asked = await Answering(" me@example.com ", "pw").AskCredentialsAsync(null, "Wrong password.", CancellationToken.None);
        Assert.Equal(new AppleCredentials("me@example.com", "pw"), asked);
        Assert.Equal([XcodesPromptKind.AppleId, XcodesPromptKind.Password], _asked.Select(p => p.Kind));
        Assert.Equal("Wrong password.", _asked[0].Error);
        Assert.Equal("me@example.com", _asked[1].AppleId);

        _asked.Clear();
        AppleCredentials? known = await Answering("pw").AskCredentialsAsync("saved@example.com", "Wrong password.", CancellationToken.None);
        Assert.Equal(new AppleCredentials("saved@example.com", "pw"), known);
        XcodesPrompt password = Assert.Single(_asked);
        Assert.Equal(("saved@example.com", "Wrong password."), (password.AppleId, password.Error));
    }

    [Fact]
    public async Task AnEmptyAnswerCancels()
    {
        Assert.Null(await Answering("").AskCredentialsAsync(null, null, CancellationToken.None));
        Assert.Null(await Answering("me@example.com", null).AskCredentialsAsync(null, null, CancellationToken.None));
        Assert.Null(await Answering(" ").AskCodeAsync(new AppleCodeRequest(AppleCodeDelivery.TrustedDevice, 6, null, true, null), CancellationToken.None));
    }

    [Fact]
    public async Task SmsAnswerAsksForATextMessageOnlyWhenOneCanBeSent()
    {
        var device = new AppleCodeRequest(AppleCodeDelivery.TrustedDevice, 6, null, CanRequestSms: true, null);
        Assert.Equal(new AppleCodeAnswer(null, UseSms: true), await Answering("SMS").AskCodeAsync(device, CancellationToken.None));
        Assert.Equal(new AppleCodeAnswer("sms"), await Answering("sms").AskCodeAsync(device with { CanRequestSms = false }, CancellationToken.None));
        Assert.Equal(new AppleCodeAnswer("123456"), await Answering(" 123456 ").AskCodeAsync(device, CancellationToken.None));
        Assert.All(_asked, prompt => Assert.Equal(XcodesPromptKind.TwoFactorCode, prompt.Kind));
    }

    [Fact]
    public async Task TextMessageCodeSaysWhereItWasSent()
    {
        var request = new AppleCodeRequest(AppleCodeDelivery.Sms, 6, new AppleTrustedPhone(1, "+1 (•••) •••-••12"), false, "Wrong code.");

        await Answering("123456").AskCodeAsync(request, CancellationToken.None);

        XcodesPrompt prompt = Assert.Single(_asked);
        Assert.Equal(XcodesPromptKind.SmsCode, prompt.Kind);
        Assert.Equal("Sent to +1 (•••) •••-••12.", prompt.Message);
        Assert.Equal("Wrong code.", prompt.Error);
    }

    [Fact]
    public async Task ChoosesTheOnlyPhoneAndAsksAgainForAnInvalidChoice()
    {
        AppleTrustedPhone first = new(1, "••12"), second = new(7, "••34");
        Assert.Same(first, await Answering().ChoosePhoneAsync([first], CancellationToken.None));
        Assert.Empty(_asked);

        Assert.Same(second, await Answering("3", "x", "2").ChoosePhoneAsync([first, second], CancellationToken.None));
        Assert.Equal(3, _asked.Count);
        Assert.Equal(["1: ••12", "2: ••34"], _asked[0].Context);
        Assert.Null(_asked[0].Error);
        Assert.Equal("Enter a number from 1 to 2.", _asked[2].Error);
    }

    [Fact]
    public async Task FederatedSignInAcceptsOnlyAnHttpsAddress()
    {
        Uri? callback = await Answering("http://example.com/done", "not a url", "https://idp.example.com/done?code=1")
            .AskFederatedCallbackAsync(new Uri("https://idp.example.com/start"), CancellationToken.None);

        Assert.Equal(new Uri("https://idp.example.com/done?code=1"), callback);
        Assert.Equal(3, _asked.Count);
        Assert.All(_asked, prompt => Assert.Equal(["https://idp.example.com/start"], prompt.Context));
        Assert.NotNull(_asked[1].Error);
    }
}

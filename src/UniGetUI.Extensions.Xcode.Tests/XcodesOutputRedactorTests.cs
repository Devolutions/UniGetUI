namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class XcodesOutputRedactorTests
{
    [Fact]
    public void RemovesOverlappingAnswersWithoutReplacingTheRedactionMarker()
    {
        var redactor = new XcodesOutputRedactor(null);
        redactor.RememberAnswer(XcodesPromptKind.Password, "redacted");
        redactor.RememberAnswer(XcodesPromptKind.TwoFactorCode, "redacted-longer");

        Assert.Equal("[redacted] and [redacted]", redactor.ForLog("redacted-longer and redacted"));
    }

    [Fact]
    public void KeepsTheFederatedSignInAddressOnlyInTransientPromptContext()
    {
        const string address = "https://id.example.test/login?state=private&login_hint=developer@example.test";
        var redactor = new XcodesOutputRedactor(null);
        redactor.RememberAnswer(XcodesPromptKind.AppleId, "developer@example.test");

        Assert.Equal($"Open {address}", redactor.ForContext($"Open {address}"));
        Assert.Equal("Open [URL redacted]", redactor.ForLog($"Open {address}"));
        Assert.Equal("Cookie: [redacted]", redactor.ForContext("Cookie: session=private"));
        Assert.Equal("Authorization: [redacted]", redactor.ForLog("Authorization: Bearer private"));
        Assert.Equal("access_token=[redacted]", redactor.ForLog("access_token=private"));
    }

    [Fact]
    public void RemovesASubmittedCallbackEvenFromLaterPromptContext()
    {
        const string callback = "https://id.example.test/callback?token=private";
        var redactor = new XcodesOutputRedactor(null);
        redactor.RememberAnswer(XcodesPromptKind.FederatedUrl, callback);

        Assert.Equal("Rejected [redacted]", redactor.ForContext($"Rejected {callback}"));
        Assert.Equal("Rejected [URL redacted]", redactor.ForLog($"Rejected {callback}"));
    }

    [Fact]
    public void SmsChoiceAndPhoneIndexDoNotRedactOrdinaryOutput()
    {
        var redactor = new XcodesOutputRedactor(null);
        redactor.RememberAnswer(XcodesPromptKind.TwoFactorCode, "sms");
        redactor.RememberAnswer(XcodesPromptKind.PhoneNumber, "1");

        Assert.Equal("SMS sent to phone 1", redactor.ForLog("SMS sent to phone 1"));
        redactor.RememberAnswer(XcodesPromptKind.PhoneNumber, "15551234567");
        Assert.Equal("Phone [redacted]", redactor.ForLog("Phone 15551234567"));
    }

    [Fact]
    public void KeepsAnswerWhitespaceFromLeakingWhenOutputTrimsIt()
    {
        var redactor = new XcodesOutputRedactor(null);
        redactor.RememberAnswer(XcodesPromptKind.Password, " secret ");

        Assert.Equal("Rejected [redacted]", redactor.ForLog("Rejected secret"));
        Assert.Equal("Rejected[redacted]", redactor.ForLog("Rejected secret "));
    }

    [Fact]
    public void RemovesPartsOfAnEnvironmentPasswordPrintedOnSeparateLines()
    {
        var redactor = new XcodesOutputRedactor("first-part\nsecond-part");

        Assert.Equal("[redacted]\n[redacted]", redactor.ForError("first-part\nsecond-part"));
    }

    [Fact]
    public void RemovesTerminalSequencesBeforeRedactingErrors()
    {
        var redactor = new XcodesOutputRedactor("private-password");

        Assert.Equal("Error: [redacted]", redactor.ForError("Error: private-\u001b[31mpassword\u001b[0m"));
    }
}

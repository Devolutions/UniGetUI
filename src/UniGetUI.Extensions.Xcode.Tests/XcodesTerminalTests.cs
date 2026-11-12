namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class XcodesPromptsTests
{
    [Theory]
    [InlineData("Apple ID: ", XcodesPromptKind.AppleId)]
    [InlineData("Apple ID Password: ", XcodesPromptKind.Password)]
    [InlineData("Apple ID Password (me@example.com): ", XcodesPromptKind.Password)]
    [InlineData("macOS User Password: ", XcodesPromptKind.SudoPassword)]
    [InlineData("Enter the 6 digit code from one of your trusted devices: ", XcodesPromptKind.TwoFactorCode)]
    [InlineData("Enter the 4 digit code from one of your trusted devices:", XcodesPromptKind.TwoFactorCode)]
    [InlineData("Enter the 6 digit code sent to +1 (555) 010-0012: ", XcodesPromptKind.SmsCode)]
    [InlineData("Select a trusted phone number to receive a code via SMS: ", XcodesPromptKind.PhoneNumber)]
    [InlineData("Paste the URL here: ", XcodesPromptKind.FederatedUrl)]
    public void Detect_NamesTheQuestionsXcodesAsks(string pending, XcodesPromptKind kind)
        => Assert.Equal(kind, XcodesPrompts.Detect(pending));

    [Theory]
    [InlineData("Enter the number of the Xcode to select: ")]
    [InlineData("Continue? ")]
    public void Detect_TreatsAnyOtherLineLeftOpenLikeAQuestionAsUnknown(string pending)
        => Assert.Equal(XcodesPromptKind.Unknown, XcodesPrompts.Detect(pending));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(1/6) Downloading Xcode 26.4: 42%")]
    [InlineData("(2/6) Unarchiving Xcode (This can take a while)")]
    [InlineData("Xcode 26.4 has been installed to /Applications/Xcode-26.4.app")]
    [InlineData("Downloading")]
    public void Detect_LeavesOrdinaryOutputAlone(string pending)
        => Assert.Null(XcodesPrompts.Detect(pending));

    [Fact]
    public void Detect_DoesNotTakeAnAnswerForAQuestion()
    {
        // A question that has already been answered no longer sits at the end of the line.
        Assert.Null(XcodesPrompts.Detect("Apple ID: me@example.com"));
    }

    [Theory]
    [InlineData("Apple ID Password (me@example.com):", "Apple ID Password:")]
    [InlineData("  Apple ID Password (me@example.com): ", "  Apple ID Password: ")]
    [InlineData("Apple ID Password (a (b) c@example.com):", "Apple ID Password:")]
    [InlineData(
        "Invalid username and password combination. Attempted to sign in with username me@example.com.",
        "Invalid username and password combination.")]
    [InlineData(
        "Error: Invalid username and password combination. Attempted to sign in with username me@example.com.  ",
        "Error: Invalid username and password combination.")]
    public void Redact_TakesTheAppleIdOutOfTheLinesThatNameIt(string line, string expected)
        => Assert.Equal(expected, XcodesPrompts.Redact(line));

    [Theory]
    [InlineData("")]
    [InlineData("Apple ID:")]
    [InlineData("Apple ID Password:")]
    [InlineData("Try entering your password again")]
    [InlineData("(1/6) Downloading Xcode 26.4: 42%")]
    [InlineData("Enter the 6 digit code sent to +1 (•••) •••-••12:")]
    [InlineData("Xcode 26.4 has been installed to /Applications/Xcode-26.4.app")]
    public void Redact_LeavesEveryOtherLineAlone(string line)
        => Assert.Equal(line, XcodesPrompts.Redact(line));

    [Fact]
    public void Redact_DoesNotChangeWhichQuestionALineIs()
    {
        Assert.Equal(XcodesPromptKind.Password, XcodesPrompts.Detect("Apple ID Password (me@example.com):"));
        Assert.Equal(XcodesPromptKind.Password, XcodesPrompts.Detect(XcodesPrompts.Redact("Apple ID Password (me@example.com):")));
    }

    [Theory]
    [InlineData(XcodesPromptKind.Password, true)]
    [InlineData(XcodesPromptKind.SudoPassword, true)]
    [InlineData(XcodesPromptKind.AppleId, false)]
    [InlineData(XcodesPromptKind.TwoFactorCode, false)]
    [InlineData(XcodesPromptKind.SmsCode, false)]
    [InlineData(XcodesPromptKind.PhoneNumber, false)]
    [InlineData(XcodesPromptKind.FederatedUrl, false)]
    [InlineData(XcodesPromptKind.Unknown, false)]
    public void IsSecret_IsTrueOnlyForPasswords(XcodesPromptKind kind, bool secret)
        => Assert.Equal(secret, new XcodesPrompt(kind, "question", []).IsSecret);
}

namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class XcodesTerminalDecoderTests
{
    [Fact]
    public void Feed_SplitsLinesAtLineFeedAndCarriageReturnLineFeed()
    {
        var decoder = new XcodesTerminalDecoder();

        IReadOnlyList<XcodesOutputLine> lines = decoder.Feed("one\r\ntwo\nthree");

        Assert.Equal(["one", "two"], lines.Select(line => line.Text));
        Assert.Equal("three", decoder.Pending);
    }

    [Fact]
    public void Feed_JoinsALineThatArrivesInPieces()
    {
        var decoder = new XcodesTerminalDecoder();

        Assert.Empty(decoder.Feed("Downl"));
        Assert.Empty(decoder.Feed("oading Xc"));
        XcodesOutputLine line = Assert.Single(decoder.Feed("ode\r\n"));

        Assert.Equal("Downloading Xcode", line.Text);
        Assert.False(decoder.HasPending);
    }

    [Fact]
    public void Feed_KeepsTheSpaceAPromptWaitsAfter_ButNotInFinishedLines()
    {
        var decoder = new XcodesTerminalDecoder();

        Assert.Equal("done", Assert.Single(decoder.Feed("done   \r\n")).Text);
        Assert.Empty(decoder.Feed("Apple ID: "));

        Assert.Equal("Apple ID: ", decoder.Pending);
    }

    [Fact]
    public void Feed_RewindsALineOnCarriageReturn()
    {
        var decoder = new XcodesTerminalDecoder();

        XcodesOutputLine line = Assert.Single(decoder.Feed("10%\r20%\r30%\r\n"));

        Assert.Equal("30%", line.Text);
    }

    [Fact]
    public void Feed_MarksALineThatOverwritesThePreviousOne()
    {
        var decoder = new XcodesTerminalDecoder();

        IReadOnlyList<XcodesOutputLine> lines = decoder.Feed(
            "\r\n"
            + "\u001b[1A\u001b[K(1/6) Downloading Xcode 26.4: 1%\r\n"
            + "\u001b[1A\u001b[K(1/6) Downloading Xcode 26.4: 2%\r\n"
            + "(2/6) Unarchiving Xcode (This can take a while)\r\n");

        Assert.Equal(
            ["", "(1/6) Downloading Xcode 26.4: 1%", "(1/6) Downloading Xcode 26.4: 2%", "(2/6) Unarchiving Xcode (This can take a while)"],
            lines.Select(line => line.Text));
        Assert.Equal([false, true, true, false], lines.Select(line => line.ReplacesPrevious));
    }

    [Fact]
    public void Feed_ReadsAnEscapeSequenceThatArrivesInPieces()
    {
        var decoder = new XcodesTerminalDecoder();

        Assert.Empty(decoder.Feed("\u001b"));
        Assert.Empty(decoder.Feed("[1"));
        XcodesOutputLine line = Assert.Single(decoder.Feed("A\u001b[Kprogress\r\n"));

        Assert.Equal("progress", line.Text);
        Assert.True(line.ReplacesPrevious);
    }

    [Fact]
    public void Feed_EraseInLineClearsAfterACarriageReturn()
    {
        var decoder = new XcodesTerminalDecoder();

        Assert.Equal("xyz", Assert.Single(decoder.Feed("abc\r\u001b[Kxyz\r\n")).Text);
    }

    [Theory]
    [InlineData("abc\u001b[2Kxyz")]
    [InlineData("abc\u001b[1Kxyz")]
    public void Feed_EraseLineParametersClearTheWholeLine(string output)
    {
        var decoder = new XcodesTerminalDecoder();

        decoder.Feed(output);

        Assert.Equal("xyz", decoder.Pending);
    }

    [Fact]
    public void Feed_DropsColoursAndOtherSequencesItDoesNotNeed()
    {
        var decoder = new XcodesTerminalDecoder();

        IReadOnlyList<XcodesOutputLine> lines = decoder.Feed(
            "\u001b[32mgreen\u001b[0m\r\n"
            + "\u001b[?25lhidden cursor\u001b[?25h\r\n"
            + "\u001b]0;window title\u0007titled\r\n"
            + "\u001b]0;window title\u001b\\again\r\n");

        Assert.Equal(["green", "hidden cursor", "titled", "again"], lines.Select(line => line.Text));
        Assert.All(lines, line => Assert.False(line.ReplacesPrevious));
    }

    [Fact]
    public void Feed_RemovesWhatScriptEchoesForAnEndOfFile()
    {
        var decoder = new XcodesTerminalDecoder();

        decoder.Feed("Apple ID: ^D\b\b");

        Assert.Equal("Apple ID: ", decoder.Pending);
    }

    [Fact]
    public void Feed_RecoversFromAStrayEscape()
    {
        var decoder = new XcodesTerminalDecoder();

        IReadOnlyList<XcodesOutputLine> lines = decoder.Feed("a\u001b\nb\r\n");

        Assert.Equal(["a", "b"], lines.Select(line => line.Text));
    }

    [Fact]
    public void Feed_KeepsTabsAndDropsOtherControlCharacters()
    {
        var decoder = new XcodesTerminalDecoder();

        XcodesOutputLine line = Assert.Single(decoder.Feed("26.6\u0007 (17F113)\tpath\u0000\r\n"));

        Assert.Equal("26.6 (17F113)\tpath", line.Text);
    }

    [Fact]
    public void Feed_OnlyMarksTheLineAfterTheCursorMoved()
    {
        var decoder = new XcodesTerminalDecoder();

        IReadOnlyList<XcodesOutputLine> lines = decoder.Feed("\u001b[1Aone\r\ntwo\r\n");

        Assert.Equal([true, false], lines.Select(line => line.ReplacesPrevious));
    }

    [Fact]
    public void Flush_ReturnsTheUnfinishedLineOnce()
    {
        var decoder = new XcodesTerminalDecoder();
        decoder.Feed("last words  ");

        XcodesOutputLine? line = decoder.Flush();

        Assert.NotNull(line);
        Assert.Equal("last words", line.Text);
        Assert.Null(decoder.Flush());
        Assert.False(decoder.HasPending);
    }

    [Fact]
    public void ClearPending_ForgetsTheUnfinishedLine()
    {
        var decoder = new XcodesTerminalDecoder();
        decoder.Feed("Apple ID: ");

        decoder.ClearPending();

        Assert.False(decoder.HasPending);
        Assert.Equal("", decoder.Pending);
        Assert.DoesNotContain(decoder.Feed("\r\n"), line => line.Text.Length > 0);
    }
}

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

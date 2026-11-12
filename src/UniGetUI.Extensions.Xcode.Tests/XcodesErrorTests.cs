namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class XcodesErrorTests
{
    [Theory]
    [InlineData("HTTP 401", true)]
    [InlineData("Received 403: Unauthorized", true)]
    [InlineData("Forbidden", true)]
    [InlineData("The archive is damaged", false)]
    [InlineData("Version 4031 is unavailable", false)]
    public void PossibleAccessFailuresAreNotTreatedAsADiagnosis(string output, bool expected)
        => Assert.Equal(expected, XcodesError.IsPossibleAccessFailure(output));

    [Fact]
    public void Summarize_FallsBackToTheExitCodeWhenNothingWasWritten()
    {
        Assert.Equal("The command exited with code 1.", XcodesError.Summarize("", 1));
        Assert.Equal("The command exited with code 73.", XcodesError.Summarize("  \n  \n", 73));
        Assert.Equal("The command failed.", XcodesError.Summarize(""));
    }

    [Fact]
    public void Summarize_ReturnsAnErrorThatCameWithoutSteps()
        => Assert.Equal("Could not find version 99.0.", XcodesError.Summarize("Could not find version 99.0.\n", 1));

    [Fact]
    public void Summarize_ReturnsWhatFollowsTheLastStep()
    {
        const string output =
            "(1/6) Downloading Xcode 26.4: 100%\n"
            + "(2/6) Unarchiving Xcode (This can take a while)\n"
            + "Using regular unxip. Try passing `--experimental-unxip` for a faster unxip\n"
            + "The archive \"Xcode_26.4.xip\" is damaged and can't be expanded.\n";

        Assert.Equal("The archive \"Xcode_26.4.xip\" is damaged and can't be expanded.", XcodesError.Summarize(output, 1));
    }

    [Fact]
    public void Summarize_JoinsAnErrorThatTakesSeveralLines()
    {
        const string output =
            "(1/6) Downloading Xcode 26.4\n"
            + "Received 403: Unauthorized. This can happen when either:\n"
            + "1. You have not accepted the latest developer agreement.\n"
            + "2. Your Apple ID is not a member of a developer team.\n";

        Assert.Equal(
            "Received 403: Unauthorized. This can happen when either: "
            + "1. You have not accepted the latest developer agreement. "
            + "2. Your Apple ID is not a member of a developer team.",
            XcodesError.Summarize(output, 1));
    }

    [Fact]
    public void Summarize_LeavesOutTheMenuXcodesOffersForAVersionThatIsNotInstalled()
    {
        const string output =
            "26.4 is not installed.\n"
            + "Available Xcode versions:\n"
            + "1) 26.4\n"
            + "2) 26.6\n"
            + "Enter the number of the Xcode to select: ";

        Assert.Equal("26.4 is not installed.", XcodesError.Summarize(output, 1));
    }

    [Fact]
    public void Summarize_IgnoresTerminalControl()
        => Assert.Equal("Missing password. Please try again.", XcodesError.Summarize("\u001b[31mMissing password. Please try again.\u001b[0m\r\n", 1));

    [Fact]
    public void Summarize_ShortensWhatIsTooLongToReadAtAGlance()
    {
        string summary = XcodesError.Summarize(new string('x', 900), 1);

        Assert.Equal(500, summary.Length);
        Assert.EndsWith("...", summary);
    }

    [Fact]
    public void Summarize_FallsBackWhenOnlyHintsRemain()
    {
        const string output =
            "(2/6) Unarchiving Xcode (This can take a while)\n"
            + "Using experimental unxip. Try passing `--no-experimental-unxip` if it fails\n";

        Assert.Equal("The command exited with code 9.", XcodesError.Summarize(output, 9));
    }
}

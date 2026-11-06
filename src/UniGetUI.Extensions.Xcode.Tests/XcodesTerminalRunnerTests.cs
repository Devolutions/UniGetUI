using System.Diagnostics;

namespace UniGetUI.Extensions.Xcode.Tests;

/// <summary>
/// Runs shell scripts in place of xcodes under the pseudo-terminal the real runner uses, so they only run on a
/// Mac, which is where BSD <c>script</c> is. A run that hangs is cancelled after a minute instead of stalling the suite.
/// </summary>
public sealed class XcodesTerminalRunnerTests
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(60);

    private static bool OnMac => OperatingSystem.IsMacOS();

    private static async Task<XcodesResult> RunAsync(
        FakeXcodesScript script,
        string[]? arguments = null,
        XcodesPromptHandler? prompts = null,
        List<XcodesOutputLine>? output = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        using var safety = new CancellationTokenSource(SafetyNet);
        return await new XcodesTerminalRunner(script.Path, environment)
            .RunAsync(arguments ?? ["install", "26.4"], prompts, output is null ? null : output.Add, safety.Token);
    }

    private static string[] Lines(XcodesResult result) => result.StandardOutput.Split('\n');

    [Fact]
    public async Task RunAsync_ReturnsTheLinesAsATerminalShowsThem()
    {
        if (!OnMac)
            return;
        using var script = new FakeXcodesScript("""
            echo "(1/6) Downloading Xcode 26.4"
            echo "(6/6) Finishing installation"
            echo "Xcode 26.4 has been installed to /Applications/Xcode-26.4.app"
            """);
        List<XcodesOutputLine> output = [];

        XcodesResult result = await RunAsync(script, output: output);

        Assert.True(result.Success);
        string[] expected =
        [
            "(1/6) Downloading Xcode 26.4",
            "(6/6) Finishing installation",
            "Xcode 26.4 has been installed to /Applications/Xcode-26.4.app",
        ];
        Assert.Equal(expected, Lines(result));
        Assert.Equal(expected, output.Select(line => line.Text));
    }

    [Fact]
    public async Task RunAsync_KeepsTheLastLineWhenItHasNoLineEnd()
    {
        if (!OnMac)
            return;
        using var script = new FakeXcodesScript("printf 'first\\nno line end'\n");

        XcodesResult result = await RunAsync(script);

        Assert.Equal(["first", "no line end"], Lines(result));
    }

    [Fact]
    public async Task RunAsync_ReplacesAProgressLineInsteadOfListingEveryUpdate()
    {
        if (!OnMac)
            return;
        // xcodes writes an empty line, then writes each download update over the line above the cursor.
        using var script = new FakeXcodesScript("""
            printf '\n'
            printf '\033[1A\033[K(1/6) Downloading Xcode 26.4: 10%%\n'
            printf '\033[1A\033[K(1/6) Downloading Xcode 26.4: 50%%\n'
            echo "(2/6) Unarchiving Xcode (This can take a while)"
            """);
        List<XcodesOutputLine> output = [];

        XcodesResult result = await RunAsync(script, output: output);

        Assert.Equal(["(1/6) Downloading Xcode 26.4: 50%", "(2/6) Unarchiving Xcode (This can take a while)"], Lines(result));
        Assert.Equal([false, true, true, false], output.Select(line => line.ReplacesPrevious));
        Assert.True(XcodesOutputParser.TryParseProgress(output[2].Text, out XcodeProgress progress));
        Assert.Equal(0.5, progress.Fraction);
    }

    [Fact]
    public async Task RunAsync_ReturnsAFailureWithItsExitCodeAndOutput()
    {
        if (!OnMac)
            return;
        using var script = new FakeXcodesScript("echo \"Could not find version 99.0.\"\nexit 1\n");

        XcodesResult result = await RunAsync(script);

        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Could not find version 99.0.", XcodesError.Summarize(result.Output, result.ExitCode));
    }

    [Fact]
    public async Task RunAsync_PassesEachArgumentAsOneAndAsksForNoColour()
    {
        if (!OnMac)
            return;
        using var script = new FakeXcodesScript("for argument in \"$@\"; do echo \"[$argument]\"; done\n");

        XcodesResult result = await RunAsync(script, ["install", "26.1 Beta 2", "--select"]);

        Assert.Equal(["[install]", "[26.1 Beta 2]", "[--select]", "[--no-color]"], Lines(result));
    }

    [Fact]
    public async Task RunAsync_GivesXcodesATerminalType()
    {
        if (!OnMac)
            return;
        using var script = new FakeXcodesScript("echo \"TERM=$TERM\"\necho \"NO_COLOR=$NO_COLOR\"\n");

        XcodesResult dumb = await RunAsync(script, environment: new Dictionary<string, string?> { ["TERM"] = "dumb" });
        XcodesResult missing = await RunAsync(script, environment: new Dictionary<string, string?> { ["TERM"] = null });
        XcodesResult given = await RunAsync(script, environment: new Dictionary<string, string?> { ["TERM"] = "vt100" });

        Assert.Equal(["TERM=xterm-256color", "NO_COLOR=1"], Lines(dumb));
        Assert.Equal(["TERM=xterm-256color", "NO_COLOR=1"], Lines(missing));
        Assert.Equal(["TERM=vt100", "NO_COLOR=1"], Lines(given));
    }

    [Fact]
    public async Task RunAsync_AnswersTheAppleIdAndPasswordQuestions()
    {
        if (!OnMac)
            return;
        using var script = new FakeXcodesScript("""
            printf 'Apple ID: '
            read -r user
            printf 'Apple ID Password: '
            read -rs password
            printf '\n'
            echo "user=$user length=${#password}"
            """);
        List<XcodesPrompt> asked = [];
        List<XcodesOutputLine> output = [];

        XcodesResult result = await RunAsync(
            script,
            prompts: (prompt, _) =>
            {
                asked.Add(prompt);
                return Task.FromResult<string?>(prompt.Kind == XcodesPromptKind.AppleId ? "me@example.com" : "s3cret!");
            },
            output: output);

        Assert.True(result.Success);
        Assert.Equal([XcodesPromptKind.AppleId, XcodesPromptKind.Password], asked.Select(prompt => prompt.Kind));
        Assert.Equal(["Apple ID:", "Apple ID Password:"], asked.Select(prompt => prompt.Message));
        Assert.Equal([false, true], asked.Select(prompt => prompt.IsSecret));
        Assert.Contains("user=me@example.com length=7", Lines(result));

        // The password reaches xcodes and nowhere else, and the terminal's echo of the Apple ID is not logged twice.
        Assert.DoesNotContain("s3cret!", result.StandardOutput);
        Assert.DoesNotContain(output, line => line.Text.Contains("s3cret!", StringComparison.Ordinal));
        Assert.DoesNotContain("me@example.com", Lines(result));
        Assert.Contains("Apple ID:", Lines(result));
    }

    [Fact]
    public async Task RunAsync_ShowsAQuestionWhatCameBeforeIt()
    {
        if (!OnMac)
            return;
        using var script = new FakeXcodesScript("""
            echo 'Enter "sms" without quotes to exit this prompt and choose a phone number to send an SMS security code to.'
            printf 'Enter the 6 digit code from one of your trusted devices: '
            read -r code
            echo "code=$code"
            """);
        List<XcodesPrompt> asked = [];

        XcodesResult result = await RunAsync(script, prompts: (prompt, _) =>
        {
            asked.Add(prompt);
            return Task.FromResult<string?>("123456");
        });

        XcodesPrompt prompt = Assert.Single(asked);
        Assert.Equal(XcodesPromptKind.TwoFactorCode, prompt.Kind);
        Assert.Equal("Enter the 6 digit code from one of your trusted devices:", prompt.Message);
        Assert.Contains(prompt.Context, line => line.StartsWith("Enter \"sms\" without quotes", StringComparison.Ordinal));
        Assert.Contains("code=123456", Lines(result));
    }

    [Fact]
    public async Task RunAsync_AsksForTheMacOSPasswordWithoutLoggingIt()
    {
        if (!OnMac)
            return;
        using var script = new FakeXcodesScript("""
            printf 'macOS User Password: '
            read -rs password
            printf '\n'
            echo "length=${#password}"
            """);
        List<XcodesPrompt> asked = [];

        XcodesResult result = await RunAsync(script, ["select", "26.4"], prompts: (prompt, _) =>
        {
            asked.Add(prompt);
            return Task.FromResult<string?>("hunter22");
        });

        XcodesPrompt prompt = Assert.Single(asked);
        Assert.Equal(XcodesPromptKind.SudoPassword, prompt.Kind);
        Assert.True(prompt.IsSecret);
        Assert.Contains("length=8", Lines(result));
        Assert.DoesNotContain("hunter22", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_AnswersAQuestionItDoesNotKnow()
    {
        if (!OnMac)
            return;
        using var script = new FakeXcodesScript("""
            printf 'Enter the number of the Xcode to select: '
            read -r number
            echo "picked=$number"
            """);
        List<XcodesPrompt> asked = [];

        XcodesResult result = await RunAsync(script, ["select", "26.9"], prompts: (prompt, _) =>
        {
            asked.Add(prompt);
            return Task.FromResult<string?>("2");
        });

        Assert.Equal(XcodesPromptKind.Unknown, Assert.Single(asked).Kind);
        Assert.Contains("picked=2", Lines(result));
    }

    [Fact]
    public async Task RunAsync_SendsAnAnswerAsOneLine()
    {
        if (!OnMac)
            return;
        using var script = new FakeXcodesScript("""
            printf 'Apple ID: '
            read -r value
            echo "value=$value"
            echo "next=done"
            """);

        XcodesResult result = await RunAsync(script, prompts: (_, _) => Task.FromResult<string?>("one\r\ntwo\nthree"));

        Assert.Contains("value=onetwothree", Lines(result));
        Assert.Contains("next=done", Lines(result));
    }

    [Fact]
    public async Task RunAsync_FailsACommandThatAsksWhenNoOneCanAnswer()
    {
        if (!OnMac)
            return;
        using var script = new FakeXcodesScript("printf 'Apple ID: '\nsleep 30\n");
        var clock = Stopwatch.StartNew();

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => RunAsync(script));

        Assert.Equal("xcodes is waiting for input: Apple ID:", error.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"It took {clock.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_StopsWhenAQuestionIsLeftUnanswered()
    {
        if (!OnMac)
            return;
        using var script = new FakeXcodesScript("printf 'Apple ID: '\nsleep 30\n");
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunAsync(script, prompts: (_, _) => Task.FromResult<string?>(null)));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"It took {clock.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_StopsWhenCancelled()
    {
        if (!OnMac)
            return;
        using var script = new FakeXcodesScript("echo started\nsleep 30\n");
        using var cancellation = new CancellationTokenSource();
        List<XcodesOutputLine> output = [];
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new XcodesTerminalRunner(script.Path).RunAsync(
            ["install", "26.4"],
            prompts: null,
            line =>
            {
                output.Add(line);
                cancellation.Cancel();
            },
            cancellation.Token));

        Assert.Equal("started", Assert.Single(output).Text);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"It took {clock.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_StopsWhenCancelledWhileAskingTheQuestion()
    {
        if (!OnMac)
            return;
        using var script = new FakeXcodesScript("printf 'Apple ID: '\nsleep 30\n");
        using var cancellation = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new XcodesTerminalRunner(script.Path).RunAsync(
            ["install", "26.4"],
            async (_, token) =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                return "never";
            },
            output: null,
            cancellation.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"It took {clock.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_ReportsAHostThatCannotBeStarted()
    {
        if (!OnMac)
            return;
        using var script = new FakeXcodesScript("exit 0\n");

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(
            () => new XcodesTerminalRunner(script.Path, host: "/definitely/not/here/script").RunAsync(["install", "26.4"]));

        Assert.Contains("Could not start", error.Message);
    }

    [Fact]
    public async Task RunAsync_OnlyRunsOnMacOS()
    {
        if (OnMac)
            return;
        using var script = new FakeXcodesScript("exit 0\n");

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(
            () => new XcodesTerminalRunner(script.Path).RunAsync(["install", "26.4"]));

        Assert.Equal("Xcode can only be installed on macOS.", error.Message);
    }
}

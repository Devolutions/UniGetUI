namespace UniGetUI.Extensions.Xcode.Local;

/// <summary>
/// Runs a fixed command as root behind macOS's own administrator prompt. The command text never contains
/// caller data: each argument reaches the shell through AppleScript's <c>quoted form of</c>.
/// </summary>
internal static class XcodeAdministrator
{
    public const string CanceledMessage = "The administrator prompt was canceled.";

    /// <summary>Runs <paramref name="executable"/> with <paramref name="arguments"/> as root.</summary>
    public static Task RunAsync(XcodeToolRunner run, string executable, IReadOnlyList<string> arguments,
        string prompt, CancellationToken cancellationToken)
        => RunAsync(run, [new XcodeRootCommand(executable, arguments)], prompt, cancellationToken);

    /// <summary>
    /// Runs every command as root, one after the other, behind a single prompt. The first that fails stops the rest.
    /// </summary>
    public static async Task RunAsync(XcodeToolRunner run, IReadOnlyList<XcodeRootCommand> commands,
        string prompt, CancellationToken cancellationToken)
    {
        if (commands.Count == 0)
            return;
        foreach (XcodeRootCommand command in commands)
        {
            if (!Path.IsPathFullyQualified(command.Executable))
                throw new ArgumentException("The command must be an absolute path.", nameof(commands));
            if (command.Executable.Any(char.IsControl)
                || command.LiteralScriptIndex is { } index && (index < 0 || index >= command.Arguments.Count)
                || command.Arguments.Where((value, index) => value.Any(character =>
                    char.IsControl(character) && !(index == command.LiteralScriptIndex && character == '\n'))).Any())
                throw new XcodesException("A path for the administrator command contains control characters.");
        }
        if (prompt.Any(char.IsControl))
            throw new ArgumentException("The prompt contains control characters.", nameof(prompt));

        XcodesResult result = await run("/usr/bin/osascript", BuildArguments(commands, prompt), cancellationToken)
            .ConfigureAwait(false);
        if (result.Success)
            return;
        // -128 is AppleScript's "User canceled."
        if (result.StandardError.Contains("(-128)", StringComparison.Ordinal))
            throw new OperationCanceledException(CanceledMessage);
        throw new XcodesException(XcodesError.Summarize(result.Output, result.ExitCode));
    }

    /// <summary>
    /// argv is the prompt, then each command as its word count followed by its words. The script quotes every word
    /// and joins the commands with <c>&amp;&amp;</c>.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(IReadOnlyList<XcodeRootCommand> commands, string prompt)
    {
        List<string> osascript =
        [
            "-e", "on run argv",
            "-e", "set fullCommand to \"\"",
            "-e", "set i to 2",
            "-e", "repeat while i is less than or equal to (count of argv)",
            "-e", "set wordCount to (item i of argv) as integer",
            "-e", "set oneCommand to \"\"",
            "-e", "repeat with j from (i + 1) to (i + wordCount)",
            "-e", "if oneCommand is not \"\" then set oneCommand to oneCommand & \" \"",
            "-e", "set oneCommand to oneCommand & quoted form of (item j of argv)",
            "-e", "end repeat",
            "-e", "if fullCommand is not \"\" then set fullCommand to fullCommand & \" && \"",
            "-e", "set fullCommand to fullCommand & oneCommand",
            "-e", "set i to i + wordCount + 1",
            "-e", "end repeat",
            "-e", "do shell script fullCommand with prompt (item 1 of argv) with administrator privileges",
            "-e", "end run",
            prompt,
        ];
        foreach (XcodeRootCommand command in commands)
        {
            osascript.Add((command.Arguments.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            osascript.Add(command.Executable);
            osascript.AddRange(command.Arguments);
        }
        return osascript;
    }
}

/// <summary>One command for <see cref="XcodeAdministrator"/>: an absolute executable path and its arguments.</summary>
/// <param name="LiteralScriptIndex">The argument containing a fixed script, which may contain line breaks.</param>
internal sealed record XcodeRootCommand(
    string Executable, IReadOnlyList<string> Arguments, int? LiteralScriptIndex = null);

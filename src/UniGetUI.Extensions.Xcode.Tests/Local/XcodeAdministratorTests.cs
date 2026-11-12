using System.Diagnostics;
using UniGetUI.Extensions.Xcode.Local;

namespace UniGetUI.Extensions.Xcode.Tests.Local;

public sealed class XcodeAdministratorTests
{
    [Fact]
    public async Task RejectsControlCharactersInCallerArguments()
    {
        bool called = false;
        Task<XcodesResult> Run(string _, IReadOnlyList<string> __, CancellationToken ___)
        {
            called = true;
            return Task.FromResult(new XcodesResult(0, "", ""));
        }

        XcodeRootCommand command = new("/bin/sh", ["-c", "/bin/true\n/bin/true", "sh", "path\nwith-newline"], 1);
        await Assert.ThrowsAsync<XcodesException>(() => XcodeAdministrator.RunAsync(
            Run, [command], "Test", CancellationToken.None));

        Assert.False(called);
    }

    [Fact]
    public async Task QuotesMultilineScriptsAndCallerArgumentsInAppleScript()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        using var root = new TemporaryDirectory();
        string destination = Path.Combine(root.Path, "a 'quoted' $path with spaces");
        const string script = "/usr/bin/printf '%s' \"$2\" > \"$1\"\n/usr/bin/true";
        const string text = "a 'quoted' $value with spaces";
        XcodeRootCommand command = new("/bin/sh", ["-c", script, "sh", destination, text], 1);

        await XcodeAdministrator.RunAsync(RunWithoutElevation, [command], "Test", CancellationToken.None);

        Assert.Equal(text, await File.ReadAllTextAsync(destination));
    }

    private static async Task<XcodesResult> RunWithoutElevation(
        string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument == "do shell script fullCommand with prompt (item 1 of argv) with administrator privileges"
                ? "do shell script fullCommand" : argument);
        }
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new XcodesResult(process.ExitCode, await output, await error);
    }
}

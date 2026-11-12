using System.Diagnostics;
using UniGetUI.Extensions.Xcode.Local;

namespace UniGetUI.Extensions.Xcode.Tests.Local;

public sealed class XcodeProtectedInstallationTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly string _root;
    private readonly string _source;
    private readonly string _destination;
    private readonly string _audit;
    private readonly string _script;

    public XcodeProtectedInstallationTests()
    {
        _root = OperatingSystem.IsMacOS() && _directory.Path.StartsWith("/var/", StringComparison.Ordinal)
            ? "/private" + _directory.Path : _directory.Path;
        _audit = Path.Combine(_root, "audit");
        string apps = Directory.CreateDirectory(Path.Combine(_root, "Applications")).FullName;
        _destination = Path.Combine(apps, "Xcode-26.4.app");
        _source = FakeXcodeBundle.Create(_root, "source.app", "26.4", "17E192");
        string developerBin = Directory.CreateDirectory(Path.Combine(_source, "Contents/Developer/usr/bin")).FullName;
        Script(Path.Combine(developerBin, "xcodebuild"), $$"""
            #!/bin/sh
            stage=${0%/Xcode.app/Contents/Developer/usr/bin/xcodebuild}
            /usr/bin/printf 'xcodebuild|%s|%s|%s|%s|%s\n' "$0" "$*" "$DEVELOPER_DIR" "$HOME" "$(/usr/bin/stat -f %Lp "$stage")" >> {{Quote(_audit)}}
            """);

        string codesign = Tool("codesign", $$"""
            #!/bin/sh
            /usr/bin/printf 'codesign|%s\n' "$*" >> {{Quote(_audit)}}
            if [ -f {{Quote(Path.Combine(_root, "invalid-signature"))}} ]; then exit 1; fi
            if [ "$1" = '--verify' ]; then exit 0; fi
            /usr/bin/printf '%s\n' \
                'Identifier=com.apple.dt.Xcode' \
                'Authority=Software Signing' \
                'Authority=Apple Code Signing Certification Authority' \
                'Authority=Apple Root CA' \
                'TeamIdentifier=59GAB85EFG' >&2
            """);
        string spctl = Tool("spctl", $$"""
            #!/bin/sh
            /usr/bin/printf 'spctl|%s\n' "$*" >> {{Quote(_audit)}}
            if [ -f {{Quote(Path.Combine(_root, "gatekeeper-rejects"))}} ]; then exit 1; fi
            """);
        string ditto = Tool("ditto", $$"""
            #!/bin/sh
            /usr/bin/printf 'ditto|%s\n' "$*" >> {{Quote(_audit)}}
            if [ -f {{Quote(Path.Combine(_root, "fail-final-copy"))}} ] && [ "$3" = {{Quote(_destination)}} ]; then
                /usr/bin/touch "$3/partial"
                exit 1
            fi
            exec /usr/bin/ditto "$@"
            """);
        string stat = Tool("stat", """
            #!/bin/sh
            case "$2" in %u) echo 0;; %g) echo 0;; %d) echo 123;; %Lp) echo 700;; '%d:%i') exec /usr/bin/stat "$@";; *) exit 1;; esac
            """);
        string ls = Tool("ls", $$"""
            #!/bin/sh
            echo directory
            if [ -f {{Quote(Path.Combine(_root, "acl"))}} ] && [ "$2" = {{Quote(Path.GetDirectoryName(_destination)!)}} ]; then
                echo '0: user:other allow write'
            fi
            """);
        string sudo = Tool("sudo", $$"""
            #!/bin/sh
            if [ -f {{Quote(Path.Combine(_root, "writable"))}} ]; then exit 10; fi
            if [ -f {{Quote(Path.Combine(_root, "sudo-error"))}} ]; then exit 42; fi
            exit 20
            """);
        string security = Tool("security", $$"""
            #!/bin/sh
            /usr/bin/printf 'security|%s\n' "$*" >> {{Quote(_audit)}}
            """);
        string group = Tool("group", $$"""
            #!/bin/sh
            /usr/bin/printf 'group|%s\n' "$*" >> {{Quote(_audit)}}
            """);
        string select = Tool("select", $$"""
            #!/bin/sh
            /usr/bin/printf 'select|%s\n' "$*" >> {{Quote(_audit)}}
            """);

        _script = XcodeArchiveInstaller.ProtectedInstallationScript
            .Replace("/private/var/root/unigetui-xcode.XXXXXXXX", Path.Combine(_root, "protected.XXXXXXXX"), StringComparison.Ordinal)
            .Replace("/private/var/root", Quote(_root), StringComparison.Ordinal)
            .Replace("/usr/bin/codesign", Quote(codesign), StringComparison.Ordinal)
            .Replace("/usr/sbin/spctl", Quote(spctl), StringComparison.Ordinal)
            .Replace("/usr/bin/ditto", Quote(ditto), StringComparison.Ordinal)
            .Replace("/usr/sbin/chown", "/usr/bin/true", StringComparison.Ordinal)
            .Replace("/usr/bin/stat", Quote(stat), StringComparison.Ordinal)
            .Replace("/bin/ls", Quote(ls), StringComparison.Ordinal)
            .Replace("/usr/bin/sudo", Quote(sudo), StringComparison.Ordinal)
            .Replace("/usr/sbin/DevToolsSecurity", Quote(security), StringComparison.Ordinal)
            .Replace("/usr/sbin/dseditgroup", Quote(group), StringComparison.Ordinal)
            .Replace("/usr/bin/xcode-select", Quote(select), StringComparison.Ordinal);
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task RunsPrivilegedSetupFromThePrivateReverifiedCopy()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        CopyDirectory(_source, _destination);

        Result result = await Run(copy: false, select: true);

        Assert.True(result.ExitCode == 0, result.Error);
        string[] lines = File.ReadAllLines(_audit);
        string[] builds = lines.Where(line => line.StartsWith("xcodebuild|", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, builds.Length);
        Assert.All(builds, line =>
        {
            Assert.Contains("/protected.", line);
            Assert.DoesNotContain(_destination, line);
            Assert.Contains("|/var/root|700", line);
        });
        Assert.Contains(lines, line => line == $"codesign|--verify --deep --strict --verbose=1 {_destination}");
        Assert.Contains(lines, line => line == $"select|-s {_destination}");
        Assert.True(Array.FindIndex(lines, line => line == $"codesign|--verify --deep --strict --verbose=1 {_destination}")
                    < Array.FindIndex(lines, line => line == $"select|-s {_destination}"));
        Assert.True(Array.FindIndex(lines, line => line.StartsWith("codesign|--verify --deep --strict", StringComparison.Ordinal))
                    < Array.FindIndex(lines, line => line.StartsWith("xcodebuild|", StringComparison.Ordinal)));
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task RefusesToSelectATamperedDestinationWhenNotCopiedAsRoot()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        CopyDirectory(_source, _destination);
        File.WriteAllText(Path.Combine(_destination, "Contents/version.plist"),
            FakeXcodeBundle.Plist(("ProductBuildVersion", "17E999")));

        Result result = await Run(copy: false, select: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("requested version and build", result.Error);
        Assert.DoesNotContain("select|", File.ReadAllText(_audit));
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task RefusesToSelectASymlinkedDestinationWhenNotCopiedAsRoot()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        CopyDirectory(_source, _destination);
        string link = Path.Combine(_root, "linked-select");
        Directory.CreateSymbolicLink(link, Path.GetDirectoryName(_destination)!);

        Result result = await Run(copy: false, select: true, destination: Path.Combine(link, "Xcode-26.4.app"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("link or inaccessible", result.Error);
        Assert.DoesNotContain("select|", File.ReadAllText(_audit));
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task CopiesTheVerifiedBundleToAProtectedDestinationWithoutOverwriting()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        Result result = await Run(copy: true);

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.True(File.Exists(Path.Combine(_destination, "Contents", "Info.plist")));
        Assert.Equal("755", Convert.ToString((int)File.GetUnixFileMode(_destination) & 0x1ff, 8));
        Assert.True(Directory.Exists(_source));
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task ExistingDestinationIsPreservedBeforeAnyPrivilegedSetup()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        Directory.CreateDirectory(_destination);
        string marker = Path.Combine(_destination, "existing");
        File.WriteAllText(marker, "keep");

        Result result = await Run(copy: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("keep", File.ReadAllText(marker));
        Assert.DoesNotContain("xcodebuild|", File.ReadAllText(_audit));
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task FailedCopyRemovesOnlyTheDirectoryThisInstallClaimed()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        File.WriteAllText(Path.Combine(_root, "fail-final-copy"), "");

        Result result = await Run(copy: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(Directory.Exists(_destination));
        Assert.DoesNotContain("xcodebuild|", File.ReadAllText(_audit));
        AssertNoStagingDirectories();
    }

    [Theory]
    [InlineData("writable")]
    [InlineData("acl")]
    [InlineData("sudo-error")]
    public async Task RefusesAnUntrustedOrUnverifiableDestinationParent(string problem)
    {
        if (!OperatingSystem.IsMacOS())
            return;
        File.WriteAllText(Path.Combine(_root, problem), "");

        Result result = await Run(copy: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(Directory.Exists(_destination));
        Assert.DoesNotContain("xcodebuild|", File.ReadAllText(_audit));
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task RefusesASymlinkInThePrivilegedDestination()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        string link = Path.Combine(_root, "linked-applications");
        Directory.CreateSymbolicLink(link, Path.GetDirectoryName(_destination)!);

        Result result = await Run(copy: true, destination: Path.Combine(link, "Xcode-26.4.app"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(Directory.Exists(_destination));
        Assert.Contains("link or inaccessible", result.Error);
        AssertNoStagingDirectories();
    }

    [Theory]
    [InlineData("invalid-signature")]
    [InlineData("gatekeeper-rejects")]
    public async Task DoesNotRunSetupWhenTheProtectedCopyFailsVerification(string failure)
    {
        if (!OperatingSystem.IsMacOS())
            return;
        File.WriteAllText(Path.Combine(_root, failure), "");

        Result result = await Run(copy: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(Directory.Exists(_destination));
        Assert.DoesNotContain("xcodebuild|", File.ReadAllText(_audit));
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task DoesNotRunSetupWhenTheProtectedCopyHasADifferentBuild()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        File.WriteAllText(Path.Combine(_source, "Contents/version.plist"),
            FakeXcodeBundle.Plist(("ProductBuildVersion", "17E999")));

        Result result = await Run(copy: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("requested version and build", result.Error);
        Assert.False(Directory.Exists(_destination));
        Assert.DoesNotContain("xcodebuild|", File.ReadAllText(_audit));
        AssertNoStagingDirectories();
    }

    private async Task<Result> Run(bool copy, bool select = false, string? destination = null)
    {
        XcodeRootCommand command = XcodeArchiveInstaller.ProtectedInstallation(
            _source, destination ?? _destination, "17E192", "26.4", copy, select, 501);
        string[] arguments = [.. command.Arguments];
        arguments[command.LiteralScriptIndex!.Value] = _script;
        var start = new ProcessStartInfo(command.Executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new Result(process.ExitCode, await output, await error);
    }

    private void AssertNoStagingDirectories()
        => Assert.Empty(Directory.EnumerateDirectories(_root, "protected.*"));

    private string Tool(string name, string body)
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root, "tools")).FullName;
        return Script(Path.Combine(folder, name), body);
    }

    private static string Script(string path, string body)
    {
        File.WriteAllText(path, body + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            File.Copy(file, target);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, File.GetUnixFileMode(file));
        }
    }

    private static string Quote(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private sealed record Result(int ExitCode, string Output, string Error);
}

using System.Diagnostics;
using UniGetUI.Extensions.Xcode.Local;

namespace UniGetUI.Extensions.Xcode.Tests.Local;

public sealed class XcodeSelectorTests
{
    private const string ValidSignature = """
        Identifier=com.apple.dt.Xcode
        Authority=Software Signing
        Authority=Apple Code Signing Certification Authority
        Authority=Apple Root CA
        TeamIdentifier=59GAB85EFG
        """;

    private sealed class FakeMac
    {
        public string? Selected { get; set; }
        public List<IReadOnlyList<string>> Elevated { get; } = [];
        public string? ElevationError { get; set; }
        public bool IgnoreSelection { get; set; }
        public bool InvalidSignature { get; set; }
        public bool GatekeeperRejects { get; set; }
        public string SignatureDescription { get; set; } = ValidSignature;

        public Task<XcodesResult> Run(string executable, IReadOnlyList<string> arguments, CancellationToken _)
        {
            if (executable == "/usr/bin/xcode-select" && arguments is ["-p"])
                return Task.FromResult(Selected is null
                    ? new XcodesResult(2, "", "unable to get active developer directory")
                    : new XcodesResult(0, Selected + "/Contents/Developer\n", ""));
            if (executable == "/usr/bin/codesign" && arguments is ["--verify", ..])
                return Task.FromResult(InvalidSignature
                    ? new XcodesResult(1, "", "code object is not signed at all")
                    : new XcodesResult(0, "", ""));
            if (executable == "/usr/bin/codesign" && arguments is ["-vv", "-d", _])
                return Task.FromResult(new XcodesResult(0, "", SignatureDescription));
            if (executable == "/usr/sbin/spctl")
                return Task.FromResult(GatekeeperRejects
                    ? new XcodesResult(3, "", "rejected")
                    : new XcodesResult(0, "", ""));
            if (executable == "/usr/bin/osascript")
            {
                Elevated.Add(arguments);
                if (ElevationError is not null)
                    return Task.FromResult(new XcodesResult(1, "", ElevationError));
                int script = arguments.ToList().IndexOf(XcodeSelector.ProtectedSelectionScript);
                if (!IgnoreSelection && script >= 0)
                    Selected = arguments[script + 1];
                return Task.FromResult(new XcodesResult(0, "", ""));
            }
            return Task.FromResult(new XcodesResult(1, "", "unexpected"));
        }
    }

    [Fact]
    public async Task SelectsTheExactVerifiedPathBehindProtectedRootCheck()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var folder = new TemporaryDirectory();
        string first = FakeXcodeBundle.Create(folder.Path, "Xcode-26.4.app", "26.4", "17E192");
        string second = FakeXcodeBundle.Create(folder.Path, "Xcode copy.app", "26.4", "17E192");
        var mac = new FakeMac { Selected = InstalledXcodeScanner.Resolve(first) };

        await new XcodeSelector(mac.Run).SelectAsync(second, CancellationToken.None);

        string expected = InstalledXcodeScanner.Resolve(second);
        Assert.Equal(expected, mac.Selected);
        IReadOnlyList<string> call = Assert.Single(mac.Elevated);
        int script = call.ToList().IndexOf(XcodeSelector.ProtectedSelectionScript);
        Assert.True(script >= 0);
        Assert.Equal([expected, "17E192", "26.4"], call.Skip(script + 1));
    }

    [Fact]
    public async Task RefusesUnsignedTamperedOrSymlinkedBundlesBeforeElevation()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var folder = new TemporaryDirectory();
        string app = FakeXcodeBundle.Create(folder.Path, "Xcode.app", "26.4", "17E192");
        string symlink = Path.Combine(folder.Path, "Xcode-link.app");
        Directory.CreateSymbolicLink(symlink, app);

        var unsignedMac = new FakeMac { InvalidSignature = true };
        await Assert.ThrowsAsync<XcodesException>(() => new XcodeSelector(unsignedMac.Run).SelectAsync(app, CancellationToken.None));
        Assert.Empty(unsignedMac.Elevated);

        var gatekeeperMac = new FakeMac { GatekeeperRejects = true };
        await Assert.ThrowsAsync<XcodesException>(() => new XcodeSelector(gatekeeperMac.Run).SelectAsync(app, CancellationToken.None));
        Assert.Empty(gatekeeperMac.Elevated);

        var untrustedTeamMac = new FakeMac
        {
            SignatureDescription = ValidSignature.Replace("59GAB85EFG", "ATTACKER12", StringComparison.Ordinal),
        };
        await Assert.ThrowsAsync<XcodesException>(() => new XcodeSelector(untrustedTeamMac.Run).SelectAsync(app, CancellationToken.None));
        Assert.Empty(untrustedTeamMac.Elevated);

        var symlinkMac = new FakeMac();
        await Assert.ThrowsAsync<XcodesException>(() => new XcodeSelector(symlinkMac.Run).SelectAsync(symlink, CancellationToken.None));
        Assert.Empty(symlinkMac.Elevated);
    }

    [Fact]
    public async Task ProtectedSelectionScriptVerifiesBundleAndRejectsSymlinksAndMismatchedBuilds()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        using var folder = new TemporaryDirectory();
        string root = folder.Path.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + folder.Path : folder.Path;
        string app = FakeXcodeBundle.Create(root, "Xcode-26.4.app", "26.4", "17E192");
        string developerBin = Directory.CreateDirectory(Path.Combine(app, "Contents/Developer/usr/bin")).FullName;
        File.WriteAllText(Path.Combine(developerBin, "xcodebuild"), "#!/bin/sh\n");
        string audit = Path.Combine(root, "audit");
        string tools = Directory.CreateDirectory(Path.Combine(root, "tools")).FullName;

        string Tool(string name, string body)
        {
            string path = Path.Combine(tools, name);
            File.WriteAllText(path, body + "\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        }

        string quotedAudit = "'" + audit.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        string swapFlag = Path.Combine(root, "swap-after-verify");
        string quotedSwapFlag = "'" + swapFlag.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        string script = XcodeSelector.ProtectedSelectionScript
            .Replace("/usr/sbin/chown", "/usr/bin/true", StringComparison.Ordinal)
            .Replace("/usr/bin/codesign", Tool("codesign", $$"""
                #!/bin/sh
                if [ "$1" = '--verify' ]; then
                    if [ -f {{quotedSwapFlag}} ] && [ "${4%/Contents/Developer/usr/bin/xcodebuild}" != "$4" ]; then
                        bundle=${4%/Contents/Developer/usr/bin/xcodebuild}
                        /bin/mv "$bundle" "$bundle.old"
                        /bin/mkdir -p "$bundle"
                    fi
                    exit 0
                fi
                /usr/bin/printf '%s\n' 'Identifier=com.apple.dt.Xcode' 'Authority=Software Signing' 'Authority=Apple Code Signing Certification Authority' 'Authority=Apple Root CA' 'TeamIdentifier=59GAB85EFG' >&2
                """), StringComparison.Ordinal)
            .Replace("/usr/sbin/spctl", Tool("spctl", "#!/bin/sh\nexit 0"), StringComparison.Ordinal)
            .Replace("/usr/bin/xcode-select", Tool("select", $$"""
                #!/bin/sh
                if [ "$1" = '-p' ]; then echo '/Applications/Xcode-Prev.app/Contents/Developer'; exit 0; fi
                /usr/bin/printf 'select|%s\n' "$*" >> {{quotedAudit}}
                """), StringComparison.Ordinal);

        async Task<(int ExitCode, string Error)> RunProtectedAsync(string destination, string build = "17E192")
        {
            XcodeRootCommand command = XcodeSelector.ProtectedSelection(destination, "26.4", build);
            string[] arguments = [.. command.Arguments];
            arguments[command.LiteralScriptIndex!.Value] = script;
            var start = new ProcessStartInfo(command.Executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)!;
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await error);
        }

        // Verify a user-added writable ACL on the bundle is stripped before verification and selection.
        using (Process addAcl = Process.Start("/bin/chmod", ["+a", $"{Environment.UserName} allow write,delete", app])!)
            await addAcl.WaitForExitAsync();
        (int ok, string okErr) = await RunProtectedAsync(app);
        Assert.True(ok == 0, okErr);
        Assert.Equal($"select|-s {app}", File.ReadAllText(audit).Trim());
        using (Process checkAcl = Process.Start(new ProcessStartInfo("/bin/ls", ["-lde", app]) { RedirectStandardOutput = true })!)
        {
            string lsOut = await checkAcl.StandardOutput.ReadToEndAsync();
            await checkAcl.WaitForExitAsync();
            Assert.Single(lsOut.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }

        File.Delete(audit);
        (int wrongBuild, string buildErr) = await RunProtectedAsync(app, "17E999");
        Assert.NotEqual(0, wrongBuild);
        Assert.Contains("expected version and build", buildErr);
        Assert.False(File.Exists(audit));

        string linkedParent = Path.Combine(root, "linked");
        Directory.CreateSymbolicLink(linkedParent, root);
        (int linked, string linkErr) = await RunProtectedAsync(Path.Combine(linkedParent, "Xcode-26.4.app"));
        Assert.NotEqual(0, linked);
        Assert.Contains("link or inaccessible", linkErr);
        Assert.False(File.Exists(audit));

        string worldWritable = Directory.CreateDirectory(Path.Combine(root, "world")).FullName;
        File.SetUnixFileMode(worldWritable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        string worldApp = FakeXcodeBundle.Create(worldWritable, "Xcode-26.4.app", "26.4", "17E192");
        Directory.CreateDirectory(Path.Combine(worldApp, "Contents/Developer/usr/bin"));
        File.WriteAllText(Path.Combine(worldApp, "Contents/Developer/usr/bin/xcodebuild"), "#!/bin/sh\n");
        (int worldExit, string worldErr) = await RunProtectedAsync(worldApp);
        Assert.NotEqual(0, worldExit);
        Assert.Contains("world-writable", worldErr);
        Assert.False(File.Exists(audit));

        // Simulate replacing the bundle after the final codesign check and before xcode-select -s.
        File.WriteAllText(swapFlag, "");
        (int swappedExit, string swappedErr) = await RunProtectedAsync(app);
        Assert.NotEqual(0, swappedExit);
        Assert.Contains("changed during verification", swappedErr);
        Assert.False(File.Exists(audit));
    }

    [Fact]
    public async Task AlreadySelectedNeedsNoPrompt()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var folder = new TemporaryDirectory();
        string app = FakeXcodeBundle.Create(folder.Path, "Xcode.app", "26.4", "17E192");
        var mac = new FakeMac { Selected = InstalledXcodeScanner.Resolve(app) };

        await new XcodeSelector(mac.Run).SelectAsync(app, CancellationToken.None);

        Assert.Empty(mac.Elevated);

        var unsignedSelected = new FakeMac { Selected = InstalledXcodeScanner.Resolve(app), InvalidSignature = true };
        await Assert.ThrowsAsync<XcodesException>(() =>
            new XcodeSelector(unsignedSelected.Run).SelectAsync(app, CancellationToken.None));
        Assert.Empty(unsignedSelected.Elevated);
    }

    [Fact]
    public async Task CancelingThePromptCancelsAndAFailedSwitchIsReported()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var folder = new TemporaryDirectory();
        string app = FakeXcodeBundle.Create(folder.Path, "Xcode.app", "26.4", "17E192");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new XcodeSelector(new FakeMac { ElevationError = "execution error: User canceled. (-128)" }.Run)
                .SelectAsync(app, CancellationToken.None));
        await Assert.ThrowsAsync<XcodesException>(() =>
            new XcodeSelector(new FakeMac { IgnoreSelection = true }.Run).SelectAsync(app, CancellationToken.None));
        await Assert.ThrowsAsync<XcodesException>(() =>
            new XcodeSelector(new FakeMac().Run).SelectAsync(Path.Combine(folder.Path, "Gone.app"), CancellationToken.None));
    }
}

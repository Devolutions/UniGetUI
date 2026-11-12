using UniGetUI.Extensions.Xcode.Local;

namespace UniGetUI.Extensions.Xcode.Tests.Local;

public sealed class XcodeArchiveInstallerTests : IDisposable
{
    private const string AppleSignature = """
        Executable=/x/Xcode.app/Contents/MacOS/Xcode
        Identifier=com.apple.dt.Xcode
        Authority=Software Signing
        Authority=Apple Code Signing Certification Authority
        Authority=Apple Root CA
        TeamIdentifier=59GAB85EFG
        """;

    private readonly TemporaryDirectory _root = new();
    private readonly string _archives;
    private readonly string _applications;

    public XcodeArchiveInstallerTests()
    {
        _archives = Directory.CreateDirectory(Path.Combine(_root.Path, "archives")).FullName;
        _applications = Directory.CreateDirectory(Path.Combine(_root.Path, "Applications")).FullName;
    }

    public void Dispose() => _root.Dispose();

    private sealed class FakeMac(string build = "17E192")
    {
        public List<string> Calls { get; } = [];
        public List<IReadOnlyList<string>> Elevated { get; } = [];
        public string Signature { get; set; } = AppleSignature;
        public bool GatekeeperRejects { get; set; }
        public bool SignatureVerificationFails { get; set; }
        public bool MoveDenied { get; set; }
        public bool CreateDestinationBeforeCopy { get; set; }
        public bool CreateDestinationBeforeElevation { get; set; }
        public Action<CancellationToken>? OnElevation { get; set; }
        public string? ExpandError { get; set; }

        public Task<XcodesResult> Run(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(executable + " " + string.Join(' ', arguments));
            switch (executable)
            {
                case "/bin/sh" when arguments[4] is "/usr/bin/xip":
                    if (ExpandError is not null)
                        return Done(1, ExpandError);
                    FakeXcodeBundle.Create(arguments[3], "Xcode.app", "26.4", build);
                    return Done();
                case "/usr/sbin/spctl":
                    return GatekeeperRejects ? Done(3, "rejected") : Done(0, "accepted");
                case "/usr/bin/codesign" when arguments[0] == "--verify":
                    return SignatureVerificationFails ? Done(1, "invalid signature") : Done();
                case "/usr/bin/codesign":
                    return Done(0, Signature);
                case "/usr/bin/id":
                    return Task.FromResult(new XcodesResult(0, "501\n", ""));
                case "/bin/sh" when arguments[1].Contains("/usr/bin/ditto", StringComparison.Ordinal):
                    if (MoveDenied)
                        return Done(77, "simulated permission error");
                    if (CreateDestinationBeforeCopy)
                        Directory.CreateDirectory(arguments[4]);
                    if (Directory.Exists(arguments[4]))
                        return Done(1, "The destination already exists.");
                    CopyDirectory(arguments[3], arguments[4]);
                    return Done();
                case "/usr/bin/osascript":
                    Elevated.Add(arguments);
                    OnElevation?.Invoke(cancellationToken);
                    int script = arguments.ToList().IndexOf(XcodeArchiveInstaller.ProtectedInstallationScript);
                    if (script >= 0 && arguments[script + 5] == "1")
                    {
                        string source = arguments[script + 1];
                        string destination = arguments[script + 2];
                        if (CreateDestinationBeforeElevation)
                            Directory.CreateDirectory(destination);
                        if (Directory.Exists(destination))
                            return Done(1, "The destination already exists.");
                        CopyDirectory(source, destination);
                    }
                    return Done();
                case "/usr/bin/getconf":
                case "/usr/bin/sw_vers":
                    return Done(1, "unavailable in tests");
                default:
                    return Done(1, "unexpected " + executable);
            }
        }

        private static Task<XcodesResult> Done(int code = 0, string error = "")
            => Task.FromResult(new XcodesResult(code, "", error));

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    private string Archive()
    {
        string archive = Path.Combine(_archives, "Xcode_26.4.xip");
        File.WriteAllBytes(archive, [1, 2, 3]);
        return archive;
    }

    private async Task<XcodeInstallationResult> Install(FakeMac mac, XcodeInstallOptions options, string? build = "17E192",
        CancellationToken cancellationToken = default, XcodeArchitecture architecture = XcodeArchitecture.Unspecified)
    {
        var installer = new XcodeArchiveInstaller(mac.Run, mac.Run);
        var target = new XcodeArchiveInstaller.Target("26.4", build, architecture);
        string destination = await installer.FindDestinationAsync(_applications, target, cancellationToken);
        return await installer.InstallAsync(Archive(), target, options, _applications, destination, (_, _) => { }, cancellationToken);
    }

    [Fact]
    public async Task InstallsAVerifiedXcodeAndFinishesItBehindOnePrompt()
    {
        if (OperatingSystem.IsWindows())
            return;
        var mac = new FakeMac();

        XcodeInstallationResult installed = await Install(mac, new XcodeInstallOptions(Select: true));
        string app = installed.Path;

        Assert.Equal(Path.Combine(_applications, "Xcode-26.4.app"), app);
        Assert.Equal("17E192", installed.Build);
        Assert.Equal(XcodeArchitecture.AppleSilicon, installed.Architecture);
        Assert.True(Directory.Exists(app));
        IReadOnlyList<string> prompt = Assert.Single(mac.Elevated);
        string script = Assert.Single(prompt, value => value == XcodeArchiveInstaller.ProtectedInstallationScript);
        Assert.Contains("/usr/sbin/DevToolsSecurity", script);
        Assert.Contains("\"$xcodebuild\" -runFirstLaunch", script);
        Assert.Contains("xcodebuild=\"$staged/Contents/Developer/usr/bin/xcodebuild\"", script);
        Assert.Contains(app, prompt);
        Assert.DoesNotContain(Path.Combine(app, "Contents/Developer/usr/bin/xcodebuild"), prompt);
        Assert.True(mac.Calls.Count(call => call.StartsWith("/usr/bin/codesign --verify", StringComparison.Ordinal)) >= 3);
        Assert.True(File.Exists(Path.Combine(_archives, "Xcode_26.4.xip")));
        Assert.Equal(["Xcode_26.4.xip"], Directory.EnumerateFileSystemEntries(_archives).Select(Path.GetFileName));
    }

    [Fact]
    public async Task MovesAsRootWhenTheFolderIsNotWritable()
    {
        if (OperatingSystem.IsWindows())
            return;
        var mac = new FakeMac { MoveDenied = true };

        string app = (await Install(mac, new XcodeInstallOptions(DeleteArchive: true))).Path;

        Assert.True(Directory.Exists(app));
        IReadOnlyList<string> prompt = Assert.Single(mac.Elevated);
        Assert.Contains("/bin/sh", prompt);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_archives));
    }

    [Fact]
    public async Task AnExistingDestinationStopsThePrivilegedMoveBeforePostInstall()
    {
        if (OperatingSystem.IsWindows())
            return;
        var mac = new FakeMac { MoveDenied = true, CreateDestinationBeforeElevation = true };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => Install(mac, new XcodeInstallOptions()));

        Assert.Contains("destination already exists", error.Message);
        Assert.Single(mac.Elevated);
        Assert.True(Directory.Exists(Path.Combine(_applications, "Xcode-26.4.app")));
    }

    [Fact]
    public async Task CopyCommandFailsRatherThanAcceptingAnExistingDestination()
    {
        if (OperatingSystem.IsWindows())
            return;
        string source = Directory.CreateDirectory(Path.Combine(_root.Path, "source.app")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(_root.Path, "destination.app")).FullName;
        XcodeRootCommand command = XcodeArchiveInstaller.CopyWithoutReplacement(source, destination);
        var start = new System.Diagnostics.ProcessStartInfo(command.Executable) { UseShellExecute = false };
        foreach (string argument in command.Arguments)
            start.ArgumentList.Add(argument);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();

        Assert.NotEqual(0, process.ExitCode);
        Assert.True(Directory.Exists(source));
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public async Task AnExistingDestinationStopsTheUnprivilegedCopy()
    {
        if (OperatingSystem.IsWindows())
            return;
        var mac = new FakeMac { CreateDestinationBeforeCopy = true };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => Install(mac, new XcodeInstallOptions()));

        Assert.Contains("destination already exists", error.Message);
        Assert.Empty(mac.Elevated);
        Assert.True(Directory.Exists(Path.Combine(_applications, "Xcode-26.4.app")));
    }

    [Fact]
    public async Task CopyCommandCopiesToANewDestination()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        string source = Directory.CreateDirectory(Path.Combine(_root.Path, "source.app")).FullName;
        string destination = Path.Combine(_root.Path, "destination.app");
        await File.WriteAllTextAsync(Path.Combine(source, "marker"), "verified");
        XcodeRootCommand command = XcodeArchiveInstaller.CopyWithoutReplacement(source, destination);
        var start = new System.Diagnostics.ProcessStartInfo(command.Executable) { UseShellExecute = false };
        foreach (string argument in command.Arguments)
            start.ArgumentList.Add(argument);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("verified", await File.ReadAllTextAsync(Path.Combine(destination, "marker")));
    }

    [Fact]
    public async Task SkipsThePromptWhenAskedTo()
    {
        if (OperatingSystem.IsWindows())
            return;
        var mac = new FakeMac();

        await Install(mac, new XcodeInstallOptions(SkipAdministratorSteps: true));

        Assert.Empty(mac.Elevated);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("sealed-resource")]
    [InlineData("gatekeeper")]
    [InlineData("build")]
    public async Task NeverInstallsAnXcodeThatFailsItsChecks(string failure)
    {
        if (OperatingSystem.IsWindows())
            return;
        var mac = new FakeMac(build: failure == "build" ? "17E999" : "17E192")
        {
            Signature = failure == "signature" ? AppleSignature.Replace("59GAB85EFG", "ABCDE12345") : AppleSignature,
            GatekeeperRejects = failure == "gatekeeper",
            SignatureVerificationFails = failure == "sealed-resource",
        };

        await Assert.ThrowsAsync<XcodesException>(() => Install(mac, new XcodeInstallOptions()));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_applications));
        Assert.Empty(mac.Elevated);
        Assert.Equal(["Xcode_26.4.xip"], Directory.EnumerateFileSystemEntries(_archives).Select(Path.GetFileName));
    }

    [Fact]
    public async Task RefusesAnInstallWithoutAnExactBuild()
    {
        var mac = new FakeMac();

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => Install(mac, new XcodeInstallOptions(), null));

        Assert.Contains("exact Xcode build", error.Message);
        Assert.Empty(mac.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_applications));
    }

    [Fact]
    public async Task ACancelAfterTheAdministratorStepStartsDoesNotReportAnInstalledXcodeAsCanceled()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var cancellation = new CancellationTokenSource();
        var mac = new FakeMac
        {
            OnElevation = token =>
            {
                Assert.False(token.CanBeCanceled);
                cancellation.Cancel();
            },
        };

        string destination = (await Install(mac, new XcodeInstallOptions(), cancellationToken: cancellation.Token)).Path;

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public async Task NeverReplacesAnInstalledXcode()
    {
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(Path.Combine(_applications, "Xcode-26.4.app"));
        var mac = new FakeMac();

        await Assert.ThrowsAsync<XcodesException>(() => Install(mac, new XcodeInstallOptions()));

        Assert.DoesNotContain(mac.Calls, call => call.StartsWith("/bin/sh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallsAnotherBuildBesideTheExistingVersion()
    {
        if (OperatingSystem.IsWindows())
            return;
        string previous = FakeXcodeBundle.Create(_applications, "Xcode-26.4.app", "26.4", "17E191");
        string marker = Path.Combine(previous, "marker");
        await File.WriteAllTextAsync(marker, "keep this installation");
        var mac = new FakeMac();

        XcodeInstallationResult installed = await Install(mac, new XcodeInstallOptions());

        Assert.Equal(Path.Combine(_applications, "Xcode-26.4-17E192.app"), installed.Path);
        Assert.Equal("keep this installation", await File.ReadAllTextAsync(marker));
        Assert.True(Directory.Exists(installed.Path));
        Assert.Equal("17E192", installed.Build);
    }

    [Fact]
    public async Task RefusesToReplaceTheSameInstalledBuild()
    {
        if (OperatingSystem.IsWindows())
            return;
        string previous = FakeXcodeBundle.Create(_applications, "Xcode-26.4.app", "26.4", "17E192");
        var mac = new FakeMac();

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => Install(mac, new XcodeInstallOptions()));

        Assert.Contains(previous, error.Message);
        Assert.True(Directory.Exists(previous));
        Assert.DoesNotContain(mac.Calls, call => call.Contains("/usr/bin/xip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallsAnotherArchitectureBesideTheSameBuild()
    {
        if (OperatingSystem.IsWindows())
            return;
        string previous = FakeXcodeBundle.Create(_applications, "Xcode-26.4.app", "26.4", "17E192",
            cpus: [FakeXcodeBundle.X64]);
        var mac = new FakeMac();

        XcodeInstallationResult installed = await Install(mac, new XcodeInstallOptions(), architecture: XcodeArchitecture.AppleSilicon);

        Assert.Equal(Path.Combine(_applications, "Xcode-26.4-17E192.app"), installed.Path);
        Assert.Equal(XcodeArchitecture.AppleSilicon, installed.Architecture);
        Assert.True(Directory.Exists(previous));
    }

    [Fact]
    public async Task AddsAnArchitectureSuffixOnlyWhenTheBuildPathContainsAnotherVariant()
    {
        if (OperatingSystem.IsWindows())
            return;
        string previous = FakeXcodeBundle.Create(_applications, "Xcode-26.4.app", "26.4", "17E191");
        string universal = FakeXcodeBundle.Create(_applications, "Xcode-26.4-17E192.app", "26.4", "17E192",
            cpus: [FakeXcodeBundle.Arm64, FakeXcodeBundle.X64]);
        var mac = new FakeMac();

        XcodeInstallationResult installed = await Install(mac, new XcodeInstallOptions(), architecture: XcodeArchitecture.AppleSilicon);

        Assert.Equal(Path.Combine(_applications, "Xcode-26.4-17E192-arm64.app"), installed.Path);
        Assert.True(Directory.Exists(previous));
        Assert.True(Directory.Exists(universal));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("empty")]
    [InlineData("other-version")]
    [InlineData("other-app")]
    [InlineData("invalid-build")]
    [InlineData("symlink")]
    [InlineData("dangling-symlink")]
    [InlineData("unknown-architecture")]
    public async Task RefusesToBypassAnUnrecognizedOrUnverifiableDefaultDestination(string problem)
    {
        if (OperatingSystem.IsWindows())
            return;
        string destination = Path.Combine(_applications, "Xcode-26.4.app");
        switch (problem)
        {
            case "file":
                await File.WriteAllTextAsync(destination, "keep");
                break;
            case "empty":
                Directory.CreateDirectory(destination);
                break;
            case "other-version":
                FakeXcodeBundle.Create(_applications, "Xcode-26.4.app", "26.3", "17E191");
                break;
            case "other-app":
                FakeXcodeBundle.Create(_applications, "Xcode-26.4.app", "26.4", "17E191", bundleId: "com.example.other");
                break;
            case "invalid-build":
                FakeXcodeBundle.Create(_applications, "Xcode-26.4.app", "26.4", "not-a-build");
                break;
            case "unknown-architecture":
                FakeXcodeBundle.Create(_applications, "Xcode-26.4.app", "26.4", "17E192");
                await File.WriteAllBytesAsync(Path.Combine(destination, "Contents", "MacOS", "Xcode"), []);
                break;
            default:
                string linkTarget = problem == "symlink"
                    ? FakeXcodeBundle.Create(_root.Path, "Other.app", "26.4", "17E191")
                    : Path.Combine(_root.Path, "missing");
                Directory.CreateSymbolicLink(destination, linkTarget);
                break;
        }
        var mac = new FakeMac();

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(
            () => Install(mac, new XcodeInstallOptions(), architecture: XcodeArchitecture.AppleSilicon));

        Assert.Contains(destination, error.Message);
        Assert.Empty(mac.Calls);
        Assert.False(Path.Exists(Path.Combine(_applications, "Xcode-26.4-17E192.app")));
    }

    [Fact]
    public async Task KeepsThePreflightedDestinationWhenTheDefaultPathChangesDuringDownload()
    {
        if (OperatingSystem.IsWindows())
            return;
        string previous = FakeXcodeBundle.Create(_applications, "Xcode-26.4.app", "26.4", "17E191");
        var mac = new FakeMac();
        var installer = new XcodeArchiveInstaller(mac.Run, mac.Run);
        var target = new XcodeArchiveInstaller.Target("26.4", "17E192", XcodeArchitecture.AppleSilicon);
        string destination = await installer.FindDestinationAsync(_applications, target, CancellationToken.None);
        Directory.Move(previous, previous + ".moved");

        XcodeInstallationResult installed = await installer.InstallAsync(Archive(), target, new XcodeInstallOptions(),
            _applications, destination, (_, _) => { }, CancellationToken.None);

        Assert.Equal(Path.Combine(_applications, "Xcode-26.4-17E192.app"), installed.Path);
        Assert.False(Directory.Exists(previous));
        Assert.True(Directory.Exists(previous + ".moved"));
    }

    [Fact]
    public async Task RefusesAPathCreatedAfterPreflightBeforeUnpacking()
    {
        if (OperatingSystem.IsWindows())
            return;
        var mac = new FakeMac();
        var installer = new XcodeArchiveInstaller(mac.Run, mac.Run);
        var target = new XcodeArchiveInstaller.Target("26.4", "17E192", XcodeArchitecture.AppleSilicon);
        string destination = await installer.FindDestinationAsync(_applications, target, CancellationToken.None);
        await File.WriteAllTextAsync(destination, "keep");

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => installer.InstallAsync(
            Archive(), target, new XcodeInstallOptions(), _applications, destination, (_, _) => { }, CancellationToken.None));

        Assert.Contains(destination, error.Message);
        Assert.Equal("keep", await File.ReadAllTextAsync(destination));
        Assert.Empty(mac.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusesAFileOrDanglingSymlinkAtTheBuildQualifiedDestination(bool symlink)
    {
        if (OperatingSystem.IsWindows())
            return;
        FakeXcodeBundle.Create(_applications, "Xcode-26.4.app", "26.4", "17E191");
        string destination = Path.Combine(_applications, "Xcode-26.4-17E192.app");
        if (symlink)
            Directory.CreateSymbolicLink(destination, Path.Combine(_root.Path, "missing"));
        else
            await File.WriteAllTextAsync(destination, "keep");
        var mac = new FakeMac();

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => Install(mac, new XcodeInstallOptions()));

        Assert.Contains(destination, error.Message);
        Assert.Empty(mac.Calls);
    }

    [Fact]
    public async Task DeletesADamagedArchive()
    {
        if (OperatingSystem.IsWindows())
            return;
        var mac = new FakeMac { ExpandError = "The archive “Xcode_26.4.xip” is damaged and can’t be expanded." };

        var error = await Assert.ThrowsAsync<XcodesException>(() => Install(mac, new XcodeInstallOptions()));

        Assert.Contains("damaged", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_archives));
    }

    [Theory]
    [InlineData("26.4", "Xcode-26.4.app")]
    [InlineData("26.5 Beta 2", "Xcode-26.5-Beta-2.app")]
    [InlineData("26.5 Release Candidate", "Xcode-26.5-Release-Candidate.app")]
    public void NamesTheAppAfterItsVersion(string version, string name)
        => Assert.Equal(name, XcodeArchiveInstaller.DestinationName(version));

    [Fact]
    public void AcceptsBothOfApplesXcodeSignatures()
    {
        Assert.True(XcodeArchiveInstaller.IsAppleXcodeSignature(AppleSignature));
        Assert.True(XcodeArchiveInstaller.IsAppleXcodeSignature("""
            Identifier=com.apple.dt.Xcode
            Authority=Apple Mac OS Application Signing
            Authority=Apple Worldwide Developer Relations Certification Authority
            Authority=Apple Root CA
            TeamIdentifier=59GAB85EFG
            """));
        Assert.False(XcodeArchiveInstaller.IsAppleXcodeSignature(AppleSignature.Replace("com.apple.dt.Xcode", "com.example.Xcode")));
        Assert.False(XcodeArchiveInstaller.IsAppleXcodeSignature(AppleSignature.Replace("Authority=Apple Root CA", "Authority=Evil Root CA")));
    }

    [Fact]
    public async Task TheRealXcodeOnThisMacPassesVerification()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("UNIGETUI_XCODE_LIVE_TEST") != "1"
            || !Directory.Exists("/Applications/Xcode.app"))
            return;
        var installer = new XcodeArchiveInstaller(XcodeTools.Quick, XcodeTools.Quick);
        XcodeBundleInfo bundle = (await XcodeBundle.ReadAsync("/Applications/Xcode.app", XcodeTools.Quick, CancellationToken.None))!;

        await installer.VerifyAsync("/Applications/Xcode.app", new(bundle.ShortVersion, bundle.Build), CancellationToken.None);
    }
}

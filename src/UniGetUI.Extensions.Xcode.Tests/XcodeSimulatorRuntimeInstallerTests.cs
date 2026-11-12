namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class XcodeSimulatorRuntimeInstallerTests
{
    private static readonly XcodeSimulatorRuntime Runtime = new(
        "iOS 26.4 Simulator Runtime", "iOS", "26.4", "23E100", XcodeArchitecture.AppleSilicon,
        false, XcodeSimulatorRuntimeOrigin.AppleCatalog, 8_000_000_000,
        Distribution: XcodeSimulatorRuntimeDistribution.XcodeManaged);

    private const string Ready = """
        {"runtime":{"platformIdentifier":"com.apple.platform.iphonesimulator","version":"26.4",
        "build":"23E100","kind":"Disk Image","state":"Ready","supportedArchitectures":["arm64"]}}
        """;

    [Fact]
    public void SupportsOnlyIdentifiableModernCatalogAssets()
    {
        Assert.True(XcodeSimulatorRuntimeInstaller.Supports(Runtime));
        Assert.True(XcodeSimulatorRuntimeInstaller.Supports(Runtime with { Architecture = XcodeArchitecture.Universal }));
        Assert.False(XcodeSimulatorRuntimeInstaller.Supports(Runtime with { Origin = XcodeSimulatorRuntimeOrigin.Installed }));
        Assert.False(XcodeSimulatorRuntimeInstaller.Supports(Runtime with { Distribution = XcodeSimulatorRuntimeDistribution.Unknown }));
        Assert.False(XcodeSimulatorRuntimeInstaller.Supports(Runtime with { Architecture = XcodeArchitecture.Intel }));
        Assert.False(XcodeSimulatorRuntimeInstaller.Supports(Runtime with { Architecture = XcodeArchitecture.Unspecified }));
        Assert.False(XcodeSimulatorRuntimeInstaller.Supports(Runtime with { Platform = "macOS" }));
        Assert.False(XcodeSimulatorRuntimeInstaller.Supports(Runtime with { Version = "unknown" }));
        Assert.False(XcodeSimulatorRuntimeInstaller.Supports(Runtime with { Build = "../23E100" }));
        Assert.False(XcodeSimulatorRuntimeInstaller.Supports(Runtime with { Build = null }));
    }

    [Fact]
    public async Task PreparationSnapshotsTheSelectedToolsWithoutXcodesCredentials()
    {
        var fake = new FakeSystem();
        XcodeSimulatorRuntimeInstallPlan plan = await fake.Create().PrepareAsync(Runtime, CancellationToken.None);

        Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), plan.DeveloperDirectory);
        Assert.Equal(26, plan.XcodeVersion.Major);
        Assert.Equal(3, fake.Calls.Count);
        Assert.Equal("/usr/bin/xcode-select", fake.Calls[0].Executable);
        Assert.Equal(["-p"], fake.Calls[0].Arguments);
        Assert.Equal("/usr/bin/xcodebuild", fake.Calls[1].Executable);
        Assert.Equal(["-version"], fake.Calls[1].Arguments);
        Assert.Equal(["-help"], fake.Calls[2].Arguments);
        Assert.False(fake.Calls[0].Environment.ContainsKey("DEVELOPER_DIR"));
        Assert.Equal(plan.DeveloperDirectory, fake.Calls[1].Environment["DEVELOPER_DIR"]);
        Assert.All(fake.Calls, call =>
        {
            Assert.Null(call.Environment["XCODES_USERNAME"]);
            Assert.Null(call.Environment["XCODES_PASSWORD"]);
            Assert.False(call.Stream);
        });
    }

    [Theory]
    [InlineData("Xcode 16.0", XcodeArchitecture.Universal, "16.1")]
    [InlineData("Xcode 25.4", XcodeArchitecture.AppleSilicon, "26")]
    [InlineData("Command Line Tools", XcodeArchitecture.Universal, "did not report")]
    public async Task PreparationRejectsUnsupportedDeveloperTools(string version, XcodeArchitecture architecture, string message)
    {
        var fake = new FakeSystem { Version = version };
        XcodesException error = await Assert.ThrowsAsync<XcodesException>(
            () => fake.Create().PrepareAsync(Runtime with { Architecture = architecture }, CancellationToken.None));

        Assert.Contains(message, error.Message);
        Assert.DoesNotContain(fake.Calls, call => call.Stream);
    }

    [Fact]
    public async Task PreparationRejectsMissingDeveloperDirectory()
    {
        var fake = new FakeSystem { Directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(
            () => fake.Create().PrepareAsync(Runtime, CancellationToken.None));

        Assert.Contains("no usable directory", error.Message);
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task AChangedXcodeOrLostArchitectureSupportStopsBeforeInstallation()
    {
        var fake = new FakeSystem();
        XcodeSimulatorRuntimeInstaller installer = fake.Create();
        XcodeSimulatorRuntimeInstallPlan plan = await installer.PrepareAsync(Runtime, CancellationToken.None);
        fake.Version = "Xcode 27.0";

        XcodesException changed = await Assert.ThrowsAsync<XcodesException>(
            () => installer.InstallAsync(plan, null, CancellationToken.None));
        Assert.Contains("changed after confirmation", changed.Message);

        fake.Version = "Xcode 26.5";
        fake.Help = "-downloadPlatform";
        XcodesException unsupported = await Assert.ThrowsAsync<XcodesException>(
            () => installer.InstallAsync(plan, null, CancellationToken.None));
        Assert.Contains("cannot explicitly choose the runtime architecture", unsupported.Message);
        Assert.DoesNotContain(fake.Calls, call => call.Executable == "/usr/bin/xcrun" || call.Stream);
    }

    [Fact]
    public async Task InvalidInstallPlansCannotStartSystemTools()
    {
        var fake = new FakeSystem();
        XcodeVersion.TryParse("26.5", out XcodeVersion version);
        var plan = new XcodeSimulatorRuntimeInstallPlan(
            Runtime with { Build = "../23E100" }, Path.GetTempPath(), version);

        await Assert.ThrowsAsync<XcodesException>(
            () => fake.Create().InstallAsync(plan, null, CancellationToken.None));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task InstallTargetsAndVerifiesTheExactBuildAndArchitecture()
    {
        var fake = new FakeSystem();
        fake.LocalResults.Enqueue("{}");
        fake.LocalResults.Enqueue(Ready.Replace("23E100", "23E101", StringComparison.Ordinal));
        fake.LocalResults.Enqueue(Ready);
        List<XcodesOutputLine> output = [];
        XcodeSimulatorRuntimeInstaller installer = fake.Create();
        XcodeSimulatorRuntimeInstallPlan plan = await installer.PrepareAsync(Runtime, CancellationToken.None);

        XcodeSimulatorRuntimeInstallOutcome result = await installer.InstallAsync(plan, output.Add, CancellationToken.None);

        Assert.Equal(XcodeSimulatorRuntimeInstallOutcome.Installed, result);
        FakeSystem.Call download = Assert.Single(fake.Calls, call => call.Stream);
        Assert.Equal("/usr/bin/xcodebuild", download.Executable);
        Assert.Equal(["-downloadPlatform", "iOS", "-buildVersion", "23E100", "-architectureVariant", "arm64"],
            download.Arguments);
        Assert.Equal(plan.DeveloperDirectory, download.Environment["DEVELOPER_DIR"]);
        Assert.Null(download.Environment["XCODES_USERNAME"]);
        Assert.Null(download.Environment["XCODES_PASSWORD"]);
        Assert.Contains(output, line => line.Text == "Downloading: 42.5%");
        Assert.Equal(3, fake.Calls.Count(call => call.Executable == "/usr/bin/xcrun"));
        Assert.All(fake.Calls.Where(call => call.Executable == "/usr/bin/xcrun"), call =>
        {
            Assert.Equal(["simctl", "runtime", "list", "-j"], call.Arguments);
            Assert.Equal(plan.DeveloperDirectory, call.Environment["DEVELOPER_DIR"]);
        });
    }

    [Theory]
    [InlineData("Xcode 16.1")]
    [InlineData("Xcode 26.0")]
    public async Task UniversalArchitectureIsRequestedExplicitly(string version)
    {
        var fake = new FakeSystem { Version = version };
        fake.LocalResults.Enqueue("{}");
        fake.LocalResults.Enqueue(Ready.Replace("[\"arm64\"]", "[\"arm64\",\"x86_64\"]", StringComparison.Ordinal));
        XcodeSimulatorRuntimeInstaller installer = fake.Create();
        XcodeSimulatorRuntimeInstallPlan plan = await installer.PrepareAsync(
            Runtime with { Architecture = XcodeArchitecture.Universal }, CancellationToken.None);

        Assert.Equal(XcodeSimulatorRuntimeInstallOutcome.Installed,
            await installer.InstallAsync(plan, null, CancellationToken.None));

        string[] arguments = Assert.Single(fake.Calls, call => call.Stream).Arguments;
        Assert.Equal(["-downloadPlatform", "iOS", "-buildVersion", "23E100", "-architectureVariant", "universal"], arguments);
    }

    [Fact]
    public async Task RefusesAnXcodeThatCannotExplicitlySelectTheArchitecture()
    {
        var fake = new FakeSystem { Version = "Xcode 16.1", Help = "-downloadPlatform" };
        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => fake.Create().PrepareAsync(
            Runtime with { Architecture = XcodeArchitecture.Universal }, CancellationToken.None));

        Assert.Contains("cannot explicitly choose the runtime architecture", error.Message);
        Assert.DoesNotContain(fake.Calls, call => call.Stream);
    }

    [Theory]
    [InlineData("xcodebuild -downloadPlatform <platform> -buildVersion <build>\nxcodebuild -downloadAllPlatforms -architectureVariant <universal|arm64>")]
    [InlineData("xcodebuild -downloadPlatform <platform> -architectureVariant <universal|arm64>")]
    [InlineData("xcodebuild -downloadPlatform <platform> -buildVersion <build> -architectureVariant <universal>")]
    public async Task RequiresTheSelectedVariantAndBuildOnTheDownloadPlatformCommand(string help)
    {
        var fake = new FakeSystem { Help = help };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(
            () => fake.Create().PrepareAsync(Runtime, CancellationToken.None));

        Assert.Contains("cannot explicitly choose the runtime architecture", error.Message);
        Assert.DoesNotContain(fake.Calls, call => call.Stream);
    }

    [Fact]
    public async Task AnAlreadyReadyExactRuntimeDoesNotStartADownload()
    {
        var fake = new FakeSystem();
        fake.LocalResults.Enqueue(Ready);
        XcodeSimulatorRuntimeInstaller installer = fake.Create();
        XcodeSimulatorRuntimeInstallPlan plan = await installer.PrepareAsync(Runtime, CancellationToken.None);

        Assert.Equal(XcodeSimulatorRuntimeInstallOutcome.AlreadyAvailable,
            await installer.InstallAsync(plan, null, CancellationToken.None));
        Assert.DoesNotContain(fake.Calls, call => call.Stream);
    }

    [Theory]
    [InlineData("23E101", "26.4", "Ready", "arm64")]
    [InlineData("23E100", "26.3", "Ready", "arm64")]
    [InlineData("23E100", "26.4", "Unavailable", "arm64")]
    [InlineData("23E100", "26.4", "Ready", "x86_64")]
    public async Task DoesNotClaimSuccessWithoutTheRequestedReadyIdentity(
        string build, string version, string state, string architecture)
    {
        var fake = new FakeSystem();
        fake.LocalResults.Enqueue("{}");
        fake.LocalFallback = Ready.Replace("23E100", build, StringComparison.Ordinal)
            .Replace("26.4", version, StringComparison.Ordinal)
            .Replace("Ready", state, StringComparison.Ordinal)
            .Replace("arm64", architecture, StringComparison.Ordinal);
        XcodeSimulatorRuntimeInstaller installer = fake.Create();
        XcodeSimulatorRuntimeInstallPlan plan = await installer.PrepareAsync(Runtime, CancellationToken.None);

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(
            () => installer.InstallAsync(plan, null, CancellationToken.None));

        Assert.Contains("could not be confirmed as ready", error.Message);
        Assert.Equal(6, fake.Calls.Count(call => call.Executable == "/usr/bin/xcrun"));
    }

    [Fact]
    public async Task AFailedXcodebuildIsReportedWithoutClaimingInstallation()
    {
        var fake = new FakeSystem { InstallResult = new XcodesResult(1, "", "Download failed") };
        fake.LocalResults.Enqueue("{}");
        XcodeSimulatorRuntimeInstaller installer = fake.Create();
        XcodeSimulatorRuntimeInstallPlan plan = await installer.PrepareAsync(Runtime, CancellationToken.None);

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(
            () => installer.InstallAsync(plan, null, CancellationToken.None));

        Assert.Contains("Download failed", error.Message);
        Assert.Single(fake.Calls, call => call.Executable == "/usr/bin/xcrun");
    }

    [Fact]
    public async Task CancellationIsNotReportedAsSuccess()
    {
        var fake = new FakeSystem();
        fake.LocalResults.Enqueue("{}");
        fake.InstallResponse = async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new XcodesResult(0, "", "");
        };
        XcodeSimulatorRuntimeInstaller installer = fake.Create();
        XcodeSimulatorRuntimeInstallPlan plan = await installer.PrepareAsync(Runtime, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        Task<XcodeSimulatorRuntimeInstallOutcome> operation = installer.InstallAsync(plan, null, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Single(fake.Calls, call => call.Executable == "/usr/bin/xcrun");
    }

    [Theory]
    [InlineData("Downloading iOS: 42.5% (2.1 GB of 5.0 GB)", 42.5)]
    [InlineData("Downloading: 42,5 %", 42.5)]
    [InlineData("100%", 100)]
    public void ParsesRuntimeDownloadProgress(string text, double expected)
    {
        Assert.True(XcodesOutputParser.TryParseRuntimeProgress(text, out double actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("Installing runtime")]
    [InlineData("101%")]
    [InlineData("1000%")]
    [InlineData("-1%")]
    [InlineData("-0.5%")]
    public void RejectsInvalidRuntimeProgress(string text)
        => Assert.False(XcodesOutputParser.TryParseRuntimeProgress(text, out _));

    private sealed class FakeSystem
    {
        internal sealed record Call(
            string Executable, string[] Arguments, Dictionary<string, string?> Environment, bool Stream);

        public string Directory { get; set; } = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        public string Version { get; set; } = "Xcode 26.5";
        public string Help { get; set; } =
            "xcodebuild -downloadPlatform <platform> -buildVersion <build> -architectureVariant <universal|arm64>";
        public Queue<string> LocalResults { get; } = new();
        public string LocalFallback { get; set; } = "{}";
        public XcodesResult InstallResult { get; set; } = new(0, "", "");
        public Func<CancellationToken, Task<XcodesResult>>? InstallResponse { get; set; }
        public List<Call> Calls { get; } = [];

        public XcodeSimulatorRuntimeInstaller Create()
            => new(RunAsync, () => true, (_, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        private Task<XcodesResult> RunAsync(
            string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment,
            bool stream, Action<XcodesOutputLine>? output, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new Call(executable, [.. arguments], new Dictionary<string, string?>(environment), stream));
            if (executable == "/usr/bin/xcode-select")
                return Task.FromResult(new XcodesResult(0, Directory, ""));
            if (executable == "/usr/bin/xcrun")
                return Task.FromResult(new XcodesResult(0,
                    LocalResults.TryDequeue(out string? result) ? result : LocalFallback, ""));
            if (!stream)
                return Task.FromResult(arguments[0] == "-help"
                    ? new XcodesResult(0, "", Help)
                    : new XcodesResult(0, Version + "\nBuild version 17F42", ""));
            output?.Invoke(new XcodesOutputLine("Downloading: 42.5%"));
            return InstallResponse?.Invoke(cancellationToken) ?? Task.FromResult(InstallResult);
        }
    }
}

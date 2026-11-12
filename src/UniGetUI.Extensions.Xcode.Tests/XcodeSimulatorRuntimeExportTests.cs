using System.Diagnostics;

namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class XcodeSimulatorRuntimeExportTests
{
    private static readonly XcodeSimulatorRuntime Runtime = new(
        "iOS 26.4 Simulator Runtime", "iOS", "26.4", "23E100", XcodeArchitecture.AppleSilicon,
        false, XcodeSimulatorRuntimeOrigin.AppleCatalog, 8_000_000_000,
        Distribution: XcodeSimulatorRuntimeDistribution.XcodeManaged);

    [Theory]
    [InlineData(XcodeArchitecture.AppleSilicon, "arm64")]
    [InlineData(XcodeArchitecture.Universal, "universal")]
    public async Task ExportsTheRequestedBuildAndVariantWithoutReplacingExistingFiles(
        XcodeArchitecture architecture, string variant)
    {
        using var fixture = new Fixture();
        byte[] image = [1, 2, 3, 4, 5];
        string existing = Path.Combine(fixture.Destination, "existing.dmg");
        await File.WriteAllTextAsync(existing, "keep");
        fixture.WriteImage = directory => File.WriteAllBytes(Path.Combine(directory, "Apple runtime.DMG"), image);
        List<XcodesOutputLine> output = [];
        XcodeSimulatorRuntimeInstallPlan plan = await fixture.PrepareAsync(Runtime with { Architecture = architecture });

        XcodeSimulatorRuntimeExportResult result = await fixture.ExportAsync(plan, output: output.Add);

        XcodeSimulatorRuntimeExportPaths paths = Assert.IsType<XcodeSimulatorRuntimeExportPaths>(fixture.Paths);
        Assert.Equal(paths.ImagePath, result.ImagePath);
        Assert.Equal(fixture.Destination, Path.GetDirectoryName(result.ImagePath));
        Assert.Contains($"iOS-23E100-{variant}-", Path.GetFileName(result.ImagePath));
        Assert.EndsWith(".dmg", result.ImagePath);
        Assert.Equal(image, await File.ReadAllBytesAsync(result.ImagePath));
        Assert.Equal("keep", await File.ReadAllTextAsync(existing));
        Assert.False(File.Exists(paths.PartialPath));
        Assert.False(Directory.Exists(paths.TemporaryDirectory));
        Assert.Null(result.RetainedTemporaryDirectory);
        Assert.Contains(output, line => line.Text == "Copying runtime image: 0%");
        Assert.Contains(output, line => line.Text == "Copying runtime image: 100%");
        Assert.Contains(output, line => line.Text == "Verifying copied runtime image...");

        Fixture.Call command = Assert.Single(fixture.Calls, call => call.Stream);
        Assert.Equal("/usr/bin/xcodebuild", command.Executable);
        Assert.Equal(
            ["-downloadPlatform", "iOS", "-buildVersion", "23E100", "-architectureVariant", variant,
                "-exportPath", paths.TemporaryDirectory],
            command.Arguments);
        Assert.Equal(plan.DeveloperDirectory, command.Environment["DEVELOPER_DIR"]);
        Assert.Null(command.Environment["XCODES_USERNAME"]);
        Assert.Null(command.Environment["XCODES_PASSWORD"]);
        Assert.DoesNotContain(fixture.Calls, call => call.Executable == "/usr/bin/xcrun");
    }

    [Theory]
    [InlineData("-downloadPlatform <platform> -buildVersion <build> -architectureVariant <universal|arm64>")]
    [InlineData("-downloadPlatform <platform> -buildVersion <build> -architectureVariant <universal|arm64>\n"
        + "-downloadAllPlatforms -exportPath <path>")]
    public async Task RequiresExportOnTheExactBuildAndArchitectureCommand(string help)
    {
        using var fixture = new Fixture();
        XcodeSimulatorRuntimeInstallPlan plan = await fixture.PrepareAsync();
        fixture.Help = help;

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => fixture.ExportAsync(plan));

        Assert.Contains("cannot export", error.Message);
        Assert.Null(fixture.Paths);
        Assert.DoesNotContain(fixture.Calls, call => call.Stream);
    }

    [Fact]
    public async Task PreparationRejectsXcodeWithoutExportBeforeTheUserChoosesADestination()
    {
        using var fixture = new Fixture
        {
            Help = "xcodebuild -downloadPlatform <platform> -buildVersion <build> -architectureVariant <universal|arm64>",
        };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => fixture.PrepareAsync());

        Assert.Contains("cannot export", error.Message);
        Assert.Null(fixture.Paths);
        Assert.DoesNotContain(fixture.Calls, call => call.Stream);
    }

    [Fact]
    public async Task RechecksTheSelectedXcodeAfterConfirmation()
    {
        using var fixture = new Fixture();
        XcodeSimulatorRuntimeInstallPlan plan = await fixture.PrepareAsync();
        fixture.Version = "Xcode 27.0";

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => fixture.ExportAsync(plan));

        Assert.Contains("changed after confirmation", error.Message);
        Assert.Null(fixture.Paths);
        Assert.DoesNotContain(fixture.Calls, call => call.Stream);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("/missing-unigetui-export-folder")]
    [InlineData("/invalid\nfolder")]
    public async Task RejectsInvalidDestinationsBeforeStartingSystemTools(string directory)
    {
        using var fixture = new Fixture();
        XcodeSimulatorRuntimeInstallPlan plan = await fixture.PrepareAsync();
        fixture.Calls.Clear();

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(
            () => fixture.ExportAsync(plan, directory: directory));

        Assert.Contains("existing absolute folder", error.Message);
        Assert.Empty(fixture.Calls);
        Assert.Null(fixture.Paths);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("multiple")]
    [InlineData("empty")]
    [InlineData("symlink")]
    public async Task RejectsAmbiguousEmptyOrLinkedImagesAndRetainsTheStagingDirectory(string shape)
    {
        if (shape == "symlink" && OperatingSystem.IsWindows())
            return;

        using var fixture = new Fixture();
        string other = Path.Combine(fixture.Destination, "original.dmg");
        await File.WriteAllTextAsync(other, "original");
        fixture.WriteImage = directory =>
        {
            string image = Path.Combine(directory, "runtime.dmg");
            switch (shape)
            {
                case "multiple":
                    File.WriteAllText(image, "one");
                    File.WriteAllText(Path.Combine(directory, "another.dmg"), "two");
                    break;
                case "empty":
                    File.WriteAllBytes(image, []);
                    break;
                case "symlink":
                    File.CreateSymbolicLink(image, other);
                    break;
            }
        };
        XcodeSimulatorRuntimeInstallPlan plan = await fixture.PrepareAsync();

        await Assert.ThrowsAsync<XcodesException>(() => fixture.ExportAsync(plan));

        XcodeSimulatorRuntimeExportPaths paths = Assert.IsType<XcodeSimulatorRuntimeExportPaths>(fixture.Paths);
        Assert.True(Directory.Exists(paths.TemporaryDirectory));
        Assert.False(File.Exists(paths.ImagePath));
        Assert.True(File.Exists(paths.PartialPath));
        Assert.Equal(0, new FileInfo(paths.PartialPath).Length);
        Assert.Equal("original", await File.ReadAllTextAsync(other));
    }

    [Fact]
    public async Task RejectsANamedPipeWithoutBlocking()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        using var fixture = new Fixture();
        fixture.WriteImage = directory => CreatePipe(Path.Combine(directory, "runtime.dmg"));
        XcodeSimulatorRuntimeInstallPlan plan = await fixture.PrepareAsync();

        await Assert.ThrowsAsync<XcodesException>(
            () => fixture.ExportAsync(plan).WaitAsync(TimeSpan.FromSeconds(5)));

        XcodeSimulatorRuntimeExportPaths paths = Assert.IsType<XcodeSimulatorRuntimeExportPaths>(fixture.Paths);
        Assert.False(File.Exists(paths.ImagePath));
        Assert.True(Directory.Exists(paths.TemporaryDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DoesNotOverwriteAnExistingFinalOrPartialPath(bool partial)
    {
        using var fixture = new Fixture();
        fixture.WriteImage = directory => File.WriteAllText(Path.Combine(directory, "runtime.dmg"), "exported");
        XcodeSimulatorRuntimeInstallPlan plan = await fixture.PrepareAsync();

        await Assert.ThrowsAsync<IOException>(() => fixture.ExportAsync(plan, started: paths =>
            File.WriteAllText(partial ? paths.PartialPath : paths.ImagePath, "do not replace")));

        XcodeSimulatorRuntimeExportPaths paths = Assert.IsType<XcodeSimulatorRuntimeExportPaths>(fixture.Paths);
        Assert.Equal("do not replace", await File.ReadAllTextAsync(partial ? paths.PartialPath : paths.ImagePath));
        Assert.True(Directory.Exists(paths.TemporaryDirectory));
        if (partial)
        {
            Assert.False(File.Exists(paths.ImagePath));
            Assert.DoesNotContain(fixture.Calls, call => call.Stream);
        }
        else
            Assert.True(File.Exists(paths.PartialPath));
    }

    [Fact]
    public async Task DoesNotStartXcodeWhenDestinationIsNoLongerWritable()
    {
        using var fixture = new Fixture();
        XcodeSimulatorRuntimeInstallPlan plan = await fixture.PrepareAsync();

        await Assert.ThrowsAnyAsync<IOException>(
            () => fixture.ExportAsync(plan, started: _ => Directory.Delete(fixture.Destination)));

        Assert.DoesNotContain(fixture.Calls, call => call.Stream);
        Assert.True(Directory.Exists(Assert.IsType<XcodeSimulatorRuntimeExportPaths>(fixture.Paths).TemporaryDirectory));
    }

    [Fact]
    public async Task FailedXcodeExportKeepsItsOriginalErrorAndTemporaryData()
    {
        using var fixture = new Fixture { Result = new XcodesResult(1, "", "Export failed") };
        fixture.WriteImage = directory => File.WriteAllText(Path.Combine(directory, "partial.dmg"), "partial");
        XcodeSimulatorRuntimeInstallPlan plan = await fixture.PrepareAsync();

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => fixture.ExportAsync(plan));

        Assert.Contains("Export failed", error.Message);
        XcodeSimulatorRuntimeExportPaths paths = Assert.IsType<XcodeSimulatorRuntimeExportPaths>(fixture.Paths);
        Assert.True(Directory.Exists(paths.TemporaryDirectory));
        Assert.False(File.Exists(paths.ImagePath));
    }

    [Fact]
    public async Task CancellingXcodeKeepsItsStagingDirectoryWithoutClaimingSuccess()
    {
        using var fixture = new Fixture();
        fixture.WriteImage = directory => File.WriteAllText(Path.Combine(directory, "partial.dmg"), "partial");
        fixture.Download = async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new XcodesResult(0, "", "");
        };
        XcodeSimulatorRuntimeInstallPlan plan = await fixture.PrepareAsync();
        using var cancellation = new CancellationTokenSource();

        Task<XcodeSimulatorRuntimeExportResult> operation = fixture.ExportAsync(plan, cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        XcodeSimulatorRuntimeExportPaths paths = Assert.IsType<XcodeSimulatorRuntimeExportPaths>(fixture.Paths);
        Assert.True(Directory.Exists(paths.TemporaryDirectory));
        Assert.False(File.Exists(paths.ImagePath));
    }

    [Fact]
    public async Task CancellingTheCopyLeavesOnlyAnExplicitlyIncompleteFile()
    {
        using var fixture = new Fixture();
        fixture.WriteImage = directory => File.WriteAllBytes(Path.Combine(directory, "runtime.dmg"), [1, 2, 3]);
        XcodeSimulatorRuntimeInstallPlan plan = await fixture.PrepareAsync();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ExportAsync(
            plan, output: line =>
            {
                if (line.Text == "Copying runtime image: 0%")
                    cancellation.Cancel();
            }, cancellationToken: cancellation.Token));

        XcodeSimulatorRuntimeExportPaths paths = Assert.IsType<XcodeSimulatorRuntimeExportPaths>(fixture.Paths);
        Assert.True(Directory.Exists(paths.TemporaryDirectory));
        Assert.True(File.Exists(paths.PartialPath));
        Assert.False(File.Exists(paths.ImagePath));
    }

    [Fact]
    public async Task AChangedCopyIsNotPublished()
    {
        using var fixture = new Fixture();
        fixture.WriteImage = directory => File.WriteAllText(Path.Combine(directory, "runtime.dmg"), "original");
        XcodeSimulatorRuntimeInstallPlan plan = await fixture.PrepareAsync();

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => fixture.ExportAsync(plan, output: line =>
        {
            if (line.Text == "Verifying copied runtime image...")
                File.WriteAllText(fixture.Paths!.PartialPath, "changed");
        }));

        Assert.Contains("could not be verified", error.Message);
        XcodeSimulatorRuntimeExportPaths paths = Assert.IsType<XcodeSimulatorRuntimeExportPaths>(fixture.Paths);
        Assert.True(File.Exists(paths.PartialPath));
        Assert.False(File.Exists(paths.ImagePath));
        Assert.True(Directory.Exists(paths.TemporaryDirectory));
    }

    [Fact]
    public async Task AReplacedCopySymlinkIsNotFollowedOrPublished()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        using var fixture = new Fixture();
        string other = Path.Combine(fixture.Destination, "original.dmg");
        await File.WriteAllTextAsync(other, "same content");
        fixture.WriteImage = directory => File.WriteAllText(Path.Combine(directory, "runtime.dmg"), "same content");
        XcodeSimulatorRuntimeInstallPlan plan = await fixture.PrepareAsync();

        await Assert.ThrowsAsync<IOException>(() => fixture.ExportAsync(plan, output: line =>
        {
            if (line.Text == "Verifying copied runtime image...")
            {
                File.Delete(fixture.Paths!.PartialPath);
                File.CreateSymbolicLink(fixture.Paths.PartialPath, other);
            }
        }));

        XcodeSimulatorRuntimeExportPaths paths = Assert.IsType<XcodeSimulatorRuntimeExportPaths>(fixture.Paths);
        Assert.False(File.Exists(paths.ImagePath));
        Assert.Equal("same content", await File.ReadAllTextAsync(other));
    }

    [Fact]
    public async Task AReplacedCopyPipeIsRejectedWithoutBlocking()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        using var fixture = new Fixture();
        fixture.WriteImage = directory => File.WriteAllText(Path.Combine(directory, "runtime.dmg"), "original");
        XcodeSimulatorRuntimeInstallPlan plan = await fixture.PrepareAsync();

        await Assert.ThrowsAsync<XcodesException>(() => fixture.ExportAsync(plan, output: line =>
        {
            if (line.Text == "Verifying copied runtime image...")
            {
                File.Delete(fixture.Paths!.PartialPath);
                CreatePipe(fixture.Paths.PartialPath);
            }
        }).WaitAsync(TimeSpan.FromSeconds(5)));

        XcodeSimulatorRuntimeExportPaths paths = Assert.IsType<XcodeSimulatorRuntimeExportPaths>(fixture.Paths);
        Assert.False(File.Exists(paths.ImagePath));
        Assert.True(Directory.Exists(paths.TemporaryDirectory));
    }

    [Fact]
    public async Task ReportsTemporaryFilesThatCouldNotBeRemovedAfterSuccess()
    {
        using var fixture = new Fixture();
        fixture.WriteImage = directory =>
        {
            File.WriteAllText(Path.Combine(directory, "runtime.dmg"), "image");
            File.WriteAllText(Path.Combine(directory, "other-file"), "keep");
        };
        XcodeSimulatorRuntimeInstallPlan plan = await fixture.PrepareAsync();

        XcodeSimulatorRuntimeExportResult result = await fixture.ExportAsync(plan);

        Assert.Equal(fixture.Paths!.TemporaryDirectory, result.RetainedTemporaryDirectory);
        Assert.True(File.Exists(result.ImagePath));
        Assert.True(File.Exists(Path.Combine(result.RetainedTemporaryDirectory!, "other-file")));
    }

    private static void CreatePipe(string path)
    {
        var start = new ProcessStartInfo("/usr/bin/mkfifo") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        using Process process = Process.Start(start)!;
        Assert.True(process.WaitForExit(5_000));
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class Fixture : IDisposable
    {
        public sealed record Call(
            string Executable, string[] Arguments, Dictionary<string, string?> Environment, bool Stream);

        private readonly XcodeSimulatorRuntimeInstaller _installer;
        public string Destination { get; } = Directory.CreateTempSubdirectory("xcodes-export-tests-").FullName;
        public string Version { get; set; } = "Xcode 26.6";
        public string Help { get; set; } =
            "xcodebuild -downloadPlatform <platform> -exportPath <path> -buildVersion <build> -architectureVariant <universal|arm64>";
        public Action<string>? WriteImage { get; set; }
        public Func<CancellationToken, Task<XcodesResult>>? Download { get; set; }
        public XcodesResult Result { get; set; } = new(0, "", "");
        public XcodeSimulatorRuntimeExportPaths? Paths { get; private set; }
        public List<Call> Calls { get; } = [];

        public Fixture()
        {
            _installer = new XcodeSimulatorRuntimeInstaller(RunAsync, () => true);
        }

        public Task<XcodeSimulatorRuntimeInstallPlan> PrepareAsync(XcodeSimulatorRuntime? runtime = null)
            => _installer.PrepareExportAsync(runtime ?? Runtime, CancellationToken.None);

        public Task<XcodeSimulatorRuntimeExportResult> ExportAsync(
            XcodeSimulatorRuntimeInstallPlan plan, string? directory = null,
            Action<XcodeSimulatorRuntimeExportPaths>? started = null, Action<XcodesOutputLine>? output = null,
            CancellationToken cancellationToken = default)
            => _installer.ExportAsync(plan, directory ?? Destination, paths =>
            {
                Paths = paths;
                if (!OperatingSystem.IsWindows())
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                        File.GetUnixFileMode(paths.TemporaryDirectory) & (UnixFileMode)0x1FF);
                started?.Invoke(paths);
            }, output, cancellationToken);

        private Task<XcodesResult> RunAsync(
            string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment,
            bool stream, Action<XcodesOutputLine>? output, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new Call(executable, [.. arguments], new Dictionary<string, string?>(environment), stream));
            if (executable == "/usr/bin/xcode-select")
                return Task.FromResult(new XcodesResult(0, Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), ""));
            if (!stream)
                return Task.FromResult(arguments[0] == "-help"
                    ? new XcodesResult(0, "", Help)
                    : new XcodesResult(0, Version + "\nBuild version 17F113", ""));
            int index = Calls[^1].Arguments.AsSpan().IndexOf("-exportPath");
            Assert.True(index >= 0);
            WriteImage?.Invoke(arguments[index + 1]);
            output?.Invoke(new XcodesOutputLine("Downloading: 42.5%"));
            return Download?.Invoke(cancellationToken) ?? Task.FromResult(Result);
        }

        public void Dispose()
        {
            if (Paths is { } paths && Directory.Exists(paths.TemporaryDirectory))
                Directory.Delete(paths.TemporaryDirectory, recursive: true);
            if (Directory.Exists(Destination))
                Directory.Delete(Destination, recursive: true);
        }
    }
}

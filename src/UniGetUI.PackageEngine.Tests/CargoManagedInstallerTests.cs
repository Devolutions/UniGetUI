using System.Buffers.Binary;
using System.Net;
using System.Text;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Managers.CargoManager;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class CargoManagedInstallerTests
{
    [Theory]
    [InlineData("x86_64-pc-windows-msvc")]
    [InlineData("aarch64-pc-windows-msvc")]
    [InlineData("x86_64-unknown-linux-gnu")]
    [InlineData("aarch64-unknown-linux-musl")]
    [InlineData("x86_64-apple-darwin")]
    [InlineData("aarch64-apple-darwin")]
    public void ExecutableValidationAcceptsCorrectHeaderAndRejectsOppositeArchitecture(string target)
    {
        byte[] bytes = CargoTestData.Executable(target);
        CargoManagedInstaller.ValidateExecutable(bytes, target);
        string other = target.StartsWith("x86_64-", StringComparison.Ordinal)
            ? target.Replace("x86_64-", "aarch64-", StringComparison.Ordinal)
            : target.Replace("aarch64-", "x86_64-", StringComparison.Ordinal);
        Assert.Contains("does not match", Assert.Throws<InvalidDataException>(() =>
            CargoManagedInstaller.ValidateExecutable(bytes, other)).Message);
        Assert.Contains("truncated", Assert.Throws<InvalidDataException>(() =>
            CargoManagedInstaller.ValidateExecutable(bytes[..63], target)).Message);
    }

    [Fact]
    public void ExecutableValidationRejectsTextBadMagicAndInvalidPeOffsets()
    {
        Assert.Throws<InvalidDataException>(() =>
            CargoManagedInstaller.ValidateExecutable(new byte[128], CargoTestData.Target));
        foreach (int offset in new[] { -1, 63, 123, int.MaxValue })
        {
            byte[] bytes = CargoTestData.Executable();
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C), offset);
            Assert.Throws<InvalidDataException>(() =>
                CargoManagedInstaller.ValidateExecutable(bytes, CargoTestData.Target));
        }
    }

    [Fact]
    public void ExtractBinariesRequiresAllExpectedRenamedExecutablesAndExplicitLayout()
    {
        byte[] first = CargoTestData.Executable();
        byte[] second = CargoTestData.Executable();
        second[^1] = 42;
        byte[] zip = CargoTestData.Zip(("release/tool.exe", first), ("release/companion.exe", second),
            ("other/tool.exe", second), ("README", [1]));
        var plan = CargoTestData.Plan(binaries: ["tool", "companion"], binaryPath: "release/{bin}{binary-ext}");
        var binaries = CargoManagedInstaller.ExtractBinaries(zip, plan, default);
        Assert.Equal(["companion.exe", "tool.exe"], binaries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(first, binaries["tool.exe"]);
        Assert.Equal(second, binaries["companion.exe"]);
        Assert.Throws<InvalidDataException>(() =>
            CargoManagedInstaller.ExtractBinaries(zip, plan with { BinaryPath = null }, default));
        Assert.Throws<InvalidDataException>(() => CargoManagedInstaller.ExtractBinaries(
            CargoTestData.Zip(("release/tool.exe", first)), plan, default));
    }

    [Fact]
    public void RawExecutableIsAllowedOnlyForOneMatchingBinary()
    {
        byte[] executable = CargoTestData.Executable("aarch64-unknown-linux-musl");
        var plan = CargoTestData.Plan(target: "aarch64-unknown-linux-musl", format: "bin");
        var files = CargoManagedInstaller.ExtractBinaries(executable, plan, default);
        Assert.Equal("tool", Assert.Single(files).Key);
        Assert.Equal(executable, files["tool"]);
        Assert.Throws<InvalidDataException>(() =>
            CargoManagedInstaller.ExtractBinaries(executable, plan with { Binaries = ["tool", "second"] }, default));
        Assert.Throws<InvalidDataException>(() =>
            CargoManagedInstaller.ExtractBinaries(executable, plan with { Target = "x86_64-unknown-linux-musl" }, default));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.exe")]
    [InlineData("../outside")]
    [InlineData("tool/path")]
    [InlineData("tool.")]
    [InlineData("")]
    public void BinaryNamesRejectTraversalAndWindowsDevices(string name) =>
        Assert.Throws<InvalidDataException>(() => CargoBinaryResolver.ValidateBinaryName(name));

    [Theory]
    [InlineData("elevated")]
    [InlineData("features")]
    [InlineData("build-target")]
    [InlineData("foreign-source")]
    public async Task ManagedIneligibleOperationsDeferWithoutHttpOrMutation(string scenario)
    {
        using var sandbox = new CargoTestSandbox();
        using var environment = sandbox.IsolateEnvironment();
        using var http = new CargoTestHttpFixture();
        var manager = new Cargo();
        var builder = new PackageBuilder().WithManager(manager).WithId("fixture-tool").WithVersion("1.2.3");
        if (scenario == "foreign-source")
            builder.WithSource(new SourceBuilder().WithManager(manager).WithName("private")
                .WithUrl("https://private.test/index").Build());
        var options = new InstallOptions { CustomInstallLocation = sandbox.PathFor("root") };
        if (scenario == "elevated")
            options.RunAsAdministrator = true;
        if (scenario == "features")
            options.CustomParameters_Install.AddRange(["--features", "fast"]);
        using var target = new CargoTestEnvironment(("CARGO_BUILD_TARGET",
            scenario == "build-target" ? "aarch64-unknown-linux-musl" : null));
        List<string> logs = [];
        List<OperationProgress> progress = [];
        Assert.Null(await CargoManagedInstaller.TryPerformAsync(manager, builder.Build(), options,
            OperationType.Install, logs.Add, progress.Add, processIsElevated: false, default));
        Assert.Contains(scenario switch
        {
            "elevated" => "Elevated",
            "foreign-source" => "Non-crates.io",
            _ => "source compilation",
        }, Assert.Single(logs));
        Assert.Empty(http.Requests);
        Assert.Empty(progress);
        Assert.False(Directory.Exists(sandbox.PathFor("root")));
    }

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(true, false, false)]
    [InlineData(false, true, null)]
    [InlineData(false, false, true)]
    public async Task ElevatedExecutionAlwaysUsesSourceCompilationWithoutHttpOrMutation(
        bool processIsElevated, bool requestedElevation, bool? packageElevation)
    {
        using var sandbox = new CargoTestSandbox();
        using var environment = sandbox.IsolateEnvironment();
        using var fixture = new CargoTestHttpFixture();
        var manager = new Cargo();
        var package = new PackageBuilder().WithManager(manager).WithId(CargoTestData.Id)
            .WithVersion(CargoTestData.Version).Build();
        package.OverridenOptions.RunAsAdministrator = packageElevation;
        var options = new InstallOptions
        {
            RunAsAdministrator = requestedElevation,
            CustomInstallLocation = sandbox.PathFor("root"),
        };
        List<string> log = [];
        List<OperationProgress> progress = [];

        Assert.Null(await CargoManagedInstaller.TryPerformAsync(manager, package, options,
            OperationType.Install, log.Add, progress.Add, processIsElevated, default));

        Assert.Equal("Elevated Cargo operations use Cargo source compilation.", Assert.Single(log));
        Assert.Empty(fixture.Requests);
        Assert.Empty(progress);
        Assert.False(Directory.Exists(sandbox.PathFor("root")));
        Assert.Empty(options.Version);
    }

    [Fact]
    public async Task ShippingEntryUsesActualProcessElevation()
    {
        using var sandbox = new CargoTestSandbox();
        using var environment = sandbox.IsolateEnvironment();
        using var fixture = new CargoTestHttpFixture();
        var manager = new Cargo();
        var source = new SourceBuilder().WithManager(manager).WithName("private")
            .WithUrl("https://private.test/index").Build();
        var package = new PackageBuilder().WithManager(manager).WithSource(source)
            .WithId(CargoTestData.Id).WithVersion(CargoTestData.Version).Build();
        var options = new InstallOptions { CustomInstallLocation = sandbox.PathFor("root") };
        var helper = Assert.IsAssignableFrom<IManagedPackageOperationHelper>(manager.OperationHelper);
        List<string> log = [];
        List<OperationProgress> progress = [];

        Assert.Null(await helper.TryPerformAsync(package, options, OperationType.Install,
            log.Add, progress.Add, default));

        Assert.Equal(CoreTools.IsAdministrator()
            ? "Elevated Cargo operations use Cargo source compilation."
            : "Non-crates.io packages use their original source through Cargo.", Assert.Single(log));
        Assert.Empty(fixture.Requests);
        Assert.Empty(progress);
        Assert.False(Directory.Exists(sandbox.PathFor("root")));
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("missing-checksum")]
    [InlineData("missing-binary")]
    [InlineData("mismatched-binary")]
    public async Task ManagedInstallEnforcesIntegrityAndKeepsExactVersionOnFallback(string scenario)
    {
        using var sandbox = new CargoTestSandbox();
        using var environment = sandbox.IsolateEnvironment();
        using var fixture = new CargoTestHttpFixture();
        var manager = new Cargo
        {
            Status = new ManagerStatus { Found = true, ExecutablePath = "cargo", ExecutableCallArgs = "" },
        };
        string target = await manager.GetHostTargetAsync(default)
            ?? throw new InvalidOperationException("Local Cargo did not report its host.");
        byte[] executable = CargoTestData.Executable(target);
        string binaryName = "tool" + (target.Contains("-windows-", StringComparison.Ordinal) ? ".exe" : "");
        byte[] binary = CargoTestData.Zip(("release/" + binaryName, executable));
        byte[] source = CargoTestData.Tar(true, ("fixture-tool-1.2.3/Cargo.toml", Encoding.UTF8.GetBytes("""
            [package]
            name = "fixture-tool"
            version = "1.2.3"
            [package.metadata.binstall]
            pkg-url = "https://publisher.test/{name}/{version}/fixture.zip"
            pkg-fmt = "zip"
            """)));
        fixture.Bytes(CargoTestData.SourceUrl, source);
        if (scenario == "missing-checksum")
        {
            fixture.Status(CargoTestData.BinaryUrl + ".sha256", HttpStatusCode.NotFound);
            fixture.Status(CargoTestData.BinaryUrl + ".sha512", HttpStatusCode.NotFound);
        }
        else
            fixture.Text(CargoTestData.BinaryUrl + ".sha256", CargoTestData.Hash(binary)[7..]);
        if (scenario == "missing-binary")
            fixture.Status(CargoTestData.BinaryUrl, HttpStatusCode.NotFound);
        else
            fixture.Bytes(CargoTestData.BinaryUrl,
                scenario == "mismatched-binary" ? [.. binary, 1] : binary);
        using var api = new TestHttpServer(_ => (200, $$"""
            {"crate":{"name":"fixture-tool","max_stable_version":"1.2.3","max_version":"1.2.3","newest_version":"1.2.3"},
             "versions":[{"num":"1.2.3","checksum":"{{CargoTestData.Hash(source)[7..]}}",
               "dl_path":"/download","bin_names":["tool"],"yanked":false}]}
            """, "application/json"));
        CratesIOClient.TEST_ApiUrlOverride = api.BaseUri.AbsoluteUri.TrimEnd('/') + "/api/v1";
        CratesIOClient.InvalidateCache();
        try
        {
            var options = new InstallOptions { CustomInstallLocation = sandbox.PathFor("root") };
            var package = new PackageBuilder().WithManager(manager).WithId(CargoTestData.Id)
                .WithVersion(CargoTestData.Version).Build();
            List<string> log = [];
            List<OperationProgress> progress = [];
            Task<OperationVeredict?> Run() =>
                CargoManagedInstaller.TryPerformAsync(manager, package, options, OperationType.Install,
                    log.Add, progress.Add, processIsElevated: false, default);
            if (scenario == "mismatched-binary")
                Assert.Contains("published checksum", (await Assert.ThrowsAsync<InvalidDataException>(Run)).Message);
            else if (scenario == "verified")
            {
                Assert.Equal(OperationVeredict.Success, await Run());
                Assert.Equal(executable, File.ReadAllBytes(sandbox.PathFor("root/bin/" + binaryName)));
                Assert.Contains(progress, p => p.Stage == OperationProgressStage.Installing);
                Assert.Contains(progress, p => p.Stage == OperationProgressStage.Downloading);
            }
            else
            {
                Assert.Null(await Run());
                Assert.Contains(log, line => line.Contains("Cargo", StringComparison.Ordinal));
            }
            Assert.Equal("1.2.3", options.Version);
            Assert.Equal(["install", CargoTestData.Id, "--version", UniGetUI.Core.Tools.CoreTools.EscapeCommandLineArgument("1.2.3"),
                "--locked", "--root", UniGetUI.Core.Tools.CoreTools.EscapeCommandLineArgument(options.CustomInstallLocation)],
                manager.OperationHelper.GetParameters(package, options, OperationType.Install));
            if (scenario != "verified")
            {
                Assert.False(File.Exists(sandbox.PathFor("root/bin/" + binaryName)));
                Assert.False(File.Exists(sandbox.PathFor("root/.crates.toml")));
                Assert.False(File.Exists(sandbox.PathFor("root/.crates2.json")));
            }
            Assert.Equal(["/api/v1/crates/fixture-tool"], api.RequestPaths);
        }
        finally
        {
            CratesIOClient.TEST_ApiUrlOverride = null;
            CratesIOClient.InvalidateCache();
        }
    }
}

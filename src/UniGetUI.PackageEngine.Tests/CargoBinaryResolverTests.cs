using System.Net;
using System.Security.Cryptography;
using System.Text;
using UniGetUI.PackageEngine.Managers.CargoManager;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class CargoBinaryResolverTests
{
    private const string ReleaseUrl = "https://api.github.com/repos/publisher/tool/releases/tags/v1.2.3";
    private const string AssetName = "fixture-tool-1.2.3-x86_64-pc-windows-msvc.zip";
    private const string AssetUrl = "https://github.com/publisher/tool/releases/download/v1.2.3/" + AssetName;

    private static CargoManifest Source(CargoTestHttpFixture fixture, string metadata = "",
        string name = CargoTestData.Id, string version = CargoTestData.Version)
    {
        byte[] source = CargoTestData.Tar(true,
            ("fixture-tool-1.2.3/Cargo.toml", Encoding.UTF8.GetBytes($"""
                [package]
                name = "{name}"
                version = "{version}"
                repository = "https://github.com/publisher/tool"
                {metadata}
                """)));
        fixture.Bytes(CargoTestData.SourceUrl, source);
        return CargoTestData.Manifest(CargoTestData.VersionInfo(checksum: CargoTestData.Hash(source)[7..]));
    }

    private static Task<CargoBinaryPlan?> Resolve(CargoHttp http, CargoManifest manifest, List<string>? log = null,
        string target = CargoTestData.Target, CancellationToken cancellationToken = default) =>
        new CargoBinaryResolver(http).ResolveAsync(CargoTestData.Id, CargoTestData.Version, target,
            manifest, message => log?.Add(message), cancellationToken);

    [Fact]
    public async Task ExplicitPublisherMetadataUsesVerifiedSourceAndSeparateBinaryChecksum()
    {
        using var fixture = new CargoTestHttpFixture();
        var manifest = Source(fixture, """
            [package.metadata.binstall]
            pkg-url = "https://publisher.test/{name}/{version}/fixture.zip"
            pkg-fmt = "zip"
            bin-dir = "release/{bin}{binary-ext}"
            """);
        byte[] binary = CargoTestData.Zip(("release/tool.exe", CargoTestData.Executable()));
        fixture.Text(CargoTestData.BinaryUrl + ".sha256", CargoTestData.Hash(binary)[7..] + " *fixture.zip");
        using var http = new CargoHttp();
        var plan = Assert.IsType<CargoBinaryPlan>(await Resolve(http, manifest));
        Assert.Equal(CargoTestData.Id, plan.Id);
        Assert.Equal(CargoTestData.Version, plan.Version);
        Assert.Equal(CargoTestData.Target, plan.Target);
        Assert.Equal(["tool"], plan.Binaries);
        Assert.Equal(CargoTestData.BinaryUrl, plan.Url.AbsoluteUri);
        Assert.Equal("zip", plan.Format);
        Assert.Equal("release/{bin}{binary-ext}", plan.BinaryPath);
        Assert.Equal(CargoTestData.Hash(binary), plan.Hash);
        Assert.NotEqual("sha256:" + manifest.versions[0].checksum, plan.Hash);
        Assert.Equal([CargoTestData.SourceUrl, CargoTestData.BinaryUrl + ".sha256"],
            fixture.Requests.Select(u => u.AbsoluteUri));
        Assert.Equal(CargoTestData.Executable(),
            CargoManagedInstaller.ExtractBinaries(binary, plan, default)["tool.exe"]);
    }

    [Fact]
    public async Task TargetOverrideUsesExactTripleWithoutRequestingTheDefaultAsset()
    {
        using var fixture = new CargoTestHttpFixture();
        var manifest = Source(fixture, """
            [package.metadata.binstall]
            pkg-url = "https://publisher.test/{version}/wrong.tar"
            pkg-fmt = "tar"
            [package.metadata.binstall.overrides.x86_64-pc-windows-msvc]
            pkg-url = "https://publisher.test/{name}/{version}/fixture.zip"
            pkg-fmt = "zip"
            """);
        fixture.Text(CargoTestData.BinaryUrl + ".sha256", new string('a', 64));
        using var http = new CargoHttp();
        var plan = Assert.IsType<CargoBinaryPlan>(await Resolve(http, manifest));
        Assert.Equal(CargoTestData.BinaryUrl, plan.Url.AbsoluteUri);
        Assert.Equal("zip", plan.Format);
        Assert.Equal([CargoTestData.SourceUrl, CargoTestData.BinaryUrl + ".sha256"],
            fixture.Requests.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task GitHubReleaseDigestPinsExactAssetWithoutThirdPartyRequests()
    {
        using var fixture = new CargoTestHttpFixture();
        var manifest = Source(fixture);
        string digest = CargoTestData.Hash([10, 20, 30]);
        fixture.Text(ReleaseUrl, $$"""
            {"tag_name":"v1.2.3","draft":false,"assets":[
              {"name":"{{AssetName}}","browser_download_url":"{{AssetUrl}}","digest":"{{digest}}"},
              {"name":"fixture-tool-1.2.3-aarch64-pc-windows-msvc.zip","browser_download_url":"https://github.com/unused"}
            ]}
            """);
        using var http = new CargoHttp();
        var plan = Assert.IsType<CargoBinaryPlan>(await Resolve(http, manifest));
        Assert.Equal(AssetUrl, plan.Url.AbsoluteUri);
        Assert.Equal(digest, plan.Hash);
        Assert.Equal(CargoTestData.Target, plan.Target);
        Assert.Equal([CargoTestData.SourceUrl, ReleaseUrl], fixture.Requests.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task GitHubChecksumAssetSelectsOnlyTheMatchingFilename()
    {
        using var fixture = new CargoTestHttpFixture();
        var manifest = Source(fixture);
        string digest = new string('b', 128);
        fixture.Text(ReleaseUrl, $$"""
            {"tag_name":"v1.2.3","assets":[
              {"name":"{{AssetName}}","browser_download_url":"{{AssetUrl}}"},
              {"name":"SHA512SUMS","browser_download_url":"https://github.com/publisher/tool/sums"}
            ]}
            """);
        fixture.Text("https://github.com/publisher/tool/sums",
            new string('a', 128) + " other.zip\n" + digest + " *" + AssetName);
        using var http = new CargoHttp();
        var plan = Assert.IsType<CargoBinaryPlan>(await Resolve(http, manifest));
        Assert.Equal("sha512:" + digest, plan.Hash);
        Assert.Equal(AssetUrl, plan.Url.AbsoluteUri);
        Assert.Equal(3, fixture.Requests.Count);
    }

    [Fact]
    public async Task MissingIntegrityFallsBackButAnInvalidPresentChecksumFails()
    {
        using var fixture = new CargoTestHttpFixture();
        var manifest = Source(fixture, """
            [package.metadata.binstall]
            pkg-url = "https://publisher.test/{name}/{version}/fixture.zip"
            """);
        fixture.Status(CargoTestData.BinaryUrl + ".sha256", HttpStatusCode.NotFound);
        fixture.Status(CargoTestData.BinaryUrl + ".sha512", HttpStatusCode.Gone);
        using var http = new CargoHttp();
        List<string> log = [];
        Assert.Null(await Resolve(http, manifest, log));
        Assert.Contains("no supported checksum", Assert.Single(log));
        fixture.Text(CargoTestData.BinaryUrl + ".sha256", "not a valid checksum");
        await Assert.ThrowsAsync<InvalidDataException>(() => Resolve(http, manifest));
        Assert.DoesNotContain(fixture.Requests, u => u.AbsoluteUri == CargoTestData.BinaryUrl);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    public async Task MissingOrAmbiguousPublisherAssetsSelectExplainedSourceFallback(string scenario)
    {
        using var fixture = new CargoTestHttpFixture();
        var manifest = Source(fixture);
        if (scenario == "missing")
        {
            fixture.Status(ReleaseUrl, HttpStatusCode.NotFound);
            fixture.Status(ReleaseUrl.Replace("/v1.2.3", "/1.2.3"), HttpStatusCode.NotFound);
        }
        else
            fixture.Text(ReleaseUrl, $$"""
                {"tag_name":"v1.2.3","assets":[
                  {"name":"{{AssetName}}","browser_download_url":"{{AssetUrl}}"},
                  {"name":"fixture-tool-v1.2.3-x86_64-pc-windows-msvc.zip","browser_download_url":"{{AssetUrl}}"}
                ]}
                """);
        using var http = new CargoHttp();
        List<string> log = [];
        Assert.Null(await Resolve(http, manifest, log));
        Assert.Contains(scenario == "missing" ? "No compatible" : "refusing to guess", Assert.Single(log));
        Assert.DoesNotContain(fixture.Requests, u => u.Host.Contains("quickinstall", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("other-package", "1.2.3")]
    [InlineData("fixture-tool", "1.2.4")]
    public async Task SourceManifestIdentityMismatchFailsInsteadOfFallingBack(string name, string version)
    {
        using var fixture = new CargoTestHttpFixture();
        var manifest = Source(fixture, name: name, version: version);
        using var http = new CargoHttp();
        Assert.Contains("does not match", (await Assert.ThrowsAsync<InvalidDataException>(() =>
            Resolve(http, manifest))).Message);
        Assert.Equal(CargoTestData.SourceUrl, Assert.Single(fixture.Requests).AbsoluteUri);
    }

    [Fact]
    public async Task SourceArchiveChecksumMismatchStopsBeforeManifestOrPublisherTrust()
    {
        using var fixture = new CargoTestHttpFixture();
        var manifest = Source(fixture);
        manifest = manifest with { versions = [manifest.versions[0] with { checksum = new string('0', 64) }] };
        using var http = new CargoHttp();
        List<string> log = [];
        Assert.Contains("published checksum", (await Assert.ThrowsAsync<InvalidDataException>(() =>
            Resolve(http, manifest, log))).Message);
        Assert.Empty(log);
        Assert.Single(fixture.Requests);
    }

    [Theory]
    [InlineData("signing = { algorithm = 'minisign' }")]
    [InlineData("disabled-strategies = ['crate-meta-data']")]
    [InlineData("pkg-url = 'https://publisher.test/latest/tool.zip'")]
    [InlineData("pkg-url = 'https://publisher.test/{version}/{unknown}.zip'")]
    [InlineData("pkg-url = 'http://publisher.test/{version}/tool.zip'")]
    [InlineData("pkg-url = 'https://publisher.test/{version}/tool.xz'")]
    public async Task UnsupportedPublisherMetadataFallsBackWithoutGuessing(string metadata)
    {
        using var fixture = new CargoTestHttpFixture();
        var manifest = Source(fixture, "[package.metadata.binstall]\n" + metadata);
        using var http = new CargoHttp();
        List<string> log = [];
        Assert.Null(await Resolve(http, manifest, log));
        Assert.Contains("Using Cargo to build from source", Assert.Single(log));
        Assert.Equal(CargoTestData.SourceUrl, Assert.Single(fixture.Requests).AbsoluteUri);
    }

    [Theory]
    [InlineData("x86_64-pc-windows-msvc", true)]
    [InlineData("aarch64-pc-windows-msvc", true)]
    [InlineData("x86_64-unknown-linux-gnu", true)]
    [InlineData("aarch64-unknown-linux-musl", true)]
    [InlineData("x86_64-apple-darwin", true)]
    [InlineData("aarch64-apple-darwin", true)]
    [InlineData("i686-pc-windows-msvc", false)]
    [InlineData("x86_64-pc-windows-gnu", false)]
    [InlineData("wasm32-unknown-unknown", false)]
    public void SupportedTargetSelectionIsExplicit(string target, bool expected) =>
        Assert.Equal(expected, CargoBinaryResolver.IsSupportedTarget(target));

    [Theory]
    [InlineData("fixture-tool-1.2.3-x86_64-unknown-linux-gnu.tar.gz", true)]
    [InlineData("tool-v1.2.3-x86_64-unknown-linux-gnu.zip", true)]
    [InlineData("fixture-tool-1.2.3-x86_64-unknown-linux-musl.tar.gz", false)]
    [InlineData("fixture-tool-1.2.3-aarch64-unknown-linux-gnu.tar.gz", false)]
    [InlineData("fixture-tool-1.2.4-x86_64-unknown-linux-gnu.zip", false)]
    [InlineData("fixture-tool-1.2.3.4-x86_64-unknown-linux-gnu.zip", false)]
    [InlineData("fixture-tool-1.2.30-x86_64-unknown-linux-gnu.zip", false)]
    [InlineData("fixture-tool-1.2.3-x86_64-unknown-linux-gnu-extra.zip", false)]
    [InlineData("unrelated-1.2.3-x86_64-unknown-linux-gnu.zip", false)]
    [InlineData("fixture-tool-1.2.3-x86_64-unknown-linux-gnu.zip.sha256", false)]
    public void AssetSelectionRequiresPackageVersionArchitectureAndLibc(string name, bool expected) =>
        Assert.Equal(expected, CargoBinaryResolver.MatchesAsset(name, CargoTestData.Id,
            CargoTestData.Version, "x86_64-unknown-linux-gnu", ["tool"]));

    [Fact]
    public void ExpandSubstitutesOnlyKnownAvailableVariables()
    {
        Assert.Equal("https://publisher.test/tool/fixture-tool/1.2.3/x86_64-pc-windows-msvc/tool.exe.zip",
            CargoBinaryResolver.Expand("{repo}/{ name }/{version}/{target}/{bin}{binary-ext}{archive-suffix}",
                CargoTestData.Id, CargoTestData.Version, CargoTestData.Target,
                "https://publisher.test/tool/", "zip", "tool"));
        Assert.Null(CargoBinaryResolver.Expand("{unknown}", "id", "1", "target", null, null, null));
        Assert.Null(CargoBinaryResolver.Expand("{bin}", "id", "1", "target", null, null, null));
    }

    [Fact]
    public void ChecksumParserRequiresOneValidMatchingDigestAndVerifierRejectsTampering()
    {
        byte[] content = [1, 3, 5, 7];
        string digest = CargoTestData.Hash(content);
        Assert.Equal(digest, CargoBinaryResolver.ParseChecksum(
            new string('f', 64) + " other.zip\n" + digest[7..] + " *tool.zip\n" + digest[7..] + " tool.zip", "tool.zip"));
        Assert.Null(CargoBinaryResolver.ParseChecksum(
            digest[7..] + " tool.zip\n" + new string('0', 64) + " tool.zip", "tool.zip"));
        Assert.Null(CargoBinaryResolver.ParseChecksum(digest[7..] + " other.zip", "tool.zip"));
        Assert.Null(CargoBinaryResolver.ParseChecksum(new string('g', 64), "tool.zip"));
        CargoBinaryResolver.VerifyHash(content, digest);
        CargoBinaryResolver.VerifyHash(content, "sha512:" + Convert.ToHexString(SHA512.HashData(content)));
        Assert.Contains("published checksum", Assert.Throws<InvalidDataException>(() =>
            CargoBinaryResolver.VerifyHash([1, 3, 5, 8], digest)).Message);
        Assert.Contains("unsupported", Assert.Throws<InvalidDataException>(() =>
            CargoBinaryResolver.VerifyHash(content, "md5:" + new string('0', 32))).Message);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("wrong-tag")]
    public async Task InvalidReleaseIdentityFailsInsteadOfTryingAnotherPublisher(string scenario)
    {
        using var fixture = new CargoTestHttpFixture();
        var manifest = Source(fixture);
        string draft = scenario == "draft" ? "true" : "false";
        string tag = scenario == "draft" ? "v1.2.3" : "v1.2.4";
        fixture.Text(ReleaseUrl, $$"""{"tag_name":"{{tag}}","draft":{{draft}},"assets":[]}""");
        using var http = new CargoHttp();
        Assert.Contains("does not match", (await Assert.ThrowsAsync<InvalidDataException>(() =>
            Resolve(http, manifest))).Message);
        Assert.Equal([CargoTestData.SourceUrl, ReleaseUrl], fixture.Requests.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task UnsupportedTargetAndMissingBinaryNamesFallbackBeforeAnyNetwork()
    {
        using var fixture = new CargoTestHttpFixture();
        using var http = new CargoHttp();
        var manifest = CargoTestData.Manifest(CargoTestData.VersionInfo(binaries: []));
        List<string> log = [];
        Assert.Null(await Resolve(http, manifest, log, target: "i686-pc-windows-msvc"));
        Assert.Contains("host target", Assert.Single(log));
        log.Clear();
        Assert.Null(await Resolve(http, manifest, log));
        Assert.Contains("no known executable names", Assert.Single(log));
        Assert.Empty(fixture.Requests);
    }
}

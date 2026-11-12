using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class XcodeSimulatorRuntimesTests
{
    private const string Catalog = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist SYSTEM "file:///this-must-not-be-loaded.dtd">
        <plist version="1.0"><dict>
          <key>downloadables</key><array>
            <dict>
              <key>category</key><string>simulator</string>
              <key>contentType</key><string>cryptexDiskImage</string>
              <key>downloadMethod</key><string>mobileAsset</string>
              <key>platform</key><string>com.apple.platform.iphoneos</string>
              <key>name</key><string>iOS 26.4 Simulator Runtime</string>
              <key>simulatorVersion</key><dict>
                <key>version</key><string>26.4</string><key>buildUpdate</key><string>23E100</string>
              </dict>
              <key>architectures</key><array><string>arm64</string><string>x86_64</string></array>
              <key>fileSize</key><integer>1234567890</integer>
            </dict>
            <dict>
              <key>category</key><string>simulator</string>
              <key>contentType</key><string>diskImage</string>
              <key>platform</key><string>com.apple.platform.watchos</string>
              <key>name</key><string>watchOS 27.0 beta 2 Simulator Runtime</string>
              <key>simulatorVersion</key><dict>
                <key>version</key><string>27.0</string><key>buildUpdate</key><string>24S200</string>
              </dict>
              <key>architectures</key><array><string>arm64</string></array>
            </dict>
            <dict>
              <key>category</key><string>simulator</string>
              <key>platform</key><string>com.apple.platform.appletvos</string>
              <key>name</key><string>tvOS 27.0 Simulator Runtime</string>
              <key>simulatorVersion</key><dict>
                <key>version</key><string>27.0</string><key>buildUpdate</key><string>24K300</string>
              </dict>
              <key>architectures</key><array><string>future-cpu</string></array>
            </dict>
            <dict><key>category</key><string>other</string></dict>
          </array>
          <key>sdkToSeedMappings</key><array><dict>
            <key>platform</key><string>com.apple.platform.appletvos</string>
            <key>buildUpdate</key><string>24K300</string>
            <key>seedNumber</key><integer>1</integer>
          </dict></array>
        </dict></plist>
        """;

    private const string Installed = """
        {
          "first": {
            "platformIdentifier": "com.apple.platform.iphonesimulator",
            "version": "26.4", "build": "23E100", "kind": "Bundled with Xcode",
            "state": "Ready", "runtimeBundlePath": "/Xcode/iOS.simruntime",
            "sizeBytes": 1234, "supportedArchitectures": ["arm64", "x86_64"]
          },
          "second": {
            "platformIdentifier": "com.apple.platform.iphonesimulator",
            "version": "26.4", "build": "UnknownBuildNumber", "kind": "Disk Image",
            "state": "Unavailable", "path": "/Library/runtime.dmg"
          }
        }
        """;

    [Fact]
    public void ReadsCatalogIdentitiesWithoutLoadingItsExternalDtd()
    {
        IReadOnlyList<XcodeSimulatorRuntime> runtimes = ParseCatalog(Catalog);

        Assert.Equal(3, runtimes.Count);
        Assert.Equal(("iOS", "26.4", "23E100", XcodeArchitecture.Universal, false, 1234567890L),
            (runtimes[0].Platform, runtimes[0].Version, runtimes[0].Build, runtimes[0].Architecture,
                runtimes[0].IsPrerelease, runtimes[0].SizeBytes));
        Assert.Equal(XcodeSimulatorRuntimeOrigin.AppleCatalog, runtimes[0].Origin);
        Assert.Equal(XcodeSimulatorRuntimeDistribution.XcodeManaged, runtimes[0].Distribution);
        Assert.True(runtimes[1].IsPrerelease);
        Assert.Equal(XcodeArchitecture.AppleSilicon, runtimes[1].Architecture);
        Assert.Equal(XcodeSimulatorRuntimeDistribution.Unknown, runtimes[1].Distribution);
        Assert.True(runtimes[2].IsPrerelease);
        Assert.Equal(XcodeArchitecture.Unspecified, runtimes[2].Architecture);
    }

    [Theory]
    [InlineData("https://devimages-cdn.apple.com/downloads/xcode/simulators/runtime.dmg", true)]
    [InlineData("https://download.developer.apple.com/downloads/xcode/simulators/runtime.dmg", false)]
    [InlineData("https://attacker.example/downloads/xcode/simulators/runtime.dmg", false)]
    public void ExposesOnlyLegacyImagesOnApplesPublicCdn(string source, bool allowed)
    {
        string xml = Catalog.Replace(
            "<key>contentType</key><string>diskImage</string>",
            $"<key>contentType</key><string>package</string><key>source</key><string>{source}</string>",
            StringComparison.Ordinal);

        XcodeSimulatorRuntime runtime = ParseCatalog(xml)[1];

        Assert.Equal(allowed ? XcodeSimulatorRuntimeDistribution.PublicImage : XcodeSimulatorRuntimeDistribution.Unknown,
            runtime.Distribution);
        Assert.Equal(allowed ? source : null, runtime.DownloadUrl);
    }

    [Fact]
    public void KeepsUnknownReleaseStageUnknown()
    {
        string xml = Catalog.Replace("iOS 26.4 Simulator Runtime", "iOS 26.4 Mystery Simulator Runtime", StringComparison.Ordinal);

        Assert.Null(ParseCatalog(xml)[0].IsPrerelease);
    }

    [Fact]
    public void MissingSeedMappingsDoNotTurnAnUnmarkedRuntimeIntoARelease()
    {
        int mappings = Catalog.IndexOf("<key>sdkToSeedMappings", StringComparison.Ordinal);
        string xml = Catalog[..mappings] + "</dict></plist>";

        IReadOnlyList<XcodeSimulatorRuntime> runtimes = ParseCatalog(xml);

        Assert.Null(runtimes[0].IsPrerelease);
        Assert.True(runtimes[1].IsPrerelease);
        Assert.Null(runtimes[2].IsPrerelease);
    }

    [Fact]
    public void ReadsExplicitSDKMappingsAndNormalizesOnlyTheSDKVersion()
    {
        const string mappings = """
            <key>sdkToSimulatorMappings</key><array>
              <dict><key>sdkIdentifier</key><string>iphoneos26.4.0</string>
                <key>sdkBuildUpdate</key><string>23E100</string>
                <key>simulatorBuildUpdate</key><string>23E101</string></dict>
              <dict><key>sdkIdentifier</key><string>iphoneos26.4</string>
                <key>sdkBuildUpdate</key><string>23E100</string>
                <key>simulatorBuildUpdate</key><string>23E101</string></dict>
              <dict><key>sdkIdentifier</key><string>watchos27.0</string>
                <key>sdkBuildUpdate</key><string>24S200</string>
                <key>simulatorBuildUpdate</key><string>24S201</string></dict>
              <dict><key>sdkIdentifier</key><string>futureos1.0</string>
                <key>sdkBuildUpdate</key><string>24A100</string>
                <key>simulatorBuildUpdate</key><string>24A101</string></dict>
            </array>
            """;
        string xml = Catalog.Replace("<key>sdkToSeedMappings", mappings + "<key>sdkToSeedMappings", StringComparison.Ordinal);

        XcodeSimulatorRuntimeCatalog snapshot = XcodeSimulatorRuntimeService.ParseCatalog(Encoding.UTF8.GetBytes(xml));

        Assert.NotNull(snapshot.SDKMappings);
        Assert.Equal(2, snapshot.SDKMappings.Count);
        Assert.Contains(new XcodeSimulatorSdkMapping(new XcodeSdkIdentity("iOS", "26.4", "23E100"), "23E101"),
            snapshot.SDKMappings);
        Assert.Contains(new XcodeSimulatorSdkMapping(new XcodeSdkIdentity("watchOS", "27.0", "24S200"), "24S201"),
            snapshot.SDKMappings);
    }

    [Theory]
    [InlineData("<string>wrong type</string>")]
    [InlineData("<array><dict><key>sdkIdentifier</key><string>iphoneos26.4</string></dict></array>")]
    [InlineData("<array><dict><key>sdkIdentifier</key><string>iphoneos26.beta</string><key>sdkBuildUpdate</key><string>23E100</string><key>simulatorBuildUpdate</key><string>23E101</string></dict></array>")]
    [InlineData("<array><dict><key>sdkIdentifier</key><string>iphoneos26.4</string><key>sdkBuildUpdate</key><string>23E100</string><key>simulatorBuildUpdate</key><string>invalid</string></dict></array>")]
    public void MalformedSDKMappingsDoNotDiscardTheRuntimeCatalog(string mappings)
    {
        string xml = Catalog.Replace("<key>sdkToSeedMappings",
            $"<key>sdkToSimulatorMappings</key>{mappings}<key>sdkToSeedMappings", StringComparison.Ordinal);

        XcodeSimulatorRuntimeCatalog snapshot = XcodeSimulatorRuntimeService.ParseCatalog(Encoding.UTF8.GetBytes(xml));

        Assert.Null(snapshot.SDKMappings);
        Assert.Equal(3, snapshot.Runtimes.Count);
    }

    [Fact]
    public void MissingSDKMappingsRemainUnknown()
        => Assert.Null(XcodeSimulatorRuntimeService.ParseCatalog(Encoding.UTF8.GetBytes(Catalog)).SDKMappings);

    [Fact]
    public void JoinsOnlyExplicitSDKAndSimulatorBuildsAndExactKnownLocalIdentities()
    {
        var sdk = new XcodeSdkIdentity("iOS", "26.4", "23E100");
        var otherSdk = new XcodeSdkIdentity("iOS", "26.4", "23E999");
        XcodeSimulatorSdkMapping[] mappings =
        [
            new(sdk, "23E101"),
            new(sdk, "23E102"),
            new(otherSdk, "23E999"),
        ];
        var arm = new XcodeSimulatorRuntime("iOS runtime", "iOS", "26.4", "23E101",
            XcodeArchitecture.AppleSilicon, false, XcodeSimulatorRuntimeOrigin.AppleCatalog, null);
        XcodeSimulatorRuntime universal = arm with { Architecture = XcodeArchitecture.Universal };
        XcodeSimulatorRuntime unknown = arm with { Architecture = XcodeArchitecture.Unspecified };
        XcodeSimulatorRuntime secondBuild = arm with { Build = "23E102" };
        XcodeSimulatorRuntime wrongBuild = arm with { Build = "23E999" };
        XcodeSimulatorRuntime wrongPlatform = arm with { Platform = "watchOS" };
        XcodeSimulatorRuntime local = arm with { Origin = XcodeSimulatorRuntimeOrigin.Installed };
        XcodeSimulatorRuntime bundled = universal with { Origin = XcodeSimulatorRuntimeOrigin.Bundled };
        XcodeSimulatorRuntime localUnknown = unknown with { Origin = XcodeSimulatorRuntimeOrigin.Installed };
        XcodeSimulatorRuntime localWrongVersion = local with { Version = "26.5" };
        XcodeSimulatorRuntime localWrongBuild = local with { Build = "23E999" };
        XcodeSimulatorRuntime localWrongArchitecture = local with { Architecture = XcodeArchitecture.Intel };

        IReadOnlyList<XcodeSimulatorRuntime> matched = XcodeSimulatorRuntimeMatching.ForSDKs(
            [sdk], mappings, [arm, universal, unknown, secondBuild, wrongBuild, wrongPlatform],
            [local, bundled, localUnknown, localWrongVersion, localWrongBuild, localWrongArchitecture]);

        Assert.Equal([local, bundled, arm, universal, unknown, secondBuild], matched);
        Assert.Empty(XcodeSimulatorRuntimeMatching.ForSDKs([], mappings, [arm], [local]));
        Assert.Empty(XcodeSimulatorRuntimeMatching.ForSDKs([sdk], [], [arm], [local]));
    }

    [Fact]
    public void RefusesMalformedOrAmbiguousCatalogIdentities()
    {
        string missingBuild = Catalog.Replace("<key>buildUpdate</key><string>23E100</string>", "", StringComparison.Ordinal);
        string duplicateKey = Catalog.Replace("<key>category</key><string>simulator</string>",
            "<key>category</key><string>simulator</string><key>category</key><string>simulator</string>",
            StringComparison.Ordinal);
        string badSeed = Catalog.Replace("<integer>1</integer>", "<string>not-a-seed</string>", StringComparison.Ordinal);

        Assert.Throws<XcodesException>(() => ParseCatalog(missingBuild));
        Assert.Throws<XcodesException>(() => ParseCatalog(duplicateKey));
        Assert.Throws<XcodesException>(() => ParseCatalog(badSeed));
        Assert.Throws<XmlException>(() => ParseCatalog("<plist>"));
    }

    [Fact]
    public void ReadsInstalledAndBundledCopiesSeparately()
    {
        IReadOnlyList<XcodeSimulatorRuntime> runtimes = XcodeSimulatorRuntimeService.ParseInstalled(Installed);

        Assert.Equal(2, runtimes.Count);
        Assert.Equal(XcodeSimulatorRuntimeOrigin.Bundled, runtimes[0].Origin);
        Assert.Equal("iOS", runtimes[0].Platform);
        Assert.Equal(XcodeArchitecture.Universal, runtimes[0].Architecture);
        Assert.Equal("/Xcode/iOS.simruntime", runtimes[0].Path);
        Assert.Equal("Ready", runtimes[0].State);
        Assert.Equal(XcodeSimulatorRuntimeOrigin.Installed, runtimes[1].Origin);
        Assert.Null(runtimes[1].Build);
        Assert.Null(runtimes[1].IsPrerelease);
        Assert.Equal(XcodeArchitecture.Unspecified, runtimes[1].Architecture);
        Assert.Equal("Unavailable", runtimes[1].State);
    }

    [Fact]
    public void RejectsMalformedInstalledDataInsteadOfShowingAnEmptyList()
    {
        Assert.Throws<JsonException>(() => XcodeSimulatorRuntimeService.ParseInstalled("{"));
        Assert.Throws<XcodesException>(() => XcodeSimulatorRuntimeService.ParseInstalled("null"));
        Assert.Throws<XcodesException>(() => XcodeSimulatorRuntimeService.ParseInstalled("""{"id":{"version":"26.4"}}"""));
    }

    [Fact]
    public async Task ReadsOnlyThePublicCatalogWithoutAuthorization()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Catalog, Encoding.UTF8, "application/xml"),
        });
        using var client = new HttpClient(handler);
        var service = new XcodeSimulatorRuntimeService(client, (_, _, _) => throw new InvalidOperationException());

        Assert.Equal(3, (await service.GetCatalogAsync(CancellationToken.None)).Runtimes.Count);
        Assert.Equal("https://devimages-cdn.apple.com/downloads/xcode/simulators/index2.dvtdownloadableindex",
            handler.RequestedUri?.AbsoluteUri);
        Assert.Null(handler.Authorization);
        Assert.Null(handler.Cookie);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task CatalogFailuresDoNotSwitchToAnotherSource()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var client = new HttpClient(handler);
        var service = new XcodeSimulatorRuntimeService(client, (_, _, _) => throw new InvalidOperationException());

        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetCatalogAsync(CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task CatalogBodyLimitIsEnforcedEvenWithAnIncorrectLength()
    {
        var handler = new Handler(_ =>
        {
            var content = new StreamContent(new MemoryStream(new byte[8 * 1024 * 1024 + 1]));
            content.Headers.ContentLength = 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler);
        var service = new XcodeSimulatorRuntimeService(client, (_, _, _) => throw new InvalidOperationException());

        await Assert.ThrowsAsync<XcodesException>(() => service.GetCatalogAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AValidatedCatalogAndMappingsCanBeReadAfterRestartWithoutAnotherRequest()
    {
        using var directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "cache", "AppleSimulatorCatalog.plist");
        const string mappings = """
            <key>sdkToSimulatorMappings</key><array><dict>
              <key>sdkIdentifier</key><string>iphoneos26.4</string>
              <key>sdkBuildUpdate</key><string>23E100</string>
              <key>simulatorBuildUpdate</key><string>23E100</string>
            </dict></array>
            """;
        string xml = Catalog.Replace("<key>sdkToSeedMappings", mappings + "<key>sdkToSeedMappings", StringComparison.Ordinal);
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml),
        });
        using var client = new HttpClient(handler);
        var first = new XcodeSimulatorRuntimeService(client, (_, _, _) => throw new InvalidOperationException(),
            cacheFile: () => path);
        Assert.Null(await first.GetSavedCatalogAsync(CancellationToken.None));

        XcodeSimulatorRuntimeCatalog online = await first.GetCatalogAsync(CancellationToken.None);
        DateTime savedAt = File.GetLastWriteTimeUtc(path);
        var reopened = new XcodeSimulatorRuntimeService(client, (_, _, _) => throw new InvalidOperationException(),
            cacheFile: () => path);
        XcodeSavedSimulatorRuntimeCatalog saved = Assert.IsType<XcodeSavedSimulatorRuntimeCatalog>(
            await reopened.GetSavedCatalogAsync(CancellationToken.None));

        Assert.Equal(online.Runtimes, saved.Catalog.Runtimes);
        Assert.Equal(online.SDKMappings, saved.Catalog.SDKMappings);
        Assert.Equal(savedAt, saved.SavedAtUtc);
        Assert.Equal(DateTimeKind.Utc, saved.SavedAtUtc.Kind);
        Assert.Equal(1, handler.RequestCount);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedOrInvalidRefreshDoesNotReplaceTheSavedCatalog(bool invalidBody)
    {
        using var directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "AppleSimulatorCatalog.plist");
        bool fail = false;
        var handler = new Handler(_ => fail
            ? invalidBody
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<plist>") }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Catalog) });
        using var client = new HttpClient(handler);
        var service = new XcodeSimulatorRuntimeService(client, (_, _, _) => throw new InvalidOperationException(),
            cacheFile: () => path);
        await service.GetCatalogAsync(CancellationToken.None);
        byte[] before = await File.ReadAllBytesAsync(path);
        fail = true;

        if (invalidBody)
            await Assert.ThrowsAsync<XmlException>(() => service.GetCatalogAsync(CancellationToken.None));
        else
            await Assert.ThrowsAsync<HttpRequestException>(() => service.GetCatalogAsync(CancellationToken.None));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(3, (await service.GetSavedCatalogAsync(CancellationToken.None))!.Catalog.Runtimes.Count);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task ACacheWriteFailureDoesNotTurnAValidCatalogIntoAFailure()
    {
        using var directory = new TestDirectory();
        string notADirectory = Path.Combine(directory.Path, "file");
        await File.WriteAllTextAsync(notADirectory, "keep");
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Catalog),
        });
        using var client = new HttpClient(handler);
        var service = new XcodeSimulatorRuntimeService(client, (_, _, _) => throw new InvalidOperationException(),
            cacheFile: () => Path.Combine(notADirectory, "cache.plist"));

        Assert.Equal(3, (await service.GetCatalogAsync(CancellationToken.None)).Runtimes.Count);
        Assert.Equal("keep", await File.ReadAllTextAsync(notADirectory));
    }

    [Fact]
    public async Task ACorruptOrOversizedSavedCatalogIsRejected()
    {
        using var directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "AppleSimulatorCatalog.plist");
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException()));
        var service = new XcodeSimulatorRuntimeService(client, (_, _, _) => throw new InvalidOperationException(),
            cacheFile: () => path);
        await File.WriteAllTextAsync(path, "<plist>");

        await Assert.ThrowsAsync<XmlException>(() => service.GetSavedCatalogAsync(CancellationToken.None));
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
            file.SetLength(XcodePublicCatalogDownload.MaximumBytes + 1);
        await Assert.ThrowsAsync<XcodesException>(() => service.GetSavedCatalogAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ACancelledCacheReadDoesNotReturnSavedDetails()
    {
        using var directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "AppleSimulatorCatalog.plist");
        await File.WriteAllTextAsync(path, Catalog);
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException()));
        var service = new XcodeSimulatorRuntimeService(client, (_, _, _) => throw new InvalidOperationException(),
            cacheFile: () => path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetSavedCatalogAsync(cancellation.Token));
    }

    [Fact]
    public async Task ACancelledCacheWriteDoesNotReplaceThePreviousFile()
    {
        using var directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "AppleSimulatorCatalog.plist");
        await File.WriteAllTextAsync(path, "keep");
        var cache = new XcodePublicCatalogCache(() => path, "test catalog");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.SaveAsync(Encoding.UTF8.GetBytes(Catalog), cancellation.Token));
        Assert.Equal("keep", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task InstalledListUsesOnlyTheReadOnlySimctlCommand()
    {
        IReadOnlyList<string>? arguments = null;
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException()));
        var service = new XcodeSimulatorRuntimeService(client, (command, _, _) =>
        {
            arguments = command;
            return Task.FromResult(new XcodesResult(0, Installed, ""));
        });

        Assert.Equal(2, (await service.GetInstalledAsync(CancellationToken.None)).Count);
        Assert.Equal(["simctl", "runtime", "list", "-j"], arguments);
    }

    [Fact]
    public async Task InstalledListReportsTheToolFailure()
    {
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException()));
        var service = new XcodeSimulatorRuntimeService(client,
            (_, _, _) => Task.FromResult(new XcodesResult(1, "", "No active developer directory")));

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(
            () => service.GetInstalledAsync(CancellationToken.None));
        Assert.Contains("No active developer directory", error.Message);
    }

    [Fact]
    public async Task SystemToolRunnerDoesNotAppendAnXcodesOnlyOption()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var script = new FakeXcodesScript("for argument in \"$@\"; do echo \"[$argument]\"; done\n");

        XcodesResult result = await XcodeSimulatorRuntimeService.CreateInstalledRunner(script.Path)
            .RunAsync("simctl", "runtime", "list", "-j");

        Assert.Equal(["[simctl]", "[runtime]", "[list]", "[-j]"],
            result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    private static IReadOnlyList<XcodeSimulatorRuntime> ParseCatalog(string xml)
        => XcodeSimulatorRuntimeService.ParseCatalog(Encoding.UTF8.GetBytes(xml)).Runtimes;

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            nameof(XcodeSimulatorRuntimesTests), Guid.NewGuid().ToString("N"));

        public TestDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Uri? RequestedUri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Cookie { get; private set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestedUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Cookie = request.Headers.TryGetValues("Cookie", out IEnumerable<string>? values) ? string.Join("; ", values) : null;
            return Task.FromResult(respond(request));
        }
    }
}

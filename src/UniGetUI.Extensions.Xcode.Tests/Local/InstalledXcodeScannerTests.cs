using UniGetUI.Extensions.Xcode.Local;

namespace UniGetUI.Extensions.Xcode.Tests.Local;

public sealed class InstalledXcodeScannerTests
{
    private static XcodeToolRunner Tools(string? selected = null, List<string>? calls = null)
        => (executable, arguments, _) =>
        {
            calls?.Add($"{executable} {string.Join(' ', arguments)}");
            return Task.FromResult(executable == "/usr/bin/xcode-select" && selected is not null
                ? new XcodesResult(0, selected + "\n", "")
                : new XcodesResult(1, "", "unexpected"));
        };

    [Fact]
    public async Task ReadsEveryXcodeInTheFolderAndNothingElse()
    {
        using var folder = new TemporaryDirectory();
        FakeXcodeBundle.Create(folder.Path, "Xcode-26.4.app", "26.4", "17E192");
        FakeXcodeBundle.Create(folder.Path, "Xcode-beta.app", "26.5", "17F5012f", beta: true,
            cpus: [FakeXcodeBundle.Arm64, FakeXcodeBundle.X64]);
        FakeXcodeBundle.Create(folder.Path, "Xcode-intel.app", "15.4", "15F31d", cpus: [FakeXcodeBundle.X64]);
        FakeXcodeBundle.Create(folder.Path, "Safari.app", "26.0", "21A1", bundleId: "com.apple.Safari");
        FakeXcodeBundle.Create(folder.Path, "Broken.app", "not a version", "17E192");
        FakeXcodeBundle.Create(folder.Path, "BadBuild.app", "26.4", "17E192; rm -rf");
        Directory.CreateDirectory(Path.Combine(folder.Path, "Empty.app"));
        Directory.CreateDirectory(Path.Combine(folder.Path, "Nested"));
        FakeXcodeBundle.Create(Path.Combine(folder.Path, "Nested"), "Xcode.app", "16.0", "16A242d");
        var calls = new List<string>();

        IReadOnlyList<XcodeBundleInfo> found =
            await new InstalledXcodeScanner(Tools(calls: calls)).ScanBundlesAsync(folder.Path, CancellationToken.None);

        Assert.Equal(
            [
                ("Xcode-26.4.app", "26.4", "17E192", false, XcodeArchitecture.AppleSilicon),
                ("Xcode-beta.app", "26.5", "17F5012f", true, XcodeArchitecture.Universal),
                ("Xcode-intel.app", "15.4", "15F31d", false, XcodeArchitecture.Intel),
            ],
            found.Select(b => (Path.GetFileName(b.Path), b.ShortVersion, b.Build, b.IsBeta, b.Architecture)));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task SkipsLinksToAnApp()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var folder = new TemporaryDirectory();
        string real = FakeXcodeBundle.Create(folder.Path, "Xcode-26.4.app", "26.4", "17E192");
        Directory.CreateSymbolicLink(Path.Combine(folder.Path, "Xcode.app"), real);

        IReadOnlyList<XcodeBundleInfo> found =
            await new InstalledXcodeScanner(Tools()).ScanBundlesAsync(folder.Path, CancellationToken.None);

        Assert.Equal([real], found.Select(b => b.Path));
    }

    [Fact]
    public async Task AMissingFolderHasNoXcodes()
    {
        IReadOnlyList<XcodeBundleInfo> found = await new InstalledXcodeScanner(Tools())
            .ScanBundlesAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), CancellationToken.None);
        Assert.Empty(found);
    }

    [Fact]
    public async Task DefaultDiscoveryCombinesApplicationsWithDistinctSpotlightResults()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var root = new TemporaryDirectory();
        string applications = Path.Combine(root.Path, "z-Applications");
        string other = Path.Combine(root.Path, "a-Other");
        string local = FakeXcodeBundle.Create(applications, "Xcode.app", "26.4", "17E192");
        string elsewhere = FakeXcodeBundle.Create(other, "Xcode-beta.app", "26.5", "17F5012f", beta: true);
        string link = Path.Combine(other, "Alias.app");
        Directory.CreateSymbolicLink(link, elsewhere);
        var calls = new List<string>();
        XcodeToolRunner tools = (executable, arguments, _) =>
        {
            calls.Add($"{executable} {string.Join(' ', arguments)}");
            return Task.FromResult(new XcodesResult(0,
                $"{local}\0{elsewhere}\0{elsewhere}\0{link}\0relative.app\0{other}/missing.app\0", ""));
        };

        IReadOnlyList<XcodeBundleInfo> found =
            await new InstalledXcodeScanner(tools, applications).ScanBundlesAsync(null, CancellationToken.None);

        Assert.Equal(new[] { local, elsewhere }.Order(StringComparer.Ordinal),
            found.Select(bundle => bundle.Path).Order(StringComparer.Ordinal));
        Assert.Single(calls);
        Assert.StartsWith("/usr/bin/mdfind -0 ", calls[0]);
    }

    [Fact]
    public async Task ASpotlightFailureKeepsTheApplicationsFolderResults()
    {
        using var applications = new TemporaryDirectory();
        string local = FakeXcodeBundle.Create(applications.Path, "Xcode.app", "26.4", "17E192");

        IReadOnlyList<XcodeBundleInfo> found =
            await new InstalledXcodeScanner(Tools(), applications.Path).ScanBundlesAsync(null, CancellationToken.None);

        Assert.Equal([local], found.Select(bundle => bundle.Path));
    }

    [Fact]
    public async Task DefaultDiscoveryStillSearchesSpotlightWhenApplicationsDoesNotExist()
    {
        using var other = new TemporaryDirectory();
        string elsewhere = FakeXcodeBundle.Create(other.Path, "Xcode.app", "26.4", "17E192");
        XcodeToolRunner tools = (_, _, _) => Task.FromResult(new XcodesResult(0, elsewhere + "\0", ""));

        IReadOnlyList<XcodeBundleInfo> found = await new InstalledXcodeScanner(tools,
            Path.Combine(other.Path, "missing")).ScanBundlesAsync(null, CancellationToken.None);

        Assert.Equal([elsewhere], found.Select(bundle => bundle.Path));
    }

    [Fact]
    public async Task TheSelectedAppIsTheOneOwningTheDeveloperDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var folder = new TemporaryDirectory();
        string real = FakeXcodeBundle.Create(folder.Path, "Xcode-26.4.app", "26.4", "17E192");
        string link = Path.Combine(folder.Path, "Xcode.app");
        Directory.CreateSymbolicLink(link, real);
        var scanner = new InstalledXcodeScanner(Tools(link + "/Contents/Developer"));

        Assert.Equal(InstalledXcodeScanner.Resolve(real), await scanner.GetSelectedAppAsync(CancellationToken.None));
        Assert.Null(await new InstalledXcodeScanner(Tools("/Library/Developer/CommandLineTools"))
            .GetSelectedAppAsync(CancellationToken.None));
        Assert.Null(await new InstalledXcodeScanner(Tools()).GetSelectedAppAsync(CancellationToken.None));
        Assert.Null(InstalledXcodeScanner.AppOfDeveloperDirectory("relative/Xcode.app/Contents/Developer"));
    }

    [Fact]
    public void PropertyListsNeverExpandEntities()
    {
        byte[] xml = System.Text.Encoding.UTF8.GetBytes("""
            <?xml version="1.0"?>
            <!DOCTYPE plist [<!ENTITY e "expanded">]>
            <plist><dict><key>A</key><string>&e;</string><key>B</key><string>kept</string></dict></plist>
            """);
        IReadOnlyDictionary<string, string>? values = XcodeBundle.ParseXmlPlist(xml);
        Assert.True(values is null || !values.Values.Contains("expanded"));
        Assert.Null(XcodeBundle.ParseXmlPlist("not xml"u8.ToArray()));
    }

    [Fact]
    public async Task ReadsTheXcodeOnThisMac()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("UNIGETUI_XCODE_LIVE_TEST") != "1"
            || !Directory.Exists("/Applications/Xcode.app"))
            return;
        XcodeBundleInfo? bundle = await XcodeBundle.ReadAsync("/Applications/Xcode.app",
            (exe, args, ct) => new XcodesRunner(exe)
                .RunAsync(args, TimeSpan.FromSeconds(30), ct), CancellationToken.None);
        Assert.NotNull(bundle);
        Assert.NotEqual(XcodeArchitecture.Unspecified, bundle.Architecture);
    }
}

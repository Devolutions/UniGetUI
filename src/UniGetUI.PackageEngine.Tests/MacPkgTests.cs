using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Managers.InstallerFeeds;
using UniGetUI.PackageEngine.Managers.MacPkgManager;
using UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

namespace UniGetUI.PackageEngine.Tests;

[Collection("Installer feeds")]
public sealed class MacPkgTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), nameof(MacPkgTests), Guid.NewGuid().ToString("N"));

    public MacPkgTests()
    {
        Directory.CreateDirectory(_directory);
        PkgFeed.TestIconDirectoryOverride = Path.Combine(_directory, "extracted-icons");
        PkgInstaller.LockDirectory = Path.Combine(_directory, "lock");
        // A CI runner may be an administrator; tests choose the elevation path themselves.
        PkgInstaller.IsRoot = () => false;
        PkgInstaller.RootExecutable = () => null;
    }

    public void Dispose()
    {
        PkgFeed.TestIconDirectoryOverride = null;
        PkgInstaller.LockDirectory = PkgHelperCommand.RootDirectory;
        PkgInstaller.IsRoot = CoreTools.IsAdministrator;
        PkgInstaller.RootExecutable = () => null;
        Directory.Delete(_directory, recursive: true);
    }

    // ── A minimal xar writer, so the reader is exercised without macOS tools ──

    private sealed record XarFile(string Path, byte[] Contents, bool Compressed = true, string? Encoding = null);

    private static byte[] Xar(params XarFile[] files)
    {
        var heap = new MemoryStream();
        var toc = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><xar><toc>");
        int id = 1;
        void WriteFiles(IEnumerable<XarFile> group, int depth)
        {
            foreach (var byName in group.GroupBy(f => f.Path.Split('/')[depth]))
            {
                var leaf = byName.FirstOrDefault(f => f.Path.Split('/').Length == depth + 1);
                toc.Append($"<file id=\"{id++}\"><name>{SecurityElement.Escape(byName.Key)}</name>");
                if (leaf is null)
                {
                    toc.Append("<type>directory</type>");
                    WriteFiles(byName, depth + 1);
                }
                else
                {
                    byte[] data = leaf.Compressed ? Zlib(leaf.Contents) : leaf.Contents;
                    string encoding = leaf.Encoding ?? (leaf.Compressed ? "application/x-gzip" : "application/octet-stream");
                    toc.Append($"<type>file</type><data><length>{data.Length}</length><offset>{heap.Length}</offset>"
                        + $"<size>{leaf.Contents.Length}</size><encoding style=\"{encoding}\"/></data>");
                    heap.Write(data);
                }
                toc.Append("</file>");
            }
        }
        WriteFiles(files, 0);
        toc.Append("</toc></xar>");

        byte[] tocBytes = Encoding.UTF8.GetBytes(toc.ToString());
        byte[] compressedToc = Zlib(tocBytes);
        byte[] header = new byte[28];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x78617221);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), (ulong)compressedToc.Length);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(16), (ulong)tocBytes.Length);
        return [.. header, .. compressedToc, .. heap.ToArray()];
    }

    private static byte[] Zlib(byte[] data)
    {
        var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }

    private static string Distribution(string id, string version, string? title = null, string? hosts = null,
        bool versionOnRef = true, string component = "component.pkg") => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <installer-gui-script minSpecVersion="1">
            {(title is null ? "" : $"<title>{title}</title>")}
            <pkg-ref id="{id}"><bundle-version><bundle id="com.example.tool" path="Applications/Tool.app"/></bundle-version></pkg-ref>
            <options customize="never"{(hosts is null ? "" : $" hostArchitectures=\"{hosts}\"")}/>
            <pkg-ref id="{id}"{(versionOnRef ? $" version=\"{version}\"" : "")} onConclusion="none">#{component}</pkg-ref>
        </installer-gui-script>
        """;

    private static string PackageInfo(string id, string version) =>
        $"""<pkg-info format-version="2" identifier="{id}" version="{version}" install-location="/"><bundle path="./Applications/Tool.app" id="com.example.tool"/></pkg-info>""";

    private static byte[] ProductArchive(string id, string version, string? title = null, string? hosts = null) =>
        Xar(new XarFile("Distribution", Encoding.UTF8.GetBytes(Distribution(id, version, title, hosts))),
            new XarFile("component.pkg/PackageInfo", Encoding.UTF8.GetBytes(PackageInfo(id, version))),
            new XarFile("component.pkg/Payload", new byte[64], Compressed: false));

    private string WritePackage(string relativePath, byte[] contents)
    {
        string path = Path.Combine(_directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
        return path;
    }

    // ── Package metadata ───────────────────────────────────────────────────

    [Fact]
    public void AProductArchiveIsIdentifiedByItsFirstComponent()
    {
        var metadata = PkgMetadata.Read(new MemoryStream(ProductArchive("com.example.app.pkg", "4.2.1", "Example", "x86_64,arm64")));
        Assert.Equal("com.example.app.pkg", metadata.Id);
        Assert.Equal("4.2.1", metadata.Version);
        Assert.Equal("Example", metadata.Title);
        Assert.Equal(["x86_64", "arm64"], metadata.Architectures);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("SU_TITLE")]
    [InlineData("   ")]
    public void WithoutAUsableTitleTheInstalledAppNamesThePackage(string? title)
    {
        var metadata = PkgMetadata.Read(new MemoryStream(ProductArchive("com.example.app.pkg", "1.0", title)));
        Assert.Equal("Tool", metadata.Title);
    }

    [Fact]
    public void AComponentVersionMissingFromTheDistributionIsReadFromItsPackageInfo()
    {
        var archive = Xar(
            new XarFile("Distribution", Encoding.UTF8.GetBytes(Distribution("com.example.app.pkg", "9", versionOnRef: false, component: "My%20App.pkg"))),
            new XarFile("My App.pkg/PackageInfo", Encoding.UTF8.GetBytes(PackageInfo("com.example.app.pkg", "3.1"))));
        Assert.Equal("3.1", PkgMetadata.Read(new MemoryStream(archive)).Version);
    }

    [Fact]
    public void AComponentPackageIsIdentifiedByItsPackageInfo()
    {
        var archive = Xar(new XarFile("PackageInfo", Encoding.UTF8.GetBytes(PackageInfo("com.example.tool.pkg", "2.0")), Compressed: false));
        var metadata = PkgMetadata.Read(new MemoryStream(archive));
        Assert.Equal(("com.example.tool.pkg", "2.0", "Tool"), (metadata.Id, metadata.Version, metadata.Title));
        Assert.Empty(metadata.Architectures);
    }

    [Fact]
    public void TheOutermostAppNamesThePackageEvenWhenItsHelpersAreListedFirst()
    {
        string info = """
            <pkg-info identifier="com.axosoft.gitkraken.pkg" version="12.6.1">
              <bundle path="./GitKraken.app/Contents/Frameworks/GitKraken Helper (Plugin).app" id="a"/>
              <bundle path="./GitKraken.app/Contents/Frameworks/Mantle.framework" id="b"/>
              <bundle path="./GitKraken.app" id="com.axosoft.gitkraken"/>
            </pkg-info>
            """;
        var metadata = PkgMetadata.Read(new MemoryStream(Xar(new XarFile("PackageInfo", Encoding.UTF8.GetBytes(info)))));
        Assert.Equal("GitKraken", metadata.Title);
    }

    [Fact]
    public void FilesThatAreNotInstallerPackagesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => PkgMetadata.Read(new MemoryStream(Encoding.UTF8.GetBytes("PK\u0003\u0004 not a pkg at all"))));
        Assert.Throws<InvalidDataException>(() => PkgMetadata.Read(new MemoryStream(Xar(new XarFile("Bom", new byte[8])))));
        Assert.Throws<InvalidDataException>(() => PkgMetadata.Read(new MemoryStream(Xar(
            new XarFile("Distribution", Encoding.UTF8.GetBytes(Distribution("x", "1")), Encoding: "application/x-bzip2")))));
    }

    [Fact]
    public void AMetadataFileThatInflatesBeyondTheLimitIsRejected()
    {
        byte[] huge = new byte[PkgMetadata.MaxMetadataBytes + 1];
        var archive = Xar(new XarFile("PackageInfo", huge));
        Assert.Throws<InvalidDataException>(() => PkgMetadata.Read(new MemoryStream(archive)));
    }

    [Fact]
    public void FoldersNestedTooDeeplyAreRejected()
    {
        static MemoryStream Nested(int depth) => new(Xar(new XarFile(
            string.Join('/', Enumerable.Range(0, depth).Select(i => "d" + i)) + "/PackageInfo", Encoding.UTF8.GetBytes("<pkg-info/>"))));
        using (var shallow = Nested(10))
            Assert.NotNull(XarArchive.Open(shallow).ReadFile(string.Join('/', Enumerable.Range(0, 10).Select(i => "d" + i)) + "/PackageInfo", 1024));
        using var deep = Nested(100);
        Assert.Throws<InvalidDataException>(() => XarArchive.Open(deep));
    }

    [Fact]
    public void ADistributionWithAnEntityDeclarationDoesNotExpandIt()
    {
        string distribution = Distribution("com.example.app.pkg", "1.0", "&boom;")
            .Replace("<installer-gui-script", "<!DOCTYPE x [<!ENTITY boom \"expanded\">]>\n<installer-gui-script");
        var archive = Xar(new XarFile("Distribution", Encoding.UTF8.GetBytes(distribution)));
        Assert.ThrowsAny<System.Xml.XmlException>(() => PkgMetadata.Read(new MemoryStream(archive)));
    }

    [Theory]
    [InlineData(new string[0], Architecture.Arm64, 2)]
    [InlineData(new[] { "x86_64", "arm64" }, Architecture.Arm64, 2)]
    [InlineData(new[] { "x86_64" }, Architecture.Arm64, 1)]
    [InlineData(new[] { "arm64" }, Architecture.X64, 0)]
    [InlineData(new[] { "x86_64" }, Architecture.X64, 2)]
    public void ArchitectureFitPrefersNativeThenRosetta(string[] architectures, Architecture machine, int fit) =>
        Assert.Equal(fit, PkgMetadata.ArchitectureFit(architectures, machine));

    // ── Feeds ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://software.example.com/mac/", true)]
    [InlineData("http://127.0.0.1:8080/feed", true)]
    [InlineData("http://software.example.com/mac/", false)]
    [InlineData("ftp://software.example.com/mac/", false)]
    [InlineData("relative/folder", false)]
    [InlineData("", false)]
    public void OnlyHttpsFileAndLoopbackLocationsAreFeeds(string text, bool accepted) =>
        Assert.Equal(accepted, FeedLocation.TryParse(text, out _));

    [Fact]
    public void AnAbsolutePathIsAFolderFeed()
    {
        Assert.True(FeedLocation.TryParse(_directory, out Uri location));
        Assert.True(location.IsFile);
        Assert.Equal(Path.GetFullPath(_directory), location.LocalPath);
    }

    [Theory]
    [InlineData("https://h/mac/", "https://h/mac/index.json")]
    [InlineData("https://h/mac", "https://h/mac/index.json")]
    [InlineData("https://h/mac/catalog.json", "https://h/mac/catalog.json")]
    public void TheIndexSitsBesideThePackages(string location, string index) =>
        Assert.Equal(index, FeedLocation.IndexUrl(new Uri(location)).AbsoluteUri);

    [Theory]
    [InlineData("Zoom.pkg", "https://h/mac/Zoom.pkg")]
    [InlineData("apps/My App.pkg", "https://h/mac/apps/My%20App.pkg")]
    [InlineData("../other/Zoom.pkg", null)]
    [InlineData("apps/../../Zoom.pkg", null)]
    [InlineData("/Zoom.pkg", null)]
    [InlineData("https://evil.example.com/Zoom.pkg", null)]
    [InlineData("//evil.example.com/Zoom.pkg", null)]
    [InlineData("apps\\Zoom.pkg", null)]
    [InlineData("", null)]
    public void IndexPathsCannotLeaveTheFeed(string relative, string? resolved) =>
        Assert.Equal(resolved, FeedLocation.ResolveWithin(new Uri("https://h/mac/index.json"), relative)?.AbsoluteUri);

    [Fact]
    public void AnIndexSkipsEntriesWithoutAHashOrWithAnEscapingPath()
    {
        string hash = new('a', 64);
        string json = $$"""
            {"version":1,"packages":[
              {"id":"good","name":"Good","version":"1.0","file":"Good.pkg","sha256":"{{hash.ToUpperInvariant()}}","size":10,"icon":"Good.png"},
              {"id":"nohash","name":"No hash","version":"1.0","file":"NoHash.pkg","sha256":"","size":10},
              {"id":"escape","name":"Escape","version":"1.0","file":"../Escape.pkg","sha256":"{{hash}}","size":10},
              {"id":"","name":"No id","version":"1.0","file":"NoId.pkg","sha256":"{{hash}}","size":10}
            ]}
            """;
        var item = Assert.Single(PkgFeed.ParseIndex(Encoding.UTF8.GetBytes(json), new Uri("https://h/mac/index.json")));
        Assert.Equal("good", item.Id);
        Assert.Equal(hash, item.Sha256);
        Assert.Equal("https://h/mac/Good.pkg", item.Location.AbsoluteUri);
        Assert.Equal("https://h/mac/Good.png", item.Icon?.AbsoluteUri);
    }

    [Fact]
    public void AnIndexOfAnotherVersionIsRejected() =>
        Assert.Throws<InvalidDataException>(() => PkgFeed.ParseIndex(
            Encoding.UTF8.GetBytes("""{"version":2,"packages":[]}"""), new Uri("https://h/index.json")));

    private static PkgFeedItem Item(string id, string version, params string[] architectures) =>
        new(id, id, version, new Uri($"https://h/{id}-{version}-{string.Join('-', architectures)}.pkg"), null, 0, architectures, null);

    [Fact]
    public void TheNewestRunnableBuildOfEachPackageIsOffered()
    {
        var items = new[]
        {
            Item("app", "1.0"),
            Item("app", "2.0", "x86_64"),
            Item("app", "2.0", "arm64"),
            Item("app", "3.0", "x86_64"),
            Item("tool", "5.0", "arm64"),
        };
        // On an Intel Mac the arm64-only tool cannot run, so it is not offered at all.
        var latest = Assert.Single(PkgFeed.Latest(items, (a, b) => new Version(a).CompareTo(new Version(b)), Architecture.X64));
        Assert.Equal(("app", "3.0"), (latest.Id, latest.Version));
    }

    [Fact]
    public void ANativeBuildBeatsARosettaBuildOfTheSameVersion()
    {
        var items = new[] { Item("app", "2.0", "x86_64"), Item("app", "2.0", "arm64"), Item("app", "1.0", "arm64") };
        var best = Assert.Single(PkgFeed.Latest(items, (a, b) => new Version(a).CompareTo(new Version(b)), Architecture.Arm64));
        Assert.Equal(["arm64"], best.Architectures);
        Assert.Equal("2.0", best.Version);
    }

    [Fact]
    public void AmongReleasesOfOnePackageVersionTheNewestAppWins()
    {
        static PkgFeedItem Release(string app) => Item("app", "1.0", "arm64") with
        {
            App = new AppBundleInfo("com.example.app", "/Applications/App.app", app),
        };
        static int? Compare(string a, string b) => new Version(a).CompareTo(new Version(b));
        foreach (var items in new[] { new[] { Release("12.5.0"), Release("12.6.1") }, [Release("12.6.1"), Release("12.5.0")] })
            Assert.Equal("12.6.1", Assert.Single(PkgFeed.Latest(items, Compare, Architecture.Arm64)).App?.Version);
    }

    [Fact]
    public void AFolderFeedListsItsPackagesAndSkipsUnreadableFiles()
    {
        WritePackage("Example-4.2.pkg", ProductArchive("com.example.app.pkg", "4.2", "Example"));
        WritePackage("Example-4.2.png", [0x89, 0x50, 0x4E, 0x47]);
        WritePackage("older/Example-4.1.pkg", ProductArchive("com.example.app.pkg", "4.1", "Example"));
        WritePackage("broken.pkg", Encoding.UTF8.GetBytes("not a package"));
        WritePackage("readme.txt", Encoding.UTF8.GetBytes("hello"));
        WritePackage("a/b/c/d/too-deep.pkg", ProductArchive("com.example.deep.pkg", "1.0"));

        var items = PkgFeed.ScanFolder(_directory, CancellationToken.None);
        Assert.Equal(["4.1", "4.2"], items.Select(i => i.Version).Order());
        var newest = items.Single(i => i.Version == "4.2");
        Assert.Equal("Example", newest.Name);
        Assert.True(newest.Location.IsFile);
        Assert.Null(newest.Sha256);
        Assert.Equal(Path.Combine(_directory, "Example-4.2.png"), newest.Icon?.LocalPath);
    }

    [Fact]
    public void AFolderScanIsReusedUntilTheIndexesAreRefreshed()
    {
        string feed = Path.Combine(_directory, "reused");
        WritePackage("reused/App.pkg", ProductArchive("com.example.app.pkg", "1.0"));
        var location = new Uri(feed);
        Assert.Single(PkgFeed.List(location));
        WritePackage("reused/Tool.pkg", ProductArchive("com.example.tool.pkg", "1.0"));
        Assert.Single(PkgFeed.List(location));
        PkgFeed.ForgetCachedIndexes();
        Assert.Equal(2, PkgFeed.List(location).Count);
    }

    [Fact]
    public void AMissingFolderFeedIsReported() =>
        Assert.Throws<DirectoryNotFoundException>(() => PkgFeed.ScanFolder(Path.Combine(_directory, "unmounted"), CancellationToken.None));

    [Fact]
    public void AReplacedPackageIsReadAgain()
    {
        string path = WritePackage("App.pkg", ProductArchive("com.example.app.pkg", "1.0"));
        Assert.Equal("1.0", PkgFeed.TryReadFile(path)?.Version);
        File.WriteAllBytes(path, ProductArchive("com.example.app.pkg", "1.10"));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("1.10", PkgFeed.TryReadFile(path)?.Version);
    }

    [Fact]
    public void AnUnreachableFolderFeedKeepsItsLastListing()
    {
        string feed = Path.Combine(_directory, "share");
        WritePackage("share/App.pkg", ProductArchive("com.example.app.pkg", "1.0"));
        var location = new Uri(feed);
        Assert.Single(PkgFeed.List(location));
        Directory.Delete(feed, recursive: true);
        // Forget the brief scan cache, so the answer comes from the fallback for an unreachable feed.
        PkgFeed.ForgetCachedIndexes();
        Assert.Equal("com.example.app.pkg", Assert.Single(PkgFeed.List(location)).Id);
        // What authorizes an install reads the feed as it is now, never its last known contents.
        Assert.Throws<DirectoryNotFoundException>(() => PkgFeed.List(location, currentOnly: true));
        Assert.Throws<DirectoryNotFoundException>(() => PkgFeed.List(new Uri(Path.Combine(_directory, "never-seen"))));
    }

    // ── Index generation ───────────────────────────────────────────────────

    [Fact]
    public void TheIndexCommandRecordsEveryPackageWithItsHash()
    {
        string app = WritePackage("App.pkg", ProductArchive("com.example.app.pkg", "1.0", "App", "arm64"));
        WritePackage("App.png", [1, 2, 3]);
        WritePackage("tools/Tool 2.pkg", Xar(new XarFile("PackageInfo", Encoding.UTF8.GetBytes(PackageInfo("com.example.tool.pkg", "2.0")))));
        WritePackage("junk.pkg", [1, 2, 3]);

        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(3, PkgIndexCommand.Run([_directory], output, error));
        Assert.Contains("junk.pkg", error.ToString());

        byte[] json = File.ReadAllBytes(Path.Combine(_directory, "index.json"));
        var items = PkgFeed.ParseIndex(json, new Uri("https://h/feed/index.json"));
        var appItem = items.Single(i => i.Id == "com.example.app.pkg");
        Assert.Equal(FeedLocation.ComputeSha256(app), appItem.Sha256);
        Assert.Equal(["arm64"], appItem.Architectures);
        Assert.Equal("https://h/feed/App.png", appItem.Icon?.AbsoluteUri);
        var tool = items.Single(i => i.Id == "com.example.tool.pkg");
        Assert.Equal(("Tool", "2.0", "https://h/feed/tools/Tool%202.pkg"), (tool.Name, tool.Version, tool.Location.AbsoluteUri));
    }

    [Fact]
    public void AWindowsEntryInAnIndexIsNotAMacPackage()
    {
        string sha = new('a', 64);
        string json = $$"""
            {
              "version": 1,
              "packages": [
                { "id": "com.example.app.pkg", "name": "App", "version": "1.0", "file": "App.pkg", "sha256": "{{sha}}" },
                { "id": "Vendor.Tool", "name": "Tool", "version": "1.0", "file": "Tool.exe", "sha256": "{{sha}}", "windows": { "installer": "nsis" } },
                { "id": "{11111111-2222-3333-4444-555555555555}", "name": "Example", "version": "1.0", "file": "Example.msi", "sha256": "{{sha}}", "windows": { "installer": "msi" } }
              ]
            }
            """;
        Assert.Equal(["com.example.app.pkg"], PkgFeed.ParseIndex(Encoding.UTF8.GetBytes(json), new Uri("https://h/feed/index.json")).Select(i => i.Id));
    }

    [Fact]
    public void TheMacIndexKeepsWindowsEntriesUnderTheirOwnKeySoOlderMacsNeverSeeThem()
    {
        string pkg = WritePackage("mac/App.pkg", ProductArchive("com.example.app.pkg", "1.0", "App", "arm64"));
        string exe = WritePackage("win/Tool.exe", Encoding.ASCII.GetBytes("MZ installer"));
        WritePackage("win/Changed.msi", [1]);
        string index = Path.Combine(_directory, "index.json");
        File.WriteAllText(index, $$"""
            {
              "version": 1,
              "packages": [],
              "windowsPackages": [
                { "id": "Vendor.Tool", "name": "Tool", "version": "1.0", "file": "win/Tool.exe", "sha256": "{{FeedLocation.ComputeSha256(exe)}}", "windows": { "installer": "nsis" } },
                { "id": "Vendor.Changed", "name": "Changed", "version": "1.0", "file": "win/Changed.msi", "sha256": "{{new string('0', 64)}}", "windows": { "installer": "msi" } }
              ]
            }
            """);

        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(3, PkgIndexCommand.Run([_directory], output, error));
        Assert.Contains("win/Tool.exe: Tool (Vendor.Tool 1.0) (kept)", output.ToString());
        Assert.Contains("win/Changed.msi", error.ToString());

        byte[] json = File.ReadAllBytes(index);
        var written = FeedJson.Deserialize(json, FeedIndexJsonContext.Default.FeedIndex)!;
        Assert.Equal(["mac/App.pkg"], written.Packages.Select(e => e.File));
        Assert.Equal(["win/Tool.exe"], written.WindowsPackages!.Select(e => e.File));
        Assert.Equal(FeedLocation.ComputeSha256(pkg), PkgFeed.ParseIndex(json, new Uri("https://h/feed/index.json")).Single().Sha256);
    }

    [Fact]
    public void AnIndexWithOnlyWindowsPackagesOffersNothingToAMac()
    {
        string sha = new('a', 64);
        string json = $$"""
            {
              "version": 1,
              "packages": [],
              "windowsPackages": [
                { "id": "com.example.app.pkg", "name": "App", "version": "1.0", "file": "App.pkg", "sha256": "{{sha}}" }
              ]
            }
            """;
        Assert.Empty(PkgFeed.ParseIndex(Encoding.UTF8.GetBytes(json), new Uri("https://h/feed/index.json")));
    }

#if WINDOWS
    [Fact]
    public void BothPlatformsIndexAMixedFolderInTurnWithoutLosingTheOthersEntries()
    {
        string pkg = WritePackage("mac/App.pkg", ProductArchive("com.example.app.pkg", "1.0", "App", "arm64"));
        string exe = WritePackage("win/Tool.exe", [.. File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "cmd.exe")),
            .. new byte[4], .. BitConverter.GetBytes(0xDEADBEEF), .. Encoding.ASCII.GetBytes("NullsoftInst"), .. new byte[12]]);
        var indexUrl = new Uri("https://h/feed/index.json");
        string index = Path.Combine(_directory, "index.json");
        var windows = new UniGetUI.PackageEngine.Managers.WinInstallerManager.WinInstallerIndexer();

        Assert.Equal(0, FeedIndexCommand.Run([_directory], new StringWriter(), new StringWriter(), windows));
        Assert.Empty(FeedJson.Deserialize(File.ReadAllBytes(index), FeedIndexJsonContext.Default.FeedIndex)!.Packages);
        Assert.Empty(PkgFeed.ParseIndex(File.ReadAllBytes(index), indexUrl));

        var output = new StringWriter();
        Assert.Equal(0, PkgIndexCommand.Run([_directory], output, new StringWriter()));
        Assert.Contains("win/Tool.exe", output.ToString());
        Assert.Contains("(kept)", output.ToString());
        Assert.Equal(FeedLocation.ComputeSha256(pkg), PkgFeed.ParseIndex(File.ReadAllBytes(index), indexUrl).Single().Sha256);
        var windowsItem = UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal.WinFeed.ParseIndex(File.ReadAllBytes(index), indexUrl).Single();
        Assert.Equal((FeedLocation.ComputeSha256(exe), "https://h/feed/win/Tool.exe"), (windowsItem.Sha256, windowsItem.Location.AbsoluteUri));

        Assert.Equal(0, FeedIndexCommand.Run([_directory], new StringWriter(), new StringWriter(), windows));
        Assert.Single(PkgFeed.ParseIndex(File.ReadAllBytes(index), indexUrl));
        Assert.Single(UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal.WinFeed.ParseIndex(File.ReadAllBytes(index), indexUrl));

        File.AppendAllText(pkg, "changed");
        var error = new StringWriter();
        Assert.Equal(3, FeedIndexCommand.Run([_directory], new StringWriter(), error, windows));
        Assert.Contains("mac/App.pkg", error.ToString());
        Assert.Empty(PkgFeed.ParseIndex(File.ReadAllBytes(index), indexUrl));
    }
#endif

    [Fact]
    public void TheIndexCommandExplainsItsUsage()
    {
        var output = new StringWriter();
        Assert.Equal(2, PkgIndexCommand.Run([], output, new StringWriter()));
        Assert.Contains("pkg-index <folder>", output.ToString());
        Assert.Equal(1, PkgIndexCommand.Run([Path.Combine(_directory, "missing")], new StringWriter(), new StringWriter()));
        Assert.True(PkgIndexCommand.IsCommand(["PKG-INDEX", "x"]));
        Assert.False(PkgIndexCommand.IsCommand(["tui"]));
    }

    // ── Catalog feeds ──────────────────────────────────────────────────────

    [Fact]
    public void FeedsAreReadFromTheCatalogsThatDeclareThem()
    {
        string json = $$"""
            {"version":1,"catalogs":[
              {"id":"a","name":"Mac apps","packages":[],"pkgFeed":"https://software.example.com/mac/"},
              {"id":"b","name":"Developer tools","packages":[]},
              {"id":"c","name":"Local","packages":[],"pkgFeed":"{{_directory.Replace("\\", "\\\\")}}"},
              {"id":"d","name":"Mac apps","packages":[],"pkgFeed":"https://other.example.com/"},
              {"id":"A","name":"Duplicate id","packages":[],"pkgFeed":"https://third.example.com/"},
              {"name":"No id","packages":[],"pkgFeed":"https://fourth.example.com/"},
              {"id":"e","name":"Insecure","packages":[],"pkgFeed":"http://software.example.com/"}
            ]}
            """;
        // Feeds are told apart by catalog id: two catalogs may share a name, not an id.
        var feeds = CatalogFeeds.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal(["a", "c", "d"], feeds.Select(f => f.Id));
        Assert.Equal(["Mac apps", "Local", "Mac apps"], feeds.Select(f => f.Name));
        Assert.Equal("https://software.example.com/mac/", feeds[0].Location.AbsoluteUri);
        Assert.Equal("https://other.example.com/", feeds[2].Location.AbsoluteUri);
        Assert.True(feeds[1].Location.IsFile);
    }

    [Fact]
    public void AMissingOrBrokenCatalogDeclaresNoFeeds()
    {
        Assert.Empty(CatalogFeeds.Load(Path.Combine(_directory, "missing.json")));
        string broken = WritePackage("broken.json", Encoding.UTF8.GetBytes("{ not json"));
        Assert.Empty(CatalogFeeds.Load(broken));
    }

    // ── Receipts ───────────────────────────────────────────────────────────

    [Fact]
    public void InstalledVersionsComeFromTheReceiptsOfFeedPackagesOnly()
    {
        var previous = PkgReceipts.RunPkgUtil;
        var calls = new List<string>();
        PkgReceipts.RunPkgUtil = (_, args) =>
        {
            calls.Add(string.Join(' ', args));
            return args switch
            {
                ["--pkgs"] => (0, "com.apple.pkg.Core\ncom.example.app.pkg\ncom.example.other.pkg\n"),
                ["--pkg-info", "com.example.app.pkg"] => (0, "package-id: com.example.app.pkg\nversion: 4.2.1\nvolume: /\nlocation: /\ninstall-time: 1700000000\n"),
                _ => (1, ""),
            };
        };
        try
        {
            var versions = PkgReceipts.GetInstalledVersions(["com.example.app.pkg", "com.example.missing.pkg"], null);
            Assert.Equal("4.2.1", Assert.Single(versions).Value);
            Assert.Equal(["--pkgs", "--pkg-info com.example.app.pkg"], calls);
        }
        finally { PkgReceipts.RunPkgUtil = previous; }
    }

    // ── Installation ───────────────────────────────────────────────────────

    [Fact]
    public void TheElevatedCommandQuotesThePathForAppleScriptAndTheShell()
    {
        string command = PkgInstaller.InstallPkgCommand("/tmp/a \"b\" 'c'/package.pkg");
        Assert.Equal("/usr/sbin/installer -pkg '/tmp/a \"b\" '\\''c'\\''/package.pkg' -target / 2>&1", command);
        string script = PkgInstaller.ElevatedScript(command, "Zoom \"Pro\"");
        Assert.StartsWith("do shell script \"/usr/sbin/installer -pkg '/tmp/a \\\"b\\\" '\\\\''c'", script);
        Assert.Contains("Zoom \\\"Pro\\\"", script);
        Assert.EndsWith("with administrator privileges", script);
    }

    [Theory]
    [InlineData("/Volumes/x/GitKraken.app", "GitKraken.app")]
    [InlineData("/Volumes/x/My 'App'.app/", "My 'App'.app")]
    [InlineData("/Volumes/x/.hidden.app", null)]
    [InlineData("/Volumes/x/Tool", null)]
    [InlineData("/Volumes/x/Bad\nName.app", null)]
    public void OnlyAPlainAppNameIsCopiedToApplications(string app, string? name)
    {
        if (name is null)
            Assert.Throws<InvalidDataException>(() => PkgInstaller.SafeAppName(app));
        else
            Assert.Equal(name, PkgInstaller.SafeAppName(app));
    }

    [Fact]
    public void AStagedPackageMustMatchTheFeedHashAndIdentity()
    {
        string staged = WritePackage("staged.pkg", ProductArchive("com.example.app.pkg", "1.0"));
        string hash = FeedLocation.ComputeSha256(staged);
        var item = new PkgFeedItem("com.example.app.pkg", "App", "1.0", new Uri("https://h/App.pkg"), hash, 0, [], null);

        PkgInstaller.VerifyHash(item, staged);
        PkgInstaller.VerifyHash(item with { Sha256 = null }, staged);
        Assert.Throws<InvalidDataException>(() => PkgInstaller.VerifyHash(item with { Sha256 = new string('0', 64) }, staged));

        var metadata = PkgMetadata.Read(staged);
        PkgInstaller.VerifyIdentity(item, metadata);
        Assert.Throws<InvalidDataException>(() => PkgInstaller.VerifyIdentity(item with { Version = "2.0" }, metadata));
        Assert.Throws<InvalidDataException>(() => PkgInstaller.VerifyIdentity(item with { Id = "com.example.other.pkg" }, metadata));
    }

    [Fact]
    public async Task AFolderFeedPackageIsCopiedThenInstalledThroughTheAdministratorPrompt()
    {
        string source = WritePackage("feed/App.pkg", ProductArchive("com.example.app.pkg", "1.0"));
        var item = new PkgFeedItem("com.example.app.pkg", "App", "1.0", new Uri(source), null, new FileInfo(source).Length, [], null);
        var previous = PkgInstaller.RunProcess;
        (string File, IReadOnlyList<string> Args)? ran = null;
        PkgInstaller.RunProcess = (file, args, _) =>
        {
            ran = (file, args);
            return Task.FromResult((0, "installer: The install was successful."));
        };
        try
        {
            var log = new List<string>();
            var verdict = await PkgInstaller.InstallAsync(item, "App", log.Add, _ => { }, CancellationToken.None);
            Assert.Equal(Enums.OperationVeredict.Success, verdict);
            Assert.Equal("/usr/bin/osascript", ran?.File);
            Assert.Contains("installer: The install was successful.", log);
        }
        finally { PkgInstaller.RunProcess = previous; }
    }

    [Fact]
    public async Task CancelingDuringTheRootInstallWaitsForItToFinish()
    {
        string source = WritePackage("feed/App.pkg", ProductArchive("com.example.app.pkg", "1.0"));
        var item = new PkgFeedItem("com.example.app.pkg", "App", "1.0", new Uri(source), null, 0, [], null);
        using var cancellation = new CancellationTokenSource();
        var previous = PkgInstaller.RunProcess;
        var previousIsRoot = PkgInstaller.IsRoot;
        string? staged = null;
        bool stagedFileOutlivedTheCancel = false, tokenCouldCancel = true;
        PkgInstaller.IsRoot = () => true;
        PkgInstaller.RunProcess = async (file, args, token) =>
        {
            if (file != "/bin/sh")
                return (0, "accepted");
            // The root step names this install's own staged file; it must survive a cancel while it runs.
            staged = StagedPackage(args[1]);
            tokenCouldCancel = token.CanBeCanceled;
            cancellation.Cancel();
            await Task.Delay(50, CancellationToken.None);
            stagedFileOutlivedTheCancel = File.Exists(staged);
            return (0, "installer: The install was successful.");
        };
        try
        {
            Assert.Equal(Enums.OperationVeredict.Success,
                await PkgInstaller.InstallAsync(item, "App", _ => { }, _ => { }, cancellation.Token));
            Assert.False(tokenCouldCancel);
            Assert.True(stagedFileOutlivedTheCancel);
            Assert.False(File.Exists(staged), "The staged file is cleaned up once the install finishes.");
        }
        finally
        {
            PkgInstaller.RunProcess = previous;
            PkgInstaller.IsRoot = previousIsRoot;
        }
    }

    [Fact]
    public async Task ACanceledOperationStopsBeforeTheRootInstall()
    {
        string source = WritePackage("feed/App.pkg", ProductArchive("com.example.app.pkg", "1.0"));
        var item = new PkgFeedItem("com.example.app.pkg", "App", "1.0", new Uri(source), null, 0, [], null);
        using var cancellation = new CancellationTokenSource();
        var previous = PkgInstaller.RunProcess;
        bool ran = false;
        PkgInstaller.RunProcess = (file, _, _) =>
        {
            ran |= file != PkgInstaller.SpctlPath;
            return Task.FromResult((0, ""));
        };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PkgInstaller.InstallAsync(item, "App", _ => { },
                progress => { if (progress.Stage == Enums.OperationProgressStage.Installing) cancellation.Cancel(); }, cancellation.Token));
            Assert.False(ran);
        }
        finally { PkgInstaller.RunProcess = previous; }
    }

    [Fact]
    public async Task AFailedCopyLeavesTheInstalledAppInPlace()
    {
        if (OperatingSystem.IsWindows())
            return;

        string applications = Path.Combine(_directory, "Applications");
        string installed = WriteApp(applications, "Example App.app", "com.example.app", "1.0");
        string command = PkgInstaller.CopyAppCommand(Path.Combine(_directory, "missing", "Example App.app"), installed);
        var (code, _) = await PkgInstaller.RunProcess("/bin/sh", ["-c", command], CancellationToken.None);

        Assert.NotEqual(0, code);
        Assert.Equal("1.0", AppBundle.VersionOf(AppBundle.ReadInfoPlist(installed)));
        Assert.Equal(["Example App.app"], Directory.GetFileSystemEntries(applications).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APackageGatekeeperRejectsInstallsOnlyWhenTheCatalogAllowsIt(bool allowUnsigned)
    {
        string source = WritePackage("feed/App.pkg", ProductArchive("com.example.app.pkg", "1.0"));
        var item = new PkgFeedItem("com.example.app.pkg", "App", "1.0", new Uri(source), null, 0, [], null);
        var previous = PkgInstaller.RunProcess;
        bool installed = false;
        PkgInstaller.RunProcess = (file, args, _) =>
        {
            if (file == PkgInstaller.SpctlPath)
                return Task.FromResult((3, $"{args[^1]}: rejected\nsource=no usable signature"));
            installed = true;
            return Task.FromResult((0, "installer: The install was successful."));
        };
        try
        {
            if (allowUnsigned)
                Assert.Equal(Enums.OperationVeredict.Success, await PkgInstaller.InstallAsync(item, "App", _ => { }, _ => { },
                    CancellationToken.None, allowUnsigned: true));
            else
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                    PkgInstaller.InstallAsync(item, "App", _ => { }, _ => { }, CancellationToken.None));
                Assert.Contains("no usable signature", error.Message);
            }
            Assert.Equal(allowUnsigned, installed);
        }
        finally { PkgInstaller.RunProcess = previous; }
    }

    [Fact]
    public async Task AnUnsignedPackageAnyUserCanReplaceIsRefused()
    {
        if (OperatingSystem.IsWindows())
            return;

        string source = WritePackage("shared/App.pkg", ProductArchive("com.example.app.pkg", "1.0"));
        File.SetUnixFileMode(Path.GetDirectoryName(source)!, (UnixFileMode)Convert.ToInt32("1777", 8));
        var item = new PkgFeedItem("com.example.app.pkg", "App", "1.0", new Uri(source), null, 0, [], null);
        var previous = PkgInstaller.RunProcess;
        bool ran = false;
        PkgInstaller.RunProcess = (_, _, _) => { ran = true; return Task.FromResult((0, "")); };
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => PkgInstaller.InstallAsync(item, "App", _ => { }, _ => { },
                CancellationToken.None, allowUnsigned: true));
            Assert.False(ran);
        }
        finally { PkgInstaller.RunProcess = previous; }
    }

    [Fact]
    public void ACatalogCanAllowItsFeedsUnsignedPackages()
    {
        var feeds = CatalogFeeds.Parse(Encoding.UTF8.GetBytes("""
            {"version":1,"catalogs":[
              {"id":"signed","name":"Signed","packages":[],"pkgFeed":"https://h/a/"},
              {"id":"internal","name":"Internal","packages":[],"pkgFeed":"https://h/b/","allowUnsignedPackages":true}
            ]}
            """));
        Assert.Equal([false, true], feeds.Select(f => f.AllowUnsignedPackages));
    }

    [Fact]
    public async Task AnUpdateCutOffMidSwapIsRestoredNextTime()
    {
        if (OperatingSystem.IsWindows())
            return;

        // A swap cut off after moving the old app aside: no app in place, the old one and a partial copy beside it.
        string applications = Path.Combine(_directory, "Applications");
        string destination = Path.Combine(applications, "Example App.app");
        WriteApp(applications, ".Example App.unigetui-old", "com.example.app", "1.0");
        WriteApp(applications, ".Example App.unigetui-new", "com.example.app", "partial");
        string command = PkgInstaller.CopyAppCommand(Path.Combine(_directory, "missing", "Example App.app"), destination);
        var (code, _) = await PkgInstaller.RunProcess("/bin/sh", ["-c", command], CancellationToken.None);

        Assert.NotEqual(0, code);
        Assert.Equal("1.0", AppBundle.VersionOf(AppBundle.ReadInfoPlist(destination)));
        Assert.Equal(["Example App.app"], Directory.GetFileSystemEntries(applications).Select(Path.GetFileName));
    }

    [Fact]
    public void AToolIsDrainedAndStoppedWhenItOverrunsItsTime()
    {
        if (OperatingSystem.IsWindows())
            return;

        var chatty = Tool.Run("/bin/sh", ["-c", "head -c 300000 /dev/zero >&2; echo done"], timeout: TimeSpan.FromSeconds(30));
        Assert.Equal((0, "done\n", 300000), (chatty.ExitCode, chatty.Text, chatty.StandardError.Length));
        Assert.Throws<InvalidDataException>(() => Tool.Run("/bin/sleep", ["30"], timeout: TimeSpan.FromMilliseconds(300)));
    }

    // Like a UI thread busy waiting on the tool: whatever is posted to it never runs.
    private sealed class BlockedThreadContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
    }

    [Fact]
    public async Task RootInstallsTakeTurnsOnTheMachineWideLock()
    {
        if (OperatingSystem.IsWindows())
            return;

        string journal = Path.Combine(_directory, "journal");
        string Step(string name) => PkgInstaller.LockedCommand(
            $"echo {name}-start >> '{journal}'; /bin/sleep 0.5; echo {name}-end >> '{journal}'");
        var runs = new[] { "a", "b" }.Select(name => PkgInstaller.RunProcess("/bin/sh", ["-c", Step(name)], CancellationToken.None)).ToArray();
        Assert.All(await Task.WhenAll(runs), r => Assert.Equal(0, r.ExitCode));
        string[] lines = File.ReadAllLines(journal);
        Assert.Equal(4, lines.Length);
        Assert.Equal(lines[0][..1], lines[1][..1]);
        Assert.Equal(lines[2][..1], lines[3][..1]);
    }

    [Fact]
    public void AToolRunFromTheUiThreadDoesNotDeadlock()
    {
        if (OperatingSystem.IsWindows())
            return;

        ToolResult? result = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new BlockedThreadContext());
            result = Tool.Run("/bin/echo", ["done"]);
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        Assert.Equal("done\n", result?.Text);
    }

    [Fact]
    public void AToolIsNotHeldByAProcessItLeftRunning()
    {
        if (OperatingSystem.IsWindows())
            return;

        var previous = Tool.DrainGrace;
        Tool.DrainGrace = TimeSpan.FromMilliseconds(500);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = Tool.Run("/bin/sh", ["-c", "/bin/sleep 30 & echo done"], timeout: TimeSpan.FromSeconds(20));
            Assert.Equal((0, "done\n"), (result.ExitCode, result.Text));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally { Tool.DrainGrace = previous; }
    }

    [Fact]
    public async Task ACanceledToolIsStoppedBeforeTheCancellationIsReported()
    {
        if (OperatingSystem.IsWindows())
            return;

        int pid = 0;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Tool.RunAsync("/bin/sleep", ["30"], started: p => pid = p.Id, cancellationToken: cancel.Token));
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
    }

    [Fact]
    public async Task CancelingTheAdministratorPromptCancelsTheOperation()
    {
        string source = WritePackage("feed/App.pkg", ProductArchive("com.example.app.pkg", "1.0"));
        var item = new PkgFeedItem("com.example.app.pkg", "App", "1.0", new Uri(source), null, 0, [], null);
        var previous = PkgInstaller.RunProcess;
        PkgInstaller.RunProcess = (file, _, _) => Task.FromResult(file == PkgInstaller.SpctlPath
            ? (0, "accepted")
            : (1, "0:61: execution error: User canceled. (-128)"));
        try
        {
            Assert.Equal(Enums.OperationVeredict.Canceled,
                await PkgInstaller.InstallAsync(item, "App", _ => { }, _ => { }, CancellationToken.None));
        }
        finally { PkgInstaller.RunProcess = previous; }
    }

    [Fact]
    public async Task AnOperationCanceledBeforeTheInstallStartsInstallsNothing()
    {
        var item = new PkgFeedItem("com.example.app.pkg", "App", "1.0", new Uri("https://example.com/App.pkg"), null, 0, [], null);
        var previous = PkgInstaller.RunProcess;
        PkgInstaller.RunProcess = (_, _, _) => throw new InvalidOperationException("Nothing may run.");
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                PkgInstaller.InstallAsync(item, "App", _ => { }, _ => { }, new CancellationToken(canceled: true), feedId: "mac"));
        }
        finally { PkgInstaller.RunProcess = previous; }
    }

    [Fact]
    public async Task BehindThePromptAProtectedUniGetUIStagesAndChecksThePackageAsRoot()
    {
        var item = new PkgFeedItem("com.example.app.pkg", "App", "1.0", new Uri("https://example.com/App 1.0.pkg"), "ab", 42, [], null);
        var (previousRun, previousRoot, previousExecutable) = (PkgInstaller.RunProcess, PkgInstaller.IsRoot, PkgInstaller.RootExecutable);
        string? script = null;
        string answer = "";
        PkgInstaller.IsRoot = () => false;
        PkgInstaller.RootExecutable = () => "/Applications/UniGetUI.app/Contents/MacOS/UniGetUI";
        PkgInstaller.RunProcess = (file, args, _) =>
        {
            Assert.Equal("/usr/bin/osascript", file);
            script = args[^1];
            return Task.FromResult(answer.StartsWith("error") ? (1, answer) : (0, answer));
        };
        try
        {
            var log = new List<string>();
            answer = "{\"log\":\"Downloading...\"}\r{\"result\":\"success\",\"message\":\"Installed.\"}";
            Assert.Equal(Enums.OperationVeredict.Success,
                await PkgInstaller.InstallAsync(item, "App", log.Add, _ => { }, CancellationToken.None, allowUnsigned: true));
            Assert.Contains("'/Applications/UniGetUI.app/Contents/MacOS/UniGetUI' pkg-helper install-item '", script);
            Assert.Contains("with administrator privileges", script);
            Assert.Equal(["Downloading...", "Installed."], log.Skip(1));

            var order = JsonSerializer.Deserialize(
                JsonSerializer.Serialize(PkgElevatedInstall.From(item, allowUnsigned: true), PkgHelperJsonContext.Default.PkgElevatedInstall),
                PkgHelperJsonContext.Default.PkgElevatedInstall)!;
            var back = order.ToItem();
            Assert.Equal((item.Id, item.Version, item.Location, item.Sha256, item.Size, true),
                (back.Id, back.Version, back.Location, back.Sha256, back.Size, order.AllowUnsigned));

            answer = "{\"result\":\"failure\",\"message\":\"Gatekeeper rejects it.\"}";
            Assert.Equal(Enums.OperationVeredict.Failure,
                await PkgInstaller.InstallAsync(item, "App", _ => { }, _ => { }, CancellationToken.None));
            answer = "Unhandled exception.";
            Assert.Equal(Enums.OperationVeredict.Failure,
                await PkgInstaller.InstallAsync(item, "App", _ => { }, _ => { }, CancellationToken.None));
            answer = "error 0:61: execution error: User canceled. (-128)";
            Assert.Equal(Enums.OperationVeredict.Canceled,
                await PkgInstaller.InstallAsync(item, "App", _ => { }, _ => { }, CancellationToken.None));
        }
        finally
        {
            (PkgInstaller.RunProcess, PkgInstaller.IsRoot, PkgInstaller.RootExecutable) = (previousRun, previousRoot, previousExecutable);
        }
    }

    // ── The manager ────────────────────────────────────────────────────────

    [Fact]
    public void TheManagerListsFeedPackagesTheirReceiptsAndTheirUpdates()
    {
        string feed = Path.Combine(_directory, "feed");
        WritePackage("feed/App-1.0.pkg", ProductArchive("com.example.app.pkg", "1.0", "App"));
        WritePackage("feed/App-2.0.pkg", ProductArchive("com.example.app.pkg", "2.0", "App"));
        WritePackage("feed/Tool.pkg", ProductArchive("com.example.tool.pkg", "3.0", "Tool"));
        WritePackage("feed/Current.pkg", ProductArchive("com.example.current.pkg", "5.0", "Current"));
        string catalog = WritePackage("SoftwareCatalog.json", Encoding.UTF8.GetBytes($$"""
            {"version":1,"catalogs":[{"id":"mac","name":"Mac apps","packages":[],"pkgFeed":"{{feed.Replace("\\", "\\\\")}}"}]}
            """));

        var previousPkgUtil = PkgReceipts.RunPkgUtil;
        PkgReceipts.RunPkgUtil = (_, args) => args switch
        {
            ["--pkgs"] => (0, "com.example.app.pkg\ncom.example.current.pkg\ncom.apple.pkg.Core\n"),
            ["--pkg-info", "com.example.app.pkg"] => (0, "version: 1.0\n"),
            ["--pkg-info", "com.example.current.pkg"] => (0, "version: 5.0\n"),
            _ => (1, ""),
        };
        CatalogFeeds.TestCatalogPathOverride = catalog;
        try
        {
            var manager = new MacPkg();
            var source = Assert.Single(manager.SourcesHelper.GetSources());
            // Bundles and the CLI write a source as "Manager: name" and look the part after ": " up again.
            Assert.Equal(("mac", "Installer packages: mac"), (source.Name, source.AsString_DisplayName));
            Assert.Same(source, manager.SourcesHelper.Factory.GetSourceIfExists(source.AsString_DisplayName.Split(": ")[^1]));

            var available = manager.ListLatest();
            Assert.Equal(["com.example.app.pkg 2.0", "com.example.current.pkg 5.0", "com.example.tool.pkg 3.0"],
                available.Select(p => $"{p.Item.Id} {p.Item.Version}").Order());

            var installed = manager.ListInstalled();
            Assert.Equal(["com.example.app.pkg 1.0", "com.example.current.pkg 5.0"],
                installed.Select(p => $"{p.Item.Id} {p.Installed}").Order());
            Assert.All(installed, p => Assert.Same(source, p.Source));

            var package = new PackageClasses.Package("App", "com.example.app.pkg", "1.0", source, manager);
            Assert.Equal("2.0", manager.FindFeedItem(package)?.Version);
            Assert.Equal("1.0", manager.FindFeedItem(package, "1.0")?.Version);
            Assert.Null(manager.FindFeedItem(package, "9.9"));
            Assert.False(manager.Capabilities.CanUninstall);
            Assert.False(manager.Capabilities.Sources.CanBeEdited);

            // Without SupportsCustomPackageIcons the base helper never asks the manager for icons.
            WritePackage("feed/App-2.0.png", [0x89, 0x50, 0x4E, 0x47]);
            Assert.Equal(Path.Combine(feed, "App-2.0.png"), manager.DetailsHelper.GetIcon(package)?.LocalPath);
        }
        finally
        {
            PkgReceipts.RunPkgUtil = previousPkgUtil;
            CatalogFeeds.TestCatalogPathOverride = null;
        }
    }

    [Fact]
    public void AnUpdateIsFoundWhicheverFeedOffersItAndAPinnedBuildMustRunHere()
    {
        string first = Path.Combine(_directory, "first"), second = Path.Combine(_directory, "second");
        WritePackage("first/App-1.0.pkg", ProductArchive("com.example.app.pkg", "1.0", "App"));
        WritePackage("second/App-2.0.pkg", ProductArchive("com.example.app.pkg", "2.0", "App"));
        WritePackage("second/App-0.9.pkg", ProductArchive("com.example.app.pkg", "0.9", "App", "i386"));
        string catalog = WritePackage("SoftwareCatalog.json", Encoding.UTF8.GetBytes($$"""
            {"version":1,"catalogs":[
              {"id":"first","name":"First","packages":[],"pkgFeed":"{{first.Replace("\\", "\\\\")}}"},
              {"id":"second","name":"Second","packages":[],"pkgFeed":"{{second.Replace("\\", "\\\\")}}"}]}
            """));

        var previousPkgUtil = PkgReceipts.RunPkgUtil;
        PkgReceipts.RunPkgUtil = (_, args) => args switch
        {
            ["--pkgs"] => (0, "com.example.app.pkg\n"),
            ["--pkg-info", "com.example.app.pkg"] => (0, "version: 1.0\n"),
            _ => (1, ""),
        };
        CatalogFeeds.TestCatalogPathOverride = catalog;
        try
        {
            var manager = new MacPkg();
            var installed = Assert.Single(manager.ListInstalled());
            Assert.Equal(("second", "1.0", "2.0", true),
                (installed.Source.Name, installed.Installed, installed.Item.Version, installed.HasUpdate));

            var package = new PackageClasses.Package("App", "com.example.app.pkg", "1.0", installed.Source, manager);
            Assert.Null(manager.FindFeedItem(package, "0.9"));
            Assert.True(manager.Capabilities.SupportsCustomVersions);
        }
        finally
        {
            PkgReceipts.RunPkgUtil = previousPkgUtil;
            CatalogFeeds.TestCatalogPathOverride = null;
        }
    }

    [Fact]
    public void RefreshingReadsTheCatalogAgain()
    {
        string catalog = Path.Combine(_directory, "SoftwareCatalog.json");
        void Declare(params string[] ids) => File.WriteAllText(catalog, "{\"version\":1,\"catalogs\":["
            + string.Join(',', ids.Select(id => $$"""{"id":"{{id}}","name":"{{id}}","packages":[],"pkgFeed":"https://example.com/{{id}}/"}"""))
            + "]}");
        Declare("a");
        CatalogFeeds.TestCatalogPathOverride = catalog;
        try
        {
            var manager = new MacPkg();
            Assert.Equal(["a"], manager.SourcesHelper.GetSources().Select(s => s.Name));
            Declare("a", "b");
            manager.RefreshPackageIndexes();
            Assert.Equal(["a", "b"], manager.SourcesHelper.GetSources().Select(s => s.Name));
        }
        finally { CatalogFeeds.TestCatalogPathOverride = null; }
    }

    // ── Apps ───────────────────────────────────────────────────────────────

    [Fact]
    public void APackageRecordsTheOutermostAppAtItsInstallLocation()
    {
        string info = """
            <pkg-info identifier="com.axosoft.gitkraken.pkg" version="12.6.1" install-location="/Applications">
              <bundle path="./GitKraken.app/Contents/Frameworks/GitKraken Helper (Plugin).app" id="helper" CFBundleShortVersionString="1"/>
              <bundle path="./GitKraken.app" id="com.axosoft.gitkraken" CFBundleShortVersionString="12.6.1" CFBundleVersion="12.6.1.4"/>
            </pkg-info>
            """;
        var component = PkgMetadata.Read(new MemoryStream(Xar(new XarFile("PackageInfo", Encoding.UTF8.GetBytes(info)))));
        Assert.Equal(new AppBundleInfo("com.axosoft.gitkraken", "/Applications/GitKraken.app", "12.6.1"), component.App);

        var product = PkgMetadata.Read(new MemoryStream(ProductArchive("com.example.tool.pkg", "2.0", "Tool")));
        Assert.Equal(new AppBundleInfo("com.example.tool", "/Applications/Tool.app", null), product.App);
    }

    [Fact]
    public void APackageWhoseAppPathClimbsOutRecordsNoApp()
    {
        string info = """<pkg-info identifier="x" version="1" install-location="/Applications"><bundle path="../../etc/Evil.app" id="evil"/></pkg-info>""";
        Assert.Null(PkgMetadata.Read(new MemoryStream(Xar(new XarFile("PackageInfo", Encoding.UTF8.GetBytes(info))))).App);
    }

    private static byte[] FatHeader(params int[] cpuTypes)
    {
        byte[] header = new byte[8 + 20 * cpuTypes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)cpuTypes.Length);
        for (int i = 0; i < cpuTypes.Length; i++)
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8 + i * 20), cpuTypes[i]);
        return header;
    }

    [Fact]
    public void ExecutableArchitecturesComeFromTheMachOHeader()
    {
        Assert.Equal(["x86_64", "arm64"], AppBundle.ParseArchitectures(FatHeader(0x01000007, 0x0100000C)));
        byte[] thin = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(thin, 0xFEEDFACF);
        BinaryPrimitives.WriteInt32LittleEndian(thin.AsSpan(4), 0x0100000C);
        Assert.Equal(["arm64"], AppBundle.ParseArchitectures(thin));
        Assert.Empty(AppBundle.ParseArchitectures("#!/bin/sh\n"u8));
        Assert.Empty(AppBundle.ParseArchitectures([1, 2]));
    }

    [Fact]
    public void AnXmlPlistYieldsItsTopLevelStrings()
    {
        var values = AppBundle.ParseXmlPlist(Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>CFBundleIdentifier</key><string>com.example.app</string>
              <key>LSRequiresNativeExecution</key><true/>
              <key>CFBundleShortVersionString</key><string> 3.1 </string>
              <key>CFBundleURLTypes</key><array><dict><key>CFBundleURLName</key><string>nested</string></dict></array>
            </dict></plist>
            """));
        Assert.Equal(2, values.Count);
        Assert.Equal(("com.example.app", "3.1"), (values["CFBundleIdentifier"], values["CFBundleShortVersionString"]));
    }

    private string WriteApp(string root, string name, string id, string version, string? executableFrom = null)
    {
        string app = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(app, "Contents", "MacOS"));
        Directory.CreateDirectory(Path.Combine(app, "Contents", "Resources"));
        File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
            <key>CFBundleIdentifier</key><string>{id}</string>
            <key>CFBundleShortVersionString</key><string>{version}</string>
            <key>CFBundleExecutable</key><string>Main</string>
            <key>CFBundleIconFile</key><string>AppIcon</string>
            </dict></plist>
            """);
        if (executableFrom is not null)
            File.Copy(executableFrom, Path.Combine(app, "Contents", "MacOS", "Main"));
        return app;
    }

    [Fact]
    public void AnInstalledAppIsRecognizedByItsBundleIdAtItsPath()
    {
        string root = Path.Combine(_directory, "disk");
        WriteApp(Path.Combine(root, "Applications"), "GitKraken.app", "com.axosoft.gitkraken", "12.5.0");
        AppBundle.TestRootOverride = root;
        try
        {
            var expected = new AppBundleInfo("com.axosoft.gitkraken", "/Applications/GitKraken.app", "12.6.1");
            Assert.Equal("12.5.0", AppBundle.FindInstalled(expected)?.Version);
            Assert.Null(AppBundle.FindInstalled(expected with { BundleId = "com.other.app" }));
            Assert.Null(AppBundle.FindInstalled(expected with { Path = "/Applications/Missing.app" }));
        }
        finally { AppBundle.TestRootOverride = null; }
    }

    [Fact]
    public void AnIndexKeepsOnlyAbsoluteAppPaths()
    {
        string hash = new('a', 64);
        string Entry(string id, string path) =>
            $$$"""{"id":"{{{id}}}","name":"{{{id}}}","version":"1","file":"{{{id}}}.dmg","sha256":"{{{hash}}}","size":1,"app":{"id":"com.{{{id}}}","path":"{{{path}}}","version":"1"}}""";
        string json = $$"""{"version":1,"packages":[{{Entry("good", "/Applications/Good.app")}},{{Entry("relative", "Applications/R.app")}},{{Entry("climb", "/Applications/../etc/C.app")}},{{Entry("notapp", "/usr/local/bin/tool")}}]}""";
        var items = PkgFeed.ParseIndex(Encoding.UTF8.GetBytes(json), new Uri("https://h/index.json"));
        Assert.Equal(4, items.Count);
        Assert.Equal(new AppBundleInfo("com.good", "/Applications/Good.app", "1"), items[0].App);
        Assert.All(items.Skip(1), i => Assert.Null(i.App));
        Assert.All(items, i => Assert.Equal(FeedFileKind.DiskImage, i.Kind));
    }

    [Fact]
    public void WithoutAReceiptTheAppInPlaceMarksAPackageInstalled()
    {
        string feed = Path.Combine(_directory, "feed");
        string root = Path.Combine(_directory, "disk");
        string info = """
            <pkg-info identifier="com.axosoft.gitkraken.pkg" version="1.0" install-location="/Applications">
              <bundle path="./GitKraken.app" id="com.axosoft.gitkraken" CFBundleShortVersionString="12.6.1"/>
            </pkg-info>
            """;
        WritePackage("feed/GitKraken.pkg", Xar(new XarFile("PackageInfo", Encoding.UTF8.GetBytes(info))));
        WritePackage("feed/Other.pkg", ProductArchive("com.example.other.pkg", "1.0", "Other"));
        WriteApp(Path.Combine(root, "Applications"), "GitKraken.app", "com.axosoft.gitkraken", "12.5.0");
        string catalog = WritePackage("SoftwareCatalog.json", Encoding.UTF8.GetBytes($$"""
            {"version":1,"catalogs":[{"id":"mac","name":"Mac apps","packages":[],"pkgFeed":"{{feed.Replace("\\", "\\\\")}}"}]}
            """));

        var previousPkgUtil = PkgReceipts.RunPkgUtil;
        PkgReceipts.RunPkgUtil = (_, args) => args is ["--pkgs"] ? (0, "com.jamf.appinstallers.GitKraken\n") : (1, "");
        CatalogFeeds.TestCatalogPathOverride = catalog;
        AppBundle.TestRootOverride = root;
        try
        {
            var manager = new MacPkg();
            var installed = Assert.Single(manager.ListInstalled());
            // The package numbers itself 1.0, its app 12.6.1: the update compares the app's versions.
            Assert.Equal(("com.axosoft.gitkraken.pkg", "12.5.0", "12.6.1", true),
                (installed.Item.Id, installed.Installed, installed.Offered, installed.HasUpdate));
            var package = new PackageClasses.Package("GitKraken", installed.Item.Id, installed.Installed, installed.Source, manager);
            Assert.Equal("1.0", manager.FindFeedItem(package, "12.6.1")?.Version);
        }
        finally
        {
            PkgReceipts.RunPkgUtil = previousPkgUtil;
            CatalogFeeds.TestCatalogPathOverride = null;
            AppBundle.TestRootOverride = null;
        }
    }

    [Fact]
    public void WhenReceiptsCannotBeReadAppsAreStillRecognized()
    {
        string feed = Path.Combine(_directory, "feed");
        string root = Path.Combine(_directory, "disk");
        string info = """
            <pkg-info identifier="com.axosoft.gitkraken.pkg" version="12.6.1" install-location="/Applications">
              <bundle path="./GitKraken.app" id="com.axosoft.gitkraken" CFBundleShortVersionString="12.6.1"/>
            </pkg-info>
            """;
        WritePackage("feed/GitKraken.pkg", Xar(new XarFile("PackageInfo", Encoding.UTF8.GetBytes(info))));
        WriteApp(Path.Combine(root, "Applications"), "GitKraken.app", "com.axosoft.gitkraken", "12.6.1");
        string catalog = WritePackage("SoftwareCatalog.json", Encoding.UTF8.GetBytes($$"""
            {"version":1,"catalogs":[{"id":"mac","name":"Mac apps","packages":[],"pkgFeed":"{{feed.Replace("\\", "\\\\")}}"}]}
            """));

        var previousPkgUtil = PkgReceipts.RunPkgUtil;
        PkgReceipts.RunPkgUtil = (_, _) => (1, "");
        CatalogFeeds.TestCatalogPathOverride = catalog;
        AppBundle.TestRootOverride = root;
        try
        {
            var installed = Assert.Single(new MacPkg().ListInstalled());
            Assert.Equal(("com.axosoft.gitkraken.pkg", "12.6.1", false), (installed.Item.Id, installed.Installed, installed.HasUpdate));
        }
        finally
        {
            PkgReceipts.RunPkgUtil = previousPkgUtil;
            CatalogFeeds.TestCatalogPathOverride = null;
            AppBundle.TestRootOverride = null;
        }
    }

    [Fact]
    public async Task BinaryPlistsAreReadAndCorruptOnesFailCleanly()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        string plist = Path.Combine(_directory, "Info.plist");
        File.WriteAllText(plist, """<plist version="1.0"><dict><key>CFBundleIdentifier</key><string>com.example.binary</string></dict></plist>""");
        Run("/usr/bin/plutil", "-convert", "binary1", plist);
        Assert.Equal("com.example.binary", AppBundle.ParsePlist(File.ReadAllBytes(plist))["CFBundleIdentifier"]);

        byte[] corrupt = [.. "bplist00"u8.ToArray(), .. Enumerable.Repeat((byte)0xFF, 200_000)];
        var error = await Record.ExceptionAsync(() => Task.Run(() => AppBundle.ParsePlist(corrupt)).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.IsType<InvalidDataException>(error);
    }

    // ── Disk images (macOS only: they are mounted with hdiutil) ───────────

    private string BuildDiskImage(string name, string id, string version)
    {
        string source = Path.Combine(_directory, "dmg-" + version);
        string app = WriteApp(source, name, id, version, "/usr/bin/true");
        File.Copy("/System/Library/CoreServices/CoreTypes.bundle/Contents/Resources/GenericApplicationIcon.icns",
            Path.Combine(app, "Contents", "Resources", "AppIcon.icns"));
        WriteApp(source, "Uninstall Tool.app", id + ".uninstaller", version);
        File.CreateSymbolicLink(Path.Combine(source, "Applications"), "/Applications");
        string image = Path.Combine(_directory, $"{Path.GetFileNameWithoutExtension(name)}-{version}.dmg");
        Run("/usr/bin/hdiutil", "create", "-quiet", "-fs", "HFS+", "-format", "UDZO", "-srcfolder", source, "-volname", "Test", image);
        return image;
    }

    [Fact]
    public void ADiskImageIsDescribedByTheAppAtItsRoot()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        string image = BuildDiskImage("Example App.app", "com.example.dmgapp", "3.4.5");
        string icon = Path.Combine(_directory, "icons", "example.png");
        var metadata = PkgMetadata.Read(image, icon);
        Assert.Equal(("com.example.dmgapp", "3.4.5", "Example App"), (metadata.Id, metadata.Version, metadata.Title));
        Assert.Equal(new AppBundleInfo("com.example.dmgapp", "/Applications/Example App.app", "3.4.5"), metadata.App);
        Assert.Contains("arm64", metadata.Architectures);
        Assert.True(new FileInfo(icon).Length > 0);
        Assert.DoesNotContain(Directory.GetDirectories(Path.GetTempPath(), "UniGetUI-dmg-*"),
            d => Directory.EnumerateFileSystemEntries(d).Any());
    }

    [Fact]
    public async Task ParallelListingsReadTheSameDiskImage()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        // A second mount of an attached image fails as busy; the startup listings must not skip it.
        string image = BuildDiskImage("Parallel.app", "com.example.parallel", "1.0");
        var reads = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => PkgFeed.TryReadFile(image))));
        Assert.All(reads, metadata => Assert.Equal("com.example.parallel", metadata?.Id));
    }

    [Fact]
    public async Task ADiskImageAppReplacesTheInstalledOneInApplications()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        string image = BuildDiskImage("Example App.app", "com.example.dmgapp", "2.0");
        string root = Path.Combine(_directory, "disk");
        string installed = WriteApp(Path.Combine(root, "Applications"), "Example App.app", "com.example.dmgapp", "1.0");
        File.WriteAllText(Path.Combine(installed, "Contents", "Resources", "stale.txt"), "from 1.0");
        var item = new PkgFeedItem("com.example.dmgapp", "Example App", "2.0", new Uri(image), null, 0, [], null,
            new AppBundleInfo("com.example.dmgapp", "/Applications/Example App.app", "2.0"));

        var previousIsRoot = PkgInstaller.IsRoot;
        PkgInstaller.IsRoot = () => true;
        AppBundle.TestRootOverride = root;
        try
        {
            var log = new List<string>();
            // The test app is unsigned, so Gatekeeper rejects it and only the catalog opt-in lets it install.
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                PkgInstaller.InstallAsync(item, "Example App", log.Add, _ => { }, CancellationToken.None));
            var verdict = await PkgInstaller.InstallAsync(item, "Example App", log.Add, _ => { }, CancellationToken.None,
                allowUnsigned: true);
            Assert.True(verdict == Enums.OperationVeredict.Success, string.Join('\n', log));
            Assert.Equal("2.0", AppBundle.FindInstalled(item.App!)?.Version);
            Assert.False(File.Exists(Path.Combine(installed, "Contents", "Resources", "stale.txt")));
            Assert.Equal(["Example App.app"], Directory.GetFileSystemEntries(Path.Combine(root, "Applications")).Select(Path.GetFileName));

            Assert.Throws<InvalidDataException>(() => PkgInstaller.VerifyIdentity(item with { Version = "3.0" },
                PkgMetadata.Read(image)));
        }
        finally
        {
            PkgInstaller.IsRoot = previousIsRoot;
            AppBundle.TestRootOverride = null;
        }
    }

    // ── Payload icons ──────────────────────────────────────────────────────

    private static byte[] GzipCpio(params (string Name, byte[] Data)[] files)
    {
        static string O(long value, int width) => Convert.ToString(value, 8).PadLeft(width, '0');
        var cpio = new MemoryStream();
        void Entry(string name, byte[] data, int mode)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            string header = "070707" + O(0, 6) + O(0, 6) + O(mode, 6) + O(0, 6) + O(0, 6) + O(1, 6) + O(0, 6)
                + O(0, 11) + O(nameBytes.Length + 1, 6) + O(data.Length, 11);
            cpio.Write(Encoding.ASCII.GetBytes(header));
            cpio.Write(nameBytes);
            cpio.WriteByte(0);
            cpio.Write(data);
        }
        foreach (var (name, data) in files)
            Entry(name, data, Convert.ToInt32("100644", 8));
        Entry("TRAILER!!!", [], 0);
        var gzip = new MemoryStream();
        using (var stream = new GZipStream(gzip, CompressionLevel.Fastest, leaveOpen: true))
            stream.Write(cpio.ToArray());
        return gzip.ToArray();
    }

    private static byte[] Plist(string iconFile) => Encoding.UTF8.GetBytes(
        $"""<plist version="1.0"><dict><key>CFBundleIconFile</key><string>{iconFile}</string></dict></plist>""");

    [Fact]
    public void ThePayloadYieldsTheAppPlistAndItsTopLevelIcons()
    {
        byte[] payload = GzipCpio(
            ("./App.app/Contents/MacOS/App", new byte[5000]),
            ("./App.app/Contents/Resources/Other.icns", [1]),
            ("./App.app/Contents/Resources/Nested/Deep.icns", [2]),
            ("./App.app/Contents/Frameworks/Helper.app/Contents/Resources/AppIcon.icns", [3]),
            ("./App.app/Contents/Info.plist", Plist("AppIcon")),
            ("./App.app/Contents/Resources/AppIcon.icns", [4]),
            ("./App.app/Contents/Resources/Late.icns", [5]));
        var (plist, icons) = PkgPayload.FindAppResources(new MemoryStream(payload), "./App.app");
        Assert.NotNull(plist);
        // Stops once the named icon is found, so Late.icns is never read.
        Assert.Equal(["AppIcon.icns", "Other.icns"], icons.Keys.Order());
        Assert.Equal([4], icons["AppIcon.icns"]);
    }

    [Fact]
    public void APayloadOfManyIconsKeepsOnlyAFewAndTheNamedOne()
    {
        var files = Enumerable.Range(0, 100)
            .Select(i => ($"./App.app/Contents/Resources/Icon{i}.icns", new byte[] { (byte)i }))
            .Append(("./App.app/Contents/Info.plist", Plist("AppIcon")))
            .Append(("./App.app/Contents/Resources/AppIcon.icns", [42]))
            .ToArray();
        var (plist, icons) = PkgPayload.FindAppResources(new MemoryStream(GzipCpio(files)), "./App.app");
        Assert.NotNull(plist);
        Assert.Equal(33, icons.Count);
        Assert.Equal([42], icons["AppIcon.icns"]);

        // Checking an app's identity needs only its plist.
        var (identity, none) = PkgPayload.FindAppResources(new MemoryStream(GzipCpio(files)), "./App.app", keepIcons: false);
        Assert.NotNull(identity);
        Assert.Empty(none);
    }

    [Fact]
    public void APayloadThatIsNotGzipCpioYieldsNothing()
    {
        var (plist, icons) = PkgPayload.FindAppResources(new MemoryStream("pbzx not gzip"u8.ToArray()), "./App.app");
        Assert.Null(plist);
        Assert.Empty(icons);
    }

    [Fact]
    public void APackageWithoutAnIconIsReadOnce()
    {
        string info = """<pkg-info identifier="com.example.noicon.pkg" version="1" install-location="/Applications"><bundle path="./NoIcon.app" id="com.example.noicon"/></pkg-info>""";
        string file = WritePackage("NoIcon.pkg", Xar(
            new XarFile("PackageInfo", Encoding.UTF8.GetBytes(info)),
            new XarFile("Payload", GzipCpio(("./NoIcon.app/Contents/Info.plist", Plist("AppIcon"))), Compressed: false)));
        Assert.NotNull(PkgFeed.TryReadFile(file));
        Assert.Single(Directory.GetFiles(PkgFeed.TestIconDirectoryOverride!, "*.json"));
        // Listing does not search the payload; the first icon request does, and remembers it found none.
        Assert.Null(PkgFeed.FindIcon(file));
        Assert.Null(PkgFeed.FindOrExtractIcon(file));
        Assert.Single(Directory.GetFiles(PkgFeed.TestIconDirectoryOverride!, "*.png.none"));
        Assert.Null(PkgFeed.FindOrExtractIcon(file));
    }

    [Fact]
    public void APackageInstalledIntoItsOwnAppFolderIsThatApp()
    {
        // Devolutions Workspace installs to /Applications/<App>.app and lists only nested bundles.
        string info = """
            <pkg-info identifier="com.devolutions.workspace" version="2026.3.2" install-location="/Applications/Devolutions Password Manager.app">
              <bundle path="./Contents/Frameworks/Sparkle.framework/Versions/B/Updater.app" id="org.sparkle-project.Sparkle.Updater" CFBundleShortVersionString="2.9.1"/>
              <bundle path="./Contents/PlugIns/extension.appex" id="net.devolutions.authenticator.extension"/>
            </pkg-info>
            """;
        byte[] plist = Encoding.UTF8.GetBytes("""
            <plist version="1.0"><dict><key>CFBundleIdentifier</key><string>net.devolutions.authenticator</string>
            <key>CFBundleShortVersionString</key><string>2026.3.2</string></dict></plist>
            """);
        byte[] package = Xar(
            new XarFile("PackageInfo", Encoding.UTF8.GetBytes(info)),
            new XarFile("Payload", GzipCpio(("./Contents/Frameworks/Big.bin", new byte[100]), ("./Contents/Info.plist", plist)), Compressed: false));

        var metadata = PkgMetadata.Read(new MemoryStream(package));
        Assert.Equal("Devolutions Password Manager", metadata.Title);
        Assert.Equal(new AppBundleInfo("net.devolutions.authenticator", "/Applications/Devolutions Password Manager.app", "2026.3.2"),
            metadata.App);
    }

    [Fact]
    public void WhatWasReadFromAFileIsKeptOnDiskAcrossRestarts()
    {
        string file = WritePackage("App.pkg", ProductArchive("com.example.app.pkg", "1.0", "App"));
        Assert.Equal("App", PkgFeed.TryReadFile(file)?.Title);
        string saved = Assert.Single(Directory.GetFiles(PkgFeed.TestIconDirectoryOverride!, "*.json"));

        // A restart forgets the memory cache; the saved copy then answers without reading the file.
        File.WriteAllText(saved, File.ReadAllText(saved).Replace("\"App\"", "\"From disk\"", StringComparison.Ordinal));
        PkgFeed.ForgetReadFiles();
        Assert.Equal("From disk", PkgFeed.TryReadFile(file)?.Title);

        // A replaced file is read again.
        File.WriteAllBytes(file, ProductArchive("com.example.app.pkg", "2.0", "App"));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(("App", "2.0"), (PkgFeed.TryReadFile(file)?.Title, PkgFeed.TryReadFile(file)?.Version));
    }

    // ── Zip archives and disk images holding a package ──────────────────

    [Fact]
    public void TheAppInAZipIsFoundAtItsRootOrOneFolderDown()
    {
        static string P(string app) => app + "/Contents/Info.plist";
        Assert.Equal("Visual Studio Code.app", ZipApp.FindApp(
            [P("Visual Studio Code.app"), P("Visual Studio Code.app/Contents/Frameworks/Code Helper.app"),
             P("__MACOSX/._Visual Studio Code.app"), "Visual Studio Code.app/Contents/MacOS/Electron"]));
        Assert.Equal("Tool-1.0/Tool.app", ZipApp.FindApp([P("Tool-1.0/Tool.app"), P("Tool-1.0/Uninstall Tool.app")]));
        Assert.Throws<InvalidDataException>(() => ZipApp.FindApp([P("a/b/Deep.app")]));
        Assert.Throws<InvalidDataException>(() => ZipApp.FindApp([P("One.app"), P("Two.app")]));
        Assert.Throws<InvalidDataException>(() => ZipApp.FindApp(["readme.txt"]));
    }

    [Fact]
    public void AZipIsDescribedByItsAppWithoutBeingExtracted()
    {
        string zip = Path.Combine(_directory, "Tool.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            void Add(string name, byte[] data)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(data);
            }
            Add("Tool.app/Contents/Info.plist", Encoding.UTF8.GetBytes("""
                <plist version="1.0"><dict><key>CFBundleIdentifier</key><string>com.example.tool</string>
                <key>CFBundleShortVersionString</key><string>4.5</string><key>CFBundleExecutable</key><string>Tool</string>
                <key>CFBundleDisplayName</key><string>Example Tool</string></dict></plist>
                """));
            Add("Tool.app/Contents/MacOS/Tool", FatHeader(0x01000007, 0x0100000C));
            // A zip may repeat an entry name; reading the icon must not fail on it.
            Add("Tool.app/Contents/Resources/Tool.icns", [1, 2, 3]);
            Add("Tool.app/Contents/Resources/Tool.icns", [4, 5, 6]);
        }
        PkgMetadata.Read(zip, Path.Combine(_directory, "icons", "tool.png"));
        var metadata = PkgMetadata.Read(zip);
        Assert.Equal(("com.example.tool", "4.5", "Example Tool"), (metadata.Id, metadata.Version, metadata.Title));
        Assert.Equal(["x86_64", "arm64"], metadata.Architectures);
        Assert.Equal(new AppBundleInfo("com.example.tool", "/Applications/Tool.app", "4.5"), metadata.App);
        Assert.False(metadata.WrapsPackage);
    }

    [Theory]
    [InlineData("App.pkg", false, false)]
    [InlineData("App.dmg", false, true)]
    [InlineData("App.dmg", true, false)]
    [InlineData("App.zip", false, true)]
    public void OnlyAnAppCopiedIntoPlaceIsRecognizedWithoutAReceipt(string file, bool wrapsPackage, bool copiesAnApp) =>
        Assert.Equal(copiesAnApp, new PkgFeedItem("id", "App", "1", new Uri("https://h/" + file), null, 0, [], null,
            null, wrapsPackage).CopiesAnApp);

    [Fact]
    public async Task AZippedAppIsInstalledWithItsSymlinks()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        string source = Path.Combine(_directory, "zip-source");
        string app = WriteApp(source, "Zipped.app", "com.example.zipped", "2.0", "/usr/bin/true");
        Directory.CreateDirectory(Path.Combine(app, "Contents", "Frameworks", "Lib.framework", "Versions", "A"));
        File.CreateSymbolicLink(Path.Combine(app, "Contents", "Frameworks", "Lib.framework", "Versions", "Current"), "A");
        string zip = Path.Combine(_directory, "feed", "Zipped-2.0.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        Run("/usr/bin/ditto", "-c", "-k", "--keepParent", app, zip);

        var metadata = PkgMetadata.Read(zip);
        Assert.Equal(("com.example.zipped", "2.0"), (metadata.Id, metadata.Version));
        var item = new PkgFeedItem(metadata.Id, "Zipped", metadata.Version, new Uri(zip), null, 0, [], null, metadata.App);

        string root = Path.Combine(_directory, "disk");
        var previousIsRoot = PkgInstaller.IsRoot;
        PkgInstaller.IsRoot = () => true;
        AppBundle.TestRootOverride = root;
        try
        {
            var log = new List<string>();
            Assert.Equal(Enums.OperationVeredict.Success, await PkgInstaller.InstallAsync(item, "Zipped", log.Add, _ => { },
                CancellationToken.None, allowUnsigned: true));
            string installed = Path.Combine(root, "Applications", "Zipped.app");
            Assert.Equal("2.0", AppBundle.FindInstalled(item.App!)?.Version);
            Assert.Equal("A", new DirectoryInfo(Path.Combine(installed, "Contents", "Frameworks", "Lib.framework", "Versions", "Current")).LinkTarget);
        }
        finally
        {
            PkgInstaller.IsRoot = previousIsRoot;
            AppBundle.TestRootOverride = null;
        }
    }

    [Fact]
    public async Task ADiskImageHoldingAPackageIsThatPackage()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        string payload = Path.Combine(_directory, "pkgroot", "Applications", "Wrapped.app", "Contents");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "Info.plist"),
            """<plist version="1.0"><dict><key>CFBundleIdentifier</key><string>com.example.wrapped</string><key>CFBundleShortVersionString</key><string>3.0</string></dict></plist>""");
        string imageSource = Path.Combine(_directory, "image");
        Directory.CreateDirectory(imageSource);
        Run("/usr/bin/pkgbuild", "--root", Path.Combine(_directory, "pkgroot"), "--identifier", "com.example.wrapped.pkg",
            "--version", "3.0", "--install-location", "/", Path.Combine(imageSource, "Install Wrapped.pkg"));
        WriteApp(imageSource, "Uninstall Wrapped.app", "com.example.wrapped.uninstaller", "3.0");
        string image = Path.Combine(_directory, "Wrapped.dmg");
        Run("/usr/bin/hdiutil", "create", "-quiet", "-fs", "HFS+", "-format", "UDZO", "-srcfolder", imageSource, "-volname", "Wrapped", image);

        var metadata = PkgMetadata.Read(image);
        Assert.Equal(("com.example.wrapped.pkg", "3.0", true), (metadata.Id, metadata.Version, metadata.WrapsPackage));
        Assert.Equal(new AppBundleInfo("com.example.wrapped", "/Applications/Wrapped.app", "3.0"), metadata.App);

        var item = new PkgFeedItem(metadata.Id, "Wrapped", "3.0", new Uri(image), null, 0, [], null, metadata.App, true);
        var previous = PkgInstaller.RunProcess;
        var previousIsRoot = PkgInstaller.IsRoot;
        string? command = null;
        bool packageWasMounted = false;
        PkgInstaller.IsRoot = () => true;
        PkgInstaller.RunProcess = (file, args, _) =>
        {
            if (file == "/bin/sh")
            {
                command = args[1];
                string package = StagedPackage(command);
                packageWasMounted = File.Exists(package) && package.Contains("UniGetUI-dmg", StringComparison.Ordinal);
            }
            return Task.FromResult((0, "accepted"));
        };
        try
        {
            Assert.Equal(Enums.OperationVeredict.Success,
                await PkgInstaller.InstallAsync(item, "Wrapped", _ => { }, _ => { }, CancellationToken.None));
            Assert.Contains("/usr/sbin/installer -pkg ", command);
            Assert.EndsWith("Install Wrapped.pkg' -target / 2>&1", command.Replace("'\\''", "'").TrimEnd('\''));
            Assert.True(packageWasMounted);
        }
        finally
        {
            PkgInstaller.RunProcess = previous;
            PkgInstaller.IsRoot = previousIsRoot;
        }
    }

    [Fact]
    public void AFeedPackageHasNoStandaloneCommandLine()
    {
        var manager = new MacPkg();
        var source = new Classes.Manager.ManagerSource(manager, "mac", new Uri("https://h/"));
        var package = new PackageClasses.Package("App", "com.example.app.pkg", "1.0", source, manager);
        Assert.Throws<InvalidOperationException>(() =>
            manager.OperationHelper.GetStandaloneParameters(package, new Serializable.InstallOptions(), Enums.OperationType.Install));
    }

    [Fact]
    public void AnUpdateWithoutANewerVersionInstallsTheNewestPublished()
    {
        var manager = new MacPkg();
        var source = new Classes.Manager.ManagerSource(manager, "mac", new Uri("https://h/"));
        var options = new Serializable.InstallOptions();
        Interfaces.IPackage Upgradable(string installed, string offered) =>
            new PackageClasses.Package("App", "com.example.app.pkg", installed, offered, source, manager);
        Assert.Equal("2.0", MacPkgOperationHelper.TargetVersion(Upgradable("1.0", "2.0"), options, Enums.OperationType.Update));
        Assert.Null(MacPkgOperationHelper.TargetVersion(Upgradable("1.0", "1.0"), options, Enums.OperationType.Update));
        Assert.Null(MacPkgOperationHelper.TargetVersion(Upgradable("1.0", "1.0"), options, Enums.OperationType.Install));
        options.Version = "1.5";
        Assert.Equal("1.5", MacPkgOperationHelper.TargetVersion(Upgradable("1.0", "1.0"), options, Enums.OperationType.Install));
    }

    // ── Root helper ────────────────────────────────────────────────────────

    // The installer's package path in a root command, which runs inside the quoted lockf command.
    private static string StagedPackage(string command) =>
        System.Text.RegularExpressions.Regex.Match(command.Replace("'\\''", "'"), "-pkg '([^']+)'").Groups[1].Value;

    private string HelperCatalog(string feed) => WritePackage("SoftwareCatalog.json", Encoding.UTF8.GetBytes($$"""
        {"version":1,"catalogs":[{"id":"mac","name":"Mac apps","packages":[],"pkgFeed":"{{feed.Replace("\\", "\\\\")}}"}]}
        """));

    [Fact]
    public async Task AStandardUserInstallsThroughTheHelperWithoutAPrompt()
    {
        if (OperatingSystem.IsWindows())
            return;

        string feed = Path.Combine(_directory, "feed");
        WritePackage("feed/App.pkg", ProductArchive("com.example.app.pkg", "1.0", "App"));
        string socket = Path.Combine("/tmp", $"ugpkg-{Guid.NewGuid():N}"[..20] + ".sock");
        var previous = (PkgInstaller.RunProcess, PkgInstaller.IsRoot, PkgHelperClient.SocketPath);
        var commands = new List<string>();
        CatalogFeeds.TestCatalogPathOverride = HelperCatalog(feed);
        TrustedPath.TestOverride = _ => null;
        PkgHelperClient.SocketPath = socket;
        PkgInstaller.IsRoot = () => false;
        PkgInstaller.RunProcess = (file, args, _) =>
        {
            lock (commands) commands.Add(file + " " + string.Join(' ', args));
            return Task.FromResult((0, file == "/bin/sh" ? "installer: The install was successful." : "accepted"));
        };
        using var stop = new CancellationTokenSource();
        var server = PkgHelperCommand.ServeAsync(socket, _ => { }, stop.Token);
        try
        {
            var item = Assert.Single(PkgFeed.List(new Uri(feed)));
            var log = new List<string>();
            Assert.Equal(Enums.OperationVeredict.Success,
                await PkgInstaller.InstallAsync(item, "App", log.Add, _ => { }, CancellationToken.None, feedId: "mac"));
            Assert.Contains(log, l => l.Contains("package helper", StringComparison.Ordinal));
            Assert.Contains("installer: The install was successful.", log);
            Assert.DoesNotContain(commands, c => c.StartsWith("/usr/bin/osascript", StringComparison.Ordinal));
            Assert.Contains(commands, c => c.StartsWith("/bin/sh -c ", StringComparison.Ordinal) && c.Contains("/usr/sbin/installer -pkg ", StringComparison.Ordinal));
        }
        finally
        {
            stop.Cancel();
            await server;
            (PkgInstaller.RunProcess, PkgInstaller.IsRoot, PkgHelperClient.SocketPath) = previous;
            CatalogFeeds.TestCatalogPathOverride = null;
            TrustedPath.TestOverride = null;
        }
        Assert.False(File.Exists(socket));
    }

    [Fact]
    public async Task TheHelperRefusesWhatTheCatalogDoesNotPublishFromAProtectedPlace()
    {
        string feed = Path.Combine(_directory, "feed");
        WritePackage("feed/App.pkg", ProductArchive("com.example.app.pkg", "1.0", "App"));
        CatalogFeeds.TestCatalogPathOverride = HelperCatalog(feed);
        var previous = PkgInstaller.RunProcess;
        bool ran = false;
        PkgInstaller.RunProcess = (_, _, _) => { ran = true; return Task.FromResult((0, "")); };
        try
        {
            static async Task<string?> Ask(string catalog, string id, string version = "") =>
                (await PkgHelperCommand.InstallAsync(new PkgHelperRequest { Catalog = catalog, Id = id, Version = version }, _ => { })).Result;

            TrustedPath.TestOverride = _ => null;
            Assert.Equal(PkgHelperMessage.Refused, await Ask("other", "com.example.app.pkg"));
            Assert.Equal(PkgHelperMessage.Refused, await Ask("mac", "com.example.missing.pkg"));
            Assert.Equal(PkgHelperMessage.Refused, await Ask("mac", "com.example.app.pkg", "9.9"));
            Assert.Equal(PkgHelperMessage.Refused, await Ask("mac", "com.example.app.pkg\n--evil"));

            TrustedPath.TestOverride = path => path == feed ? "any user can change it" : null;
            Assert.Equal(PkgHelperMessage.Refused, await Ask("mac", "com.example.app.pkg"));
            TrustedPath.TestOverride = path => path.EndsWith("SoftwareCatalog.json", StringComparison.Ordinal) ? "any user can change it" : null;
            Assert.Equal(PkgHelperMessage.Refused, await Ask("mac", "com.example.app.pkg"));
            Assert.False(ran);

            TrustedPath.TestOverride = _ => null;
            Assert.Equal(PkgHelperMessage.Success, await Ask("mac", "com.example.app.pkg", "1.0"));
            Assert.True(ran);

            // A loopback port is free for any user to take, so a web feed must be https for the helper.
            ran = false;
            CatalogFeeds.TestCatalogPathOverride = WritePackage("http-catalog.json", Encoding.UTF8.GetBytes("""
                {"version":1,"catalogs":[{"id":"web","name":"Web","packages":[],"pkgFeed":"http://127.0.0.1:9/","allowUnsignedPackages":true}]}
                """));
            var refusal = await PkgHelperCommand.InstallAsync(new PkgHelperRequest { Catalog = "web", Id = "com.example.app.pkg" }, _ => { });
            Assert.Equal(PkgHelperMessage.Refused, refusal.Result);
            Assert.Contains("https", refusal.Message);
            Assert.False(ran);
        }
        finally
        {
            PkgInstaller.RunProcess = previous;
            CatalogFeeds.TestCatalogPathOverride = null;
            TrustedPath.TestOverride = null;
        }
    }

    [Theory]
    [InlineData("""{"catalog":"mac","id":"com.example.app.pkg"}""", "success")]
    [InlineData("""{"catalog":"mac","id":"com.example.app.pkg","version":null}""", "success")]
    [InlineData("""{"catalog":null,"id":"com.example.app.pkg"}""", "refused")]
    public async Task ARequestAsSentOverTheSocketIsAnsweredWhateverItOmits(string json, string expected)
    {
        // Requests are deserialized by the source generator, which leaves absent values null.
        string feed = Path.Combine(_directory, "feed");
        WritePackage("feed/App.pkg", ProductArchive("com.example.app.pkg", "1.0", "App"));
        CatalogFeeds.TestCatalogPathOverride = HelperCatalog(feed);
        TrustedPath.TestOverride = _ => null;
        var previous = PkgInstaller.RunProcess;
        PkgInstaller.RunProcess = (_, _, _) => Task.FromResult((0, "accepted"));
        try
        {
            var request = System.Text.Json.JsonSerializer.Deserialize(json, PkgHelperJsonContext.Default.PkgHelperRequest)!;
            Assert.Equal(expected, (await PkgHelperCommand.InstallAsync(request, _ => { })).Result);
        }
        finally
        {
            PkgInstaller.RunProcess = previous;
            CatalogFeeds.TestCatalogPathOverride = null;
            TrustedPath.TestOverride = null;
        }
    }

    [Fact]
    public async Task WhenTheHelperDoesNotAnswerTheAdministratorPromptIsUsed()
    {
        if (OperatingSystem.IsWindows())
            return;

        string source = WritePackage("feed/App.pkg", ProductArchive("com.example.app.pkg", "1.0"));
        var item = new PkgFeedItem("com.example.app.pkg", "App", "1.0", new Uri(source), null, 0, [], null);
        string deadSocket = WritePackage("dead.sock", []);
        var previous = (PkgInstaller.RunProcess, PkgInstaller.IsRoot, PkgHelperClient.SocketPath);
        string? ran = null;
        PkgHelperClient.SocketPath = deadSocket;
        PkgInstaller.IsRoot = () => false;
        PkgInstaller.RunProcess = (file, _, _) =>
        {
            if (file != PkgInstaller.SpctlPath) ran = file;
            return Task.FromResult((0, "accepted"));
        };
        try
        {
            var log = new List<string>();
            Assert.Equal(Enums.OperationVeredict.Success,
                await PkgInstaller.InstallAsync(item, "App", log.Add, _ => { }, CancellationToken.None, feedId: "mac"));
            Assert.Equal("/usr/bin/osascript", ran);
            Assert.Contains(log, l => l.Contains("did not install this package", StringComparison.Ordinal));
        }
        finally
        {
            (PkgInstaller.RunProcess, PkgInstaller.IsRoot, PkgHelperClient.SocketPath) = previous;
        }
    }

    [Fact]
    public async Task WhenTheHelperRefusesTheAdministratorPromptIsOffered()
    {
        if (OperatingSystem.IsWindows())
            return;

        // The helper refuses an unprotected feed; an administrator can still approve the install.
        string feed = Path.Combine(_directory, "feed");
        WritePackage("feed/App.pkg", ProductArchive("com.example.app.pkg", "1.0", "App"));
        string socket = Path.Combine("/tmp", $"ugpkg-{Guid.NewGuid():N}"[..20] + ".sock");
        var previous = (PkgInstaller.RunProcess, PkgInstaller.IsRoot, PkgHelperClient.SocketPath);
        var ran = new List<string>();
        CatalogFeeds.TestCatalogPathOverride = HelperCatalog(feed);
        TrustedPath.TestOverride = path => path == feed ? "any user can change it." : null;
        PkgHelperClient.SocketPath = socket;
        PkgInstaller.IsRoot = () => false;
        PkgInstaller.RunProcess = (file, _, _) =>
        {
            lock (ran) ran.Add(file);
            return Task.FromResult((0, "accepted"));
        };
        using var stop = new CancellationTokenSource();
        var server = PkgHelperCommand.ServeAsync(socket, _ => { }, stop.Token);
        try
        {
            var item = Assert.Single(PkgFeed.List(new Uri(feed)));
            var log = new List<string>();
            Assert.Equal(Enums.OperationVeredict.Success,
                await PkgInstaller.InstallAsync(item, "App", log.Add, _ => { }, CancellationToken.None, feedId: "mac"));
            Assert.Contains(log, l => l.Contains("not protected", StringComparison.Ordinal));
            Assert.Contains("/usr/bin/osascript", ran);
        }
        finally
        {
            stop.Cancel();
            await server;
            (PkgInstaller.RunProcess, PkgInstaller.IsRoot, PkgHelperClient.SocketPath) = previous;
            CatalogFeeds.TestCatalogPathOverride = null;
            TrustedPath.TestOverride = null;
        }
    }

    [Fact]
    public void OnlyPathsStandardUsersCannotChangeAreTrusted()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        Assert.Null(TrustedPath.WhyUntrusted("/usr/bin/true"));
        Assert.Contains("any user", TrustedPath.WhyUntrusted("/Users/Shared"));
        Assert.Contains("could not be inspected", TrustedPath.WhyUntrusted(Path.Combine(_directory, "missing")));
    }

    [Fact]
    public void ALinkIsOnlyTrustedWhereNoStandardUserCanRepointIt()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        string open = Directory.CreateDirectory(Path.Combine(_directory, "open")).FullName;
        File.SetUnixFileMode(open, (UnixFileMode)0b111_111_111);
        File.CreateSymbolicLink(Path.Combine(open, "feed"), "/usr/bin");
        Assert.Null(TrustedPath.WhyUntrusted("/usr/bin/true"));
        Assert.NotNull(TrustedPath.WhyUntrusted(Path.Combine(open, "feed", "true")));
        // Links in protected places, such as /var and /tmp, are followed and their targets checked.
        Assert.Null(TrustedPath.WhyUntrusted("/var/db"));
        Assert.Contains("any user", TrustedPath.WhyUntrusted("/tmp"));
    }

    [Fact]
    public void AnAccessEntryThatLetsOthersChangeAPathMakesItUntrusted()
    {
        Assert.Null(TrustedPath.WhyAccessListAllowsChanges("drwxr-x---+ 5 me staff 160 Oct 26 16:18 /Users/me\n 0: group:everyone deny delete\n"));
        Assert.Null(TrustedPath.WhyAccessListAllowsChanges("-rw-r--r--+ 1 root wheel 2 Oct 26 16:18 /f\n 0: user:pat allow read,readattr\n"));
        Assert.Null(TrustedPath.WhyAccessListAllowsChanges("drwxr-xr-x+ 3 root admin 96 Oct 26 16:18 /d\n 0: group:admin allow add_file,delete_child\n"));
        Assert.Equal("0: user:pat inherited allow write,append",
            TrustedPath.WhyAccessListAllowsChanges("-rw-r--r--+ 1 root wheel 2 Oct 26 16:18 /f\n 0: user:pat inherited allow write,append\n"));

        if (!OperatingSystem.IsMacOS())
            return;
        string folder = Directory.CreateDirectory(Path.Combine(_directory, "acl")).FullName;
        File.SetUnixFileMode(folder, (UnixFileMode)0b111_101_101);
        Assert.Equal(0, Tool.Run("/bin/chmod", ["+a", "everyone allow add_file", folder]).ExitCode);
        Assert.Contains("access control entry", TrustedPath.WhyUntrusted(folder));
    }

    [Fact]
    public void TheDaemonDefinitionRunsTheHelperFromThisExecutable()
    {
        string plist = PkgHelperCommand.DaemonPlist("/Applications/UniGetUI & Co.app/Contents/MacOS/UniGetUI");
        Assert.Contains("<string>/Applications/UniGetUI &amp; Co.app/Contents/MacOS/UniGetUI</string>", plist);
        Assert.Contains("<string>pkg-helper</string>", plist);
        Assert.Contains("<string>serve</string>", plist);
        Assert.Equal("com.devolutions.unigetui.pkghelper", AppBundle.ParseXmlPlist(Encoding.UTF8.GetBytes(plist))["Label"]);
        Assert.Equal(2, PkgHelperCommand.Run([], new StringWriter(), new StringWriter()));
    }

    // ── Feed preview ───────────────────────────────────────────────────────

    [Fact]
    public void TheFeedPreviewListsPackagesAndExplainsSkippedFiles()
    {
        string feed = Path.Combine(_directory, "preview");
        WritePackage("preview/App-1.0.pkg", ProductArchive("com.example.app.pkg", "1.0", "App", "arm64"));
        WritePackage("preview/sub/Broken.pkg", Encoding.UTF8.GetBytes("not a package"));
        TrustedPath.TestOverride = path => path == feed ? "any user can change it." : null;
        try
        {
            var inspection = PkgFeedInspector.Inspect(feed);
            var package = Assert.Single(inspection.Packages);
            Assert.Equal(("App", "com.example.app.pkg", "1.0", "App-1.0.pkg", ".pkg"),
                (package.Name, package.Id, package.Version, package.File, package.Kind));
            Assert.Equal(["arm64"], package.Architectures);
            var problem = Assert.Single(inspection.Problems);
            Assert.Equal(Path.Combine("sub", "Broken.pkg"), problem.File);
            Assert.Contains("not a flat installer package", problem.Reason);
            if (OperatingSystem.IsMacOS())
                Assert.Contains("package helper will not install from it", inspection.Warning);
        }
        finally { TrustedPath.TestOverride = null; }

        Assert.False(PkgFeedInspector.IsValidLocation("http://software.example.com/"));
        Assert.Throws<InvalidDataException>(() => PkgFeedInspector.Inspect("relative/folder"));
        Assert.Throws<DirectoryNotFoundException>(() => PkgFeedInspector.Inspect(Path.Combine(_directory, "unmounted")));
    }

    // ── Real packages ──────────────────────────────────────────────────────

    [Fact]
    public void PackagesBuiltByApplesToolsAreRead()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        string root = Path.Combine(_directory, "root", "Applications", "Hello.app", "Contents");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Info.plist"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict><key>CFBundleIdentifier</key><string>com.example.hello</string>
            <key>CFBundleIconFile</key><string>Hello</string></dict></plist>
            """);
        Directory.CreateDirectory(Path.Combine(root, "Resources"));
        File.Copy("/System/Library/CoreServices/CoreTypes.bundle/Contents/Resources/GenericApplicationIcon.icns",
            Path.Combine(root, "Resources", "Hello.icns"));
        string component = Path.Combine(_directory, "component.pkg");
        string product = Path.Combine(_directory, "Hello.pkg");
        Run("/usr/bin/pkgbuild", "--root", Path.Combine(_directory, "root"), "--identifier", "com.example.hello.pkg",
            "--version", "1.2.0", "--install-location", "/", component);
        Run("/usr/bin/productbuild", "--package", component, product);

        Assert.Equal(("com.example.hello.pkg", "1.2.0", "Hello"),
            (PkgMetadata.Read(component).Id, PkgMetadata.Read(component).Version, PkgMetadata.Read(component).Title));
        string icon = Path.Combine(_directory, "read-icon.png");
        var metadata = PkgMetadata.Read(product, icon);
        Assert.Equal(("com.example.hello.pkg", "1.2.0", "Hello"), (metadata.Id, metadata.Version, metadata.Title));
        Assert.True(new FileInfo(icon).Length > 0, "The icon was not extracted from the payload.");

        // In a feed, listing leaves the payload alone and the first icon request extracts it.
        Assert.NotNull(PkgFeed.TryReadFile(product));
        Assert.Null(PkgFeed.FindIcon(product));
        Assert.True(new FileInfo(PkgFeed.FindOrExtractIcon(product)!).Length > 0);
    }

    private static void Run(string file, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}

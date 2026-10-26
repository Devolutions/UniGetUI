using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using UniGetUI.PackageEngine.Managers.MacPkgManager;
using UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

namespace UniGetUI.PackageEngine.Tests;

public sealed class MacPkgTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), nameof(MacPkgTests), Guid.NewGuid().ToString("N"));

    public MacPkgTests()
    {
        Directory.CreateDirectory(_directory);
        PkgFeed.TestIconDirectoryOverride = Path.Combine(_directory, "extracted-icons");
    }

    public void Dispose()
    {
        PkgFeed.TestIconDirectoryOverride = null;
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
        Assert.Equal(accepted, PkgFeed.TryParseLocation(text, out _));

    [Fact]
    public void AnAbsolutePathIsAFolderFeed()
    {
        Assert.True(PkgFeed.TryParseLocation(_directory, out Uri location));
        Assert.True(location.IsFile);
        Assert.Equal(Path.GetFullPath(_directory), location.LocalPath);
    }

    [Theory]
    [InlineData("https://h/mac/", "https://h/mac/index.json")]
    [InlineData("https://h/mac", "https://h/mac/index.json")]
    [InlineData("https://h/mac/catalog.json", "https://h/mac/catalog.json")]
    public void TheIndexSitsBesideThePackages(string location, string index) =>
        Assert.Equal(index, PkgFeed.IndexUrl(new Uri(location)).AbsoluteUri);

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
        Assert.Equal(resolved, PkgFeed.ResolveWithin(new Uri("https://h/mac/index.json"), relative)?.AbsoluteUri);

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
        Assert.Equal(PkgFeed.ComputeSha256(app), appItem.Sha256);
        Assert.Equal(["arm64"], appItem.Architectures);
        Assert.Equal("https://h/feed/App.png", appItem.Icon?.AbsoluteUri);
        var tool = items.Single(i => i.Id == "com.example.tool.pkg");
        Assert.Equal(("Tool", "2.0", "https://h/feed/tools/Tool%202.pkg"), (tool.Name, tool.Version, tool.Location.AbsoluteUri));
    }

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
        var feeds = PkgCatalogFeeds.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal(["a", "c", "d"], feeds.Select(f => f.Id));
        Assert.Equal(["Mac apps", "Local", "Mac apps"], feeds.Select(f => f.Name));
        Assert.Equal("https://software.example.com/mac/", feeds[0].Location.AbsoluteUri);
        Assert.Equal("https://other.example.com/", feeds[2].Location.AbsoluteUri);
        Assert.True(feeds[1].Location.IsFile);
    }

    [Fact]
    public void AMissingOrBrokenCatalogDeclaresNoFeeds()
    {
        Assert.Empty(PkgCatalogFeeds.Load(Path.Combine(_directory, "missing.json")));
        string broken = WritePackage("broken.json", Encoding.UTF8.GetBytes("{ not json"));
        Assert.Empty(PkgCatalogFeeds.Load(broken));
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
        string hash = PkgFeed.ComputeSha256(staged);
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
            staged = System.Text.RegularExpressions.Regex.Match(args[1], "-pkg '([^']+)'").Groups[1].Value;
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
        var feeds = PkgCatalogFeeds.Parse(Encoding.UTF8.GetBytes("""
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
        PkgCatalogFeeds.TestCatalogPathOverride = catalog;
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
            PkgCatalogFeeds.TestCatalogPathOverride = null;
        }
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
        Assert.All(items, i => Assert.True(i.IsDiskImage));
    }

    [Fact]
    public void WithoutAReceiptTheAppInPlaceMarksAPackageInstalled()
    {
        string feed = Path.Combine(_directory, "feed");
        string root = Path.Combine(_directory, "disk");
        string info = """
            <pkg-info identifier="com.axosoft.gitkraken.pkg" version="12.6.1" install-location="/Applications">
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
        PkgCatalogFeeds.TestCatalogPathOverride = catalog;
        AppBundle.TestRootOverride = root;
        try
        {
            var installed = Assert.Single(new MacPkg().ListInstalled());
            Assert.Equal(("com.axosoft.gitkraken.pkg", "12.5.0", true), (installed.Item.Id, installed.Installed, installed.HasUpdate));
        }
        finally
        {
            PkgReceipts.RunPkgUtil = previousPkgUtil;
            PkgCatalogFeeds.TestCatalogPathOverride = null;
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
        PkgCatalogFeeds.TestCatalogPathOverride = catalog;
        AppBundle.TestRootOverride = root;
        try
        {
            var installed = Assert.Single(new MacPkg().ListInstalled());
            Assert.Equal(("com.axosoft.gitkraken.pkg", "12.6.1", false), (installed.Item.Id, installed.Installed, installed.HasUpdate));
        }
        finally
        {
            PkgReceipts.RunPkgUtil = previousPkgUtil;
            PkgCatalogFeeds.TestCatalogPathOverride = null;
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
        Assert.DoesNotContain(Directory.GetDirectories(Path.Combine(Path.GetTempPath(), "UniGetUI-dmg")),
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

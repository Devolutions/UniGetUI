#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UniGetUI.PackageEngine.Enums;
using Architecture = System.Runtime.InteropServices.Architecture;
using UniGetUI.PackageEngine.Managers.InstallerFeeds;
using UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;
using UniGetUI.PackageEngine.Managers.WinInstallerManager;
using UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal;

namespace UniGetUI.PackageEngine.Tests;

[Collection("Installer feeds")]
public sealed class WinInstallerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), nameof(WinInstallerTests), Guid.NewGuid().ToString("N"));
    private readonly List<InstallerProcess> _started = [];
    private int _exitCode;

    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private const string UpgradeCode = "{11111111-2222-3333-4444-555555555555}";
    private const string ProductCode = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}";

    public WinInstallerTests()
    {
        Directory.CreateDirectory(_directory);
        WinFeed.TestIconDirectoryOverride = Path.Combine(_directory, "icons");
        InstallReceipts.TestPathOverride = Path.Combine(_directory, "receipts.json");
        WinFeed.ForgetReadFiles();
        WinFeed.ForgetCachedIndexes();
        WinInstallerRunner.IsAdministrator = () => false;
        WinInstallerRunner.RunProcess = process =>
        {
            _started.Add(process);
            return Task.FromResult(_exitCode);
        };
        WinInstallerRunner.UninstallPollInterval = TimeSpan.FromMilliseconds(10);
        WinInstallerRunner.UninstallSettleTime = TimeSpan.FromMilliseconds(100);
        Authenticode.Verify = _ => new SignatureCheck(0, "Example Corp");
        InstalledMatcher.RelatedProducts = _ => [];
        ArpEntries.Source = () => [];
    }

    public void Dispose()
    {
        WinFeed.TestIconDirectoryOverride = null;
        InstallReceipts.TestPathOverride = null;
        CatalogFeeds.TestCatalogPathOverride = null;
        WinInstallerRunner.IsAdministrator = UniGetUI.Core.Tools.CoreTools.IsAdministrator;
        WinInstallerRunner.RunProcess = WinInstallerRunner.RunProcessAsync;
        Authenticode.Verify = Authenticode.VerifyFile;
        InstalledMatcher.RelatedProducts = MsiDatabase.RelatedProducts;
        ArpEntries.Source = ArpEntries.ReadRegistry;
        WinFeed.ForgetReadFiles();
        WinFeed.ForgetCachedIndexes();
        Directory.Delete(_directory, recursive: true);
    }

    private string WriteFile(string relative, byte[] contents)
    {
        string path = Path.Combine(_directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
        return path;
    }

    private static byte[] NsisOverlay()
    {
        byte[] header = new byte[28];
        BitConverter.GetBytes(0xDEADBEEF).CopyTo(header, 4);
        Encoding.ASCII.GetBytes("NullsoftInst").CopyTo(header, 8);
        return header;
    }

    private string Installer(string relative, byte[]? overlay = null) =>
        WriteFile(relative, [.. File.ReadAllBytes(Cmd), .. overlay ?? NsisOverlay()]);

    private string Manifest(string installer, string json) => WriteFile(Path.ChangeExtension(Path.GetRelativePath(_directory, installer), ".json"),
        Encoding.UTF8.GetBytes(json));

    private static string CmdVersion() => InstallerFile.CleanVersion(PeReader.Read(Cmd).String("ProductVersion"))!;

    private static ArpEntry Entry(string key, string? name = null, string? version = null, string? publisher = null,
        bool perMachine = true, bool msi = false, bool hidden = false, string? uninstall = null, string? quiet = null,
        params string[] bundleCodes) =>
        new(key, perMachine, name, version, publisher, uninstall, quiet, msi, hidden, bundleCodes);

    private static WinFeedItem Item(string id, string name, string version, InstallerDetails details, params string[] architectures) =>
        new(id, name, version, new Uri(@"C:\feed\" + id + (details.Kind == InstallerKind.Msi ? ".msi" : ".exe")), null, 1,
            architectures, null, details);

    [Fact]
    public void ThePeReaderAgreesWithWindowsOnVersionResources()
    {
        foreach (string file in new[] { Environment.ProcessPath!, typeof(object).Assembly.Location, typeof(WinInstallerTests).Assembly.Location })
        {
            var pe = PeReader.Read(file);
            var expected = FileVersionInfo.GetVersionInfo(file);
            Assert.Equal(expected.ProductName, pe.String("ProductName"));
            Assert.Equal(expected.CompanyName, pe.String("CompanyName"));
            Assert.Equal(expected.ProductVersion, pe.String("ProductVersion"));
            Assert.Equal($"{expected.FileMajorPart}.{expected.FileMinorPart}.{expected.FileBuildPart}.{expected.FilePrivatePart}",
                pe.FixedFileVersion);
        }
    }

    [Fact]
    public void ThePeReaderReportsTheMachineAndTheLargestIcon()
    {
        var pe = PeReader.Read(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe"));
        ushort expected = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? PeReader.MachineArm64 : PeReader.MachineAmd64;
        Assert.Equal(expected, pe.Machine);
        Assert.NotNull(pe.Icon);
        Assert.True(PeReader.IsPng(pe.Icon) || (pe.Icon[2] == 1 && pe.Icon[4] == 1));
    }

    [Fact]
    public void ThePeReaderRefusesWhatIsNotAnExecutable()
    {
        Assert.Throws<InvalidDataException>(() => PeReader.Read(new MemoryStream([0x4D, 0x5A, 1, 2])));
        Assert.Throws<InvalidDataException>(() => PeReader.Read(new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 4096)))));
        byte[] truncated = File.ReadAllBytes(Cmd)[..1024];
        Assert.Throws<InvalidDataException>(() => PeReader.Read(new MemoryStream(truncated)));
    }

    [Fact]
    public void InstallerTechnologiesAreRecognized()
    {
        var plain = PeReader.Read(Cmd);
        Assert.Equal(InstallerKind.Exe, InstallerKinds.Detect(plain));
        Assert.Equal(InstallerKind.Nsis, InstallerKinds.Detect(PeReader.Read(Installer("nsis.exe"))));
        Assert.Equal(InstallerKind.Inno, InstallerKinds.Detect(PeReader.Read(Installer("inno.exe",
            [.. Encoding.ASCII.GetBytes("rDlPtS\u00cd\u00e6\u00d7{"), .. Encoding.ASCII.GetBytes("Inno Setup Setup Data (6.3.0)")]))));
        Assert.Equal(InstallerKind.Inno, InstallerKinds.Detect(plain with
        {
            Strings = new Dictionary<string, string> { ["Comments"] = "This installation was built with Inno Setup." },
        }));
        Assert.Equal(InstallerKind.Burn, InstallerKinds.Detect(plain with { Sections = [".text", ".wixburn", ".rsrc"] }));
        Assert.Equal(InstallerKind.InstallShield, InstallerKinds.Detect(plain with
        {
            Strings = new Dictionary<string, string> { ["InternalName"] = "InstallShield Setup.exe" },
        }));
    }

    [Fact]
    public void AnExeIsIdentifiedByItsVersionResource()
    {
        var metadata = InstallerFile.Read(Installer("feed/Tool-Setup.exe"));
        string product = PeReader.Read(Cmd).String("ProductName")!;
        Assert.Equal(InstallerFile.CleanName(product), metadata.Name);
        Assert.Equal(InstallerFile.MakeId(metadata.Name), metadata.Id);
        Assert.Equal(CmdVersion(), metadata.Version);
        Assert.Equal(InstallerKind.Nsis, metadata.Details.Kind);
        Assert.Equal("/S", metadata.Details.InstallArguments);
        Assert.Equal("Microsoft Corporation", metadata.Details.Publisher);
    }

    [Fact]
    public void AManifestFillsInWhatTheExeCannotSay()
    {
        string exe = Installer("Vendor.exe", [1, 2, 3]);
        var error = Assert.Throws<InvalidDataException>(() => InstallerFile.Read(exe));
        Assert.Contains("installArguments", error.Message);
        Assert.Contains("Vendor.json", error.Message);

        Manifest(exe, """
            {
              // the vendor documents these switches
              "id": "Vendor.Tool",
              "name": "Vendor Tool",
              "version": "5.1",
              "installArguments": "--silent --no-reboot",
              "uninstallArguments": "--uninstall --silent",
              "displayName": "Vendor Tool Pro",
              "uninstallKey": "VendorTool",
              "architectures": ["AMD64"],
              "scope": "user",
            }
            """);
        var metadata = InstallerFile.Read(exe);
        Assert.Equal(("Vendor.Tool", "Vendor Tool", "5.1"), (metadata.Id, metadata.Name, metadata.Version));
        Assert.Equal(InstallerKind.Exe, metadata.Details.Kind);
        Assert.Equal("--silent --no-reboot", metadata.Details.InstallArguments);
        Assert.Equal("--uninstall --silent", metadata.Details.UninstallArguments);
        Assert.Equal(("Vendor Tool Pro", "VendorTool"), (metadata.Details.DisplayName, metadata.Details.UninstallKey));
        Assert.Equal(["x64"], metadata.Architectures);
        Assert.True(metadata.Details.PerUser);

        Manifest(exe, "{ \"version\": ");
        Assert.Contains("not valid", Assert.Throws<InvalidDataException>(() => InstallerFile.Read(exe)).Message);
    }

    [Fact]
    public void AnMsiSaysEverythingItself()
    {
        string msi = Msi("feed/Example.msi", new()
        {
            ["ProductCode"] = ProductCode.ToLowerInvariant(),
            ["UpgradeCode"] = UpgradeCode,
            ["ProductVersion"] = "3.2.1",
            ["ProductName"] = "Example App 3.2.1 (x64 edition)",
            ["Manufacturer"] = "Example Corp",
            ["ALLUSERS"] = "2",
            ["MSIINSTALLPERUSER"] = "1",
        }, "x64;1033");
        var metadata = InstallerFile.Read(msi);
        Assert.Equal((UpgradeCode, "Example App", "3.2.1"), (metadata.Id, metadata.Name, metadata.Version));
        Assert.Equal(InstallerKind.Msi, metadata.Details.Kind);
        Assert.Equal((ProductCode, UpgradeCode), (metadata.Details.ProductCode, metadata.Details.UpgradeCode));
        Assert.Equal("Example App 3.2.1 (x64 edition)", metadata.Details.DisplayName);
        Assert.Equal(["x64"], metadata.Architectures);
        Assert.True(metadata.Details.PerUser);
        Assert.Equal("Example Corp", metadata.Details.Publisher);

        string machine = Msi("Machine.msi", new() { ["ProductCode"] = ProductCode, ["ProductVersion"] = "1.0", ["ALLUSERS"] = "1" }, "Intel;1033");
        var machineWide = InstallerFile.Read(machine);
        Assert.Equal(ProductCode, machineWide.Id);
        Assert.Equal(["x86"], machineWide.Architectures);
        Assert.False(machineWide.Details.PerUser);

        Assert.Throws<InvalidDataException>(() => InstallerFile.Read(Msi("NoCode.msi", new() { ["ProductVersion"] = "1.0" }, "x64;0")));
        Assert.Throws<InvalidDataException>(() => InstallerFile.Read(WriteFile("Broken.msi", [1, 2, 3])));
    }

    [Theory]
    [InlineData("7-Zip 23.01 (x64 edition)", "7-Zip")]
    [InlineData("Notepad++ (64-bit x64)", "Notepad++")]
    [InlineData("Zoom Workplace", "Zoom Workplace")]
    [InlineData("MyApp version 1.5", "MyApp")]
    [InlineData("Python 3.12.1 (64-bit)", "Python")]
    [InlineData("Visual Studio Installer", "Visual Studio Installer")]
    [InlineData("Node.js", "Node.js")]
    public void ProductNamesLoseTheirVersionAndArchitecture(string text, string expected) =>
        Assert.Equal(expected, InstallerFile.CleanName(text));

    [Theory]
    [InlineData("npp.8.6.2.Installer.x64", "npp")]
    [InlineData("Zoom Setup", "Zoom")]
    [InlineData("tool-win64-setup", "tool")]
    public void FileNamesAlsoLoseTheirSetupWords(string text, string expected) =>
        Assert.Equal(expected, InstallerFile.CleanName(text, isProductName: false));

    [Theory]
    [InlineData("8.6.2", "8.6.2")]
    [InlineData(" v1.2.3 (build 77)", "1.2.3")]
    [InlineData("1, 2, 3, 4", "1.2.3.4")]
    [InlineData("0.0.0.0", null)]
    [InlineData("beta", null)]
    [InlineData(null, null)]
    public void VersionsAreReadFromTheirLeadingNumbers(string? text, string? expected) =>
        Assert.Equal(expected, InstallerFile.CleanVersion(text));

    [Fact]
    public void BuildsArePickedForTheMachineTheyRunOn()
    {
        static int? Compare(string a, string b) => new Version(a).CompareTo(new Version(b));
        var x86 = Item("A", "A", "2.0", new InstallerDetails(InstallerKind.Msi), "x86");
        var x64 = Item("A", "A", "2.0", new InstallerDetails(InstallerKind.Msi), "x64");
        var arm = Item("A", "A", "2.0", new InstallerDetails(InstallerKind.Msi), "arm64");
        var older = Item("A", "A", "1.0", new InstallerDetails(InstallerKind.Msi), "arm64");
        Assert.Same(arm, WinFeed.Latest([x86, x64, arm, older], Compare, Architecture.Arm64).Single());
        Assert.Same(x64, WinFeed.Latest([x86, x64, arm, older], Compare, Architecture.X64).Single());
        Assert.Same(x64, WinFeed.Latest([x86, x64, older], Compare, Architecture.Arm64).Single());
        Assert.Empty(WinFeed.Latest([arm, older], Compare, Architecture.X64));
    }

    [Fact]
    public void AnEntryNamedExactlyByThePackageIsIt()
    {
        var item = Item("A", "Tool", "1.0", new InstallerDetails(InstallerKind.Exe, UninstallKey: "ToolKey"));
        var wanted = Entry("toolkey", "Something else");
        Assert.Same(wanted, InstalledMatcher.Find(item, [Entry("Tool", "Tool"), wanted]));
        Assert.Null(InstalledMatcher.Find(item, [Entry("Tool", "Tool")]));
    }

    [Fact]
    public void AnMsiIsFoundByItsUpgradeCodeOrProductCodeButNeverByName()
    {
        var item = Item(UpgradeCode, "Example", "2.0", new InstallerDetails(InstallerKind.Msi, ProductCode: ProductCode, UpgradeCode: UpgradeCode));
        var older = Entry("{99999999-0000-0000-0000-000000000000}", "Example", "1.0", msi: true);
        InstalledMatcher.RelatedProducts = code => code == UpgradeCode ? [older.Key] : [];
        Assert.Same(older, InstalledMatcher.Find(item, [Entry("Example", "Example"), older]));

        InstalledMatcher.RelatedProducts = _ => [];
        var same = Entry(ProductCode, "Example", "2.0", msi: true);
        Assert.Same(same, InstalledMatcher.Find(item, [same]));
        Assert.Null(InstalledMatcher.Find(item, [Entry("Example", "Example", "2.0")]));
    }

    [Fact]
    public void ABundleIsFoundByItsUpgradeCode()
    {
        var item = Item("B", "Bundle", "2.0", new InstallerDetails(InstallerKind.Burn, UpgradeCode: UpgradeCode));
        var bundle = Entry("{00000000-1111-1111-1111-000000000000}", "Bundle Runtime", "1.0", bundleCodes: [UpgradeCode]);
        Assert.Same(bundle, InstalledMatcher.Find(item, [bundle]));
    }

    [Fact]
    public void AnExeIsFoundByItsNameAndPublisher()
    {
        var item = Item("Tool", "Tool", "1.7", new InstallerDetails(InstallerKind.Inno, Publisher: "Vendor Inc."));
        var inno = Entry("{1234}_is1", "Tool version 1.5", "1.5", "Vendor");
        var other = Entry("Tool", "Tool 1.5", "1.5", "Vendor");
        Assert.Same(inno, InstalledMatcher.Find(item, [other, inno]));
        Assert.Same(other, InstalledMatcher.Find(item, [other, Entry("Tool2", "Tool", "1.0", "Someone Else")]));
        Assert.Null(InstalledMatcher.Find(item, [Entry("Hidden", "Tool", "1.0", hidden: true)]));
        Assert.Null(InstalledMatcher.Find(item, [Entry("Toolbox", "Toolbox", "1.0", "Vendor")]));
        Assert.Null(InstalledMatcher.Find(item, [Entry("Tool", "Tool", "1.0", "Other Vendor")]));
    }

    [Fact]
    public void ASideBySideProductOfAnotherMajorVersionIsNotIt()
    {
        var sdk = Item("Microsoft.NET.SDK", "Microsoft .NET SDK", "10.1.326.7603",
            new InstallerDetails(InstallerKind.Burn, Publisher: "Microsoft Corporation"));
        var eight = Entry("{064a9976-69e7-4aa2-9c9d-474d120ab7f6}", "Microsoft .NET SDK 8.0.133 (arm64)", "8.1.3326.52309", "Microsoft Corporation");
        var ten = Entry("{0aa1d1ed-0000-0000-0000-000000000000}", "Microsoft .NET SDK 10.0.103 (arm64)", "10.1.326.7603", "Microsoft Corporation");
        Assert.Null(InstalledMatcher.Find(sdk, [eight]));
        Assert.Same(ten, InstalledMatcher.Find(sdk, [eight, ten]));
        Assert.Same(eight, InstalledMatcher.Find(sdk with { Version = "8.1.3400" }, [eight]));
    }

    [Fact]
    public void TheNewestOfSeveralInstalledVersionsIsTheInstalledOne()
    {
        var msi = Item(UpgradeCode, "Example", "3.0", new InstallerDetails(InstallerKind.Msi, UpgradeCode: UpgradeCode));
        var older = Entry("{00000000-0000-0000-0000-000000000001}", "Example", "1.0", msi: true);
        var newer = Entry("{00000000-0000-0000-0000-000000000002}", "Example", "2.0", msi: true);
        InstalledMatcher.RelatedProducts = _ => [older.Key, newer.Key];
        Assert.Same(newer, InstalledMatcher.Find(msi, [older, newer]));

        var exe = Item("Tool", "Tool", "3.0", new InstallerDetails(InstallerKind.Exe, Publisher: "Vendor"));
        var first = Entry("A", "Tool", "1.0", "Vendor");
        var second = Entry("B", "Tool", "2.5", "Vendor");
        Assert.Same(second, InstalledMatcher.Find(exe, [first, second]));
    }

    [Fact]
    public void AnExeWithOnlyAFixedFileVersionIsStillRead()
    {
        var pe = PeReader.Read(Cmd) with
        {
            Strings = new Dictionary<string, string>(),
            FixedProductVersion = "0.0.0.0",
            FixedFileVersion = "3.4.5.6",
        };
        var metadata = InstallerFile.FromExe(pe, new InstallerManifest { InstallArguments = "/q" }, "Tool");
        Assert.Equal("3.4.5.6", metadata.Version);
    }

    [Theory]
    [InlineData("工具", "工具")]
    [InlineData("编辑器", "编辑器")]
    [InlineData("Программа", "Программа")]
    [InlineData("应用 Pro", "应用.Pro")]
    [InlineData("Café Tool", "Café.Tool")]
    [InlineData("-Tool", "Tool")]
    public void AnIdKeepsTheLettersOfAnyScript(string name, string expected) =>
        Assert.Equal(expected, InstallerFile.MakeId(name));

    private const string MediaTable = "CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` LONG NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)";
    private const string FileTable = "CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` SHORT NOT NULL PRIMARY KEY `File`)";

    [Fact]
    public void AnMsiWhoseFilesAreOutsideItIsRefused()
    {
        static Dictionary<string, string> Properties() => new() { ["ProductCode"] = ProductCode, ["ProductVersion"] = "1.0" };

        string external = Msi("External.msi", Properties(), "x64;1033", null,
            MediaTable, "INSERT INTO `Media` (`DiskId`, `LastSequence`, `Cabinet`) VALUES (1, 1, 'data1.cab')");
        Assert.Contains("data1.cab", Assert.Throws<InvalidDataException>(() => InstallerFile.Read(external)).Message);

        string loose = Msi("Loose.msi", Properties(), "x64;1033", null,
            MediaTable, "INSERT INTO `Media` (`DiskId`, `LastSequence`) VALUES (1, 1)",
            FileTable, "INSERT INTO `File` (`File`, `Component_`, `FileName`, `FileSize`, `Sequence`) VALUES ('App', 'Main', 'app.exe', 10, 1)");
        Assert.Contains("uncompressed", Assert.Throws<InvalidDataException>(() => InstallerFile.Read(loose)).Message);

        string embedded = Msi("Embedded.msi", Properties(), "x64;1033", null,
            MediaTable, "INSERT INTO `Media` (`DiskId`, `LastSequence`, `Cabinet`) VALUES (1, 1, '#cab1.cab')",
            FileTable, "INSERT INTO `File` (`File`, `Component_`, `FileName`, `FileSize`, `Sequence`) VALUES ('App', 'Main', 'app.exe', 10, 1)");
        Assert.Equal("1.0", InstallerFile.Read(embedded).Version);
    }

    [Fact]
    public void AnMsiWithoutAllUsersInstallsForTheUserWithoutElevation()
    {
        string msi = Msi("PerUser.msi", new() { ["ProductCode"] = ProductCode, ["ProductVersion"] = "1.0" }, "x64;1033");
        Assert.True(InstallerFile.Read(msi).Details.PerUser);
        var item = WinFeed.ScanFolder(_directory, CancellationToken.None).First(i => i.Location.LocalPath.EndsWith("PerUser.msi"));
        Assert.False(WinInstallerRunner.InstallProcess(item, "C:\\staged.msi", "C:\\log.txt").Elevated);
    }

    [Fact]
    public void AFeedPackageHasNoStandaloneCommandLine()
    {
        var manager = new WinInstaller();
        var source = new UniGetUI.PackageEngine.Classes.Manager.ManagerSource(manager, "feed", new Uri("https://h/"));
        var package = new UniGetUI.PackageEngine.PackageClasses.Package("Tool", "Vendor.Tool", "1.0", source, manager);
        foreach (var operation in new[] { OperationType.Install, OperationType.Update, OperationType.Uninstall })
            Assert.Throws<InvalidOperationException>(() =>
                manager.OperationHelper.GetStandaloneParameters(package, new UniGetUI.PackageEngine.Serializable.InstallOptions(), operation));
    }

    [Fact]
    public void AReceiptKeepsAnInstallFromBeingOfferedAgain()
    {
        var entry = Entry("Tool", "Tool", "2.0.0.1234");
        Assert.Equal("2.0.0.1234", InstallReceipts.InstalledVersion("Tool", entry));
        InstallReceipts.Record("Tool", entry, "2.0");
        Assert.Equal("2.0", InstallReceipts.InstalledVersion("Tool", entry));
        Assert.Equal("2.1.0.1300", InstallReceipts.InstalledVersion("Tool", entry with { DisplayVersion = "2.1.0.1300" }));
        Assert.Equal("2.0.0.1234", InstallReceipts.InstalledVersion("Other", entry));
        InstallReceipts.Record("Tool", entry with { DisplayVersion = "3.0" }, "3.0");
        Assert.Equal("2.0.0.1234", InstallReceipts.InstalledVersion("Tool", entry));
    }

    [Fact]
    public void UninstallsRunWhatThePackageRegisteredSilently()
    {
        string folder = Path.Combine(_directory, "Program Files", "Tool");
        string uninstaller = WriteFile(Path.Combine("Program Files", "Tool", "unins000.exe"), [0]);

        var msi = WinInstallerRunner.UninstallProcess(null, Entry(ProductCode.ToLowerInvariant(), msi: true, uninstall: "MsiExec.exe /X" + ProductCode));
        Assert.Equal((WinInstallerRunner.MsiExecPath, $"/x {ProductCode} /qn /norestart", true), (msi!.FileName, msi.Arguments, msi.Elevated));

        var inno = WinInstallerRunner.UninstallProcess(null, Entry("{1}_is1", uninstall: $"\"{uninstaller}\""));
        Assert.Equal((uninstaller, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"), (inno!.FileName, inno.Arguments));

        var unquoted = WinInstallerRunner.UninstallProcess(new InstallerDetails(InstallerKind.Nsis),
            Entry("Tool", uninstall: $"{uninstaller} /remove", perMachine: false));
        Assert.Equal((uninstaller, "/remove /S", false), (unquoted!.FileName, unquoted.Arguments, unquoted.Elevated));

        var quiet = WinInstallerRunner.UninstallProcess(null, Entry("Tool", uninstall: $"\"{uninstaller}\"", quiet: $"\"{uninstaller}\" /quiet"));
        Assert.Equal((uninstaller, "/quiet", true), (quiet!.FileName, quiet.Arguments, quiet.Elevated));

        var given = WinInstallerRunner.UninstallProcess(new InstallerDetails(InstallerKind.Exe, UninstallArguments: "--silent"),
            Entry("Tool", uninstall: $"\"{uninstaller}\"", quiet: $"\"{uninstaller}\" /quiet"));
        Assert.Equal("--silent", given!.Arguments);

        Assert.Null(WinInstallerRunner.UninstallProcess(new InstallerDetails(InstallerKind.Exe), Entry("Tool", uninstall: $"\"{uninstaller}\"")));
        Assert.Null(WinInstallerRunner.UninstallProcess(null, Entry("{1}_is1", uninstall: $"\"{Path.Combine(folder, "missing.exe")}\"")));
        Assert.Null(WinInstallerRunner.UninstallProcess(null, Entry("{1}_is1")));
    }

    [Fact]
    public async Task AnUninstallSucceedsOnceItsEntryIsGone()
    {
        string uninstaller = WriteFile("uninst.exe", [0]);
        var entry = Entry("Tool", "Tool", "1.0", uninstall: $"\"{uninstaller}\"");
        var log = new List<string>();
        var entries = new List<ArpEntry> { entry };
        ArpEntries.Source = entries.ToArray;
        WinInstallerRunner.RunProcess = process =>
        {
            _started.Add(process);
            entries.Clear();
            return Task.FromResult(0);
        };
        Assert.Equal(OperationVeredict.Success, await WinInstallerRunner.UninstallAsync("Tool", new InstallerDetails(InstallerKind.Nsis), entry, log.Add, _ => { }));
        Assert.Equal("/S", _started.Single().Arguments);

        entries.Add(entry);
        WinInstallerRunner.RunProcess = _ => Task.FromResult(0);
        Assert.Equal(OperationVeredict.Failure, await WinInstallerRunner.UninstallAsync("Tool", new InstallerDetails(InstallerKind.Nsis), entry, log.Add, _ => { }));
        Assert.Contains(log, l => l.Contains("still listed"));
    }

    [Fact]
    public async Task AnInstallRunsTheCheckedCopySilently()
    {
        string exe = Installer("feed/Tool Setup.exe");
        var metadata = InstallerFile.Read(exe);
        var item = WinFeed.ScanFolder(Path.Combine(_directory, "feed"), CancellationToken.None).Single();
        var log = new List<string>();

        Assert.Equal(OperationVeredict.Success, await WinInstallerRunner.InstallAsync(item, false, log.Add, _ => { }, CancellationToken.None));
        var process = _started.Single();
        Assert.Equal("Tool Setup.exe", Path.GetFileName(process.FileName));
        Assert.NotEqual(exe, process.FileName);
        Assert.Equal(("/S", false), (process.Arguments, process.Elevated));
        Assert.Contains(log, l => l.Contains("signed by Example Corp"));
        Assert.False(Directory.Exists(Path.GetDirectoryName(process.FileName)));
        Assert.Equal(metadata.Id, item.Id);

        _exitCode = 3010;
        Assert.Equal(OperationVeredict.Success, await WinInstallerRunner.InstallAsync(item, false, log.Add, _ => { }, CancellationToken.None));
        _exitCode = 1;
        Assert.Equal(OperationVeredict.Failure, await WinInstallerRunner.InstallAsync(item, false, log.Add, _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task AnUnsignedInstallerNeedsItsCatalogsPermission()
    {
        Installer("feed/Tool.exe");
        var item = WinFeed.ScanFolder(Path.Combine(_directory, "feed"), CancellationToken.None).Single();
        Authenticode.Verify = _ => new SignatureCheck(unchecked((int)0x800B0100), null);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            WinInstallerRunner.InstallAsync(item, false, _ => { }, _ => { }, CancellationToken.None));
        Assert.Contains("not signed", error.Message);
        Assert.Empty(_started);

        var log = new List<string>();
        Assert.Equal(OperationVeredict.Success, await WinInstallerRunner.InstallAsync(item, true, log.Add, _ => { }, CancellationToken.None));
        Assert.Contains(log, l => l.Contains("allows unsigned"));
    }

    [Fact]
    public async Task AFileReplacedSinceItWasListedIsRefused()
    {
        string exe = Installer("feed/Tool.exe");
        var item = WinFeed.ScanFolder(Path.Combine(_directory, "feed"), CancellationToken.None).Single();
        Manifest(exe, "{ \"version\": \"9.9\" }");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            WinInstallerRunner.InstallAsync(item, false, _ => { }, _ => { }, CancellationToken.None));
        Assert.Contains("Reload the feed", error.Message);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task AnMsiInstallsThroughMsiexecWithALog()
    {
        Msi("feed/Example.msi", new() { ["ProductCode"] = ProductCode, ["ProductVersion"] = "1.0", ["ProductName"] = "Example" }, "x64;1033");
        var item = WinFeed.ScanFolder(Path.Combine(_directory, "feed"), CancellationToken.None).Single();
        WinInstallerRunner.IsAdministrator = () => true;
        Assert.Equal(OperationVeredict.Success, await WinInstallerRunner.InstallAsync(item, false, _ => { }, _ => { }, CancellationToken.None));
        var process = _started.Single();
        Assert.Equal(WinInstallerRunner.MsiExecPath, process.FileName);
        Assert.Matches("^/i \".*Example\\.msi\" /qn /norestart /l\\*v \".*install\\.log\"$", process.Arguments);
        Assert.False(process.Elevated);
    }

    [Fact]
    public void ElevationIsDeclinedAsACancel()
    {
        Assert.Equal(OperationVeredict.Canceled, WinInstallerRunner.Verdict(1602, _ => { }));
        Assert.Equal(OperationVeredict.Success, WinInstallerRunner.Verdict(1641, _ => { }));
    }

    [Fact]
    public void AnOddVendorFileNameIsNotKeptForTheStagedCopy()
    {
        var details = new InstallerDetails(InstallerKind.Exe);
        Assert.Equal("Tool Setup (x64).exe", WinInstallerRunner.StagedName(Item("a", "a", "1", details) with { Location = new Uri(@"C:\f\Tool Setup (x64).exe") }));
        Assert.Equal("setup.exe", WinInstallerRunner.StagedName(Item("a", "a", "1", details) with { Location = new Uri(@"C:\f\a&calc.exe") }));
        Assert.Equal("setup.msi", WinInstallerRunner.StagedName(Item("a", "a", "1", new InstallerDetails(InstallerKind.Msi)) with { Location = new Uri(@"C:\f\x.exe") }));
    }

    [Fact]
    public void TheIndexCommandIndexesWindowsFilesAndKeepsMacOnes()
    {
        string exe = Installer("Tool.exe");
        WriteFile("Tool.png", [1, 2, 3]);
        Msi("sub/Example.msi", new() { ["ProductCode"] = ProductCode, ["UpgradeCode"] = UpgradeCode, ["ProductVersion"] = "2.0" }, "x64;1033");
        Installer("Tool-uninstaller.exe");
        WriteFile("junk.exe", [1, 2, 3]);
        string mac = WriteFile("App.pkg", [9, 9, 9]);
        string changed = WriteFile("Changed.dmg", [7]);
        WriteFile("index.json", Encoding.UTF8.GetBytes($$"""
            {
              "version": 1,
              "packages": [
                { "id": "com.example.app", "name": "App", "version": "1.0", "file": "App.pkg", "sha256": "{{FeedLocation.ComputeSha256(mac)}}", "size": 3 },
                { "id": "com.example.changed", "name": "Changed", "version": "1.0", "file": "Changed.dmg", "sha256": "{{new string('0', 64)}}", "size": 1 },
                { "id": "com.example.gone", "name": "Gone", "version": "1.0", "file": "Gone.zip", "sha256": "{{new string('0', 64)}}", "size": 1 }
              ]
            }
            """));

        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(3, FeedIndexCommand.Run([_directory], output, error, new WinInstallerIndexer()));
        Assert.Contains("junk.exe", error.ToString());
        Assert.Contains("Changed.dmg", error.ToString());
        Assert.Contains("App.pkg: App (com.example.app 1.0) (kept)", output.ToString());

        byte[] json = File.ReadAllBytes(Path.Combine(_directory, "index.json"));
        var written = FeedJson.Deserialize(json, FeedIndexJsonContext.Default.FeedIndex)!;
        Assert.Equal(["App.pkg"], written.Packages.Select(e => e.File));
        Assert.Equal(["Tool.exe", "sub/Example.msi"], written.WindowsPackages!.Select(e => e.File));
        var indexUrl = new Uri("https://h/feed/index.json");
        var items = WinFeed.ParseIndex(json, indexUrl);
        Assert.Equal(2, items.Count);
        var tool = items.Single(i => i.Details.Kind == InstallerKind.Nsis);
        Assert.Equal(FeedLocation.ComputeSha256(exe), tool.Sha256);
        Assert.Equal("https://h/feed/Tool.png", tool.Icon?.AbsoluteUri);
        Assert.Equal("/S", tool.Details.InstallArguments);
        var msi = items.Single(i => i.Details.Kind == InstallerKind.Msi);
        Assert.Equal((UpgradeCode, "https://h/feed/sub/Example.msi", ProductCode), (msi.Id, msi.Location.AbsoluteUri, msi.Details.ProductCode));

        var macItems = PkgFeed.ParseIndex(json, indexUrl);
        Assert.Equal("com.example.app", macItems.Single().Id);
    }

    [Fact]
    public void AWebIndexEntryMustSayHowItInstallsAndBeListedForWindows()
    {
        string sha = new('a', 64);
        string json = $$"""
            {
              "version": 1,
              "packages": [
                { "id": "old-style", "name": "Old", "version": "1", "file": "old.exe", "sha256": "{{sha}}", "windows": { "installer": "inno" } }
              ],
              "windowsPackages": [
                { "id": "ok", "name": "Ok", "version": "1", "file": "ok.exe", "sha256": "{{sha}}", "windows": { "installer": "inno" } },
                { "id": "noblock", "name": "No", "version": "1", "file": "no.exe", "sha256": "{{sha}}" },
                { "id": "unknown", "name": "Unknown", "version": "1", "file": "u.exe", "sha256": "{{sha}}", "windows": { "installer": "exe" } },
                { "id": "given", "name": "Given", "version": "1", "file": "g.exe", "sha256": "{{sha}}", "windows": { "installer": "exe", "installArguments": "-q" } },
                { "id": "mismatch", "name": "M", "version": "1", "file": "m.exe", "sha256": "{{sha}}", "windows": { "installer": "msi" } },
                { "id": "escape", "name": "E", "version": "1", "file": "../e.exe", "sha256": "{{sha}}", "windows": { "installer": "nsis" } }
              ]
            }
            """;
        var items = WinFeed.ParseIndex(Encoding.UTF8.GetBytes(json), new Uri("https://h/feed/index.json"));
        Assert.Equal(["ok", "given"], items.Select(i => i.Id));
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-", items[0].Details.InstallArguments);
    }

    [Fact]
    public void TheManagerListsFeedPackagesAndRecognizesInstalledOnes()
    {
        string feed = Path.Combine(_directory, "feed");
        Installer("feed/Tool.exe");
        Msi("feed/Example.msi", new()
        {
            ["ProductCode"] = ProductCode, ["UpgradeCode"] = UpgradeCode, ["ProductVersion"] = "2.0", ["ProductName"] = "Example",
        }, "Intel;1033");
        CatalogFeeds.TestCatalogPathOverride = WriteFile("SoftwareCatalog.json", Encoding.UTF8.GetBytes($$"""
            { "version": 1, "catalogs": [ { "id": "it-apps", "name": "IT apps", "packages": [], "pkgFeed": "{{feed.Replace(@"\", @"\\")}}" } ] }
            """));
        var manager = new WinInstaller();
        var listed = manager.ListLatest();
        Assert.Equal(2, listed.Count);
        Assert.All(listed, p => Assert.Equal("it-apps", p.Source.Name));

        string toolName = listed.Single(p => p.Item.Details.Kind == InstallerKind.Nsis).Item.Name;
        InstalledMatcher.RelatedProducts = code => code == UpgradeCode ? ["{12345678-0000-0000-0000-000000000000}"] : [];
        ArpEntries.Source = () =>
        [
            Entry("{12345678-0000-0000-0000-000000000000}", "Example", "1.0", msi: true),
            Entry("SomeTool", toolName, CmdVersion(), "Microsoft"),
        ];
        var installed = manager.ListInstalled();
        Assert.Equal(2, installed.Count);
        var example = installed.Single(p => p.Item.Id == UpgradeCode);
        Assert.Equal(("1.0", true), (example.Installed, example.HasUpdate));
        var tool = installed.Single(p => p.Item.Id != UpgradeCode);
        Assert.False(tool.HasUpdate);
    }

    private static byte[] Ico(params (byte Width, ushort Bits, byte[] Image)[] images)
    {
        var ico = new List<byte> { 0, 0, 1, 0, (byte)images.Length, 0 };
        int offset = 6 + 16 * images.Length;
        foreach (var (width, bits, image) in images)
        {
            ico.AddRange([width, width, 0, 0, 1, 0, (byte)bits, (byte)(bits >> 8)]);
            ico.AddRange(BitConverter.GetBytes(image.Length));
            ico.AddRange(BitConverter.GetBytes(offset));
            offset += image.Length;
        }
        foreach (var (_, _, image) in images)
            ico.AddRange(image);
        return [.. ico];
    }

    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    [Fact]
    public void AnMsiShowsTheLargestImageOfTheIconItGivesAppsAndFeatures()
    {
        byte[] small = [.. Enumerable.Repeat((byte)7, 40)];
        string msi = Msi("Icon.msi", new() { ["ProductCode"] = ProductCode, ["ProductVersion"] = "1.0", ["ARPPRODUCTICON"] = "Product.ico" },
            "x64;1033", [("Other.ico", Ico((16, 32, small))), ("Product.ico", Ico((16, 32, small), (0, 32, Png), (48, 32, small)))]);
        Assert.Equal(Png, InstallerFile.Read(msi).Icon);

        string withoutProductIcon = Msi("Any.msi", new() { ["ProductCode"] = ProductCode, ["ProductVersion"] = "1.0" },
            "x64;1033", [("Only.ico", Ico((32, 32, small)))]);
        byte[] icon = InstallerFile.Read(withoutProductIcon).Icon!;
        Assert.Equal(Ico((32, 32, small)), icon);

        Assert.Null(InstallerFile.Read(Msi("None.msi", new() { ["ProductCode"] = ProductCode, ["ProductVersion"] = "1.0" }, "x64;1033")).Icon);
    }

    [Fact]
    public void AnMsiIconStoredAsAnExecutableIsReadFromItsResources()
    {
        string regedit = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe");
        string msi = Msi("Exe.msi", new() { ["ProductCode"] = ProductCode, ["ProductVersion"] = "1.0", ["ARPPRODUCTICON"] = "App.exe" },
            "x64;1033", [("App.exe", File.ReadAllBytes(regedit))]);
        Assert.Equal(PeReader.Read(regedit).Icon, InstallerFile.Read(msi).Icon);
    }

    [Fact]
    public void ListingsReadingAFileAtOnceAllSeeItsIcon()
    {
        string regedit = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe");
        for (int i = 0; i < 6; i++)
            Msi($"busy/App{i}.msi", new() { ["ProductCode"] = Guid.NewGuid().ToString("B"), ["ProductVersion"] = "1.0", ["ARPPRODUCTICON"] = "App.exe" },
                "x64;1033", [("App.exe", File.ReadAllBytes(regedit))]);
        string folder = Path.Combine(_directory, "busy");
        var listings = new IReadOnlyList<WinFeedItem>[12];
        Parallel.For(0, listings.Length, new ParallelOptions { MaxDegreeOfParallelism = 12 },
            i => listings[i] = WinFeed.ScanFolder(folder, CancellationToken.None));
        Assert.All(listings, listing => Assert.All(listing, item => Assert.NotNull(item.Icon)));
        Assert.All(listings, listing => Assert.Equal(6, listing.Count));
    }

    [Fact]
    public void AnIconWhoseImagesLieOutsideItIsIgnored()
    {
        byte[] ico = Ico((0, 32, Png));
        BitConverter.GetBytes(10_000).CopyTo(ico, 18);
        Assert.Null(PeReader.BestIconInIco(ico));
        Assert.Null(PeReader.BestIconInIco([0, 0, 1]));
        Assert.Null(PeReader.IconFromFile([(byte)'M', (byte)'Z', 0, 0]));
        Assert.Equal(Png, PeReader.IconFromFile(Ico((16, 8, [9, 9]), (0, 32, Png))));
    }

    private string Msi(string relative, Dictionary<string, string> properties, string template,
        (string Name, byte[] Data)[]? icons = null, params string[] statements)
    {
        string path = Path.Combine(_directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Check(MsiOpenDatabase(path, 3, out nint database));
        try
        {
            Execute(database, "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` LONGCHAR NOT NULL PRIMARY KEY `Property`)");
            foreach (var (name, value) in properties)
                Execute(database, $"INSERT INTO `Property` (`Property`, `Value`) VALUES ('{name}', '{value}')");
            foreach (string statement in statements)
                Execute(database, statement);
            if (icons is not null)
            {
                Execute(database, "CREATE TABLE `Icon` (`Name` CHAR(72) NOT NULL, `Data` OBJECT NOT NULL PRIMARY KEY `Name`)");
                foreach (var (name, data) in icons)
                {
                    string stream = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
                    File.WriteAllBytes(stream, data);
                    nint record = MsiCreateRecord(2);
                    Check(MsiRecordSetString(record, 1, name));
                    Check(MsiRecordSetStream(record, 2, stream));
                    Check(MsiDatabaseOpenView(database, "INSERT INTO `Icon` (`Name`, `Data`) VALUES (?, ?)", out nint view));
                    Check(MsiViewExecute(view, record));
                    MsiCloseHandle(view);
                    MsiCloseHandle(record);
                }
            }
            Check(MsiGetSummaryInformation(database, null, 1, out nint summary));
            Check(MsiSummaryInfoSetProperty(summary, 7, 30, 0, 0, template));
            Check(MsiSummaryInfoPersist(summary));
            MsiCloseHandle(summary);
            Check(MsiDatabaseCommit(database));
        }
        finally { MsiCloseHandle(database); }
        return path;

        static void Execute(nint database, string query)
        {
            Check(MsiDatabaseOpenView(database, query, out nint view));
            try { Check(MsiViewExecute(view, 0)); }
            finally { MsiCloseHandle(view); }
        }

        static void Check(uint result) => Assert.Equal(0u, result);
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")]
    private static extern uint MsiOpenDatabase(string path, nint persist, out nint database);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")]
    private static extern uint MsiDatabaseOpenView(nint database, string query, out nint view);

    [DllImport("msi.dll")]
    private static extern uint MsiViewExecute(nint view, nint record);

    [DllImport("msi.dll")]
    private static extern uint MsiDatabaseCommit(nint database);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetSummaryInformationW")]
    private static extern uint MsiGetSummaryInformation(nint database, string? path, uint updateCount, out nint summary);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiSummaryInfoSetPropertyW")]
    private static extern uint MsiSummaryInfoSetProperty(nint summary, uint property, uint dataType, int integer, nint fileTime, string? value);

    [DllImport("msi.dll")]
    private static extern uint MsiSummaryInfoPersist(nint summary);

    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(nint handle);

    [DllImport("msi.dll")]
    private static extern nint MsiCreateRecord(uint fields);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordSetStringW")]
    private static extern uint MsiRecordSetString(nint record, uint field, string value);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordSetStreamW")]
    private static extern uint MsiRecordSetStream(nint record, uint field, string path);
}
#endif

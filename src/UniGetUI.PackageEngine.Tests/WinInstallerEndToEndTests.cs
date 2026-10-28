#if WINDOWS
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using Architecture = System.Runtime.InteropServices.Architecture;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Managers.InstallerFeeds;
using UniGetUI.PackageEngine.Managers.WinInstallerManager;
using UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageOperations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Tests;

public sealed class InstallerEndToEndFactAttribute : FactAttribute
{
    public const string Variable = "UNIGETUI_INSTALLER_E2E";

    public InstallerEndToEndFactAttribute(bool needsInno = false, bool elevated = false)
    {
        string? mode = Environment.GetEnvironmentVariable(Variable);
        if (mode is null || mode == "0")
            Skip = $"Installs real packages for the current user; set {Variable}=1 to run.";
        else if (elevated && mode != "elevated")
            Skip = $"Installs for every user behind a UAC prompt; set {Variable}=elevated to run.";
        else if (needsInno && WinInstallerEndToEndTests.InnoCompiler is null)
            Skip = "Inno Setup (ISCC.exe) is not installed.";
    }
}

[Collection("Installer feeds")]
public sealed class WinInstallerEndToEndTests : IDisposable
{
    private const string Vendor = "UniGetUI Test Vendor";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "UniGetUI-E2E", Guid.NewGuid().ToString("N")[..8]);
    private readonly string _feed;
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6];
    private readonly List<string> _log = [];
    private readonly List<string> _productCodes = [];
    private readonly List<string> _innoFolders = [];
    private readonly List<string> _registryKeys = [];

    public static string? InnoCompiler =>
        new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Inno Setup 6", "ISCC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Inno Setup 6", "ISCC.exe"),
        }.FirstOrDefault(File.Exists);

    public WinInstallerEndToEndTests()
    {
        _feed = Path.Combine(_root, "feed");
        Directory.CreateDirectory(_feed);
        Directory.CreateDirectory(Path.Combine(_root, "build"));
        WinFeed.TestIconDirectoryOverride = Path.Combine(_root, "icons");
        InstallReceipts.TestPathOverride = Path.Combine(_root, "receipts.json");
        WinFeed.ForgetReadFiles();
        WinFeed.ForgetCachedIndexes();
        WinInstallerRunner.IsAdministrator = CoreTools.IsAdministrator;
        bool elevatedMode = Environment.GetEnvironmentVariable(InstallerEndToEndFactAttribute.Variable) == "elevated";
        WinInstallerRunner.RunProcess = process => process.Elevated && !elevatedMode
            ? throw new InvalidOperationException($"Asked for administrator rights to run {process.FileName} {process.Arguments}")
            : WinInstallerRunner.RunProcessAsync(process);
        WinInstallerRunner.UninstallSettleTime = TimeSpan.FromMinutes(2);
        WinInstallerRunner.UninstallPollInterval = TimeSpan.FromMilliseconds(500);
        Authenticode.Verify = Authenticode.VerifyFile;
        InstalledMatcher.RelatedProducts = MsiDatabase.RelatedProducts;
        ArpEntries.Source = ArpEntries.ReadRegistry;
        Catalog(_feed, allowUnsigned: true);
    }

    public void Dispose()
    {
        foreach (string code in _productCodes)
            Run(WinInstallerRunner.MsiExecPath, $"/x {code} /qn /norestart");
        foreach (string folder in _innoFolders.Where(Directory.Exists))
            foreach (string uninstaller in Directory.GetFiles(folder, "unins*.exe"))
                Run(uninstaller, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART");
        foreach (string key in _registryKeys)
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        WinFeed.TestIconDirectoryOverride = null;
        InstallReceipts.TestPathOverride = null;
        CatalogFeeds.TestCatalogPathOverride = null;
        WinFeed.ForgetReadFiles();
        WinFeed.ForgetCachedIndexes();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Catalog(string feed, bool allowUnsigned) =>
        CatalogFeeds.TestCatalogPathOverride = Write(Path.Combine(_root, "SoftwareCatalog.json"), $$"""
            { "version": 1, "catalogs": [ { "id": "e2e", "name": "End to end", "packages": [],
              "pkgFeed": "{{feed.Replace(@"\", @"\\")}}", "allowUnsignedPackages": {{(allowUnsigned ? "true" : "false")}} } ] }
            """);

    private static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static int Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit(TimeSpan.FromMinutes(5));
        return process.ExitCode;
    }

    private WinInstaller Manager()
    {
        var manager = new WinInstaller();
        manager.RefreshPackageIndexes();
        WinFeed.ForgetReadFiles();
        return manager;
    }

    private async Task<OperationVeredict?> Perform(WinInstaller manager, IPackage package, OperationType operation)
    {
        var helper = (IManagedPackageOperationHelper)manager.OperationHelper;
        var verdict = await helper.TryPerformAsync(package, new InstallOptions(), operation, _log.Add, _ => { }, CancellationToken.None);
        _log.Add($"=> {operation} {package.Id}: {verdict}");
        return verdict;
    }

    private static Package Offered(WinInstaller manager, string id)
    {
        var (source, item) = manager.ListLatest().Single(p => p.Item.Id == id);
        return new Package(item.Name, item.Id, item.Version, source, manager);
    }

    private static Package Upgradable(WinInstaller manager, string id)
    {
        var installed = manager.ListInstalled().Single(p => p.Item.Id == id);
        Assert.True(installed.HasUpdate);
        return new Package(installed.Item.Name, installed.Item.Id, installed.Installed, installed.Item.Version, installed.Source, manager);
    }

    private static Package Installed(WinInstaller manager, string id)
    {
        var installed = manager.ListInstalled().Single(p => p.Item.Id == id);
        return new Package(installed.Item.Name, installed.Item.Id, installed.Installed, installed.Source, manager);
    }

    private string Log => string.Join(Environment.NewLine, _log);

    private void Succeeded(OperationVeredict? verdict) => Assert.True(verdict == OperationVeredict.Success, Log);

    private static IReadOnlyList<ArpEntry> EntriesNamed(string name) =>
        ArpEntries.ReadRegistry().Where(e => e.DisplayName?.Contains(name, StringComparison.Ordinal) == true).ToArray();

    // ── Windows Installer ─────────────────────────────────────────────────

    [InstallerEndToEndFact]
    public async Task AnMsiInstallsUpdatesAndUninstallsForTheUser()
    {
        string name = $"UniGetUI E2E Msi {_tag}";
        string upgradeCode = Guid.NewGuid().ToString("B").ToUpperInvariant();
        BuildMsi(Path.Combine(_feed, "Example-1.0.msi"), name, "1.0.0", upgradeCode);

        var manager = Manager();
        Assert.Empty(manager.ListInstalled());
        Succeeded(await Perform(manager, Offered(manager, upgradeCode), OperationType.Install));
        Assert.Equal("1.0.0", MsiRegistryVersion(name));
        var first = Assert.Single(EntriesNamed(name));
        Assert.False(first.PerMachine, Log);
        Assert.True(first.WindowsInstaller);

        manager = Manager();
        var installed = Assert.Single(manager.ListInstalled());
        Assert.Equal(("1.0.0", false), (installed.Installed, installed.HasUpdate));

        BuildMsi(Path.Combine(_feed, "Example-2.0.msi"), name, "2.0.0", upgradeCode);
        manager = Manager();
        Succeeded(await Perform(manager, Upgradable(manager, upgradeCode), OperationType.Update));
        Assert.Equal("2.0.0", MsiRegistryVersion(name));
        var second = Assert.Single(EntriesNamed(name));
        Assert.NotEqual(first.Key, second.Key);
        Assert.Equal("2.0.0", second.DisplayVersion);
        manager = Manager();
        Assert.False(Assert.Single(manager.ListInstalled()).HasUpdate, Log);

        Succeeded(await Perform(manager, Installed(manager, upgradeCode), OperationType.Uninstall));
        Assert.Empty(EntriesNamed(name));
        Assert.Null(MsiRegistryVersion(name));
        Assert.Empty(Manager().ListInstalled());
    }

    [InstallerEndToEndFact]
    public async Task TheAppsOperationsInstallUpdateAndUninstallAFeedPackage()
    {
        _ = new DiscoverablePackagesLoader([]);
        _ = new UpgradablePackagesLoader([]);
        _ = new InstalledPackagesLoader([]);
        string name = $"UniGetUI E2E Operations {_tag}";
        string upgradeCode = Guid.NewGuid().ToString("B").ToUpperInvariant();
        BuildMsi(Path.Combine(_feed, "Ops-1.0.msi"), name, "1.0.0", upgradeCode);

        var manager = Manager();
        using (var install = new InstallPackageOperation(Offered(manager, upgradeCode), new InstallOptions(), IgnoreParallelInstalls: true))
            await Completes(install);
        Assert.Equal("1.0.0", MsiRegistryVersion(name));

        BuildMsi(Path.Combine(_feed, "Ops-2.0.msi"), name, "2.0.0", upgradeCode);
        manager = Manager();
        using (var update = new UpdatePackageOperation(Upgradable(manager, upgradeCode), new InstallOptions(), IgnoreParallelInstalls: true))
            await Completes(update);
        Assert.Equal("2.0.0", MsiRegistryVersion(name));

        manager = Manager();
        using (var uninstall = new UninstallPackageOperation(Installed(manager, upgradeCode), new InstallOptions(), IgnoreParallelInstalls: true))
            await Completes(uninstall);
        Assert.Empty(EntriesNamed(name));
        Assert.Null(MsiRegistryVersion(name));
    }

    private static async Task Completes(AbstractOperation operation)
    {
        await operation.MainThread().WaitAsync(TimeSpan.FromMinutes(5));
        string output = string.Join(Environment.NewLine, operation.GetOutput().Select(l => l.Item1));
        Assert.True(operation.Status == OperationStatus.Succeeded, output);
    }

    [InstallerEndToEndFact]
    public async Task AWebFeedInstallsThroughItsIndexAndRefusesATamperedFile()
    {
        string name = $"UniGetUI E2E Web {_tag}";
        string upgradeCode = Guid.NewGuid().ToString("B").ToUpperInvariant();
        string msi = BuildMsi(Path.Combine(_feed, "web", "Web.msi"), name, "3.1.0", upgradeCode);
        string folder = Path.GetDirectoryName(msi)!;
        Assert.Equal(0, FeedIndexCommand.Run([folder], new StringWriter(), new StringWriter(), new WinInstallerIndexer()));

        using var server = new FolderServer(folder);
        Catalog(server.Url, allowUnsigned: true);
        var manager = Manager();
        var offered = Offered(manager, upgradeCode);
        Assert.Equal("3.1.0", offered.VersionString);
        Assert.StartsWith(server.Url, manager.FindFeedItem(offered)!.Location.AbsoluteUri);

        byte[] original = File.ReadAllBytes(msi);
        File.WriteAllBytes(msi, [.. original, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Perform(manager, offered, OperationType.Install));
        Assert.Empty(EntriesNamed(name));

        File.WriteAllBytes(msi, original);
        Succeeded(await Perform(manager, offered, OperationType.Install));
        Assert.Contains(_log, l => l.Contains("Downloading", StringComparison.Ordinal));
        Assert.Equal("3.1.0", Assert.Single(Manager().ListInstalled()).Installed);
        Succeeded(await Perform(manager, Installed(manager, upgradeCode), OperationType.Uninstall));
        Assert.Empty(EntriesNamed(name));
    }

    [InstallerEndToEndFact]
    public async Task AnUnsignedInstallerIsRefusedWithoutItsCatalogsPermission()
    {
        string name = $"UniGetUI E2E Unsigned {_tag}";
        string upgradeCode = Guid.NewGuid().ToString("B").ToUpperInvariant();
        string msi = BuildMsi(Path.Combine(_feed, "Unsigned.msi"), name, "1.0.0", upgradeCode);
        Assert.False(Authenticode.VerifyFile(msi).IsValid);

        Catalog(_feed, allowUnsigned: false);
        var manager = Manager();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Perform(manager, Offered(manager, upgradeCode), OperationType.Install));
        Assert.Contains("not signed", error.Message);
        Assert.Empty(EntriesNamed(name));
    }

    // ── Inno Setup ────────────────────────────────────────────────────────

    [InstallerEndToEndFact(needsInno: true)]
    public async Task AnInnoInstallerIsRecognizedByNameAcrossAMajorUpdate()
    {
        string name = $"UniGetUI E2E Inno {_tag}";
        string appId = Guid.NewGuid().ToString("B").ToUpperInvariant();
        BuildInno(Path.Combine(_feed, "Inno-1.5.exe"), name, appId, "1.5");

        var manager = Manager();
        var offered = Assert.Single(manager.ListLatest());
        Assert.Equal((InstallerKind.Inno, "1.5"), (offered.Item.Details.Kind, offered.Item.Version));
        Succeeded(await Perform(manager, Offered(manager, offered.Item.Id), OperationType.Install));
        var entry = Assert.Single(EntriesNamed(name));
        Assert.Equal(($"{appId}_is1", false), (entry.Key, entry.PerMachine));
        Assert.True(File.Exists(Path.Combine(InnoFolder(name), "payload.txt")));

        BuildInno(Path.Combine(_feed, "Inno-2.0.exe"), name, appId, "2.0");
        manager = Manager();
        Succeeded(await Perform(manager, Upgradable(manager, offered.Item.Id), OperationType.Update));
        Assert.Equal("2.0", Assert.Single(EntriesNamed(name)).DisplayVersion);
        manager = Manager();
        var installed = Assert.Single(manager.ListInstalled());
        Assert.Equal(("2.0", false), (installed.Installed, installed.HasUpdate));

        Succeeded(await Perform(manager, Installed(manager, offered.Item.Id), OperationType.Uninstall));
        Assert.Empty(EntriesNamed(name));
        Assert.False(File.Exists(Path.Combine(InnoFolder(name), "payload.txt")));
    }

    [InstallerEndToEndFact(needsInno: true)]
    public async Task AnInstallerRegisteringAnotherVersionIsNotOfferedAgain()
    {
        string name = $"UniGetUI E2E Build {_tag}";
        string appId = Guid.NewGuid().ToString("B").ToUpperInvariant();
        BuildInno(Path.Combine(_feed, "Build.exe"), name, appId, "4.2", productVersion: "4.2.0.1234");

        var manager = Manager();
        var item = Assert.Single(manager.ListLatest()).Item;
        Assert.Equal("4.2.0.1234", item.Version);
        Succeeded(await Perform(manager, Offered(manager, item.Id), OperationType.Install));
        Assert.Equal("4.2", Assert.Single(EntriesNamed(name)).DisplayVersion);

        var installed = Assert.Single(Manager().ListInstalled());
        Assert.Equal(("4.2.0.1234", false), (installed.Installed, installed.HasUpdate));

        InstallReceipts.TestPathOverride = Path.Combine(_root, "no-receipts.json");
        Assert.True(Assert.Single(Manager().ListInstalled()).HasUpdate);
    }

    [InstallerEndToEndFact(needsInno: true)]
    public async Task AManifestDrivesAnInstallerUniGetUIDoesNotRecognize()
    {
        string name = $"UniGetUI E2E Manifest {_tag}";
        string appId = Guid.NewGuid().ToString("B").ToUpperInvariant();
        string exe = BuildInno(Path.Combine(_feed, "Vendor.exe"), name, appId, "7.0");
        Write(Path.ChangeExtension(exe, ".json"), $$"""
            {
              "id": "Vendor.Manifest",
              "name": "Manifest app",
              "installer": "exe",
              "installArguments": "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
              "uninstallArguments": "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
              "uninstallKey": "{{appId}}_is1",
              "scope": "user"
            }
            """);

        var manager = Manager();
        var item = Assert.Single(manager.ListLatest()).Item;
        Assert.Equal(("Vendor.Manifest", InstallerKind.Exe), (item.Id, item.Details.Kind));
        Succeeded(await Perform(manager, Offered(manager, item.Id), OperationType.Install));
        manager = Manager();
        Assert.Equal("7.0", Assert.Single(manager.ListInstalled()).Installed);
        Succeeded(await Perform(manager, Installed(manager, item.Id), OperationType.Uninstall));
        Assert.Empty(EntriesNamed(name));
    }

    // ── Every user ────────────────────────────────────────────────────────

    [InstallerEndToEndFact(elevated: true)]
    public async Task AnMsiForEveryUserInstallsAndUninstallsBehindUac()
    {
        string name = $"UniGetUI E2E Machine {_tag}";
        string upgradeCode = Guid.NewGuid().ToString("B").ToUpperInvariant();
        BuildMsi(Path.Combine(_feed, "Machine.msi"), name, "1.0.0", upgradeCode, perMachine: true);

        var manager = Manager();
        Assert.True(Assert.Single(manager.ListLatest()).Item.Details.PerMachine);
        Succeeded(await Perform(manager, Offered(manager, upgradeCode), OperationType.Install));
        Assert.True(Assert.Single(EntriesNamed(name)).PerMachine, Log);
        manager = Manager();
        Succeeded(await Perform(manager, Installed(manager, upgradeCode), OperationType.Uninstall));
        Assert.Empty(EntriesNamed(name));
    }

    [InstallerEndToEndFact(needsInno: true, elevated: true)]
    public async Task AnInnoInstallerForEveryUserElevatesItself()
    {
        string name = $"UniGetUI E2E Admin {_tag}";
        string appId = Guid.NewGuid().ToString("B").ToUpperInvariant();
        BuildInno(Path.Combine(_feed, "Admin.exe"), name, appId, "1.0", perMachine: true);

        var manager = Manager();
        Succeeded(await Perform(manager, Offered(manager, Assert.Single(manager.ListLatest()).Item.Id), OperationType.Install));
        Assert.True(Assert.Single(EntriesNamed(name)).PerMachine, Log);
        manager = Manager();
        Succeeded(await Perform(manager, Installed(manager, Assert.Single(manager.ListInstalled()).Item.Id), OperationType.Uninstall));
        Assert.Empty(EntriesNamed(name));
    }

    // ── Fixtures ──────────────────────────────────────────────────────────

    private string InnoFolder(string name, bool perMachine = false) => Path.Combine(
        perMachine ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        "UniGetUI E2E", name);

    private string BuildInno(string output, string name, string appId, string version, string? productVersion = null, bool perMachine = false)
    {
        string build = Path.Combine(_root, "build", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(build, "payload.txt"), $"{name} {version}");
        string numeric = string.Join('.', (productVersion ?? version).Split('.').Concat(["0", "0", "0"]).Take(4));
        string script = string.Join(Environment.NewLine,
            "[Setup]",
            "AppId={" + appId,
            "AppName=" + name,
            "AppVersion=" + version,
            "AppPublisher=" + Vendor,
            "VersionInfoVersion=" + numeric,
            "VersionInfoProductName=" + name,
            "VersionInfoProductTextVersion=" + (productVersion ?? version),
            "VersionInfoCompany=" + Vendor,
            @"DefaultDirName={" + (perMachine ? "autopf" : "localappdata") + @"}" + (perMachine ? "" : @"\Programs") + @"\UniGetUI E2E\" + name,
            "PrivilegesRequired=" + (perMachine ? "admin" : "lowest"),
            "DisableProgramGroupPage=yes",
            "DisableDirPage=yes",
            "Compression=zip",
            "OutputDir=" + build,
            "OutputBaseFilename=setup",
            "",
            "[Files]",
            "Source: \"" + Path.Combine(build, "payload.txt") + "\"; DestDir: \"{app}\"");
        string iss = Path.Combine(build, "setup.iss");
        File.WriteAllText(iss, script);
        var compile = Process.Start(new ProcessStartInfo(WinInstallerEndToEndTests.InnoCompiler!, $"/Q \"{iss}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        string errors = compile.StandardError.ReadToEnd() + compile.StandardOutput.ReadToEnd();
        compile.WaitForExit();
        Assert.True(compile.ExitCode == 0, errors);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.Copy(Path.Combine(build, "setup.exe"), output, overwrite: true);
        _innoFolders.Add(InnoFolder(name, perMachine));
        return output;
    }

    private string? MsiRegistryVersion(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\UniGetUI E2E\" + name);
        return key?.GetValue("Version") as string;
    }

    private string BuildMsi(string path, string name, string version, string upgradeCode, bool perMachine = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string productCode = Guid.NewGuid().ToString("B").ToUpperInvariant();
        _productCodes.Add(productCode);
        _registryKeys.Add(@"Software\UniGetUI E2E\" + name);
        Check(MsiOpenDatabase(path, 3, out nint database));
        try
        {
            string[] tables =
            [
                "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` LONGCHAR NOT NULL LOCALIZABLE PRIMARY KEY `Property`)",
                "CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)",
                "CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)",
                "CREATE TABLE `Registry` (`Registry` CHAR(72) NOT NULL, `Root` SHORT NOT NULL, `Key` CHAR(255) NOT NULL LOCALIZABLE, `Name` CHAR(255) LOCALIZABLE, `Value` LONGCHAR LOCALIZABLE, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Registry`)",
                "CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`)",
                "CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`)",
                "CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` LONG NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)",
                "CREATE TABLE `InstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)",
                "CREATE TABLE `Upgrade` (`UpgradeCode` CHAR(38) NOT NULL, `VersionMin` CHAR(20), `VersionMax` CHAR(20), `Language` CHAR(255), `Attributes` LONG NOT NULL, `Remove` CHAR(255), `ActionProperty` CHAR(72) NOT NULL PRIMARY KEY `UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, `Attributes`)",
            ];
            foreach (string table in tables)
                Execute(database, table);
            var properties = new Dictionary<string, string>
            {
                ["ProductCode"] = productCode,
                ["UpgradeCode"] = upgradeCode,
                ["ProductName"] = name,
                ["ProductVersion"] = version,
                ["Manufacturer"] = Vendor,
                ["ProductLanguage"] = "1033",
                ["SecureCustomProperties"] = "OLDERFOUND",
                ["ALLUSERS"] = perMachine ? "1" : "2",
            };
            if (!perMachine)
                properties["MSIINSTALLPERUSER"] = "1";
            foreach (var (key, value) in properties)
                Execute(database, $"INSERT INTO `Property` (`Property`, `Value`) VALUES ('{key}', '{value}')");
            Execute(database, "INSERT INTO `Directory` (`Directory`, `DefaultDir`) VALUES ('TARGETDIR', 'SourceDir')");
            Execute(database, "INSERT INTO `Media` (`DiskId`, `LastSequence`) VALUES (1, 0)");
            Execute(database, $"INSERT INTO `Component` (`Component`, `ComponentId`, `Directory_`, `Attributes`, `KeyPath`) VALUES ('Main', '{Guid.NewGuid().ToString("B").ToUpperInvariant()}', 'TARGETDIR', 4, 'Version')");
            Execute(database, $"INSERT INTO `Registry` (`Registry`, `Root`, `Key`, `Name`, `Value`, `Component_`) VALUES ('Version', 1, 'Software\\UniGetUI E2E\\{name}', 'Version', '{version}', 'Main')");
            Execute(database, "INSERT INTO `Feature` (`Feature`, `Title`, `Display`, `Level`, `Attributes`) VALUES ('Main', 'Main', 0, 1, 0)");
            Execute(database, "INSERT INTO `FeatureComponents` (`Feature_`, `Component_`) VALUES ('Main', 'Main')");
            Execute(database, $"INSERT INTO `Upgrade` (`UpgradeCode`, `VersionMin`, `VersionMax`, `Attributes`, `ActionProperty`) VALUES ('{upgradeCode}', '0.0.0', '{version}', 256, 'OLDERFOUND')");
            (string Action, int Sequence, string? Condition)[] sequence =
            [
                ("FindRelatedProducts", 25, null), ("CostInitialize", 800, null), ("FileCost", 900, null),
                ("CostFinalize", 1000, null), ("InstallValidate", 1400, null), ("InstallInitialize", 1500, null),
                ("RemoveExistingProducts", 1510, null), ("ProcessComponents", 1600, null), ("UnpublishFeatures", 1800, null),
                ("RemoveRegistryValues", 2600, null), ("WriteRegistryValues", 5000, null), ("RegisterUser", 6000, null),
                ("RegisterProduct", 6100, null), ("PublishFeatures", 6300, null), ("PublishProduct", 6400, null),
                ("InstallFinalize", 6600, null),
            ];
            foreach (var (action, number, condition) in sequence)
                Execute(database, condition is null
                    ? $"INSERT INTO `InstallExecuteSequence` (`Action`, `Sequence`) VALUES ('{action}', {number})"
                    : $"INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) VALUES ('{action}', '{condition}', {number})");

            Check(MsiGetSummaryInformation(database, null, 6, out nint summary));
            Check(MsiSummaryInfoSetProperty(summary, 7, 30, 0, 0, RuntimeInformation.OSArchitecture == Architecture.X86 ? "Intel;1033" : "x64;1033"));
            Check(MsiSummaryInfoSetProperty(summary, 9, 30, 0, 0, Guid.NewGuid().ToString("B").ToUpperInvariant()));
            Check(MsiSummaryInfoSetProperty(summary, 14, 3, 200, 0, null));
            Check(MsiSummaryInfoSetProperty(summary, 15, 3, 2, 0, null));
            Check(MsiSummaryInfoSetProperty(summary, 2, 30, 0, 0, name));
            Check(MsiSummaryInfoSetProperty(summary, 4, 30, 0, 0, Vendor));
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

    private sealed class FolderServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _folder;

        public FolderServer(string folder)
        {
            _folder = folder;
            int port = Random.Shared.Next(40000, 60000);
            Url = $"http://localhost:{port}/feed/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string Url { get; }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception) { return; }
                string relative = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath["/feed/".Length..]);
                string file = Path.Combine(_folder, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(file))
                {
                    byte[] body = File.ReadAllBytes(file);
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                }
                else
                    context.Response.StatusCode = 404;
                context.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
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
}
#endif

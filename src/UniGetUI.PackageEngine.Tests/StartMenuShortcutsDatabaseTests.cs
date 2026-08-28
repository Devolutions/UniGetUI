using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

public sealed class StartMenuShortcutsDatabaseTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(StartMenuShortcutsDatabaseTests),
        Guid.NewGuid().ToString("N")
    );

    private readonly string _userPrograms;
    private readonly string _commonPrograms;

    public StartMenuShortcutsDatabaseTests()
    {
        _userPrograms = Path.Combine(_testRoot, "User", "Programs");
        _commonPrograms = Path.Combine(_testRoot, "Common", "Programs");

        Directory.CreateDirectory(_userPrograms);
        Directory.CreateDirectory(_commonPrograms);

        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Directory.CreateDirectory(CoreData.UniGetUIInstallationOptionsDirectory);
        Settings.ResetSettings();

        StartMenuShortcutsDatabase.TEST_UserProgramsOverride = _userPrograms;
        StartMenuShortcutsDatabase.TEST_CommonProgramsOverride = _commonPrograms;
        StartMenuShortcutsDatabase.ResetDatabase();
    }

    public void Dispose()
    {
        StartMenuShortcutsDatabase.ResetDatabase();
        StartMenuShortcutsDatabase.TEST_UserProgramsOverride = null;
        StartMenuShortcutsDatabase.TEST_CommonProgramsOverride = null;
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private static IPackage BuildPackage(string id = "Contoso.Tool", string name = "Contoso Tool")
    {
        var manager = new PackageManagerBuilder().Build();
        return new PackageBuilder().WithManager(manager).WithId(id).WithName(name).Build();
    }

    private static string CreateShortcut(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "shortcut");
        return path;
    }

    [Fact]
    public void RuleRoundTripAndRemovalWork()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);

        Assert.Null(StartMenuShortcutsDatabase.GetRule(packageId));
        Assert.False(StartMenuShortcutsDatabase.HasRule(package));

        StartMenuShortcutsDatabase.SetRule(packageId, "  Dev Tools  ");

        Assert.Equal("Dev Tools", StartMenuShortcutsDatabase.GetRule(packageId));
        Assert.True(StartMenuShortcutsDatabase.HasRule(package));
        Assert.Equal("Dev Tools", StartMenuShortcutsDatabase.GetRules()[packageId]);

        Assert.True(StartMenuShortcutsDatabase.RemoveRule(packageId));
        Assert.False(StartMenuShortcutsDatabase.RemoveRule(packageId));
        Assert.Null(StartMenuShortcutsDatabase.GetRule(packageId));
    }

    [Fact]
    public void SettingAnEmptyRuleRemovesIt()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);

        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");
        StartMenuShortcutsDatabase.SetRule(packageId, "   ");

        Assert.False(StartMenuShortcutsDatabase.HasRule(package));
    }

    [Fact]
    public void GetShortcutsOnDiskFindsNestedShortcutsOnBothRoots()
    {
        string nested = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");
        string url = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Visit Contoso.url");
        string common = CreateShortcut(_commonPrograms, "Machine Wide App.lnk");
        string ignored = CreateShortcut(_userPrograms, "NotAShortcut.txt");

        var shortcuts = StartMenuShortcutsDatabase.GetShortcutsOnDisk();

        Assert.Contains(nested, shortcuts);
        Assert.Contains(url, shortcuts);
        Assert.Contains(common, shortcuts);
        Assert.DoesNotContain(ignored, shortcuts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("Dev Tools\\..\\..\\Escaped")]
    [InlineData("C:\\Windows\\System32")]
    public void ResolveTargetDirectoryRejectsInvalidFolders(string folder)
    {
        Assert.Null(StartMenuShortcutsDatabase.ResolveTargetDirectory(folder));
    }

    [Fact]
    public void ResolveTargetDirectoryReturnsPathUnderUserPrograms()
    {
        string? resolved = StartMenuShortcutsDatabase.ResolveTargetDirectory("Dev Tools/Editors");

        Assert.Equal(Path.Combine(_userPrograms, "Dev Tools", "Editors"), resolved);
    }

    [Fact]
    public void HandleNewShortcutsRelocatesMatchingShortcutsAndPrunesEmptyFolders()
    {
        var package = BuildPackage();
        StartMenuShortcutsDatabase.SetRule(
            StartMenuShortcutsDatabase.GetIdForPackage(package),
            "Dev Tools"
        );

        var before = StartMenuShortcutsDatabase.GetShortcutsOnDisk();

        string vendorFolder = Path.Combine(_userPrograms, "Contoso");
        string shortcut = CreateShortcut(vendorFolder, "Contoso Tool.lnk");
        string website = CreateShortcut(vendorFolder, "Visit Contoso.url");

        int relocated = StartMenuShortcutsDatabase.HandleNewShortcuts(package, before);

        Assert.Equal(2, relocated);
        Assert.False(File.Exists(shortcut));
        Assert.False(File.Exists(website));
        Assert.False(Directory.Exists(vendorFolder));
        Assert.True(File.Exists(Path.Combine(_userPrograms, "Dev Tools", "Contoso Tool.lnk")));
        Assert.True(File.Exists(Path.Combine(_userPrograms, "Dev Tools", "Visit Contoso.url")));
    }

    [Fact]
    public void HandleNewShortcutsLeavesUnrelatedAndPreexistingShortcutsAlone()
    {
        var package = BuildPackage();
        StartMenuShortcutsDatabase.SetRule(
            StartMenuShortcutsDatabase.GetIdForPackage(package),
            "Dev Tools"
        );

        string preexisting = CreateShortcut(_userPrograms, "Contoso Tool.lnk");
        var before = StartMenuShortcutsDatabase.GetShortcutsOnDisk();

        string unrelated = CreateShortcut(Path.Combine(_userPrograms, "Fabrikam"), "Fabrikam.lnk");

        int relocated = StartMenuShortcutsDatabase.HandleNewShortcuts(package, before);

        Assert.Equal(0, relocated);
        Assert.True(File.Exists(preexisting));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void HandleNewShortcutsDoesNothingWithoutARule()
    {
        var package = BuildPackage();
        var before = StartMenuShortcutsDatabase.GetShortcutsOnDisk();
        string shortcut = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");

        Assert.Equal(0, StartMenuShortcutsDatabase.HandleNewShortcuts(package, before));
        Assert.True(File.Exists(shortcut));
    }

    [Fact]
    public void HandleNewShortcutsIgnoresAnInvalidRule()
    {
        var package = BuildPackage();
        StartMenuShortcutsDatabase.SetRule(
            StartMenuShortcutsDatabase.GetIdForPackage(package),
            "..\\Escaped"
        );

        var before = StartMenuShortcutsDatabase.GetShortcutsOnDisk();
        string shortcut = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");

        Assert.Equal(0, StartMenuShortcutsDatabase.HandleNewShortcuts(package, before));
        Assert.True(File.Exists(shortcut));
    }

    [Fact]
    public void ReplayRelocationsMovesRecreatedShortcutsBack()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        var before = StartMenuShortcutsDatabase.GetShortcutsOnDisk();
        CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");
        StartMenuShortcutsDatabase.HandleNewShortcuts(package, before);

        string recreated = CreateShortcut(
            Path.Combine(_userPrograms, "Contoso"),
            "Contoso Tool.lnk"
        );

        Assert.Equal(1, StartMenuShortcutsDatabase.ReplayRelocations(packageId));
        Assert.False(File.Exists(recreated));
        Assert.True(File.Exists(Path.Combine(_userPrograms, "Dev Tools", "Contoso Tool.lnk")));
    }

    [Fact]
    public void RelocationsAreRecordedPerPackage()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        var before = StartMenuShortcutsDatabase.GetShortcutsOnDisk();
        string shortcut = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");
        StartMenuShortcutsDatabase.HandleNewShortcuts(package, before);

        var relocations = StartMenuShortcutsDatabase.GetRelocationsForPackage(packageId);

        Assert.Single(relocations);
        Assert.Equal(shortcut, relocations[0].OriginalPath);
        Assert.Equal(
            Path.Combine(_userPrograms, "Dev Tools", "Contoso Tool.lnk"),
            relocations[0].RelocatedPath
        );
        Assert.Empty(StartMenuShortcutsDatabase.GetRelocationsForPackage("other\\Package"));
    }

    [Fact]
    public void CleanupForPackageDeletesRelocatedShortcutsAndForgetsThem()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        var before = StartMenuShortcutsDatabase.GetShortcutsOnDisk();
        CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");
        StartMenuShortcutsDatabase.HandleNewShortcuts(package, before);

        int deleted = StartMenuShortcutsDatabase.CleanupForPackage(packageId);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(_userPrograms, "Dev Tools", "Contoso Tool.lnk")));
        Assert.False(Directory.Exists(Path.Combine(_userPrograms, "Dev Tools")));
        Assert.Empty(StartMenuShortcutsDatabase.GetRelocationsForPackage(packageId));
    }

    [Fact]
    public void StatusRoundTripAndGetAllShortcutsIncludeTrackedPaths()
    {
        string shortcut = Path.Combine(_userPrograms, "Gone", "Contoso Tool.lnk");

        Assert.Equal(StartMenuShortcutsDatabase.Status.Unknown, StartMenuShortcutsDatabase.GetStatus(shortcut));

        StartMenuShortcutsDatabase.SetStatus(shortcut, StartMenuShortcutsDatabase.Status.Delete);
        Assert.Equal(StartMenuShortcutsDatabase.Status.Delete, StartMenuShortcutsDatabase.GetStatus(shortcut));
        Assert.Contains(shortcut, StartMenuShortcutsDatabase.GetAllShortcuts());

        StartMenuShortcutsDatabase.SetStatus(shortcut, StartMenuShortcutsDatabase.Status.Maintain);
        Assert.Equal(StartMenuShortcutsDatabase.Status.Maintain, StartMenuShortcutsDatabase.GetStatus(shortcut));

        StartMenuShortcutsDatabase.SetStatus(shortcut, StartMenuShortcutsDatabase.Status.Unknown);
        Assert.Equal(StartMenuShortcutsDatabase.Status.Unknown, StartMenuShortcutsDatabase.GetStatus(shortcut));
        Assert.DoesNotContain(shortcut, StartMenuShortcutsDatabase.GetVerdicts().Keys);
    }

    [Fact]
    public void HandleNewShortcutsDeletesShortcutsMarkedForDeletion()
    {
        var package = BuildPackage();
        string website = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Visit Contoso.url");
        StartMenuShortcutsDatabase.SetStatus(website, StartMenuShortcutsDatabase.Status.Delete);

        var before = StartMenuShortcutsDatabase.GetShortcutsOnDisk();

        Assert.Equal(1, StartMenuShortcutsDatabase.HandleNewShortcuts(package, before));
        Assert.False(File.Exists(website));
    }

    [Fact]
    public void HandleNewShortcutsMarksNewShortcutsAsPendingWhenAsking()
    {
        Settings.Set(Settings.K.AskAboutNewStartMenuShortcuts, true);

        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        var before = StartMenuShortcutsDatabase.GetShortcutsOnDisk();
        string shortcut = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");

        StartMenuShortcutsDatabase.HandleNewShortcuts(package, before);

        var pending = StartMenuShortcutsDatabase.GetPendingShortcuts();
        Assert.Single(pending);
        Assert.Equal(packageId, pending[0].PackageId);
        Assert.Equal(shortcut, pending[0].ShortcutPath);
        Assert.True(File.Exists(shortcut));

        Assert.True(StartMenuShortcutsDatabase.RemoveFromPending(packageId, shortcut));
        Assert.Empty(StartMenuShortcutsDatabase.GetPendingShortcuts());
    }

    [Fact]
    public void HandleNewShortcutsDoesNotAskWhenAskingIsDisabled()
    {
        var package = BuildPackage();
        var before = StartMenuShortcutsDatabase.GetShortcutsOnDisk();
        CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");

        StartMenuShortcutsDatabase.HandleNewShortcuts(package, before);

        Assert.Empty(StartMenuShortcutsDatabase.GetPendingShortcuts());
    }

    [Fact]
    public void PendingShortcutsForgetFilesThatNoLongerExist()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        string shortcut = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");

        StartMenuShortcutsDatabase.MarkPending(packageId, shortcut);
        Assert.Single(StartMenuShortcutsDatabase.GetPendingShortcuts());

        File.Delete(shortcut);
        Assert.Empty(StartMenuShortcutsDatabase.GetPendingShortcuts());
    }

    [Fact]
    public void ApplyRuleRelocatesTheGivenShortcuts()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        string shortcut = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");

        Assert.Equal(0, StartMenuShortcutsDatabase.ApplyRule(packageId, [shortcut]));

        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        Assert.Equal(1, StartMenuShortcutsDatabase.ApplyRule(packageId, [shortcut]));
        Assert.False(File.Exists(shortcut));
        Assert.True(File.Exists(Path.Combine(_userPrograms, "Dev Tools", "Contoso Tool.lnk")));
        Assert.Single(StartMenuShortcutsDatabase.GetRelocationsForPackage(packageId));
    }

    [Fact]
    public void ShouldTrackShortcutsFollowsRulesAskingAndDeleteVerdicts()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);

        Assert.False(StartMenuShortcutsDatabase.ShouldTrackShortcuts(package));

        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");
        Assert.True(StartMenuShortcutsDatabase.ShouldTrackShortcuts(package));
        StartMenuShortcutsDatabase.RemoveRule(packageId);

        Settings.Set(Settings.K.AskAboutNewStartMenuShortcuts, true);
        Assert.True(StartMenuShortcutsDatabase.ShouldTrackShortcuts(package));
        Settings.Set(Settings.K.AskAboutNewStartMenuShortcuts, false);
        Assert.False(StartMenuShortcutsDatabase.ShouldTrackShortcuts(package));

        StartMenuShortcutsDatabase.SetStatus(
            Path.Combine(_userPrograms, "Anything.lnk"),
            StartMenuShortcutsDatabase.Status.Delete
        );
        Assert.True(StartMenuShortcutsDatabase.ShouldTrackShortcuts(package));
    }

    [Fact]
    public void FindRelocatableShortcutsMatchesExistingShortcutsOfThePackage()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);

        string appShortcut = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");
        string website = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Visit Contoso.url");
        string unrelated = CreateShortcut(Path.Combine(_userPrograms, "Fabrikam"), "Fabrikam.lnk");

        var candidates = StartMenuShortcutsDatabase.FindRelocatableShortcuts(packageId);

        Assert.Contains(appShortcut, candidates);
        Assert.Contains(website, candidates);
        Assert.DoesNotContain(unrelated, candidates);
    }

    [Fact]
    public void FindRelocatableShortcutsSkipsShortcutsAlreadyInTheTargetFolder()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        CreateShortcut(Path.Combine(_userPrograms, "Dev Tools"), "Contoso Tool.lnk");
        string outside = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Helper.lnk");

        var candidates = StartMenuShortcutsDatabase.FindRelocatableShortcuts(packageId);

        Assert.Equal([outside], candidates);
    }

    [Fact]
    public void FindRelocatableShortcutsIgnoresShortcutsItAlreadyRelocated()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        string shortcut = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");
        Assert.Equal(1, StartMenuShortcutsDatabase.ApplyRule(packageId, [shortcut]));

        Assert.Empty(StartMenuShortcutsDatabase.FindRelocatableShortcuts(packageId));
    }

    [Fact]
    public void FindRelocatableShortcutsWorksFromThePackageIdentityAlone()
    {
        string packageId = "winget\\Python.Python.3.13";
        string shortcut = CreateShortcut(Path.Combine(_userPrograms, "Python 3.13"), "IDLE.lnk");
        string other = CreateShortcut(Path.Combine(_userPrograms, "Fabrikam"), "Fabrikam.lnk");

        var candidates = StartMenuShortcutsDatabase.FindRelocatableShortcuts(packageId);

        Assert.Contains(shortcut, candidates);
        Assert.DoesNotContain(other, candidates);
    }

    [Fact]
    public void HandleNewShortcutsStillLeavesPreexistingShortcutsForConfirmation()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        string preexisting = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");
        var before = StartMenuShortcutsDatabase.GetShortcutsOnDisk();

        Assert.Equal(0, StartMenuShortcutsDatabase.HandleNewShortcuts(package, before));
        Assert.True(File.Exists(preexisting));
        Assert.Contains(preexisting, StartMenuShortcutsDatabase.FindRelocatableShortcuts(packageId));
    }

    [Fact]
    public void GetShortcutsOnDiskSkipsHiddenShortcuts()
    {
        string visible = CreateShortcut(_userPrograms, "Visible.lnk");
        string hidden = CreateShortcut(_userPrograms, "Hidden.lnk");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        var shortcuts = StartMenuShortcutsDatabase.GetShortcutsOnDisk();

        Assert.Contains(visible, shortcuts);
        Assert.DoesNotContain(hidden, shortcuts);
    }

    [Fact]
    public void GetTrackedShortcutsOnlyReturnsShortcutsUniGetUIHasActedOn()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);

        string untouched = CreateShortcut(Path.Combine(_userPrograms, "Windows Kits"), "Some Tool.lnk");
        string verdicted = CreateShortcut(_userPrograms, "Judged.lnk");
        string pending = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");
        string relocatable = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Helper.lnk");

        StartMenuShortcutsDatabase.SetStatus(verdicted, StartMenuShortcutsDatabase.Status.Maintain);
        StartMenuShortcutsDatabase.MarkPending(packageId, pending);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");
        StartMenuShortcutsDatabase.ApplyRule(packageId, [relocatable]);

        var tracked = StartMenuShortcutsDatabase.GetTrackedShortcuts();

        Assert.Contains(verdicted, tracked);
        Assert.Contains(pending, tracked);
        Assert.Contains(Path.Combine(_userPrograms, "Dev Tools", "Contoso Helper.lnk"), tracked);
        Assert.DoesNotContain(untouched, tracked);
        Assert.Contains(untouched, StartMenuShortcutsDatabase.GetAllShortcuts());
    }

    [Fact]
    public void GetAllRelocatedShortcutsReportsEveryRecordedDestination()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        string first = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");
        string second = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Helper.lnk");
        StartMenuShortcutsDatabase.ApplyRule(packageId, [first, second]);

        var relocated = StartMenuShortcutsDatabase.GetAllRelocatedShortcuts();

        Assert.Equal(2, relocated.Count);
        Assert.All(relocated, path => Assert.StartsWith(Path.Combine(_userPrograms, "Dev Tools"), path));
    }

    [Fact]
    public void GetUserProgramFoldersListsExistingFoldersRelativeToPrograms()
    {
        Directory.CreateDirectory(Path.Combine(_userPrograms, "Dev Tools", "Editors"));
        Directory.CreateDirectory(Path.Combine(_userPrograms, "Contoso"));
        Directory.CreateDirectory(Path.Combine(_commonPrograms, "Machine Wide"));

        var folders = StartMenuShortcutsDatabase.GetUserProgramFolders();

        Assert.Contains("Contoso", folders);
        Assert.Contains("Dev Tools", folders);
        Assert.Contains(Path.Combine("Dev Tools", "Editors"), folders);
        Assert.DoesNotContain("Machine Wide", folders);
        Assert.DoesNotContain(_userPrograms, folders);
    }

    [Fact]
    public void GetUserProgramFoldersOffersOnlyValidRuleTargets()
    {
        Directory.CreateDirectory(Path.Combine(_userPrograms, "Dev Tools", "Editors"));

        foreach (var folder in StartMenuShortcutsDatabase.GetUserProgramFolders())
        {
            Assert.NotNull(StartMenuShortcutsDatabase.ResolveTargetDirectory(folder));
        }
    }

    [Theory]
    [InlineData("Adobe Digital Editions.lnk", "Adobe")]
    [InlineData("Legit Software.lnk", "Legit")]
    public void ShortIdentifiersNoLongerMatchByAccident(string fileName, string folder)
    {
        string packageId = "winget\\Git.Git";
        string unrelated = CreateShortcut(Path.Combine(_userPrograms, folder), fileName);

        Assert.DoesNotContain(
            unrelated,
            StartMenuShortcutsDatabase.FindRelocatableShortcuts(packageId)
        );
    }

    [Fact]
    public void ShortIdentifiersStillMatchTheirOwnShortcuts()
    {
        string packageId = "winget\\Git.Git";
        string own = CreateShortcut(Path.Combine(_userPrograms, "Git"), "Git Bash.lnk");

        Assert.Contains(own, StartMenuShortcutsDatabase.FindRelocatableShortcuts(packageId));
    }

    [Fact]
    public void MachineWideShortcutsAreNeverRelocated()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        string machineWide = CreateShortcut(
            Path.Combine(_commonPrograms, "Contoso"),
            "Contoso Tool.lnk"
        );

        Assert.DoesNotContain(
            machineWide,
            StartMenuShortcutsDatabase.FindRelocatableShortcuts(packageId)
        );
        Assert.Equal(0, StartMenuShortcutsDatabase.ApplyRule(packageId, [machineWide]));
        Assert.True(File.Exists(machineWide));
        Assert.True(Directory.Exists(Path.Combine(_commonPrograms, "Contoso")));

        var before = StartMenuShortcutsDatabase.GetShortcutsOnDisk();
        before.Remove(machineWide);

        StartMenuShortcutsDatabase.HandleNewShortcuts(package, before);
        Assert.True(File.Exists(machineWide));
    }

    [Fact]
    public void PruningNeverTouchesTheMachineWideTree()
    {
        string machineWide = CreateShortcut(
            Path.Combine(_commonPrograms, "Contoso"),
            "Contoso Tool.lnk"
        );

        Assert.True(StartMenuShortcutsDatabase.DeleteFromDisk(machineWide));
        Assert.True(Directory.Exists(Path.Combine(_commonPrograms, "Contoso")));
    }

    [Fact]
    public void RelocatingDoesNotOverwriteAShortcutAlreadyInTheTarget()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        string occupied = CreateShortcut(
            Path.Combine(_userPrograms, "Dev Tools"),
            "Contoso Tool.lnk"
        );
        File.WriteAllText(occupied, "the one that was already there");

        string incoming = CreateShortcut(
            Path.Combine(_userPrograms, "Contoso"),
            "Contoso Tool.lnk"
        );

        Assert.Equal(1, StartMenuShortcutsDatabase.ApplyRule(packageId, [incoming]));
        Assert.Equal("the one that was already there", File.ReadAllText(occupied));
        Assert.True(
            File.Exists(Path.Combine(_userPrograms, "Dev Tools", "Contoso Tool (2).lnk"))
        );
    }

    [Fact]
    public void ReplayStillOverwritesItsOwnRecordedDestination()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        string shortcut = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");
        StartMenuShortcutsDatabase.ApplyRule(packageId, [shortcut]);

        CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");

        Assert.Equal(1, StartMenuShortcutsDatabase.ReplayRelocations(packageId));
        Assert.Single(
            Directory.GetFiles(Path.Combine(_userPrograms, "Dev Tools"), "Contoso Tool*.lnk")
        );
    }

    [Fact]
    public void VerdictsAndRelocationRecordsIgnorePathCasing()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);

        string shortcut = CreateShortcut(_userPrograms, "Contoso Tool.lnk");
        StartMenuShortcutsDatabase.SetStatus(
            shortcut.ToUpperInvariant(),
            StartMenuShortcutsDatabase.Status.Delete
        );

        Assert.Equal(
            StartMenuShortcutsDatabase.Status.Delete,
            StartMenuShortcutsDatabase.GetStatus(shortcut)
        );
        Assert.Single(StartMenuShortcutsDatabase.GetAllShortcuts());

        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");
        string moved = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Helper.lnk");
        StartMenuShortcutsDatabase.ApplyRule(packageId, [moved]);

        Assert.Single(
            StartMenuShortcutsDatabase.GetRelocationsForPackage(packageId.ToUpperInvariant())
        );
    }

    [Fact]
    public void IsManagedShortcutPathOnlyAcceptsStartMenuPaths()
    {
        Assert.True(
            StartMenuShortcutsDatabase.IsManagedShortcutPath(
                Path.Combine(_userPrograms, "Contoso Tool.lnk")
            )
        );
        Assert.True(
            StartMenuShortcutsDatabase.IsManagedShortcutPath(
                Path.Combine(_commonPrograms, "Machine Wide.lnk")
            )
        );
        Assert.False(
            StartMenuShortcutsDatabase.IsManagedShortcutPath(Path.Combine(_testRoot, "taxes.xlsx"))
        );
        Assert.False(StartMenuShortcutsDatabase.IsManagedShortcutPath(_userPrograms));
    }

    [Fact]
    public void PendingRecordsForVanishedShortcutsArePruned()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        string shortcut = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");

        StartMenuShortcutsDatabase.MarkPending(packageId, shortcut);
        File.Delete(shortcut);

        Assert.Empty(StartMenuShortcutsDatabase.GetPendingShortcuts());
        Assert.False(StartMenuShortcutsDatabase.RemoveFromPending(packageId, shortcut));
    }

    [Theory]
    [InlineData("Python", "Python.lnk")]
    [InlineData("  Python  ", "Python.lnk")]
    [InlineData("Python.lnk", "Python.lnk")]
    [InlineData("Py:th*on?", "Python.lnk")]
    [InlineData("", "IDLE (Python 3.13).lnk")]
    [InlineData("   ", "IDLE (Python 3.13).lnk")]
    [InlineData(":::", "IDLE (Python 3.13).lnk")]
    public void BuildFileNameKeepsTheExtensionAndRefusesJunk(string newName, string expected)
    {
        Assert.Equal(
            expected,
            StartMenuShortcutsDatabase.BuildFileName(
                Path.Combine(_userPrograms, "IDLE (Python 3.13).lnk"),
                newName
            )
        );
    }

    [Fact]
    public void BuildFileNameCannotEscapeTheTargetFolder()
    {
        string built = StartMenuShortcutsDatabase.BuildFileName(
            Path.Combine(_userPrograms, "App.lnk"),
            "..\\..\\evil"
        );

        Assert.Equal("evil.lnk", built);
        Assert.Equal(built, Path.GetFileName(built));
    }

    [Fact]
    public void ApplyRuleRenamesWhileRelocating()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        string shortcut = CreateShortcut(
            Path.Combine(_userPrograms, "Contoso"),
            "Contoso Tool (ARM64).lnk"
        );

        Assert.Equal(1, StartMenuShortcutsDatabase.ApplyRule(packageId, [(shortcut, "Contoso")]));

        Assert.False(File.Exists(shortcut));
        Assert.True(File.Exists(Path.Combine(_userPrograms, "Dev Tools", "Contoso.lnk")));

        var relocations = StartMenuShortcutsDatabase.GetRelocationsForPackage(packageId);
        Assert.Equal(
            Path.Combine(_userPrograms, "Dev Tools", "Contoso.lnk"),
            relocations[0].RelocatedPath
        );
    }

    [Fact]
    public void ARenameSurvivesTheNextUpgrade()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        string shortcut = CreateShortcut(
            Path.Combine(_userPrograms, "Contoso"),
            "Contoso Tool (ARM64).lnk"
        );
        StartMenuShortcutsDatabase.ApplyRule(packageId, [(shortcut, "Contoso")]);

        CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool (ARM64).lnk");

        Assert.Equal(1, StartMenuShortcutsDatabase.ReplayRelocations(packageId));
        Assert.True(File.Exists(Path.Combine(_userPrograms, "Dev Tools", "Contoso.lnk")));
        Assert.False(
            File.Exists(Path.Combine(_userPrograms, "Dev Tools", "Contoso Tool (ARM64).lnk"))
        );
    }

    [Fact]
    public void RenamingInPlaceIsStillApplied()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        string alreadyInTarget = CreateShortcut(
            Path.Combine(_userPrograms, "Dev Tools"),
            "Contoso Tool (ARM64).lnk"
        );

        Assert.Equal(
            1,
            StartMenuShortcutsDatabase.ApplyRule(packageId, [(alreadyInTarget, "Contoso")])
        );
        Assert.False(File.Exists(alreadyInTarget));
        Assert.True(File.Exists(Path.Combine(_userPrograms, "Dev Tools", "Contoso.lnk")));
    }

    [Fact]
    public void DroppingTheFolderStopsRelocatingButKeepsTheRecordsForCleanup()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        string shortcut = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");
        StartMenuShortcutsDatabase.ApplyRule(packageId, [shortcut]);

        StartMenuShortcutsDatabase.RemoveRule(packageId);

        var before = StartMenuShortcutsDatabase.GetShortcutsOnDisk();
        string recreated = CreateShortcut(
            Path.Combine(_userPrograms, "Contoso"),
            "Contoso Tool.lnk"
        );

        StartMenuShortcutsDatabase.HandleNewShortcuts(package, before);

        Assert.True(File.Exists(recreated));
        Assert.Single(StartMenuShortcutsDatabase.GetRelocationsForPackage(packageId));
        Assert.Equal(1, StartMenuShortcutsDatabase.CleanupForPackage(packageId));
    }

    [Fact]
    public void DeletingARelocatedShortcutKeepsItDeletedOnTheNextUpgrade()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        string shortcut = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");
        StartMenuShortcutsDatabase.ApplyRule(packageId, [shortcut]);

        string relocated = Path.Combine(_userPrograms, "Dev Tools", "Contoso Tool.lnk");
        StartMenuShortcutsDatabase.SetStatus(relocated, StartMenuShortcutsDatabase.Status.Delete);
        StartMenuShortcutsDatabase.DeleteFromDisk(relocated);

        var before = StartMenuShortcutsDatabase.GetShortcutsOnDisk();
        string recreated = CreateShortcut(
            Path.Combine(_userPrograms, "Contoso"),
            "Contoso Tool.lnk"
        );

        StartMenuShortcutsDatabase.HandleNewShortcuts(package, before);

        Assert.False(File.Exists(recreated));
        Assert.False(File.Exists(relocated));
    }

    [Fact]
    public void ApplyRuleOnlyReportsTheShortcutsItCouldHandle()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        string locked = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");
        string machineWide = CreateShortcut(
            Path.Combine(_commonPrograms, "Contoso"),
            "Contoso Helper.lnk"
        );

        using (
            new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
        )
        {
            Assert.Equal(
                0,
                StartMenuShortcutsDatabase.ApplyRule(
                    packageId,
                    [locked, machineWide],
                    out var handled
                )
            );

            Assert.DoesNotContain(locked, handled);
            Assert.Contains(machineWide, handled);
        }
    }

    [Fact]
    public void ResettingTheShortcutStatusesKeepsTheFolderRules()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        string shortcut = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");
        StartMenuShortcutsDatabase.ApplyRule(packageId, [shortcut]);
        StartMenuShortcutsDatabase.SetStatus(
            CreateShortcut(_userPrograms, "Contoso Helper.lnk"),
            StartMenuShortcutsDatabase.Status.Delete
        );

        StartMenuShortcutsDatabase.ResetShortcutStatuses();

        Assert.Empty(StartMenuShortcutsDatabase.GetVerdicts());
        Assert.Equal("Dev Tools", StartMenuShortcutsDatabase.GetRule(packageId));
        Assert.Single(StartMenuShortcutsDatabase.GetRelocationsForPackage(packageId));
    }

    [Fact]
    public void OnlyShortcutFilesAreRecognized()
    {
        Assert.True(StartMenuShortcutsDatabase.IsShortcutFile("Contoso Tool.LNK"));
        Assert.True(StartMenuShortcutsDatabase.IsShortcutFile("Contoso Tool.url"));
        Assert.False(StartMenuShortcutsDatabase.IsShortcutFile("Contoso Tool.exe"));
        Assert.False(StartMenuShortcutsDatabase.IsShortcutFile("Contoso Tool"));
    }

    [Fact]
    public void ShortcutsOfAnotherProductAreNotClaimedByAShortIdentifier()
    {
        var package = BuildPackage("Git.Git", "Git");
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        string other = CreateShortcut(_userPrograms, "GitHub Desktop.lnk");
        string own = CreateShortcut(_userPrograms, "Git Bash.lnk");

        var relocatable = StartMenuShortcutsDatabase.FindRelocatableShortcuts(packageId);

        Assert.DoesNotContain(other, relocatable);
        Assert.Contains(own, relocatable);
    }

    [Fact]
    public void CleanupKeepsTheRecordOfAShortcutItCouldNotDelete()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        string shortcut = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");
        StartMenuShortcutsDatabase.ApplyRule(packageId, [shortcut]);

        string relocated = Path.Combine(_userPrograms, "Dev Tools", "Contoso Tool.lnk");

        using (new FileStream(relocated, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(0, StartMenuShortcutsDatabase.CleanupForPackage(packageId));
            Assert.Single(StartMenuShortcutsDatabase.GetRelocationsForPackage(packageId));
        }

        Assert.Equal(1, StartMenuShortcutsDatabase.CleanupForPackage(packageId));
        Assert.Empty(StartMenuShortcutsDatabase.GetRelocationsForPackage(packageId));
    }

    [Fact]
    public void SettingAStatusResolvesThePendingReview()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);

        string shortcut = CreateShortcut(_userPrograms, "Contoso Tool.lnk");
        StartMenuShortcutsDatabase.MarkPending(packageId, shortcut);

        Assert.Single(StartMenuShortcutsDatabase.GetPendingShortcuts());
        Assert.Equal(1, StartMenuShortcutsDatabase.RemovePendingShortcuts(shortcut));
        Assert.Empty(StartMenuShortcutsDatabase.GetPendingShortcuts());
    }

    [Fact]
    public void ChangingTheFolderMovesWhatTheOldOneHadTaken()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        string shortcut = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");
        StartMenuShortcutsDatabase.ApplyRule(packageId, [shortcut]);

        StartMenuShortcutsDatabase.SetRule(packageId, "Utilities");

        Assert.Equal(1, StartMenuShortcutsDatabase.RebaseRelocations(packageId));

        string moved = Path.Combine(_userPrograms, "Utilities", "Contoso Tool.lnk");
        Assert.True(File.Exists(moved));
        Assert.False(File.Exists(Path.Combine(_userPrograms, "Dev Tools", "Contoso Tool.lnk")));
        Assert.Equal(
            moved,
            StartMenuShortcutsDatabase.GetRelocationsForPackage(packageId).Single().RelocatedPath
        );
    }

    [Fact]
    public void ReplayFollowsTheFolderThePackageHasNow()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        StartMenuShortcutsDatabase.SetRule(packageId, "Dev Tools");

        string shortcut = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");
        StartMenuShortcutsDatabase.ApplyRule(packageId, [shortcut]);

        StartMenuShortcutsDatabase.SetRule(packageId, "Utilities");
        CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");

        Assert.Equal(1, StartMenuShortcutsDatabase.ReplayRelocations(packageId));
        Assert.True(File.Exists(Path.Combine(_userPrograms, "Utilities", "Contoso Tool.lnk")));
    }

    [Fact]
    public void AShortcutAnotherPackageOwnsIsLeftAlone()
    {
        var owner = BuildPackage("Contoso.Tool", "Contoso Tool");
        string ownerId = StartMenuShortcutsDatabase.GetIdForPackage(owner);
        StartMenuShortcutsDatabase.SetRule(ownerId, "Dev Tools");

        string shortcut = CreateShortcut(Path.Combine(_userPrograms, "Contoso"), "Contoso Tool.lnk");
        StartMenuShortcutsDatabase.ApplyRule(ownerId, [shortcut]);

        string relocated = Path.Combine(_userPrograms, "Dev Tools", "Contoso Tool.lnk");
        Assert.True(File.Exists(relocated));

        var other = BuildPackage("Contoso.Tool.Helper", "Contoso Tool Helper");
        string otherId = StartMenuShortcutsDatabase.GetIdForPackage(other);
        StartMenuShortcutsDatabase.SetRule(otherId, "Helpers");

        Assert.DoesNotContain(
            relocated,
            StartMenuShortcutsDatabase.FindRelocatableShortcuts(otherId)
        );

        StartMenuShortcutsDatabase.HandleNewShortcuts(other, []);

        Assert.True(File.Exists(relocated));
        Assert.Empty(StartMenuShortcutsDatabase.GetRelocationsForPackage(otherId));
    }

    [Fact]
    public void TheClosestNameWinsAShortcutTwoPackagesResemble()
    {
        var tool = BuildPackage("Contoso.Tool", "Contoso Tool");
        var helper = BuildPackage("Contoso.Tool.Helper", "Contoso Tool Helper");
        string toolId = StartMenuShortcutsDatabase.GetIdForPackage(tool);
        string helperId = StartMenuShortcutsDatabase.GetIdForPackage(helper);

        StartMenuShortcutsDatabase.SetRule(toolId, "Dev Tools");
        StartMenuShortcutsDatabase.SetRule(helperId, "Helpers");

        var before = StartMenuShortcutsDatabase.GetShortcutsOnDisk();
        CreateShortcut(_userPrograms, "Contoso Tool.lnk");
        CreateShortcut(_userPrograms, "Contoso Tool Helper.lnk");

        StartMenuShortcutsDatabase.HandleNewShortcuts(tool, before);

        Assert.True(File.Exists(Path.Combine(_userPrograms, "Dev Tools", "Contoso Tool.lnk")));
        Assert.True(File.Exists(Path.Combine(_userPrograms, "Contoso Tool Helper.lnk")));

        StartMenuShortcutsDatabase.HandleNewShortcuts(helper, before);

        Assert.True(
            File.Exists(Path.Combine(_userPrograms, "Helpers", "Contoso Tool Helper.lnk"))
        );
    }

    [Fact]
    public void ShortcutsBehindAJunctionAreNotManaged()
    {
        string outside = Path.Combine(_testRoot, "Outside");
        Directory.CreateDirectory(outside);
        string target = CreateShortcut(outside, "Elsewhere.lnk");

        string link = Path.Combine(_userPrograms, "Escape");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception)
        {
            return;
        }

        string throughLink = Path.Combine(link, "Elsewhere.lnk");

        Assert.True(File.Exists(throughLink));
        Assert.False(StartMenuShortcutsDatabase.IsManagedShortcutPath(throughLink));
        Assert.DoesNotContain(throughLink, StartMenuShortcutsDatabase.GetShortcutsOnDisk());
        Assert.Null(StartMenuShortcutsDatabase.ResolveTargetDirectory("Escape"));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void ARuleWrittenWithAnotherCasingIsStillTheSameRule()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);

        StartMenuShortcutsDatabase.SetRule(packageId.ToUpperInvariant(), "Dev Tools");

        Assert.Equal("Dev Tools", StartMenuShortcutsDatabase.GetRule(packageId));
        Assert.True(StartMenuShortcutsDatabase.HasRule(package));

        StartMenuShortcutsDatabase.SetRule(packageId.ToLowerInvariant(), "Utilities");

        Assert.Single(StartMenuShortcutsDatabase.GetRules());
        Assert.Equal("Utilities", StartMenuShortcutsDatabase.GetRule(packageId));

        Assert.True(StartMenuShortcutsDatabase.RemoveRule(packageId));
        Assert.Empty(StartMenuShortcutsDatabase.GetRules());
    }

    [Fact]
    public void ARuleWrittenWithAnotherCasingStillRelocates()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);

        StartMenuShortcutsDatabase.SetRule(packageId.ToUpperInvariant(), "Dev Tools");

        var before = StartMenuShortcutsDatabase.GetShortcutsOnDisk();
        CreateShortcut(_userPrograms, "Contoso Tool.lnk");

        Assert.Equal(1, StartMenuShortcutsDatabase.HandleNewShortcuts(package, before));
        Assert.True(File.Exists(Path.Combine(_userPrograms, "Dev Tools", "Contoso Tool.lnk")));
    }

    [Fact]
    public void APendingReviewIsFoundWhicheverCasingAsksForIt()
    {
        var package = BuildPackage();
        string packageId = StartMenuShortcutsDatabase.GetIdForPackage(package);
        string shortcut = CreateShortcut(_userPrograms, "Contoso Tool.lnk");

        StartMenuShortcutsDatabase.MarkPending(packageId, shortcut);
        StartMenuShortcutsDatabase.MarkPending(packageId.ToUpperInvariant(), shortcut);

        Assert.Single(StartMenuShortcutsDatabase.GetPendingShortcuts());
        Assert.True(
            StartMenuShortcutsDatabase.RemoveFromPending(
                packageId.ToUpperInvariant(),
                shortcut.ToUpperInvariant()
            )
        );
        Assert.Empty(StartMenuShortcutsDatabase.GetPendingShortcuts());
    }

    [Fact]
    public void PruningNeverDeletesTheStartMenuRoots()
    {
        string shortcut = CreateShortcut(_userPrograms, "Contoso Tool.lnk");

        Assert.True(StartMenuShortcutsDatabase.DeleteFromDisk(shortcut));
        Assert.True(Directory.Exists(_userPrograms));
    }
}

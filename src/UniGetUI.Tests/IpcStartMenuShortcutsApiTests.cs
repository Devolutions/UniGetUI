using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Interface;
using UniGetUI.PackageEngine.Classes.Packages.Classes;

namespace UniGetUI.Tests;

public sealed class IpcStartMenuShortcutsApiTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(IpcStartMenuShortcutsApiTests),
        Guid.NewGuid().ToString("N")
    );

    private readonly string _userPrograms;

    public IpcStartMenuShortcutsApiTests()
    {
        _userPrograms = Path.Combine(_testRoot, "User", "Programs");
        Directory.CreateDirectory(_userPrograms);
        Directory.CreateDirectory(Path.Combine(_testRoot, "Common", "Programs"));

        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();

        StartMenuShortcutsDatabase.TEST_UserProgramsOverride = _userPrograms;
        StartMenuShortcutsDatabase.TEST_CommonProgramsOverride = Path.Combine(
            _testRoot,
            "Common",
            "Programs"
        );
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

    [Fact]
    public void SetShortcutStoresTheCanonicalPath()
    {
        string vendor = Path.Combine(_userPrograms, "Vendor");
        Directory.CreateDirectory(vendor);

        string canonical = Path.Combine(_userPrograms, "Contoso Tool.lnk");
        File.WriteAllText(canonical, "shortcut");

        var result = IpcStartMenuShortcutsApi.SetShortcut(
            new IpcStartMenuShortcutRequest
            {
                Path = Path.Combine(vendor, "..", "Contoso Tool.lnk"),
                Status = "keep",
            }
        );

        Assert.Equal(canonical, result.Shortcut?.Path);
        Assert.Equal(canonical, StartMenuShortcutsDatabase.GetVerdicts().Keys.Single());
        Assert.Equal(
            StartMenuShortcutsDatabase.Status.Maintain,
            StartMenuShortcutsDatabase.GetStatus(canonical)
        );
    }

    [Fact]
    public void SetShortcutRejectsAFileThatIsNotAShortcut()
    {
        string document = Path.Combine(_userPrograms, "taxes.xlsx");
        File.WriteAllText(document, "not a shortcut");

        Assert.Throws<InvalidOperationException>(
            () =>
                IpcStartMenuShortcutsApi.SetShortcut(
                    new IpcStartMenuShortcutRequest { Path = document, Status = "delete" }
                )
        );

        Assert.True(File.Exists(document));
    }
}

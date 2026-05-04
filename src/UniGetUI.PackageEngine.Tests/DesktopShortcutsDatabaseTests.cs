using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Classes.Packages.Classes;

namespace UniGetUI.PackageEngine.Tests;

public sealed class DesktopShortcutsDatabaseTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(DesktopShortcutsDatabaseTests),
        Guid.NewGuid().ToString("N")
    );

    public DesktopShortcutsDatabaseTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
        DesktopShortcutsDatabase.ResetDatabase();
        DesktopShortcutsDatabase.GetUnknownShortcuts().Clear();
    }

    public void Dispose()
    {
        DesktopShortcutsDatabase.ResetDatabase();
        DesktopShortcutsDatabase.GetUnknownShortcuts().Clear();
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [Fact]
    public void AddStatusRoundTripAndResetDatabaseWorkForTrackedShortcuts()
    {
        string shortcutPath = Path.Combine(_testRoot, "SyntheticShortcut.lnk");

        DesktopShortcutsDatabase.AddToDatabase(shortcutPath, DesktopShortcutsDatabase.Status.Maintain);
        Assert.Equal(DesktopShortcutsDatabase.Status.Maintain, DesktopShortcutsDatabase.GetStatus(shortcutPath));
        Assert.Contains(shortcutPath, DesktopShortcutsDatabase.GetAllShortcuts());

        DesktopShortcutsDatabase.AddToDatabase(shortcutPath, DesktopShortcutsDatabase.Status.Delete);
        Assert.Equal(DesktopShortcutsDatabase.Status.Delete, DesktopShortcutsDatabase.GetStatus(shortcutPath));

        DesktopShortcutsDatabase.AddToDatabase(shortcutPath, DesktopShortcutsDatabase.Status.Unknown);
        Assert.Equal(DesktopShortcutsDatabase.Status.Unknown, DesktopShortcutsDatabase.GetStatus(shortcutPath));
        Assert.DoesNotContain(shortcutPath, DesktopShortcutsDatabase.GetDatabase().Keys);

        DesktopShortcutsDatabase.AddToDatabase(shortcutPath, DesktopShortcutsDatabase.Status.Delete);
        DesktopShortcutsDatabase.ResetDatabase();
        Assert.Equal(DesktopShortcutsDatabase.Status.Unknown, DesktopShortcutsDatabase.GetStatus(shortcutPath));
    }

    [Fact]
    public void DeleteFromDiskRemovesExistingShortcutFile()
    {
        string shortcutPath = Path.Combine(_testRoot, "DeleteMe.lnk");
        File.WriteAllText(shortcutPath, "synthetic shortcut");

        bool deleted = DesktopShortcutsDatabase.DeleteFromDisk(shortcutPath);

        Assert.True(deleted);
        Assert.False(File.Exists(shortcutPath));
    }
}

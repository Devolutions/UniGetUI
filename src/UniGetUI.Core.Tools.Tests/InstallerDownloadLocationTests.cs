using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Core.Tools.Tests;

public class InstallerDownloadLocationTests : IDisposable
{
    private readonly string _testRoot;

    public InstallerDownloadLocationTests()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            nameof(InstallerDownloadLocationTests),
            Guid.NewGuid().ToString("N")
        );
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
    }

    [Fact]
    public void NoCustomDirectoryIsSetByDefault()
    {
        Assert.Null(InstallerDownloadLocation.GetCustomDirectory());
        Assert.False(InstallerDownloadLocation.IsCustomDirectorySet);
        Assert.Null(InstallerDownloadLocation.ResolveStartDirectory());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankCustomDirectoriesAreTreatedAsUnset(string stored)
    {
        Settings.SetValue(Settings.K.DefaultInstallerDownloadDirectory, stored);

        Assert.Null(InstallerDownloadLocation.GetCustomDirectory());
        Assert.False(InstallerDownloadLocation.IsCustomDirectorySet);
        Assert.Null(InstallerDownloadLocation.ResolveStartDirectory());
    }

    [Fact]
    public void AnUnsetCustomDirectoryStillDownloadsToTheDefaultOne()
    {
        Assert.Equal(
            CoreData.UniGetUI_DefaultInstallerDownloadDirectory,
            InstallerDownloadLocation.ResolveExistingDirectory()
        );
    }

    [Fact]
    public void AnExistingCustomDirectoryIsUsed()
    {
        string custom = Path.Combine(_testRoot, "Installers");
        Directory.CreateDirectory(custom);
        InstallerDownloadLocation.SetCustomDirectory(custom);

        Assert.Equal(custom, InstallerDownloadLocation.GetCustomDirectory());
        Assert.True(InstallerDownloadLocation.IsCustomDirectorySet);
        Assert.Equal(custom, InstallerDownloadLocation.ResolveStartDirectory());
    }

    [Fact]
    public void SurroundingWhitespaceIsTrimmedFromTheCustomDirectory()
    {
        string custom = Path.Combine(_testRoot, "Installers");
        Directory.CreateDirectory(custom);
        InstallerDownloadLocation.SetCustomDirectory($"  {custom}  ");

        Assert.Equal(custom, InstallerDownloadLocation.GetCustomDirectory());
        Assert.Equal(custom, InstallerDownloadLocation.ResolveStartDirectory());
    }

    [Fact]
    public void AMissingCustomDirectoryLeavesTheStartLocationToTheSystem()
    {
        string custom = Path.Combine(_testRoot, "Gone");
        InstallerDownloadLocation.SetCustomDirectory(custom);

        Assert.Equal(custom, InstallerDownloadLocation.GetCustomDirectory());
        Assert.True(InstallerDownloadLocation.IsCustomDirectorySet);
        Assert.Null(InstallerDownloadLocation.ResolveStartDirectory());
    }

    [Fact]
    public void SettingANullCustomDirectoryClearsIt()
    {
        string custom = Path.Combine(_testRoot, "Installers");
        Directory.CreateDirectory(custom);
        InstallerDownloadLocation.SetCustomDirectory(custom);
        InstallerDownloadLocation.SetCustomDirectory(null);

        Assert.Null(InstallerDownloadLocation.GetCustomDirectory());
        Assert.False(InstallerDownloadLocation.IsCustomDirectorySet);
    }

    [Fact]
    public void ResolveExistingDirectoryCreatesTheCustomDirectory()
    {
        string custom = Path.Combine(_testRoot, "Created");
        InstallerDownloadLocation.SetCustomDirectory(custom);

        Assert.Null(InstallerDownloadLocation.ResolveStartDirectory());
        Assert.Equal(custom, InstallerDownloadLocation.ResolveExistingDirectory());
        Assert.True(Directory.Exists(custom));
        Assert.Equal(custom, InstallerDownloadLocation.ResolveStartDirectory());
    }

    [Fact]
    public void ResolveExistingDirectoryRecreatesAConfiguredDirectoryThatWasDeleted()
    {
        string custom = Path.Combine(_testRoot, "Deleted");
        Directory.CreateDirectory(custom);
        InstallerDownloadLocation.SetCustomDirectory(custom);
        Directory.Delete(custom);

        Assert.Equal(custom, InstallerDownloadLocation.ResolveExistingDirectory());
        Assert.True(Directory.Exists(custom));
    }

    [Fact]
    public void DefaultDirectoryMatchesTheCoreDataDefault()
    {
        Assert.Equal(
            CoreData.UniGetUI_DefaultInstallerDownloadDirectory,
            InstallerDownloadLocation.DefaultDirectory
        );
    }

    [Fact]
    public void TheDefaultDirectoryIsAnAbsolutePath()
    {
        string directory = CoreData.UniGetUI_DefaultInstallerDownloadDirectory;

        Assert.False(string.IsNullOrWhiteSpace(directory));
        Assert.True(Path.IsPathFullyQualified(directory));
    }

    public void Dispose()
    {
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
        GC.SuppressFinalize(this);
    }
}

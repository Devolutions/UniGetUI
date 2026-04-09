using System.Text.Json;
using UniGetUI.Core.Data;

namespace UniGetUI.Core.SettingsEngine.Tests;

public sealed class SettingsImportExportTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(SettingsImportExportTests),
        Guid.NewGuid().ToString("N")
    );

    public SettingsImportExportTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
    }

    public void Dispose()
    {
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [Fact]
    public void ExportToStringJson_ExcludesSensitiveFiles()
    {
        Settings.Set(Settings.K.FreshBoolSetting, true);
        Settings.SetValue(Settings.K.FreshValue, "configured");
        File.WriteAllText(Path.Combine(CoreData.UniGetUIUserConfigurationDirectory, "TelemetryClientToken"), "secret");
        File.WriteAllText(Path.Combine(CoreData.UniGetUIUserConfigurationDirectory, "CurrentSessionToken"), "secret");

        var exported = JsonSerializer.Deserialize<Dictionary<string, string>>(Settings.ExportToString_JSON());

        Assert.NotNull(exported);
        Assert.Contains(Settings.ResolveKey(Settings.K.FreshBoolSetting), exported.Keys);
        Assert.Equal("configured", exported[Settings.ResolveKey(Settings.K.FreshValue)]);
        Assert.DoesNotContain("TelemetryClientToken", exported.Keys);
        Assert.DoesNotContain("CurrentSessionToken", exported.Keys);
    }

    [Fact]
    public void ImportFromStringJson_ResetsExistingFilesAndReloadsCache()
    {
        Settings.Set(Settings.K.Test1, true);
        Settings.SetValue(Settings.K.FreshValue, "old-value");

        string importedJson = JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                [Settings.ResolveKey(Settings.K.Test2)] = "",
                [Settings.ResolveKey(Settings.K.FreshValue)] = "new-value",
            }
        );

        Settings.ImportFromString_JSON(importedJson);

        Assert.False(File.Exists(Path.Combine(CoreData.UniGetUIUserConfigurationDirectory, Settings.ResolveKey(Settings.K.Test1))));
        Assert.True(Settings.Get(Settings.K.Test2));
        Assert.Equal("new-value", Settings.GetValue(Settings.K.FreshValue));
    }

    [Fact]
    public void ImportFromFileJson_CopiesSourceWhenBackupLivesInSettingsDirectory()
    {
        Settings.SetValue(Settings.K.FreshValue, "before-import");
        string exportPath = Path.Combine(CoreData.UniGetUIUserConfigurationDirectory, "settings-backup.json");

        Settings.ExportToFile_JSON(exportPath);
        Settings.SetValue(Settings.K.FreshValue, "after-export");

        Settings.ImportFromFile_JSON(exportPath);

        Assert.Equal("before-import", Settings.GetValue(Settings.K.FreshValue));
    }
}

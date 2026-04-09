using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;

namespace UniGetUI.Tests;

public sealed class CLIHandlerTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(CLIHandlerTests),
        Guid.NewGuid().ToString("N")
    );
    private readonly string _secureSettingsRoot;

    public CLIHandlerTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        _secureSettingsRoot = Path.Combine(_testRoot, "SecureSettings");
        SecureSettings.TEST_SecureSettingsRootOverride = _secureSettingsRoot;
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
    }

    public void Dispose()
    {
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        SecureSettings.TEST_SecureSettingsRootOverride = null;
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [Fact]
    public void ImportSettings_ReturnsNoSuchFileWhenInputIsMissing()
    {
        int result = CLIHandler.ImportSettings(["unigetui", CLIHandler.IMPORT_SETTINGS, Path.Combine(_testRoot, "missing.json")]);

        Assert.Equal(-1073741809, result);
    }

    [Fact]
    public void ExportAndImportSettings_RoundTripConfiguration()
    {
        string exportPath = Path.Combine(_testRoot, "settings.json");
        Settings.Set(Settings.K.FreshBoolSetting, true);
        Settings.SetValue(Settings.K.FreshValue, "before-export");

        Assert.Equal(0, CLIHandler.ExportSettings(["unigetui", CLIHandler.EXPORT_SETTINGS, exportPath]));

        Settings.Set(Settings.K.FreshBoolSetting, false);
        Settings.SetValue(Settings.K.FreshValue, "after-export");

        Assert.Equal(0, CLIHandler.ImportSettings(["unigetui", CLIHandler.IMPORT_SETTINGS, exportPath]));
        Assert.True(Settings.Get(Settings.K.FreshBoolSetting));
        Assert.Equal("before-export", Settings.GetValue(Settings.K.FreshValue));

        var exported = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(exportPath));
        Assert.NotNull(exported);
        Assert.Equal("before-export", exported[Settings.ResolveKey(Settings.K.FreshValue)]);
    }

    [Fact]
    public void EnableDisableAndSetValue_MutateSettings()
    {
        Assert.Equal(0, CLIHandler.EnableSetting(["unigetui", CLIHandler.ENABLE_SETTING, nameof(Settings.K.Test1)]));
        Assert.True(Settings.Get(Settings.K.Test1));

        Assert.Equal(0, CLIHandler.SetSettingsValue(["unigetui", CLIHandler.SET_SETTING_VAL, nameof(Settings.K.FreshValue), "cli-value"]));
        Assert.Equal("cli-value", Settings.GetValue(Settings.K.FreshValue));

        Assert.Equal(0, CLIHandler.DisableSetting(["unigetui", CLIHandler.DISABLE_SETTING, nameof(Settings.K.Test1)]));
        Assert.False(Settings.Get(Settings.K.Test1));
    }

    [Fact]
    public void EnableAndDisableSecureSettingForUser_MutateSecureSettings()
    {
        string user = "cli-user";
        string setting = "AllowCLIArguments";

        Assert.Equal(
            0,
            CLIHandler.EnableSecureSettingForUser(
                ["unigetui", CLIHandler.ENABLE_SECURE_SETTING_FOR_USER, user, setting]
            )
        );
        Assert.True(
            File.Exists(
                Path.Combine(
                    _secureSettingsRoot,
                    user,
                    setting
                )
            )
        );

        Assert.Equal(
            0,
            CLIHandler.DisableSecureSettingForUser(
                ["unigetui", CLIHandler.DISABLE_SECURE_SETTING_FOR_USER, user, setting]
            )
        );
        Assert.False(
            File.Exists(
                Path.Combine(
                    _secureSettingsRoot,
                    user,
                    setting
                )
            )
        );
    }
}

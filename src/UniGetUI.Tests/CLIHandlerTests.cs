using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Shared;

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
        int result = SharedPreUiCommandDispatcher.ImportSettings(
            ["unigetui", SharedPreUiCommandDispatcher.ImportSettingsArgument, Path.Combine(_testRoot, "missing.json")],
            SharedPreUiCommandDispatcher.WindowsCliExitCodes
        );

        Assert.Equal(-1073741809, result);
    }

    [Fact]
    public void ExportAndImportSettings_RoundTripConfiguration()
    {
        string exportPath = Path.Combine(_testRoot, "settings.json");
        Settings.Set(Settings.K.FreshBoolSetting, true);
        Settings.SetValue(Settings.K.FreshValue, "before-export");

        Assert.Equal(
            0,
            SharedPreUiCommandDispatcher.ExportSettings(
                ["unigetui", SharedPreUiCommandDispatcher.ExportSettingsArgument, exportPath],
                SharedPreUiCommandDispatcher.WindowsCliExitCodes
            )
        );

        Settings.Set(Settings.K.FreshBoolSetting, false);
        Settings.SetValue(Settings.K.FreshValue, "after-export");

        Assert.Equal(
            0,
            SharedPreUiCommandDispatcher.ImportSettings(
                ["unigetui", SharedPreUiCommandDispatcher.ImportSettingsArgument, exportPath],
                SharedPreUiCommandDispatcher.WindowsCliExitCodes
            )
        );
        Assert.True(Settings.Get(Settings.K.FreshBoolSetting));
        Assert.Equal("before-export", Settings.GetValue(Settings.K.FreshValue));

        var exported = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(exportPath));
        Assert.NotNull(exported);
        Assert.Equal("before-export", exported[Settings.ResolveKey(Settings.K.FreshValue)]);
    }

    [Fact]
    public void EnableDisableAndSetValue_MutateSettings()
    {
        Assert.Equal(
            0,
            SharedPreUiCommandDispatcher.EnableSetting(
                ["unigetui", SharedPreUiCommandDispatcher.EnableSettingArgument, nameof(Settings.K.Test1)],
                SharedPreUiCommandDispatcher.WindowsCliExitCodes
            )
        );
        Assert.True(Settings.Get(Settings.K.Test1));

        Assert.Equal(
            0,
            SharedPreUiCommandDispatcher.SetSettingValue(
                [
                    "unigetui",
                    SharedPreUiCommandDispatcher.SetSettingValueArgument,
                    nameof(Settings.K.FreshValue),
                    "cli-value",
                ],
                SharedPreUiCommandDispatcher.WindowsCliExitCodes
            )
        );
        Assert.Equal("cli-value", Settings.GetValue(Settings.K.FreshValue));

        Assert.Equal(
            0,
            SharedPreUiCommandDispatcher.DisableSetting(
                ["unigetui", SharedPreUiCommandDispatcher.DisableSettingArgument, nameof(Settings.K.Test1)],
                SharedPreUiCommandDispatcher.WindowsCliExitCodes
            )
        );
        Assert.False(Settings.Get(Settings.K.Test1));
    }

    [Fact]
    public void EnableAndDisableSecureSettingForUser_MutateSecureSettings()
    {
        string user = "cli-user";
        string setting = "AllowCLIArguments";

        Assert.Equal(
            0,
            SharedPreUiCommandDispatcher.EnableSecureSettingForUser(
                ["unigetui", SecureSettings.Args.ENABLE_FOR_USER, user, setting],
                SharedPreUiCommandDispatcher.WindowsCliExitCodes
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
            SharedPreUiCommandDispatcher.DisableSecureSettingForUser(
                ["unigetui", SecureSettings.Args.DISABLE_FOR_USER, user, setting],
                SharedPreUiCommandDispatcher.WindowsCliExitCodes
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

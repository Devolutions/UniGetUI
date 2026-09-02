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

    [Theory]
    [InlineData(@"..\..\escaped.bat")]
    [InlineData(@"..\escaped.bat")]
    [InlineData("../../escaped.bat")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"nested\escaped.bat")]
    [InlineData("nested/escaped.bat")]
    [InlineData(@"C:\escaped.bat")]
    [InlineData("escaped.bat:stream")]
    public void ImportFromStringJson_DiscardsKeysThatAreNotPlainFileNames(string key)
    {
        string json = JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                [key] = "payload",
                [Settings.ResolveKey(Settings.K.FreshValue)] = "legitimate",
            }
        );

        Settings.ImportFromString_JSON(json);

        Assert.Equal("legitimate", Settings.GetValue(Settings.K.FreshValue));
        Assert.Empty(
            Directory.EnumerateFiles(_testRoot, "escaped.bat", SearchOption.AllDirectories)
        );
        Assert.DoesNotContain(
            Directory.EnumerateFiles(CoreData.UniGetUIUserConfigurationDirectory),
            file => File.ReadAllText(file) == "payload"
        );
    }

    [Fact]
    public void ImportFromStringJson_DoesNotWriteOutsideTheConfigurationDirectory()
    {
        string escaped = Path.Combine(_testRoot, "startup-payload.bat");
        string json = JsonSerializer.Serialize(
            new Dictionary<string, string> { [@"..\..\startup-payload.bat"] = "@echo payload" }
        );

        Settings.ImportFromString_JSON(json);

        Assert.False(File.Exists(escaped));
    }

    [Theory]
    [InlineData(@".\TelemetryClientToken")]
    [InlineData(@"nested\..\TelemetryClientToken")]
    [InlineData("./TelemetryClientToken")]
    public void ImportFromStringJson_DiscardsPathQualifiedKeysTargetingExcludedFiles(string key)
    {
        string token = Path.Combine(
            CoreData.UniGetUIUserConfigurationDirectory,
            "TelemetryClientToken"
        );
        File.WriteAllText(token, "original-token");

        string json = JsonSerializer.Serialize(
            new Dictionary<string, string> { [key] = "attacker-token" }
        );

        Settings.ImportFromString_JSON(json);

        Assert.Equal("original-token", File.ReadAllText(token));
    }

    [Fact]
    public void ImportFromStringJson_DiscardsExcludedKeys()
    {
        string token = Path.Combine(
            CoreData.UniGetUIUserConfigurationDirectory,
            "TelemetryClientToken"
        );
        File.WriteAllText(token, "original-token");

        string json = JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                ["TelemetryClientToken"] = "attacker-token",
                ["CurrentSessionToken"] = "attacker-session",
            }
        );

        Settings.ImportFromString_JSON(json);

        Assert.Equal("original-token", File.ReadAllText(token));
        Assert.False(
            File.Exists(
                Path.Combine(CoreData.UniGetUIUserConfigurationDirectory, "CurrentSessionToken")
            )
        );
    }

    [Theory]
    [InlineData("TelemetryClientToken ")]
    [InlineData("TelemetryClientToken.")]
    [InlineData("TelemetryClientToken..")]
    [InlineData("telemetryclienttoken")]
    [InlineData("TELEMETRYCLIENTTOKEN")]
    public void ImportFromStringJson_DiscardsKeysThatCollideWithExcludedFiles(string key)
    {
        string token = Path.Combine(
            CoreData.UniGetUIUserConfigurationDirectory,
            "TelemetryClientToken"
        );
        File.WriteAllText(token, "original-token");

        string json = JsonSerializer.Serialize(
            new Dictionary<string, string> { [key] = "attacker-token" }
        );

        Settings.ImportFromString_JSON(json);

        Assert.Equal("original-token", File.ReadAllText(token));
    }

    [Theory]
    [InlineData("FreshValue ")]
    [InlineData("FreshValue.")]
    public void ImportFromStringJson_DiscardsKeysTheFileSystemWouldRewrite(string key)
    {
        string json = JsonSerializer.Serialize(
            new Dictionary<string, string> { [key] = "mangled" }
        );

        Settings.ImportFromString_JSON(json);

        Assert.NotEqual("mangled", Settings.GetValue(Settings.K.FreshValue));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("NUL")]
    [InlineData("con")]
    [InlineData("LPT1")]
    [InlineData("COM1.json")]
    public void ImportFromStringJson_DiscardsReservedDeviceNames(string key)
    {
        string json = JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                [key] = "payload",
                [Settings.ResolveKey(Settings.K.FreshValue)] = "legitimate",
            }
        );

        Settings.ImportFromString_JSON(json);

        Assert.Equal("legitimate", Settings.GetValue(Settings.K.FreshValue));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(CoreData.UniGetUIUserConfigurationDirectory),
            file => File.ReadAllText(file) == "payload"
        );
    }

    [Theory]
    [InlineData("TELEME~1")]
    [InlineData("teleme~1")]
    [InlineData("CURREN~1")]
    public void ImportFromStringJson_DiscardsShortNameAliasesOfExcludedFiles(string key)
    {
        string token = Path.Combine(
            CoreData.UniGetUIUserConfigurationDirectory,
            "TelemetryClientToken"
        );
        File.WriteAllText(token, "original-token");

        string json = JsonSerializer.Serialize(
            new Dictionary<string, string> { [key] = "attacker-token" }
        );

        Settings.ImportFromString_JSON(json);

        Assert.Equal("original-token", File.ReadAllText(token));
    }

    [Fact]
    public void ImportFromStringJson_ContinuesWhenAKeyCollidesWithAnExistingDirectory()
    {
        Directory.CreateDirectory(
            Path.Combine(
                CoreData.UniGetUIUserConfigurationDirectory,
                Settings.ResolveKey(Settings.K.Test1)
            )
        );

        string json = JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                [Settings.ResolveKey(Settings.K.Test1)] = "payload",
                [Settings.ResolveKey(Settings.K.FreshValue)] = "legitimate",
            }
        );

        Assert.Throws<IOException>(() => Settings.ImportFromString_JSON(json));

        Assert.Equal("legitimate", Settings.GetValue(Settings.K.FreshValue));
        Assert.True(
            Directory.Exists(
                Path.Combine(
                    CoreData.UniGetUIUserConfigurationDirectory,
                    Settings.ResolveKey(Settings.K.Test1)
                )
            )
        );
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    public void ImportFromStringJson_KeepsExistingSettingsWhenContentIsMalformed(string content)
    {
        Settings.SetValue(Settings.K.FreshValue, "must-survive");

        Assert.ThrowsAny<Exception>(() => Settings.ImportFromString_JSON(content));

        Assert.Equal("must-survive", Settings.GetValue(Settings.K.FreshValue));
    }

    [Theory]
    [InlineData("NotARealSettingName")]
    [InlineData("evil.bat")]
    [InlineData("NotARealSettingName.json")]
    public void ImportFromStringJson_DiscardsKeysThatAreNotKnownSettingNames(string key)
    {
        string json = JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                [key] = "payload",
                [Settings.ResolveKey(Settings.K.FreshValue)] = "legitimate",
            }
        );

        Settings.ImportFromString_JSON(json);

        Assert.Equal("legitimate", Settings.GetValue(Settings.K.FreshValue));
        Assert.False(
            File.Exists(Path.Combine(CoreData.UniGetUIUserConfigurationDirectory, key))
        );
    }

    [Fact]
    public void ImportFromStringJson_AcceptsKnownSettingNamesAndTheirJsonCompanions()
    {
        string listKey = $"{Settings.ResolveKey(Settings.K.FreshValue)}.json";
        string json = JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                [Settings.ResolveKey(Settings.K.FreshValue)] = "kept",
                [listKey] = "[]",
            }
        );

        Settings.ImportFromString_JSON(json);

        Assert.Equal("kept", Settings.GetValue(Settings.K.FreshValue));
        Assert.True(
            File.Exists(Path.Combine(CoreData.UniGetUIUserConfigurationDirectory, listKey))
        );
    }

    [Theory]
    [InlineData("telemetryclienttoken")]
    [InlineData("TELEMETRYCLIENTTOKEN")]
    public void ExportToStringJson_ExcludesSensitiveFilesRegardlessOfCasing(string storedName)
    {
        File.WriteAllText(
            Path.Combine(CoreData.UniGetUIUserConfigurationDirectory, storedName),
            "secret"
        );

        var exported = JsonSerializer.Deserialize<Dictionary<string, string>>(
            Settings.ExportToString_JSON()
        );

        Assert.NotNull(exported);
        Assert.DoesNotContain("secret", exported!.Values);
    }

    [Theory]
    [InlineData("freshvalue")]
    [InlineData("FRESHVALUE")]
    [InlineData("freshvalue.json")]
    public void ImportFromStringJson_RequiresCanonicalCasingForKnownSettingNames(string key)
    {
        string json = JsonSerializer.Serialize(
            new Dictionary<string, string> { [key] = "payload" }
        );

        Settings.ImportFromString_JSON(json);

        Assert.False(
            File.Exists(Path.Combine(CoreData.UniGetUIUserConfigurationDirectory, key))
        );
    }

    [Fact]
    public void ImportFromStringJson_ThrowsWhenAWriteFailsInsteadOfReportingSuccess()
    {
        string blocked = Path.Combine(
            CoreData.UniGetUIUserConfigurationDirectory,
            Settings.ResolveKey(Settings.K.FreshValue)
        );
        Directory.CreateDirectory(blocked);

        string json = JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                [Settings.ResolveKey(Settings.K.FreshValue)] = "cannot-be-written",
            }
        );

        Assert.ThrowsAny<Exception>(() => Settings.ImportFromString_JSON(json));
    }

    [Fact]
    public void EveryKeyExportProducesIsAcceptedByImport()
    {
        string configuration = CoreData.UniGetUIUserConfigurationDirectory;

        foreach (Settings.K key in Enum.GetValues<Settings.K>())
        {
            if (key is Settings.K.Unset)
                continue;

            string resolved = Settings.ResolveKey(key);
            File.WriteAllText(Path.Combine(configuration, resolved), "value");
            File.WriteAllText(Path.Combine(configuration, $"{resolved}.json"), "[]");
        }

        File.WriteAllText(Path.Combine(configuration, "PendingDesktopShortcuts.json"), "[]");
        File.WriteAllText(Path.Combine(configuration, "PendingStartMenuShortcuts.json"), "[]");

        var exported = JsonSerializer.Deserialize<Dictionary<string, string>>(
            Settings.ExportToString_JSON()
        );
        Assert.NotNull(exported);

        Settings.ImportFromString_JSON(JsonSerializer.Serialize(exported));

        List<string> lost = exported!
            .Keys.Where(key => !File.Exists(Path.Combine(configuration, key)))
            .ToList();

        Assert.True(
            lost.Count is 0,
            $"export produced keys that import discarded: {string.Join(", ", lost)}"
        );
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

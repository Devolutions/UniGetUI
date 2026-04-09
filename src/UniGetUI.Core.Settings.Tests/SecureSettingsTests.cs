using System.Reflection;
using UniGetUI.Core.Tools;
using SecureSettingsStore = UniGetUI.Core.SettingsEngine.SecureSettings.SecureSettings;

namespace UniGetUI.Core.SettingsEngine.Tests;

public sealed class SecureSettingsTests : IDisposable
{
    private readonly string _testRoot;

    public SecureSettingsTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"UniGetUI-SecureSettingsTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        SecureSettingsStore.TEST_SecureSettingsRootOverride = _testRoot;
        ClearSecureSettingsCache();
    }

    public void Dispose()
    {
        ClearSecureSettingsCache();
        SecureSettingsStore.TEST_SecureSettingsRootOverride = null;

        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    [Theory]
    [InlineData(SecureSettingsStore.K.AllowCLIArguments, "AllowCLIArguments")]
    [InlineData(SecureSettingsStore.K.AllowImportingCLIArguments, "AllowImportingCLIArguments")]
    [InlineData(SecureSettingsStore.K.AllowPrePostOpCommand, "AllowPrePostInstallCommands")]
    [InlineData(SecureSettingsStore.K.AllowImportPrePostOpCommands, "AllowImportingPrePostInstallCommands")]
    [InlineData(SecureSettingsStore.K.ForceUserGSudo, "ForceUserGSudo")]
    [InlineData(SecureSettingsStore.K.AllowCustomManagerPaths, "AllowCustomManagerPaths")]
    public void ResolveKey_ReturnsExpectedMappings(SecureSettingsStore.K key, string expected)
    {
        Assert.Equal(expected, SecureSettingsStore.ResolveKey(key));
    }

    [Fact]
    public void ResolveKey_ThrowsForUnsetAndUnknownKeys()
    {
        Assert.Throws<InvalidDataException>(() =>
            SecureSettingsStore.ResolveKey(SecureSettingsStore.K.Unset)
        );
        Assert.Throws<KeyNotFoundException>(() =>
            SecureSettingsStore.ResolveKey((SecureSettingsStore.K)999)
        );
    }

    [Fact]
    public void Get_ReturnsFalseWhenSettingDoesNotExist()
    {
        Assert.False(SecureSettingsStore.Get(SecureSettingsStore.K.AllowCLIArguments));
        Assert.False(Directory.Exists(GetCurrentUserSettingsDirectory()));
    }

    [Fact]
    public void ApplyForUser_CreatesAndRemovesSanitizedFile()
    {
        const string username = "test:user?";
        const string setting = "setting<with>invalid|chars";

        Assert.Equal(0, SecureSettingsStore.ApplyForUser(username, setting, true));
        Assert.True(File.Exists(GetSettingsFilePath(username, setting)));

        Assert.Equal(0, SecureSettingsStore.ApplyForUser(username, setting, false));
        Assert.False(File.Exists(GetSettingsFilePath(username, setting)));
    }

    [Fact]
    public void Get_RefreshesCachedValueAfterApplyForUserWrites()
    {
        string username = Environment.UserName;
        string setting = SecureSettingsStore.ResolveKey(SecureSettingsStore.K.AllowCLIArguments);

        Assert.False(SecureSettingsStore.Get(SecureSettingsStore.K.AllowCLIArguments));

        Assert.Equal(0, SecureSettingsStore.ApplyForUser(username, setting, true));
        Assert.True(File.Exists(GetSettingsFilePath(username, setting)));
        Assert.True(SecureSettingsStore.Get(SecureSettingsStore.K.AllowCLIArguments));

        Assert.Equal(0, SecureSettingsStore.ApplyForUser(username, setting, false));
        Assert.False(File.Exists(GetSettingsFilePath(username, setting)));
        Assert.False(SecureSettingsStore.Get(SecureSettingsStore.K.AllowCLIArguments));
    }

    private string GetCurrentUserSettingsDirectory() =>
        Path.Combine(_testRoot, CoreTools.MakeValidFileName(Environment.UserName));

    private string GetSettingsFilePath(string username, string setting) =>
        Path.Combine(
            _testRoot,
            CoreTools.MakeValidFileName(username),
            CoreTools.MakeValidFileName(setting)
        );

    private static void ClearSecureSettingsCache()
    {
        FieldInfo? cacheField = typeof(SecureSettingsStore).GetField(
            "_cache",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(cacheField);

        var cache = Assert.IsType<Dictionary<string, bool>>(cacheField.GetValue(null));
        cache.Clear();
    }
}

using System.Collections.Concurrent;
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

    [Fact]
    public async Task Get_AllowsConcurrentCacheMisses()
    {
        string username = Environment.UserName;
        string setting = SecureSettingsStore.ResolveKey(
            SecureSettingsStore.K.AllowCustomManagerPaths
        );
        Assert.Equal(0, SecureSettingsStore.ApplyForUser(username, setting, true));

        for (int iteration = 0; iteration < 25; iteration++)
        {
            ClearSecureSettingsCache();
            using ManualResetEventSlim startGate = new(false);

            Task<bool>[] tasks = Enumerable
                .Range(0, 64)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        startGate.Wait();
                        return SecureSettingsStore.Get(
                            SecureSettingsStore.K.AllowCustomManagerPaths
                        );
                    })
                )
                .ToArray();

            startGate.Set();
            bool[] results = await Task.WhenAll(tasks);

            Assert.All(results, Assert.True);
        }
    }

    [Theory]
    [InlineData("..", "AllowCLIArguments")]
    [InlineData("CurrentUser", "..")]
    [InlineData("..", "..")]
    [InlineData("", "AllowCLIArguments")]
    [InlineData("CurrentUser", "")]
    [InlineData("   ", "AllowCLIArguments")]
    public void ApplyForUser_RefusesComponentsThatEscapeTheSecureSettingsRoot(
        string username,
        string setting
    )
    {
        string parent = Directory.GetParent(_testRoot)!.FullName;
        string[] before = Directory.GetFileSystemEntries(parent);

        int result = SecureSettingsStore.ApplyForUser(username, setting, true);

        Assert.NotEqual(0, result);
        Assert.Equal(before.Length, Directory.GetFileSystemEntries(parent).Length);
    }

    [Theory]
    [InlineData("..", "AllowCLIArguments")]
    [InlineData("CurrentUser", "..")]
    [InlineData("", "")]
    public void GetForUser_RefusesComponentsThatEscapeTheSecureSettingsRoot(
        string username,
        string setting
    )
    {
        Assert.False(SecureSettingsStore.GetForUser(username, setting));
    }

    [Fact]
    public void ApplyForUser_StillWritesInsideTheSecureSettingsRoot()
    {
        int result = SecureSettingsStore.ApplyForUser("CurrentUser", "AllowCLIArguments", true);

        Assert.Equal(0, result);
        Assert.True(
            File.Exists(Path.Combine(_testRoot, "CurrentUser", "AllowCLIArguments"))
        );
        Assert.True(SecureSettingsStore.GetForUser("CurrentUser", "AllowCLIArguments"));
    }

    [Fact]
    public void GetForUser_InvalidComponentsDoNotAliasACachedValidEntry()
    {
        const string setting = "AllowCLIArguments";

        Assert.Equal(0, SecureSettingsStore.ApplyForUser("_", setting, true));
        Assert.True(SecureSettingsStore.GetForUser("_", setting));

        Assert.False(SecureSettingsStore.GetForUser("..", setting));
        Assert.False(SecureSettingsStore.GetForUser(".", setting));
        Assert.False(SecureSettingsStore.GetForUser("   ", setting));

        Assert.True(SecureSettingsStore.GetForUser("_", setting));
    }

    [Fact]
    public void GetForUser_InvalidComponentsDoNotPoisonTheCacheForValidOnes()
    {
        const string setting = "AllowCLIArguments";

        Assert.False(SecureSettingsStore.GetForUser("..", setting));

        Assert.Equal(0, SecureSettingsStore.ApplyForUser("_", setting, true));
        Assert.True(SecureSettingsStore.GetForUser("_", setting));
    }

    [Fact]
    public void ApplyForUser_RefusesWhenTheUserDirectoryIsALink()
    {
        string outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        string linked = Path.Combine(_testRoot, "LinkedUser");

        try
        {
            Directory.CreateSymbolicLink(linked, outside);
        }
        catch
        {
            return;
        }

        int result = SecureSettingsStore.ApplyForUser("LinkedUser", "AllowCLIArguments", true);

        Assert.NotEqual(0, result);
        Assert.Empty(Directory.GetFiles(outside));
        Directory.Delete(outside, recursive: true);
    }

    private string GetCurrentUserSettingsDirectory() =>
        Path.Combine(_testRoot, CoreTools.MakeValidFileName(Environment.UserName));

    private string GetSettingsFilePath(string username, string setting) =>
        Path.Combine(
            _testRoot,
            CoreTools.MakeValidFileName(username),
            CoreTools.MakeValidFileName(setting)
        );

    private static ConcurrentDictionary<string, bool> GetCache()
    {
        FieldInfo? cacheField = typeof(SecureSettingsStore).GetField(
            "_cache",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(cacheField);

        return Assert.IsType<ConcurrentDictionary<string, bool>>(cacheField.GetValue(null));
    }

    private static void ClearSecureSettingsCache() => GetCache().Clear();
}

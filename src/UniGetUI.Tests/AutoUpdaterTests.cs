using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace UniGetUI.Tests;

public sealed class AutoUpdaterTests
{
    [Theory]
    [InlineData("https://devolutions.net/productinfo.json", false, true)]
    [InlineData("https://updates.devolutions.net/productinfo.json", false, true)]
    [InlineData("https://notdevolutions.net/productinfo.json", false, false)]
    [InlineData("https://github.com/Devolutions/UniGetUI/releases", false, true)]
    [InlineData("http://devolutions.net/productinfo.json", false, false)]
    [InlineData("http://contoso.invalid/file.exe", true, true)]
    public void IsSourceUrlAllowed_RestrictsUnsafeOrUnexpectedHosts(
        string url,
        bool allowUnsafeUrls,
        bool expected
    )
    {
        Assert.Equal(expected, AutoUpdater.IsSourceUrlAllowed(url, allowUnsafeUrls));
    }

    [Fact]
    public void SelectInstallerFile_PrefersExecutableForCurrentArchitecture()
    {
        string targetArch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => "x64",
        };
        var preferred = new AutoUpdater.ProductInfoFile
        {
            Arch = targetArch,
            Type = "exe",
            Url = "https://example.test/app.exe",
            Hash = "hash-exe",
        };

        var selected = AutoUpdater.SelectInstallerFile(
            [
                new AutoUpdater.ProductInfoFile
                {
                    Arch = "Any",
                    Type = "exe",
                    Url = "https://example.test/any.exe",
                    Hash = "hash-any",
                },
                new AutoUpdater.ProductInfoFile
                {
                    Arch = targetArch,
                    Type = "msi",
                    Url = "https://example.test/app.msi",
                    Hash = "hash-msi",
                },
                preferred,
            ]
        );

        Assert.Same(preferred, selected);
    }

    [Fact]
    public void ParseVersionOrFallback_ParsesTrimmedVersionsAndFallsBackForInvalidInput()
    {
        Version fallback = new(9, 9, 9, 9);

        Assert.Equal(new Version(1, 2, 3), AutoUpdater.ParseVersionOrFallback("v1.2.3", fallback));
        Assert.Equal(fallback, AutoUpdater.ParseVersionOrFallback("not-a-version", fallback));
    }

    [Fact]
    public void NormalizeThumbprint_RemovesNonHexCharactersAndLowercases()
    {
        Assert.Equal("abcdef1234", AutoUpdater.NormalizeThumbprint("AB:CD ef-12_34"));
    }

#if DEBUG
    [Fact]
    public void RegistryHelpers_ParseTrimmedStringsAndTruthyValues()
    {
        string keyPath = $@"Software\Devolutions\UniGetUI.Tests\{Guid.NewGuid():N}";
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath)!;
        key.SetValue("ProductInfoUrl", " https://devolutions.net/custom.json ");
        key.SetValue("AllowUnsafe", "yes");

        try
        {
            Assert.Equal("https://devolutions.net/custom.json", AutoUpdater.GetRegistryString(key, "ProductInfoUrl"));
            Assert.True(AutoUpdater.GetRegistryBool(key, "AllowUnsafe"));
            Assert.Null(AutoUpdater.GetRegistryString(key, "Missing"));
            Assert.False(AutoUpdater.GetRegistryBool(key, "Missing"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }
#endif
}

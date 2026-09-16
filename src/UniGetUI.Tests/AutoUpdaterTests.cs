using System.Runtime.InteropServices;
using Microsoft.Win32;
using UniGetUI.Shared;

namespace UniGetUI.Tests;

public sealed class AutoUpdaterTests
{
    [Fact]
    public void InstallerArguments_LeaveARegularInstallToTheInstallersOwnDirectoryLogic()
    {
        string arguments = AutoUpdaterInstallerArguments.ForWindows(
            false,
            @"C:\Program Files\UniGetUI",
            WindowsInstallScope.Unknown
        );

        Assert.DoesNotContain("/DIR=", arguments);
        Assert.DoesNotContain("/TASKS=", arguments);
        Assert.Contains("/SILENT", arguments);
    }

    [Fact]
    public void InstallerArguments_PinAPortableInstallToItsOwnDirectory()
    {
        string arguments = AutoUpdaterInstallerArguments.ForWindows(
            true,
            @"E:\Portable Apps\UniGetUI",
            WindowsInstallScope.Unknown
        );

        Assert.Contains(@"/DIR=""E:\Portable Apps\UniGetUI""", arguments);
        Assert.Contains(@"/TASKS=""portableinstall""", arguments);
        Assert.Contains("/SILENT", arguments);
    }

    [Fact]
    public void InstallerArguments_DoNotLeaveATrailingSeparatorBeforeTheClosingQuote()
    {
        string arguments = AutoUpdaterInstallerArguments.ForWindows(
            true,
            @"E:\UniGetUI\",
            WindowsInstallScope.Unknown
        );

        Assert.Contains(@"/DIR=""E:\UniGetUI""", arguments);
        Assert.DoesNotContain(@"\""", arguments);
    }

    [Fact]
    public void InstallerArguments_KeepTheSeparatorForAVolumeRoot()
    {
        string arguments = AutoUpdaterInstallerArguments.ForWindows(
            true,
            @"E:\",
            WindowsInstallScope.Unknown
        );

        Assert.Contains(@"/DIR=""E:\""", arguments);
        Assert.DoesNotContain(@"/DIR=""E:""", arguments);
    }

    [Fact]
    public void InstallerArguments_FallBackToDefaultsWhenTheDirectoryIsUnknown()
    {
        Assert.DoesNotContain("/DIR=", AutoUpdaterInstallerArguments.ForWindows(true, "", WindowsInstallScope.Unknown));
    }

    [Fact]
    public void InstallerArguments_RequestAnAllUsersInstallForASystemWideCopy()
    {
        string arguments = AutoUpdaterInstallerArguments.ForWindows(
            false,
            @"C:\Program Files\UniGetUI",
            WindowsInstallScope.AllUsers
        );

        Assert.Contains("/ALLUSERS", arguments);
        Assert.DoesNotContain("/CURRENTUSER", arguments);
    }

    [Fact]
    public void InstallerArguments_RequestAPerUserInstallForAPerUserCopy()
    {
        string arguments = AutoUpdaterInstallerArguments.ForWindows(
            false,
            @"C:\Users\someone\AppData\Local\Programs\UniGetUI",
            WindowsInstallScope.CurrentUser
        );

        Assert.Contains("/CURRENTUSER", arguments);
        Assert.DoesNotContain("/ALLUSERS", arguments);
    }

    [Fact]
    public void InstallerArguments_LeaveTheInstallModeToTheInstallerWhenTheScopeIsUnknown()
    {
        string arguments = AutoUpdaterInstallerArguments.ForWindows(
            false,
            @"D:\Builds\UniGetUI",
            WindowsInstallScope.Unknown
        );

        Assert.DoesNotContain("/ALLUSERS", arguments);
        Assert.DoesNotContain("/CURRENTUSER", arguments);
    }

    [Fact]
    public void InstallerArguments_KeepTheInstallModeAlongsideThePortableArguments()
    {
        string arguments = AutoUpdaterInstallerArguments.ForWindows(
            true,
            @"C:\Program Files\UniGetUI Portable",
            WindowsInstallScope.AllUsers
        );

        Assert.Contains("/ALLUSERS", arguments);
        Assert.Contains(@"/DIR=""C:\Program Files\UniGetUI Portable""", arguments);
        Assert.Contains(@"/TASKS=""portableinstall""", arguments);
    }

    [Fact]
    public void ResolveInstallScope_PrefersTheSystemWideEntryOverAStalePerUserOne()
    {
        WindowsInstallScope scope = AutoUpdaterInstallerArguments.ResolveInstallScope(
            @"C:\Program Files\UniGetUI",
            @"C:\Program Files\UniGetUI",
            @"C:\Users\someone\AppData\Local\Programs\UniGetUI",
            [@"C:\Program Files"]
        );

        Assert.Equal(WindowsInstallScope.AllUsers, scope);
    }

    [Fact]
    public void ResolveInstallScope_MatchesThePerUserEntryWhenTheCopyLivesThere()
    {
        WindowsInstallScope scope = AutoUpdaterInstallerArguments.ResolveInstallScope(
            @"C:\Users\someone\AppData\Local\Programs\UniGetUI",
            @"C:\Program Files\UniGetUI",
            @"C:\Users\someone\AppData\Local\Programs\UniGetUI",
            [@"C:\Program Files"]
        );

        Assert.Equal(WindowsInstallScope.CurrentUser, scope);
    }

    [Fact]
    public void ResolveInstallScope_IgnoresCasingAndTrailingSeparators()
    {
        WindowsInstallScope scope = AutoUpdaterInstallerArguments.ResolveInstallScope(
            @"c:\program files\unigetui\",
            @"C:\Program Files\UniGetUI",
            null,
            []
        );

        Assert.Equal(WindowsInstallScope.AllUsers, scope);
    }

    [Fact]
    public void ResolveInstallScope_FallsBackToTheProgramFilesRootsWhenNoEntryMatches()
    {
        WindowsInstallScope scope = AutoUpdaterInstallerArguments.ResolveInstallScope(
            @"C:\Program Files\UniGetUI",
            null,
            null,
            [@"C:\Program Files", @"C:\Program Files (x86)"]
        );

        Assert.Equal(WindowsInstallScope.AllUsers, scope);
    }

    [Fact]
    public void ResolveInstallScope_DoesNotMatchASiblingOfAProgramFilesRoot()
    {
        WindowsInstallScope scope = AutoUpdaterInstallerArguments.ResolveInstallScope(
            @"C:\Program Files Custom\UniGetUI",
            null,
            null,
            [@"C:\Program Files"]
        );

        Assert.Equal(WindowsInstallScope.Unknown, scope);
    }

    [Fact]
    public void ResolveInstallScope_ReturnsUnknownForAnUnrecognizedDirectory()
    {
        Assert.Equal(
            WindowsInstallScope.Unknown,
            AutoUpdaterInstallerArguments.ResolveInstallScope(@"D:\Builds\UniGetUI", null, null, [])
        );
        Assert.Equal(
            WindowsInstallScope.Unknown,
            AutoUpdaterInstallerArguments.ResolveInstallScope("", @"C:\Program Files\UniGetUI", null, [])
        );
    }

    [Fact]
    public void PreferMatchingPath_PicksTheRegistryViewThatMatchesTheRunningCopy()
    {
        Assert.Equal(
            @"C:\Program Files\UniGetUI",
            AutoUpdaterInstallerArguments.PreferMatchingPath(
                @"C:\Program Files\UniGetUI",
                @"C:\Stale\UniGetUI",
                @"C:\Program Files\UniGetUI"
            )
        );
    }

    [Fact]
    public void PreferMatchingPath_FallsBackToTheFirstRecordedPath()
    {
        Assert.Equal(
            @"C:\Stale\UniGetUI",
            AutoUpdaterInstallerArguments.PreferMatchingPath(
                @"C:\Program Files\Elsewhere",
                @"C:\Stale\UniGetUI",
                @"C:\Other\UniGetUI"
            )
        );

        Assert.Equal(
            @"C:\Other\UniGetUI",
            AutoUpdaterInstallerArguments.PreferMatchingPath(@"", null, @"C:\Other\UniGetUI")
        );

        Assert.Null(AutoUpdaterInstallerArguments.PreferMatchingPath(@"C:\Any", null, null));
    }

    [Fact]
    public void DetectInstallScope_ReturnsUnknownForADirectoryThatIsNotAnInstallation()
    {
        string unrelated = Path.Combine(Path.GetTempPath(), "UniGetUI.ScopeProbe.NotAnInstall");

        Assert.Equal(
            WindowsInstallScope.Unknown,
            AutoUpdaterInstallerArguments.DetectInstallScope(unrelated)
        );
        Assert.Equal(
            WindowsInstallScope.Unknown,
            AutoUpdaterInstallerArguments.DetectInstallScope("")
        );
    }

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
        Assert.Equal(expected, AutoUpdaterHelpers.IsSourceUrlAllowed(url, allowUnsafeUrls));
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
        var preferred = new AutoUpdaterHelpers.ProductInfoFile
        {
            Arch = targetArch,
            Type = "exe",
            Url = "https://example.test/app.exe",
            Hash = "hash-exe",
        };

        var selected = AutoUpdaterHelpers.SelectInstallerFile(
            [
                new AutoUpdaterHelpers.ProductInfoFile
                {
                    Arch = "Any",
                    Type = "exe",
                    Url = "https://example.test/any.exe",
                    Hash = "hash-any",
                },
                new AutoUpdaterHelpers.ProductInfoFile
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

        Assert.Equal(new Version(1, 2, 3), AutoUpdaterHelpers.ParseVersionOrFallback("v1.2.3", fallback));
        Assert.Equal(fallback, AutoUpdaterHelpers.ParseVersionOrFallback("not-a-version", fallback));
    }

    [Fact]
    public void NormalizeThumbprint_RemovesNonHexCharactersAndLowercases()
    {
        Assert.Equal("abcdef1234", AutoUpdaterHelpers.NormalizeThumbprint("AB:CD ef-12_34"));
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
            Assert.Equal("https://devolutions.net/custom.json", AutoUpdaterHelpers.GetRegistryString(key, "ProductInfoUrl"));
            Assert.True(AutoUpdaterHelpers.GetRegistryBool(key, "AllowUnsafe"));
            Assert.Null(AutoUpdaterHelpers.GetRegistryString(key, "Missing"));
            Assert.False(AutoUpdaterHelpers.GetRegistryBool(key, "Missing"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }
#endif
}

using System.Runtime.InteropServices;
using Microsoft.Win32;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Data;
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
        Assert.Equal(new Version(2026, 3, 2), AutoUpdaterHelpers.ParseVersionOrFallback("2026.3.2-beta1", fallback));
        Assert.Equal(fallback, AutoUpdaterHelpers.ParseVersionOrFallback("not-a-version", fallback));
    }

    [Theory]
    [InlineData("2026.3.1", "2026.3.2-beta1", true)]
    [InlineData("2026.3.2-beta1", "2026.3.2-beta2", true)]
    [InlineData("2026.3.2-beta2", "2026.3.2-beta3", true)]
    [InlineData("2026.3.2-beta2", "2026.3.2-beta10", true)]
    [InlineData("2026.3.2-beta3", "2026.3.2", true)]
    [InlineData("2026.3.2-beta3", "2026.3.2.0", true)]
    [InlineData("2026.3.2", "2026.3.3-beta1", true)]
    [InlineData("2026.3.2-beta1", "2026.3.2-beta1", false)]
    [InlineData("2026.3.2-beta1", "2026.3.2.0-beta1", false)]
    [InlineData("2026.3.2-beta2", "2026.3.2-beta1", false)]
    [InlineData("2026.3.2", "2026.3.2.0", false)]
    [InlineData("2026.3.2", "2026.3.2-beta3", false)]
    [InlineData("2026.3.2.0", "2026.3.2-beta3", false)]
    public void IsUpdateAvailable_OrdersBetaProgressionAndFinalReleaseCorrectly(
        string currentVersion,
        string availableVersion,
        bool expectedUpgradable
    )
    {
        Assert.Equal(
            expectedUpgradable,
            AutoUpdaterHelpers.IsUpdateAvailable(currentVersion, availableVersion)
        );
    }

    [Theory]
    [InlineData("2026.3.2", "2026.3.2.0", true)]
    [InlineData("v2026.3.2", "2026.3.2.0", true)]
    [InlineData("2026.3.2-beta1", "2026.3.2-beta1", true)]
    [InlineData("2026.3.2-beta1", "2026.3.2.0-beta1", true)]
    [InlineData("2026.3.2-beta1", "2026.3.2-beta2", false)]
    [InlineData("2026.3.2-beta1", "2026.3.2", false)]
    [InlineData("2026.3.2-beta1", "2026.3.2.0", false)]
    public void VersionsMatch_DistinguishesPrereleaseAndNormalizesFourPartVersions(
        string left,
        string right,
        bool expectedMatch
    )
    {
        Assert.Equal(expectedMatch, AutoUpdaterHelpers.VersionsMatch(left, right));
        Assert.Equal(expectedMatch, AvaloniaAutoUpdater.VersionsMatch(left, right));
    }

    [Theory]
    [InlineData("2026.3.2-beta1", true)]
    [InlineData("2026.3.2-beta2", true)]
    [InlineData("2026.3.2.0-rc1", true)]
    [InlineData("2026.3.2", false)]
    [InlineData("2026.3.2.0", false)]
    [InlineData("not-a-version", false)]
    public void IsPreReleaseVersion_DetectsPreReleaseStrings(string? rawVersion, bool expected)
    {
        Assert.Equal(expected, AutoUpdaterHelpers.IsPreReleaseVersion(rawVersion));
    }

    [Fact]
    public void SelectChannel_ChoosesExpectedChannelAcrossStableAndBetaCycles()
    {
        var stableOld = new AutoUpdaterHelpers.ProductInfoChannel { Version = "2026.3.1.0" };
        var betaNew = new AutoUpdaterHelpers.ProductInfoChannel { Version = "2026.3.2-beta2" };
        var stableFinal = new AutoUpdaterHelpers.ProductInfoChannel { Version = "2026.3.2.0" };

        // Stable user always gets Current
        Assert.Same(
            stableOld,
            AutoUpdaterHelpers.SelectChannel(stableOld, betaNew, useBeta: false)
        );

        // Beta user gets Beta when Beta is ahead of Current (2026.3.2-beta2 > 2026.3.1.0)
        Assert.Same(
            betaNew,
            AutoUpdaterHelpers.SelectChannel(stableOld, betaNew, useBeta: true)
        );

        // Beta user gets Current when stable final ships (2026.3.2.0 > 2026.3.2-beta2)
        Assert.Same(
            stableFinal,
            AutoUpdaterHelpers.SelectChannel(stableFinal, betaNew, useBeta: true)
        );

        // Beta user falls back to Current if Beta channel is absent
        Assert.Same(
            stableFinal,
            AutoUpdaterHelpers.SelectChannel(stableFinal, beta: null, useBeta: true)
        );
    }

    [Fact]
    public void CompareUpdaterVersions_MaintainsTransitiveOrderAcrossNumericAndAlphanumericLabels()
    {
        const string beta2 = "2026.3.2-beta2";
        const string beta10 = "2026.3.2-beta10";
        const string beta1a = "2026.3.2-beta1a";

        Assert.True(AutoUpdaterHelpers.CompareUpdaterVersions(beta2, beta10) < 0);
        Assert.True(AutoUpdaterHelpers.CompareUpdaterVersions(beta10, beta1a) < 0);
        Assert.True(AutoUpdaterHelpers.CompareUpdaterVersions(beta2, beta1a) < 0);

        Assert.True(AutoUpdaterHelpers.CompareUpdaterVersions(beta10, beta2) > 0);
        Assert.True(AutoUpdaterHelpers.CompareUpdaterVersions(beta1a, beta10) > 0);
        Assert.True(AutoUpdaterHelpers.CompareUpdaterVersions(beta1a, beta2) > 0);
    }

    [Fact]
    public async Task UpdaterAttemptCoordinator_CoalescesConcurrentRequestsThroughConsentAndCompletion()
    {
        var coordinator = new AutoUpdaterHelpers.UpdaterAttemptCoordinator();
        var attemptGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int attemptInvocations = 0;
        int joinedCount = 0;
        int busyNotificationCount = 0;
        string? consentNotifiedVersion = null;

        Task<bool> firstAttempt = coordinator.RunOrJoinAsync(
            manualCheck: false,
            () =>
            {
                Interlocked.Increment(ref attemptInvocations);
                return attemptGate.Task;
            }
        );

        Assert.True(coordinator.IsActive);
        Assert.False(coordinator.IsManualCheck);

        // A manual check while busy reuses the in-flight attempt and promotes it to manual.
        Task<bool> secondAttempt = coordinator.RunOrJoinAsync(
            manualCheck: true,
            () =>
            {
                Interlocked.Increment(ref attemptInvocations);
                return Task.FromResult(false);
            },
            onJoinedActiveAttempt: () => Interlocked.Increment(ref joinedCount),
            onManualJoinWhileAwaitingConsent: version => consentNotifiedVersion = version,
            onManualJoinWhileBusy: () => Interlocked.Increment(ref busyNotificationCount)
        );

        Assert.Same(firstAttempt, secondAttempt);
        Assert.Equal(1, attemptInvocations);
        Assert.Equal(1, joinedCount);
        Assert.Equal(1, busyNotificationCount);
        Assert.Null(consentNotifiedVersion);
        Assert.True(coordinator.IsManualCheck);

        // Transition the active attempt into awaiting installation consent.
        coordinator.MarkAwaitingConsent("2026.3.2-beta1");

        Task<bool> thirdAttempt = coordinator.RunOrJoinAsync(
            manualCheck: true,
            () =>
            {
                Interlocked.Increment(ref attemptInvocations);
                return Task.FromResult(false);
            },
            onJoinedActiveAttempt: () => Interlocked.Increment(ref joinedCount),
            onManualJoinWhileAwaitingConsent: version => consentNotifiedVersion = version,
            onManualJoinWhileBusy: () => Interlocked.Increment(ref busyNotificationCount)
        );

        Assert.Same(firstAttempt, thirdAttempt);
        Assert.Equal(1, attemptInvocations);
        Assert.Equal(2, joinedCount);
        Assert.Equal(1, busyNotificationCount);
        Assert.Equal("2026.3.2-beta1", consentNotifiedVersion);

        coordinator.ClearAwaitingConsent();
        attemptGate.SetResult(true);

        Assert.True(await firstAttempt);
        Assert.True(await secondAttempt);
        Assert.True(await thirdAttempt);
        Assert.False(coordinator.IsActive);
        Assert.False(coordinator.IsManualCheck);

        // After completion, a new request starts a new attempt.
        Task<bool> nextAttempt = coordinator.RunOrJoinAsync(
            manualCheck: false,
            () =>
            {
                Interlocked.Increment(ref attemptInvocations);
                return Task.FromResult(true);
            }
        );

        Assert.True(await nextAttempt);
        Assert.Equal(2, attemptInvocations);
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.0.0", true)]
    [InlineData("1.0.0.0", true)]
    [InlineData("v1.0", true)]
    [InlineData("v1.0.0", true)]
    [InlineData("v1.0.0.0", true)]
    [InlineData("1.0.1", false)]
    [InlineData("1.0.0.1", false)]
    [InlineData("1.1.0", false)]
    [InlineData("2026.1.0", false)]
    [InlineData("2026.1.1", false)]
    [InlineData("2026.2.0", false)]
    [InlineData("2026.3.2", false)]
    [InlineData("3.3.7", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not-a-version", false)]
    public void IsDefaultDevVersion_IdentifiesDefaultDevVersionsOnly(
        string? rawVersion,
        bool expected
    )
    {
        Assert.Equal(expected, AutoUpdaterHelpers.IsDefaultDevVersion(rawVersion));
        Assert.Equal(expected, AvaloniaAutoUpdater.IsDefaultDevVersion(rawVersion));
    }

    [Fact]
    public async Task CheckAndInstallUpdatesAsync_SkipsWhenRunningDefaultDevVersion()
    {
        if (!AvaloniaAutoUpdater.IsDefaultDevVersion(CoreData.VersionName))
        {
            return;
        }

        Assert.True(await AvaloniaAutoUpdater.CheckAndInstallUpdatesAsync());
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

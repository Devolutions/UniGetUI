using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using UniGetUI.Core.Data;
using UniGetUI.Extensions.Xcode;
using UniGetUI.Interface;
using UniGetUI.Interface.Enums;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Tests;

public sealed class XcodeTests : IDisposable
{
    // 26.2 and 26.4 are installed, 26.5 is the newest release, and 26.5 comes in two builds.
    private const string DefaultList =
        "26.0 (17A324) [Apple Silicon]\n"
        + "26.1 Beta 2 (17B5035f) [Apple Silicon]\n"
        + "26.2 (17C52) [Apple Silicon] (Installed)\n"
        + "26.4 (17E192) [Apple Silicon] (Installed, Selected)\n"
        + "26.5 (17F42) [Apple Silicon]\n"
        + "26.5 (17F43) [Apple Silicon]\n"
        + "26.6 Beta (17G5033d) [Apple Silicon]\n"
        + "\n"
        + "Showing Xcodes for this Mac by default: Apple Silicon (arm64).\n";

    private const string DefaultInstalled =
        "26.2 (17C52) [Apple Silicon]\t/Applications/Xcode-26.2.app\n"
        + "26.4 (17E192) [Apple Silicon] (Selected)\t/Applications/Xcode-26.4.app\n";

    private const string OnlyInstalledList =
        "26.2 (17C52) [Apple Silicon] (Installed)\n"
        + "26.4 (17E192) [Apple Silicon] (Installed, Selected)\n";

    private const string SecondSelected =
        "26.2 (17C52) [Apple Silicon] (Selected)\t/Applications/Xcode-26.2.app\n"
        + "26.4 (17E192) [Apple Silicon]\t/Applications/Xcode-26.4.app\n";

    private const string OnlyNewestInstalled =
        "26.4 (17E192) [Apple Silicon] (Selected)\t/Applications/Xcode-26.4.app\n";

    private const string WithNewestInstalled =
        DefaultInstalled + "26.5 (17F42) [Apple Silicon]\t/Applications/Xcode-26.5.app\n";

    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(XcodeTests).Assembly);
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), nameof(XcodeTests), Guid.NewGuid().ToString("N"));

    public XcodeTests()
    {
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        CoreSettings.ResetSettings();
    }

    public void Dispose()
    {
        CoreSettings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        try
        {
            Directory.Delete(_testRoot, recursive: true);
        }
        catch (IOException) { }
    }

    // ─── Navigation, sidebar and extension ───────────────────────────────────

    [Fact]
    public void TheXcodePageIsInTheSidebarOnlyWhenEnabled()
    {
        Assert.Equal(
            [PageType.Discover, PageType.Updates, PageType.Installed, PageType.Bundles, PageType.Xcode],
            NavigationPageOrder.DefaultPages(catalogEnabled: false, xcodeEnabled: true));
        Assert.Equal(
            [PageType.Discover, PageType.Updates, PageType.Installed, PageType.Bundles],
            NavigationPageOrder.Resolve("Xcode,Discover", catalogEnabled: false));
        Assert.Equal(
            [PageType.Xcode, PageType.Discover, PageType.Catalog, PageType.Updates, PageType.Installed, PageType.Bundles],
            NavigationPageOrder.Resolve("Xcode,Discover", catalogEnabled: true, xcodeEnabled: true));
    }

    [Fact]
    public void XcodeAndGitRepositoriesAreIndependent()
    {
        Assert.Equal(
            [PageType.Discover, PageType.Updates, PageType.Installed, PageType.Bundles, PageType.GitRepositories, PageType.Xcode],
            NavigationPageOrder.DefaultPages(catalogEnabled: false, gitRepositoriesEnabled: true, xcodeEnabled: true));
        Assert.Equal(
            [PageType.Xcode, PageType.Discover, PageType.Updates, PageType.Installed, PageType.Bundles],
            NavigationPageOrder.Resolve(
                "GitRepositories,Xcode,Discover",
                catalogEnabled: false,
                gitRepositoriesEnabled: false,
                xcodeEnabled: true));
        Assert.Equal("", NavigationPageOrder.Serialize(NavigationPageOrder.DefaultPages(true, true, true)));
    }

    [Fact]
    public void ReorderingWithXcodeHiddenKeepsItInPlace()
    {
        PageType[] full = NavigationPageOrder.Resolve(
            "Discover,Xcode,Updates,Installed,Bundles,Catalog", catalogEnabled: true, xcodeEnabled: true);
        PageType[] merged = NavigationPageOrder.Merge(full,
            [PageType.Bundles, PageType.Discover, PageType.Updates, PageType.Installed, PageType.Catalog]);

        Assert.Equal(
            [PageType.Bundles, PageType.Xcode, PageType.Discover, PageType.Updates, PageType.Installed, PageType.Catalog],
            merged);
        Assert.Equal(
            merged,
            NavigationPageOrder.Resolve(NavigationPageOrder.Serialize(merged), catalogEnabled: true, xcodeEnabled: true));
    }

    [Fact]
    public Task TurningXcodeOnAndOffNeedsNoRestart() => _session.Dispatch(() =>
    {
        var sidebar = new SidebarViewModel();
        Assert.DoesNotContain(PageType.Xcode, sidebar.NavigationPages);

        sidebar.XcodeEnabled = true;
        Assert.Equal(PageType.Xcode, sidebar.NavigationPages.Last());

        sidebar.GitRepositoriesEnabled = true;
        Assert.Equal([PageType.GitRepositories, PageType.Xcode], sidebar.NavigationPages.TakeLast(2).ToArray());

        Assert.False(sidebar.XcodeBadgeVisible);
        sidebar.XcodeBadgeCount = 1;
        Assert.True(sidebar.XcodeBadgeVisible);
        Assert.Equal("1", sidebar.XcodeBadgeText);
        sidebar.XcodeBadgeCount = 120;
        Assert.Equal("99+", sidebar.XcodeBadgeText);

        sidebar.XcodeIsLoading = true;
        sidebar.XcodeEnabled = false;
        Assert.DoesNotContain(PageType.Xcode, sidebar.NavigationPages);
        Assert.Contains(PageType.GitRepositories, sidebar.NavigationPages);
        Assert.False(sidebar.XcodeBadgeVisible);
        Assert.False(sidebar.XcodeIsLoading);
    }, CancellationToken.None);

    [Fact]
    public void XcodeIsAnIpcNavigationTarget()
    {
        Assert.Contains("xcode", IpcAppPages.SupportedPages);
        Assert.Equal("xcode", IpcAppPages.NormalizePageName(" Xcode "));
        Assert.Equal("xcode", IpcAppPages.ToPageName(nameof(PageType.Xcode)));
    }

    [Fact]
    public Task TheXcodeExtensionIsDescribedAndOffByDefault() => _session.Dispatch(() =>
    {
        AppExtension extension = AppExtensions.Xcode;

        Assert.Equal("xcode", extension.Id);
        Assert.Equal("Xcode", extension.Name);
        Assert.NotEmpty(extension.Description);
        Assert.Equal(IconType.Xcode, extension.Icon);
        Assert.Equal(CoreSettings.K.EnableXcode, extension.EnabledSetting);
        Assert.Equal(PageType.Xcode, extension.Page);
        Assert.Equal(typeof(XcodeSettings), extension.SettingsPage);
        Assert.False(extension.IsEnabled);
        Assert.Equal("", extension.Status());
    }, CancellationToken.None);

    [Fact]
    public Task TheXcodeExtensionIsRememberedAndNeedsNoWindowToBeTurnedOnOrOff() => _session.Dispatch(() =>
    {
        AppExtension extension = AppExtensions.Xcode;

        extension.SetEnabled(true);
        Assert.True(extension.IsEnabled);
        Assert.True(CoreSettings.Get(CoreSettings.K.EnableXcode));
        Assert.False(AppExtensions.GitRepositories.IsEnabled);

        extension.SetEnabled(false);
        Assert.False(extension.IsEnabled);
        Assert.False(CoreSettings.Get(CoreSettings.K.EnableXcode));
    }, CancellationToken.None);

    [Fact]
    public Task TheXcodeExtensionIsListedOnMacOnly() => _session.Dispatch(() =>
    {
        Assert.Equal(OperatingSystem.IsMacOS(), AppExtensions.All.Contains(AppExtensions.Xcode));
        Assert.Contains(AppExtensions.GitRepositories, AppExtensions.All);
    }, CancellationToken.None);

    [Fact]
    public Task TheXcodeSettingsPageOpensFromTheExtensionsPage() => _session.Dispatch(() =>
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var page = Assert.IsType<XcodeSettings>(AppExtensions.Xcode.CreateSettingsPage());
        Assert.Equal("Xcode", page.ShortTitle);
        Assert.True(page.CanGoBack);
    }, CancellationToken.None);

    // ─── Preferences ─────────────────────────────────────────────────────────

    [Fact]
    public void TheSettingsPageOffersTheSameCheckIntervalsAsThePage()
    {
        Assert.Equal(
            XcodePreferences.CheckIntervals.Select(minutes => minutes > 0 ? minutes.ToString(CultureInfo.InvariantCulture) : ""),
            XcodeSettingsViewModel.CheckIntervalItems.Select(item => item.Value));
    }

    [Theory]
    [InlineData(240)]
    [InlineData(1440)]
    [InlineData(10080)]
    public void AnOfferedCheckIntervalIsKept(int minutes)
    {
        Assert.Equal(0, XcodePreferences.CheckIntervalMinutes);

        XcodePreferences.CheckIntervalMinutes = minutes;

        Assert.Equal(minutes, XcodePreferences.CheckIntervalMinutes);
        Assert.Equal(
            minutes.ToString(CultureInfo.InvariantCulture),
            CoreSettings.GetValue(CoreSettings.K.XcodeCheckInterval));

        XcodePreferences.CheckIntervalMinutes = 0;
        Assert.Equal(0, XcodePreferences.CheckIntervalMinutes);
        Assert.Equal("", CoreSettings.GetValue(CoreSettings.K.XcodeCheckInterval));
    }

    [Theory]
    [InlineData("7")]
    [InlineData("soon")]
    [InlineData("-240")]
    [InlineData("0")]
    public void AnUnofferedCheckIntervalReadsAsNever(string stored)
    {
        CoreSettings.SetValue(CoreSettings.K.XcodeCheckInterval, stored);

        Assert.Equal(0, XcodePreferences.CheckIntervalMinutes);
    }

    [Fact]
    public void EveryPreferenceIsOffUntilItIsChosen()
    {
        Assert.False(XcodePreferences.IncludePrereleases);
        Assert.False(XcodePreferences.SelectAfterInstall);
        Assert.False(XcodePreferences.SkipAdministratorSteps);
        Assert.False(XcodePreferences.DeleteArchive);
        Assert.False(XcodePreferences.FastUnarchive);
        Assert.Equal(new XcodeInstallOptions(false, false, false, false), XcodePreferences.InstallOptions);
    }

    [Fact]
    public void TheInstallOptionsFollowTheSettings()
    {
        XcodePreferences.SelectAfterInstall = true;
        XcodePreferences.FastUnarchive = true;
        Assert.Equal(new XcodeInstallOptions(true, false, false, true), XcodePreferences.InstallOptions);

        XcodePreferences.SkipAdministratorSteps = true;
        XcodePreferences.DeleteArchive = true;
        Assert.Equal(new XcodeInstallOptions(true, true, true, true), XcodePreferences.InstallOptions);

        XcodePreferences.SelectAfterInstall = false;
        XcodePreferences.FastUnarchive = false;
        Assert.Equal(new XcodeInstallOptions(false, true, true, false), XcodePreferences.InstallOptions);
    }

    // ─── What the lists show ─────────────────────────────────────────────────

    [Theory]
    [InlineData("17E192", XcodeArchitecture.AppleSilicon, "Build 17E192 · Apple Silicon")]
    [InlineData("17E192", XcodeArchitecture.Unspecified, "Build 17E192")]
    [InlineData(null, XcodeArchitecture.Intel, "Intel")]
    [InlineData(null, XcodeArchitecture.Universal, "Universal")]
    [InlineData(null, XcodeArchitecture.Unspecified, "")]
    public void TheDetailLineOnlyShowsWhatIsKnown(string? build, XcodeArchitecture architecture, string expected)
    {
        Assert.Equal(expected, XcodeText.Details(build, architecture));
    }

    [Fact]
    public void AnInstalledXcodeDescribesItself()
    {
        var selected = new InstalledXcodeItemViewModel(new InstalledXcode(
            "26.4", "17E192", XcodeArchitecture.AppleSilicon, true, "/Applications/Xcode-26.4.app"));

        Assert.Equal("Xcode 26.4", selected.Title);
        Assert.Equal("26.4", selected.Version);
        Assert.Equal("/Applications/Xcode-26.4.app", selected.Location);
        Assert.Equal("Build 17E192 · Apple Silicon", selected.DetailText);
        Assert.True(selected.IsSelected);
        Assert.False(selected.CanSelect);
        Assert.False(selected.IsPrerelease);
        Assert.True(selected.IsIdle);
        selected.IsBusy = true;
        Assert.False(selected.IsIdle);

        var beta = new InstalledXcodeItemViewModel(new InstalledXcode(
            "26.6 Beta", null, XcodeArchitecture.Unspecified, false, "/Applications/Xcode-26.6-Beta.app"));

        Assert.True(beta.IsPrerelease);
        Assert.True(beta.CanSelect);
        Assert.Equal("", beta.DetailText);
    }

    [Fact]
    public void AnAvailableXcodeDescribesItself()
    {
        var beta = new AvailableXcodeItemViewModel(
            new XcodeRelease("26.6 Beta", "17G5033d", XcodeArchitecture.AppleSilicon, false, false));

        Assert.Equal("Xcode 26.6 Beta", beta.Title);
        Assert.Equal("26.6 Beta", beta.Version);
        Assert.True(beta.IsPrerelease);
        Assert.Equal("Build 17G5033d · Apple Silicon", beta.DetailText);

        var stable = new AvailableXcodeItemViewModel(
            new XcodeRelease("26.5", null, XcodeArchitecture.Intel, false, false));

        Assert.False(stable.IsPrerelease);
        Assert.Equal("Intel", stable.DetailText);
    }

    // ─── Loading ─────────────────────────────────────────────────────────────

    [Fact]
    public Task BeforeItHasLoadedThePageSaysSo()
    {
        var fake = new FakeXcodes();
        return WithPage(fake, vm =>
        {
            Assert.False(vm.HasLoaded);
            Assert.False(vm.NoneInstalled);
            Assert.False(vm.HasUpdate);
            Assert.Equal("Reading the Xcode versions...", vm.AvailableEmptyText);
            Assert.Equal("", vm.XcodesVersionText);
            Assert.Equal("", vm.LastCheckedText);
            AssertCommands(fake.Commands);
        }, initialize: false);
    }

    [Fact]
    public Task LoadingListsWhatIsInstalledAndWhatCanBeInstalled()
    {
        var fake = new FakeXcodes();
        return WithPage(fake, vm =>
        {
            AssertCommands(fake.Commands, "version", "installed", "list");
            AssertCommands(fake.TerminalCommands);

            Assert.False(vm.IsXcodesMissing);
            Assert.Equal("2.1.0", vm.XcodesVersion);
            Assert.Equal("xcodes 2.1.0", vm.XcodesVersionText);

            Assert.Equal(["26.4", "26.2"], InstalledVersions(vm));
            Assert.True(vm.HasInstalled);
            Assert.False(vm.NoneInstalled);
            Assert.True(vm.Installed[0].IsSelected);
            Assert.False(vm.Installed[0].CanSelect);
            Assert.True(vm.Installed[1].CanSelect);
            Assert.Equal("/Applications/Xcode-26.4.app", vm.Installed[0].Location);

            Assert.Equal(["26.5", "26.0"], AvailableVersions(vm));
            Assert.True(vm.HasAvailable);
            Assert.False(vm.IsAvailableEmpty);

            Assert.Equal("26.5", vm.UpdateRelease?.Version);
            Assert.True(vm.HasUpdate);
            Assert.Equal(1, vm.UpdateCount);
            Assert.Equal("Update to Xcode 26.5", vm.UpdateButtonText);

            Assert.True(vm.HasLoaded);
            Assert.Null(vm.LastChecked);
            Assert.Equal("", vm.LastCheckedText);
            Assert.False(vm.IsBusy);
            Assert.False(vm.Notice.IsOpen);
            Assert.False(vm.HasOperation);
        });
    }

    [Fact]
    public Task APreReleaseIsOfferedOnlyWhenAsked() => WithPage(new FakeXcodes(), vm =>
    {
        Assert.False(vm.IncludePrereleases);

        vm.IncludePrereleases = true;

        Assert.True(CoreSettings.Get(CoreSettings.K.XcodeIncludePrereleases));
        Assert.Equal(["26.6 Beta", "26.5", "26.1 Beta 2", "26.0"], AvailableVersions(vm));
        Assert.Equal([true, false, true, false], vm.Available.Select(item => item.IsPrerelease).ToArray());
        Assert.Equal("26.6 Beta", vm.UpdateVersion);

        vm.IncludePrereleases = false;

        Assert.False(CoreSettings.Get(CoreSettings.K.XcodeIncludePrereleases));
        Assert.Equal(["26.5", "26.0"], AvailableVersions(vm));
        Assert.Equal("26.5", vm.UpdateVersion);
    });

    [Fact]
    public Task ThePreReleaseChoiceIsReadWhenThePageIsCreated()
    {
        CoreSettings.Set(CoreSettings.K.XcodeIncludePrereleases, true);
        return WithPage(new FakeXcodes(), vm =>
        {
            Assert.True(vm.IncludePrereleases);
            Assert.Equal("26.6 Beta", vm.UpdateVersion);
        });
    }

    [Fact]
    public Task ChangingThePreferencesInSettingsReachesThePage() => WithPage(new FakeXcodes(), vm =>
    {
        Assert.False(vm.IncludePrereleases);

        CoreSettings.Set(CoreSettings.K.XcodeIncludePrereleases, true);
        vm.ReloadPreferences();

        Assert.True(vm.IncludePrereleases);
        Assert.Contains(vm.Available, item => item.IsPrerelease);
        Assert.Equal("26.6 Beta", vm.UpdateVersion);
    });

    [Fact]
    public Task SearchingNarrowsTheVersionsThatCanBeInstalled() => WithPage(new FakeXcodes(), vm =>
    {
        vm.IncludePrereleases = true;

        AssertSearch(vm, "beta", "26.6 Beta", "26.1 Beta 2");
        AssertSearch(vm, "  Xcode 26.1 ", "26.1 Beta 2");
        AssertSearch(vm, "17F43", "26.5");
        Assert.Equal("Build 17F43 · Apple Silicon", vm.Available[0].DetailText);
        AssertSearch(vm, "", "26.6 Beta", "26.5", "26.1 Beta 2", "26.0");

        vm.SearchText = "nothing";
        Assert.Empty(vm.Available);
        Assert.True(vm.IsAvailableEmpty);
        Assert.False(vm.HasAvailable);
        Assert.Equal("No version matches your search", vm.AvailableEmptyText);
    });

    [Fact]
    public Task WithNothingInstalledNoUpdateIsOffered()
    {
        var fake = new FakeXcodes
        {
            ListOutput = "26.4 (17E192) [Apple Silicon]\n26.5 (17F42) [Apple Silicon]\n",
            InstalledOutput = "",
        };
        return WithPageAsync(fake, async vm =>
        {
            Assert.True(vm.HasLoaded);
            Assert.True(vm.NoneInstalled);
            Assert.False(vm.HasInstalled);
            Assert.Null(vm.UpdateRelease);
            Assert.Equal(0, vm.UpdateCount);
            Assert.Equal(["26.5", "26.4"], AvailableVersions(vm));

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            AssertNotice(
                vm, InfoBarSeverity.Informational,
                "No Xcode is installed yet", "Choose a version from the list below to install it.");
        });
    }

    [Fact]
    public Task WhenTheNewestIsInstalledThePageIsUpToDate()
    {
        var fake = new FakeXcodes { ListOutput = OnlyInstalledList };
        return WithPageAsync(fake, async vm =>
        {
            Assert.False(vm.HasUpdate);
            Assert.Empty(vm.Available);
            Assert.Equal("There is no other version to install", vm.AvailableEmptyText);

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            AssertNotice(
                vm, InfoBarSeverity.Success,
                "Xcode is up to date", "No release newer than Xcode 26.4 was found.");

            vm.SearchText = "26";
            Assert.Equal("No version matches your search", vm.AvailableEmptyText);
        });
    }

    // ─── Checking for updates ────────────────────────────────────────────────

    [Fact]
    public Task CheckingForUpdatesDownloadsTheListAgainAndSaysWhatItFound()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            fake.Commands.Clear();

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            AssertCommands(fake.Commands, "version", "installed", "update");
            Assert.NotNull(vm.LastChecked);
            Assert.StartsWith("Checked ", vm.LastCheckedText);
            Assert.False(vm.IsBusy);
            AssertNotice(
                vm, InfoBarSeverity.Informational,
                "Xcode 26.5 is available", "The newest Xcode you have is 26.4.");
            Assert.Equal("26.5", vm.UpdateVersion);
        });
    }

    [Fact]
    public Task AnAutomaticCheckRefreshesTheListWithoutSayingAnything()
    {
        XcodePreferences.CheckIntervalMinutes = 1440;
        var fake = new FakeXcodes();
        return WithPage(fake, vm =>
        {
            AssertCommands(fake.Commands, "version", "installed", "update");
            Assert.NotNull(vm.LastChecked);
            Assert.True(vm.HasUpdate);
            Assert.False(vm.Notice.IsOpen);
        });
    }

    [Fact]
    public Task WhenTheListCannotBeRefreshedTheOneItAlreadyHadIsShown()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            fake.Override(command => command == "update" ? FakeXcodes.Failure("The network is down") : null);
            fake.Commands.Clear();

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            AssertCommands(fake.Commands, "version", "installed", "update", "list");
            Assert.Null(vm.LastChecked);
            Assert.True(vm.HasLoaded);
            Assert.Equal("26.5", vm.UpdateVersion);
            Assert.True(vm.Notice.IsOpen);
            Assert.Equal(InfoBarSeverity.Warning, vm.Notice.Severity);
            Assert.Equal("Could not check for new versions", vm.Notice.Title);
            Assert.Contains("The network is down", vm.Notice.Message);
        });
    }

    [Fact]
    public Task WhenXcodesCannotReadTheVersionsOnlyAnAskedCheckSaysSo()
    {
        var fake = new FakeXcodes();
        fake.Override(command => command == "installed" ? FakeXcodes.Failure("Permission denied") : null);
        return WithPageAsync(fake, async vm =>
        {
            Assert.False(vm.HasLoaded);
            Assert.False(vm.Notice.IsOpen);

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            Assert.False(vm.HasLoaded);
            Assert.True(vm.Notice.IsOpen);
            Assert.Equal(InfoBarSeverity.Error, vm.Notice.Severity);
            Assert.Equal("Could not read the Xcode versions", vm.Notice.Title);
            Assert.Contains("Permission denied", vm.Notice.Message);
        });
    }

    [Fact]
    public Task OnlyOneThingRunsAtATime()
    {
        var fake = new FakeXcodes();
        var gate = new TaskCompletionSource<XcodesResult>();
        fake.OverrideAsync(command => command == "list" ? gate.Task : null);
        return WithPageAsync(fake, async vm =>
        {
            Task first = vm.InitializeAsync();

            AssertCommands(fake.Commands, "version", "installed", "list");
            Assert.True(vm.IsBusy);
            Assert.False(vm.CanStartOperation);
            Assert.True(vm.ShowBusyBar);

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            AssertCommands(fake.Commands, "version", "installed", "list");

            gate.SetResult(FakeXcodes.Success(DefaultList));
            await first;

            Assert.False(vm.IsBusy);
            Assert.True(vm.CanStartOperation);
            Assert.True(vm.HasLoaded);
        }, initialize: false);
    }

    [Fact]
    public Task AStoppedPageDoesNothing()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            vm.Stop();
            vm.Stop();

            await vm.InitializeAsync();
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            await vm.RecheckCommand.ExecuteAsync(null);

            AssertCommands(fake.Commands);
            Assert.False(vm.HasLoaded);
        }, initialize: false);
    }

    // ─── When xcodes is not there ────────────────────────────────────────────

    [Fact]
    public Task WithoutXcodesThePageSaysHowToGetItAndLooksAgainOnRequest()
    {
        var fake = new FakeXcodes { Executable = null };
        return WithPageAsync(fake, async vm =>
        {
            Assert.True(vm.IsXcodesMissing);
            Assert.Equal("", vm.XcodesVersion);
            Assert.False(vm.HasLoaded);
            AssertCommands(fake.Commands);
            Assert.True(vm.Notice.IsOpen);
            Assert.Equal(InfoBarSeverity.Error, vm.Notice.Severity);
            Assert.Equal("xcodes was not found", vm.Notice.Title);
            Assert.False(vm.Notice.IsClosable);
            Assert.Equal("Look again", vm.Notice.ActionButtonText);
            Assert.Same(vm.RecheckCommand, vm.Notice.ActionButtonCommand);

            fake.Executable = FakeXcodes.DefaultExecutable;
            await vm.RecheckCommand.ExecuteAsync(null);

            Assert.False(vm.IsXcodesMissing);
            Assert.False(vm.Notice.IsOpen);
            Assert.Equal("", vm.Notice.ActionButtonText);
            Assert.Null(vm.Notice.ActionButtonCommand);
            Assert.Equal("2.1.0", vm.XcodesVersion);
            Assert.True(vm.HasLoaded);
        });
    }

    [Fact]
    public Task AnXcodesThatCannotBeStartedIsReportedWithItsPath()
    {
        var fake = new FakeXcodes();
        fake.Override(command => command == "version" ? FakeXcodes.Failure("dyld: Library not loaded") : null);
        return WithPage(fake, vm =>
        {
            AssertCommands(fake.Commands, "version");
            Assert.True(vm.IsXcodesMissing);
            Assert.Equal("xcodes could not be started", vm.Notice.Title);
            Assert.Contains(FakeXcodes.DefaultExecutable, vm.Notice.Message);
            Assert.Equal("Look again", vm.Notice.ActionButtonText);
        });
    }

    // ─── Selecting ───────────────────────────────────────────────────────────

    [Fact]
    public Task SelectingAVersionMakesItTheOneCommandLineToolsUse()
    {
        var fake = new FakeXcodes();
        fake.RespondInTerminal = (_, _, _, _) =>
        {
            fake.InstalledOutput = SecondSelected;
            return Task.FromResult(FakeXcodes.Success(""));
        };
        return WithPageAsync(fake, async vm =>
        {
            fake.Commands.Clear();

            await vm.SelectCommand.ExecuteAsync(vm.Installed[1]);

            AssertCommands(fake.TerminalCommands, "select 26.2");
            AssertCommands(fake.Commands, "installed", "list");
            Assert.False(vm.HasOperation);
            Assert.False(vm.IsOperationRunning);
            AssertNotice(
                vm, InfoBarSeverity.Success,
                "Xcode 26.2 is now the selected Xcode", "Command line tools such as xcodebuild use it.");
            Assert.True(Assert.Single(vm.Installed, item => item.IsSelected).Version == "26.2");
        });
    }

    [Fact]
    public Task SelectingWhatIsAlreadySelectedDoesNothing()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            fake.Commands.Clear();

            await vm.SelectCommand.ExecuteAsync(vm.Installed[0]);
            await vm.SelectCommand.ExecuteAsync((InstalledXcodeItemViewModel?)null);

            AssertCommands(fake.Commands);
            AssertCommands(fake.TerminalCommands);
            Assert.False(vm.HasOperation);
        });
    }

    [Fact]
    public Task ARunningOperationBlocksOthersAndCanBeCancelled()
    {
        var fake = new FakeXcodes();
        var started = new TaskCompletionSource();
        fake.RespondInTerminal = async (_, _, _, cancellation) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellation);
            return FakeXcodes.Success("");
        };
        return WithPageAsync(fake, async vm =>
        {
            fake.Commands.Clear();

            Task selecting = vm.SelectCommand.ExecuteAsync(vm.Installed[1]);
            await started.Task;

            Assert.True(vm.IsOperationRunning);
            Assert.True(vm.HasOperation);
            Assert.False(vm.IsOperationFinished);
            Assert.Equal("Selecting Xcode 26.2", vm.OperationTitle);
            Assert.False(vm.CanStartOperation);
            Assert.False(await vm.ConfirmQuitAsync());

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            AssertCommands(fake.Commands);

            vm.CancelOperation();
            await selecting;

            Assert.False(vm.IsOperationRunning);
            Assert.False(vm.HasOperation);
            Assert.False(vm.IsBusy);
            Assert.True(await vm.ConfirmQuitAsync());
            AssertNotice(vm, InfoBarSeverity.Informational, "Selecting Xcode 26.2 was cancelled", "");
        });
    }

    [Fact]
    public Task AFailedOperationKeepsItsLogOpenUntilItIsDismissed()
    {
        var fake = new FakeXcodes();
        fake.RespondInTerminal = (_, _, _, _) => Task.FromResult(FakeXcodes.Failure("Xcode is damaged"));
        return WithPageAsync(fake, async vm =>
        {
            await vm.SelectCommand.ExecuteAsync(vm.Installed[1]);

            Assert.True(vm.HasOperation);
            Assert.False(vm.IsOperationRunning);
            Assert.True(vm.IsOperationFinished);
            Assert.True(vm.IsLogExpanded);
            Assert.False(vm.IsBusy);
            Assert.Equal("Could not select Xcode 26.2", vm.OperationTitle);
            Assert.True(vm.Notice.IsOpen);
            Assert.Equal(InfoBarSeverity.Error, vm.Notice.Severity);
            Assert.Equal("Could not select Xcode 26.2", vm.Notice.Title);
            Assert.Contains("Xcode is damaged", vm.Notice.Message);

            vm.DismissOperationCommand.Execute(null);

            Assert.False(vm.HasOperation);
        });
    }

    [Fact]
    public Task TheProgressPanelFollowsWhatXcodesWrites()
    {
        var fake = new FakeXcodes();
        XcodeViewModel? page = null;
        string? startStatus = null;
        bool? startIndeterminate = null;
        string? step = null;
        string? status = null;
        bool? indeterminate = null;
        double progress = -1;
        string? log = null;
        fake.RespondInTerminal = (_, _, output, _) =>
        {
            startStatus = page!.OperationStatus;
            startIndeterminate = page.OperationIsIndeterminate;

            output!(new XcodesOutputLine("(1/6) Downloading Xcode 26.2: 42%"));
            output(new XcodesOutputLine("(1/6) Downloading Xcode 26.2: 43%", ReplacesPrevious: true));
            output(new XcodesOutputLine("Waiting for the download to resume"));
            Dispatcher.UIThread.RunJobs();

            step = page.OperationStep;
            status = page.OperationStatus;
            indeterminate = page.OperationIsIndeterminate;
            progress = page.OperationProgress;
            log = page.OperationLog;
            return Task.FromResult(FakeXcodes.Success(""));
        };
        return WithPageAsync(fake, async vm =>
        {
            page = vm;

            await vm.SelectCommand.ExecuteAsync(vm.Installed[1]);

            Assert.Equal("Starting xcodes...", startStatus);
            Assert.True(startIndeterminate);
            Assert.Equal("Step 1 of 6", step);
            Assert.Equal("Downloading Xcode 26.2: 43%", status);
            Assert.False(indeterminate);
            Assert.Equal(43.0, progress, 6);
            Assert.Equal(
                "(1/6) Downloading Xcode 26.2: 43%" + Environment.NewLine + "Waiting for the download to resume",
                log);
        });
    }

    [Fact]
    public Task WhatXcodesWritesJustBeforeItFailsIsKeptInTheLog()
    {
        var fake = new FakeXcodes();
        fake.RespondInTerminal = async (_, _, output, _) =>
        {
            // The terminal is read off the interface thread, so its last lines are posted just before it ends.
            await Task.Run(() =>
            {
                output!(new XcodesOutputLine("(1/6) Downloading Xcode 26.2: 3%"));
                output(new XcodesOutputLine("Unable to authenticate"));
            });
            return FakeXcodes.Failure("Unable to authenticate");
        };
        return WithPageAsync(fake, async vm =>
        {
            await vm.SelectCommand.ExecuteAsync(vm.Installed[1]);

            Assert.True(vm.IsOperationFinished);
            Assert.Equal(
                "(1/6) Downloading Xcode 26.2: 3%" + Environment.NewLine + "Unable to authenticate",
                vm.OperationLog);
        });
    }

    [Fact]
    public Task AQuestionAskedJustBeforeItFailsDoesNotLeaveItsStatusOnTheFailedPanel()
    {
        var fake = new FakeXcodes();
        fake.RespondInTerminal = async (_, prompts, _, cancellation) =>
        {
            await prompts!(new XcodesPrompt(XcodesPromptKind.AppleId, "Apple ID:", []), cancellation);
            return FakeXcodes.Failure("Unable to authenticate");
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.Ask = (_, _) => Task.FromResult<string?>("me@example.com");

            await vm.SelectCommand.ExecuteAsync(vm.Installed[1]);
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsOperationFinished);
            Assert.Equal("", vm.OperationStatus);
        });
    }

    [Fact]
    public Task QuestionsFromXcodesAreAskedInTheWindow()
    {
        var fake = new FakeXcodes();
        XcodeViewModel? page = null;
        string? answer = null;
        string? status = null;
        fake.RespondInTerminal = async (_, prompts, _, cancellation) =>
        {
            answer = await prompts!(new XcodesPrompt(XcodesPromptKind.AppleId, "Apple ID:", []), cancellation);
            Dispatcher.UIThread.RunJobs();
            status = page!.OperationStatus;
            return FakeXcodes.Success("");
        };
        return WithPageAsync(fake, async vm =>
        {
            page = vm;
            XcodesPrompt? asked = null;
            vm.Ask = (prompt, _) =>
            {
                asked = prompt;
                return Task.FromResult<string?>("me@example.com");
            };

            await vm.SelectCommand.ExecuteAsync(vm.Installed[1]);

            Assert.Equal("me@example.com", answer);
            Assert.Equal(XcodesPromptKind.AppleId, asked?.Kind);
            Assert.Equal("Waiting for your answer...", status);
        });
    }

    // ─── Installing and updating ─────────────────────────────────────────────

    [Fact]
    public Task InstallingAVersionRunsXcodesAndSaysWhereItWent()
    {
        var fake = new FakeXcodes();
        fake.RespondInTerminal = (_, _, _, _) =>
        {
            fake.InstalledOutput = WithNewestInstalled;
            return Task.FromResult(FakeXcodes.Success(""));
        };
        return WithPageAsync(fake, async vm =>
        {
            XcodeRelease? asked = null;
            InstalledXcode? replaced = new InstalledXcode("none", null, XcodeArchitecture.Unspecified, false, "");
            vm.ConfirmInstall = (release, replacing) =>
            {
                asked = release;
                replaced = replacing;
                return Task.FromResult(true);
            };

            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);

            Assert.Equal("26.5", asked?.Version);
            Assert.Null(replaced);
            AssertCommands(fake.TerminalCommands, "install 26.5");
            Assert.False(vm.HasOperation);
            AssertNotice(vm, InfoBarSeverity.Success, "Xcode 26.5 was installed", "/Applications/Xcode-26.5.app");
            Assert.Equal(["26.5", "26.4", "26.2"], InstalledVersions(vm));
            Assert.Equal(["26.0"], AvailableVersions(vm));
            Assert.Null(vm.UpdateRelease);
        });
    }

    [Fact]
    public Task InstallingFollowsTheChosenOptions()
    {
        XcodePreferences.SelectAfterInstall = true;
        XcodePreferences.SkipAdministratorSteps = true;
        XcodePreferences.DeleteArchive = true;
        XcodePreferences.FastUnarchive = true;
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _) => Task.FromResult(true);

            await vm.InstallCommand.ExecuteAsync(vm.Available[1]);

            AssertCommands(
                fake.TerminalCommands,
                "install 26.0 --select --no-superuser --empty-trash --experimental-unxip");
        });
    }

    [Fact]
    public Task DecliningTheConfirmationInstallsNothing()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _) => Task.FromResult(false);
            fake.Commands.Clear();

            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);
            await vm.InstallCommand.ExecuteAsync(null);

            AssertCommands(fake.TerminalCommands);
            AssertCommands(fake.Commands);
            Assert.False(vm.HasOperation);
            Assert.False(vm.IsBusy);
        });
    }

    [Fact]
    public Task WithoutAWindowNothingIsInstalledOrUninstalled()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            fake.Commands.Clear();

            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);
            await vm.UpdateXcodeCommand.ExecuteAsync(null);
            await vm.UninstallCommand.ExecuteAsync(vm.Installed[1]);

            AssertCommands(fake.TerminalCommands);
            AssertCommands(fake.Commands);
            Assert.False(vm.HasOperation);
            Assert.False(vm.IsBusy);
        });
    }

    [Fact]
    public Task AFailedInstallationIsReported()
    {
        var fake = new FakeXcodes();
        fake.RespondInTerminal = (_, _, _, _) => Task.FromResult(FakeXcodes.Failure("There is not enough space"));
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _) => Task.FromResult(true);

            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);

            Assert.True(vm.IsOperationFinished);
            Assert.Equal("Could not install Xcode 26.5", vm.OperationTitle);
            Assert.Equal(InfoBarSeverity.Error, vm.Notice.Severity);
            Assert.Equal("Could not install Xcode 26.5", vm.Notice.Title);
            Assert.Contains("There is not enough space", vm.Notice.Message);
            Assert.Equal(["26.4", "26.2"], InstalledVersions(vm));
        });
    }

    [Fact]
    public Task AnInstallationCanBeCancelled()
    {
        var fake = new FakeXcodes();
        var started = new TaskCompletionSource();
        fake.RespondInTerminal = async (_, _, _, cancellation) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellation);
            return FakeXcodes.Success("");
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _) => Task.FromResult(true);

            Task installing = vm.InstallCommand.ExecuteAsync(vm.Available[0]);
            await started.Task;
            Assert.Equal("Installing Xcode 26.5", vm.OperationTitle);

            vm.CancelOperation();
            await installing;

            Assert.False(vm.HasOperation);
            AssertNotice(vm, InfoBarSeverity.Informational, "The installation of Xcode 26.5 was cancelled", "");
        });
    }

    [Fact]
    public Task UpdatingInstallsTheNewestReleaseNextToTheOldOneAndOffersToRemoveIt()
    {
        var fake = new FakeXcodes();
        fake.RespondInTerminal = (_, _, _, _) =>
        {
            fake.InstalledOutput = WithNewestInstalled;
            return Task.FromResult(FakeXcodes.Success(""));
        };
        return WithPageAsync(fake, async vm =>
        {
            XcodeRelease? asked = null;
            InstalledXcode? replaced = null;
            vm.ConfirmInstall = (release, replacing) =>
            {
                asked = release;
                replaced = replacing;
                return Task.FromResult(true);
            };

            await vm.UpdateXcodeCommand.ExecuteAsync(null);

            Assert.Equal("26.5", asked?.Version);
            Assert.Equal("26.4", replaced?.Version);
            AssertCommands(fake.TerminalCommands, "install 26.5");
            AssertNotice(vm, InfoBarSeverity.Success, "Xcode 26.5 was installed", "Xcode 26.4 is still installed.");
            Assert.Equal("Uninstall Xcode 26.4", vm.Notice.ActionButtonText);
            Assert.NotNull(vm.Notice.ActionButtonCommand);

            vm.ConfirmUninstall = _ => Task.FromResult((true, false));
            fake.Override(command =>
            {
                if (command != "uninstall 26.4")
                    return null;

                fake.InstalledOutput = "26.2 (17C52) [Apple Silicon]\t/Applications/Xcode-26.2.app\n"
                    + "26.5 (17F42) [Apple Silicon] (Selected)\t/Applications/Xcode-26.5.app\n";
                return FakeXcodes.Success("");
            });

            await ((IAsyncRelayCommand)vm.Notice.ActionButtonCommand!).ExecuteAsync(null);

            AssertNotice(vm, InfoBarSeverity.Success, "Xcode 26.4 was uninstalled", "It is in the Trash.");
            Assert.Equal(["26.5", "26.2"], InstalledVersions(vm));
        });
    }

    [Fact]
    public Task WithNothingNewerThereIsNothingToUpdate()
    {
        var fake = new FakeXcodes { ListOutput = OnlyInstalledList };
        return WithPageAsync(fake, async vm =>
        {
            var asked = false;
            vm.ConfirmInstall = (_, _) =>
            {
                asked = true;
                return Task.FromResult(true);
            };

            await vm.UpdateXcodeCommand.ExecuteAsync(null);

            Assert.False(asked);
            AssertCommands(fake.TerminalCommands);
            Assert.False(vm.HasOperation);
        });
    }

    [Fact]
    public Task AnOlderReleaseIsNeverOfferedAsAnUpdate()
    {
        var fake = new FakeXcodes { InstalledOutput = OnlyNewestInstalled };
        fake.ListOutput = "26.2 (17C52) [Apple Silicon]\n26.4 (17E192) [Apple Silicon] (Installed, Selected)\n";
        return WithPage(fake, vm =>
        {
            Assert.False(vm.HasUpdate);
            Assert.Equal(["26.2"], AvailableVersions(vm));
        });
    }

    // ─── Uninstalling ────────────────────────────────────────────────────────

    [Fact]
    public Task UninstallingCanDeleteTheXcodeInsteadOfMovingItToTheTrash()
    {
        var fake = new FakeXcodes();
        fake.Override(command =>
        {
            if (!command.StartsWith("uninstall ", StringComparison.Ordinal))
                return null;

            fake.InstalledOutput = "26.4 (17E192) [Apple Silicon] (Selected)\t/Applications/Xcode-26.4.app\n";
            return FakeXcodes.Success("");
        });
        return WithPageAsync(fake, async vm =>
        {
            InstalledXcodeItemViewModel? asked = null;
            vm.ConfirmUninstall = item =>
            {
                asked = item;
                return Task.FromResult((true, true));
            };
            fake.Commands.Clear();

            await vm.UninstallCommand.ExecuteAsync(vm.Installed[1]);

            Assert.Equal("26.2", asked?.Version);
            AssertCommands(fake.Commands, "uninstall 26.2 --empty-trash", "installed", "list");
            AssertNotice(vm, InfoBarSeverity.Success, "Xcode 26.2 was uninstalled", "");
            Assert.Equal(["26.4"], InstalledVersions(vm));
        });
    }

    [Fact]
    public Task UninstallingMovesTheXcodeToTheTrashUnlessToldOtherwise()
    {
        var fake = new FakeXcodes();
        fake.Override(command => command.StartsWith("uninstall ", StringComparison.Ordinal)
            ? FakeXcodes.Success("")
            : null);
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmUninstall = _ => Task.FromResult((true, false));
            fake.Commands.Clear();

            await vm.UninstallCommand.ExecuteAsync(vm.Installed[1]);

            Assert.Equal("uninstall 26.2", fake.Commands[0]);
            AssertNotice(vm, InfoBarSeverity.Success, "Xcode 26.2 was uninstalled", "It is in the Trash.");
        });
    }

    [Fact]
    public Task DecliningTheConfirmationUninstallsNothing()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmUninstall = _ => Task.FromResult((false, false));
            InstalledXcodeItemViewModel item = vm.Installed[1];
            fake.Commands.Clear();

            await vm.UninstallCommand.ExecuteAsync(item);
            await vm.UninstallCommand.ExecuteAsync(null);

            AssertCommands(fake.Commands);
            Assert.False(item.IsBusy);
            Assert.False(vm.Notice.IsOpen);
            Assert.Equal(["26.4", "26.2"], InstalledVersions(vm));
        });
    }

    [Fact]
    public Task AFailedUninstallIsReportedAndTheItemIsFreedAgain()
    {
        var fake = new FakeXcodes();
        fake.Override(command => command.StartsWith("uninstall ", StringComparison.Ordinal)
            ? FakeXcodes.Failure("Permission denied")
            : null);
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmUninstall = _ => Task.FromResult((true, false));
            InstalledXcodeItemViewModel item = vm.Installed[1];

            await vm.UninstallCommand.ExecuteAsync(item);

            Assert.False(item.IsBusy);
            Assert.True(vm.Notice.IsOpen);
            Assert.Equal(InfoBarSeverity.Error, vm.Notice.Severity);
            Assert.Equal("Could not uninstall Xcode 26.2", vm.Notice.Title);
            Assert.Contains("Permission denied", vm.Notice.Message);
            Assert.Equal(["26.4", "26.2"], InstalledVersions(vm));
        });
    }

    // ─── The page itself ─────────────────────────────────────────────────────

    [Fact]
    public Task ThePageShowsWhatIsInstalledAndWhatCanBeInstalled() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes();
        var vm = new XcodeViewModel(fake.Service);
        var view = new XcodePage(vm);
        var window = new Window { Width = 900, Height = 700, Content = view };

        try
        {
            await vm.InitializeAsync();
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            string[] shown = VisibleTexts(view);
            Assert.Contains("Installed on this Mac", shown);
            Assert.Contains("Available to install", shown);
            Assert.Contains("Xcode 26.4", shown);
            Assert.Contains("Xcode 26.2", shown);
            Assert.Contains("Xcode 26.5", shown);
            Assert.Contains("xcodes 2.1.0", shown);
            Assert.Contains("Update to Xcode 26.5", shown);
            Assert.DoesNotContain("xcodes was not found", shown);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task ThePageSaysSoWhenXcodesIsMissing() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes { Executable = null };
        var vm = new XcodeViewModel(fake.Service);
        var view = new XcodePage(vm);
        var window = new Window { Width = 900, Height = 700, Content = view };

        try
        {
            await vm.InitializeAsync();
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            string[] shown = VisibleTexts(view);
            Assert.Contains("xcodes was not found", shown);
            Assert.Contains("Look again", shown);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    // ─── The question dialog ─────────────────────────────────────────────────

    [Theory]
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("Go to https://idmsa.apple.com/auth?x=1 now", "https://idmsa.apple.com/auth?x=1")]
    [InlineData("ftp://example.com/file", null)]
    [InlineData("file:///Users/me/file", null)]
    [InlineData("/Applications/Xcode.app", null)]
    [InlineData("nothing to see here", null)]
    [InlineData("", null)]
    public void TheSignInAddressIsTheFirstWebAddressInWhatXcodesWrote(string line, string? expected)
    {
        Assert.Equal(expected, XcodePromptDialog.FindWebAddress([line]));
    }

    [Fact]
    public void TheSignInAddressMayBeOnALaterLine()
    {
        Assert.Equal(
            "https://a.example/x",
            XcodePromptDialog.FindWebAddress(["First line", "then https://a.example/x", "https://b.example"]));
        Assert.Null(XcodePromptDialog.FindWebAddress([]));
    }

    [Theory]
    [InlineData(XcodesPromptKind.AppleId, "Sign in to Apple", "Continue", "Apple ID")]
    [InlineData(XcodesPromptKind.Password, "Apple ID password", "Sign in", "Password")]
    [InlineData(XcodesPromptKind.TwoFactorCode, "Verification code", "Continue", "Verification code")]
    [InlineData(XcodesPromptKind.SmsCode, "Verification code", "Continue", "Verification code")]
    [InlineData(XcodesPromptKind.PhoneNumber, "Choose a phone number", "Continue", "Number")]
    [InlineData(XcodesPromptKind.FederatedUrl, "Sign in with your organization", "Continue", "Address")]
    [InlineData(XcodesPromptKind.SudoPassword, "macOS password", "Continue", "Password")]
    [InlineData(XcodesPromptKind.Unknown, "xcodes needs an answer", "Continue", "Answer")]
    public Task EveryQuestionHasATitleAndALabel(XcodesPromptKind kind, string title, string primary, string label)
        => _session.Dispatch(() =>
        {
            var dialog = new XcodePromptDialog(new XcodesPrompt(kind, "", []));

            Assert.Equal(title, dialog.Title);
            Assert.Equal(primary, Primary(dialog).Content);
            Assert.Equal("Cancel", Secondary(dialog).Content);
            Assert.Equal(label, AnswerBox(dialog).PlaceholderText);
        }, CancellationToken.None);

    [Theory]
    [InlineData(XcodesPromptKind.AppleId, '\0')]
    [InlineData(XcodesPromptKind.Password, '•')]
    [InlineData(XcodesPromptKind.TwoFactorCode, '\0')]
    [InlineData(XcodesPromptKind.SmsCode, '\0')]
    [InlineData(XcodesPromptKind.PhoneNumber, '\0')]
    [InlineData(XcodesPromptKind.FederatedUrl, '\0')]
    [InlineData(XcodesPromptKind.SudoPassword, '•')]
    [InlineData(XcodesPromptKind.Unknown, '\0')]
    public Task OnlyPasswordsAreHiddenWhileTheyAreTyped(XcodesPromptKind kind, char passwordChar)
        => _session.Dispatch(() =>
        {
            var dialog = new XcodePromptDialog(new XcodesPrompt(kind, "", []));

            Assert.Equal(passwordChar, AnswerBox(dialog).PasswordChar);
        }, CancellationToken.None);

    [Fact]
    public Task AnAnswerIsOnlyGivenOnceItIsConfirmed() => _session.Dispatch(() =>
    {
        var dialog = new XcodePromptDialog(new XcodesPrompt(XcodesPromptKind.AppleId, "Apple ID:", []));
        AnswerBox(dialog).Text = "  me@example.com ";
        Assert.Null(dialog.Answer);

        Click(Primary(dialog));

        Assert.Equal("me@example.com", dialog.Answer);
    }, CancellationToken.None);

    [Fact]
    public Task CancellingGivesNoAnswer() => _session.Dispatch(() =>
    {
        var dialog = new XcodePromptDialog(new XcodesPrompt(XcodesPromptKind.AppleId, "Apple ID:", []));
        AnswerBox(dialog).Text = "me@example.com";

        Click(Secondary(dialog));

        Assert.Null(dialog.Answer);
    }, CancellationToken.None);

    [Theory]
    [InlineData(XcodesPromptKind.Password)]
    [InlineData(XcodesPromptKind.SudoPassword)]
    public Task APasswordIsSentExactlyAsItWasTyped(XcodesPromptKind kind) => _session.Dispatch(() =>
    {
        var dialog = new XcodePromptDialog(new XcodesPrompt(kind, "Password:", []));
        AnswerBox(dialog).Text = " p@ss word ";

        Click(Primary(dialog));

        Assert.Equal(" p@ss word ", dialog.Answer);
    }, CancellationToken.None);

    [Fact]
    public Task PressingEnterConfirmsTheAnswer() => _session.Dispatch(() =>
    {
        var dialog = new XcodePromptDialog(new XcodesPrompt(XcodesPromptKind.AppleId, "Apple ID:", []));
        TextBox box = AnswerBox(dialog);
        box.Text = "me@example.com";

        var enter = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
        box.RaiseEvent(enter);

        Assert.True(enter.Handled);
        Assert.Equal("me@example.com", dialog.Answer);
    }, CancellationToken.None);

    [Fact]
    public Task ACodeCanBeSentByTextMessageInstead() => _session.Dispatch(() =>
    {
        var dialog = new XcodePromptDialog(new XcodesPrompt(XcodesPromptKind.TwoFactorCode, "Code:", []));
        Button text = Assert.Single(Body(dialog).Children.OfType<Button>());
        Assert.Equal("Send a text message instead", text.Content);
        Assert.Null(dialog.Answer);

        Click(text);

        Assert.Equal("sms", dialog.Answer);
    }, CancellationToken.None);

    [Theory]
    [InlineData(XcodesPromptKind.AppleId)]
    [InlineData(XcodesPromptKind.Password)]
    [InlineData(XcodesPromptKind.SmsCode)]
    [InlineData(XcodesPromptKind.PhoneNumber)]
    [InlineData(XcodesPromptKind.SudoPassword)]
    [InlineData(XcodesPromptKind.Unknown)]
    public Task OnlyACodeCanBeSentByTextMessageInstead(XcodesPromptKind kind) => _session.Dispatch(() =>
    {
        var dialog = new XcodePromptDialog(new XcodesPrompt(kind, "Question", ["Something xcodes wrote"]));

        Assert.Empty(Body(dialog).Children.OfType<Button>());
    }, CancellationToken.None);

    [Theory]
    [InlineData(XcodesPromptKind.PhoneNumber, true)]
    [InlineData(XcodesPromptKind.FederatedUrl, true)]
    [InlineData(XcodesPromptKind.Unknown, true)]
    [InlineData(XcodesPromptKind.AppleId, false)]
    [InlineData(XcodesPromptKind.Password, false)]
    [InlineData(XcodesPromptKind.TwoFactorCode, false)]
    [InlineData(XcodesPromptKind.SmsCode, false)]
    [InlineData(XcodesPromptKind.SudoPassword, false)]
    public Task OnlySomeQuestionsShowWhatXcodesWrote(XcodesPromptKind kind, bool shown) => _session.Dispatch(() =>
    {
        string[] context = ["Trusted phone numbers:", "  1: ending in 12", "  2: ending in 34"];
        var dialog = new XcodePromptDialog(new XcodesPrompt(kind, "Question", context));

        List<ScrollViewer> scrolls = Body(dialog).Children.OfType<ScrollViewer>().ToList();
        if (!shown)
        {
            Assert.Empty(scrolls);
            return;
        }

        var text = Assert.IsType<SelectableTextBlock>(Assert.Single(scrolls).Content);
        Assert.Equal(string.Join(Environment.NewLine, context), text.Text);
    }, CancellationToken.None);

    [Fact]
    public Task AnUnknownQuestionIsShownAsXcodesWroteIt() => _session.Dispatch(() =>
    {
        var asked = new XcodePromptDialog(
            new XcodesPrompt(XcodesPromptKind.Unknown, "Install the extra components? [y/N]", []));
        Assert.Equal(
            "Install the extra components? [y/N]",
            Assert.IsType<TextBlock>(Body(asked).Children[0]).Text);

        var silent = new XcodePromptDialog(new XcodesPrompt(XcodesPromptKind.Unknown, "", []));
        Assert.Equal(
            "xcodes is waiting for your answer to continue.",
            Assert.IsType<TextBlock>(Body(silent).Children[0]).Text);
    }, CancellationToken.None);

    [Fact]
    public Task ATextMessageQuestionShowsWhatXcodesSaid() => _session.Dispatch(() =>
    {
        var dialog = new XcodePromptDialog(
            new XcodesPrompt(XcodesPromptKind.SmsCode, "Code sent to a phone ending in 12", []));

        Assert.Contains(
            "Code sent to a phone ending in 12",
            Body(dialog).Children.OfType<TextBlock>().Select(text => text.Text));
    }, CancellationToken.None);

    [Fact]
    public Task TheSignInPageOfAnOrganizationCanBeOpenedFromTheQuestion() => _session.Dispatch(() =>
    {
        var withAddress = new XcodePromptDialog(new XcodesPrompt(
            XcodesPromptKind.FederatedUrl, "Paste the URL here:", ["Open https://idmsa.apple.com/auth to sign in"]));
        Button open = Assert.Single(Body(withAddress).Children.OfType<Button>());
        Assert.Equal("Open the sign-in page", open.Content);

        var withoutAddress = new XcodePromptDialog(new XcodesPrompt(
            XcodesPromptKind.FederatedUrl, "Paste the URL here:", ["Sign in with your organization"]));
        Assert.Empty(Body(withoutAddress).Children.OfType<Button>());
    }, CancellationToken.None);

    [Fact]
    public Task WithoutTheMainWindowAQuestionGoesUnanswered() => DispatchAsync(async () =>
    {
        string? answer = await XcodePromptDialog.AskAsync(
            new XcodesPrompt(XcodesPromptKind.AppleId, "Apple ID:", []), CancellationToken.None);

        Assert.Null(answer);
    });

    // ─── Helpers ─────────────────────────────────────────────────────────────

    // The generic overload awaits the operation; Dispatch(Action) would make an async lambda async void.
    private Task DispatchAsync(Func<Task> action) => _session.Dispatch<bool>(async () =>
    {
        await action();
        return true;
    }, CancellationToken.None);

    private Task WithPageAsync(FakeXcodes fake, Func<XcodeViewModel, Task> test, bool initialize = true)
        => DispatchAsync(async () =>
        {
            var vm = new XcodeViewModel(fake.Service);
            try
            {
                if (initialize)
                    await vm.InitializeAsync();

                await test(vm);
            }
            finally
            {
                vm.Stop();
            }
        });

    private Task WithPage(FakeXcodes fake, Action<XcodeViewModel> test, bool initialize = true)
        => WithPageAsync(fake, vm =>
        {
            test(vm);
            return Task.CompletedTask;
        }, initialize);

    private static void EnsureTheme()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any())
            Application.Current.Styles.Add(new FluentTheme());
    }

    private static string[] VisibleTexts(Visual root) => root.GetVisualDescendants().OfType<TextBlock>()
        .Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text))
        .Select(text => text.Text!)
        .ToArray();

    private static string[] AvailableVersions(XcodeViewModel vm) => vm.Available.Select(item => item.Version).ToArray();

    private static string[] InstalledVersions(XcodeViewModel vm) => vm.Installed.Select(item => item.Version).ToArray();

    private static void AssertCommands(List<string> actual, params string[] expected)
        => Assert.Equal(expected, actual.ToArray());

    private static void AssertSearch(XcodeViewModel vm, string search, params string[] expected)
    {
        vm.SearchText = search;
        Assert.Equal(expected, AvailableVersions(vm));
    }

    private static void AssertNotice(XcodeViewModel vm, InfoBarSeverity severity, string title, string message)
    {
        Assert.True(vm.Notice.IsOpen);
        Assert.Equal(severity, vm.Notice.Severity);
        Assert.Equal(title, vm.Notice.Title);
        Assert.Equal(message, vm.Notice.Message);
    }

    private static Button Primary(XcodePromptDialog dialog) => dialog.FindControl<Button>("PrimaryButton")!;

    private static Button Secondary(XcodePromptDialog dialog) => dialog.FindControl<Button>("SecondaryButton")!;

    private static StackPanel Body(XcodePromptDialog dialog)
        => Assert.IsType<StackPanel>(dialog.FindControl<ContentControl>("BodyPresenter")!.Content);

    private static TextBox AnswerBox(XcodePromptDialog dialog) => Body(dialog).Children.OfType<TextBox>().Single();

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>An xcodes that answers from memory, and keeps what it was asked.</summary>
    private sealed class FakeXcodes
    {
        public const string DefaultExecutable = "/opt/homebrew/bin/xcodes";

        public FakeXcodes()
        {
            Respond = command => Task.FromResult(Answer(command));
            RespondInTerminal = (_, _, _, _) => Task.FromResult(Success(""));
            Service = new XcodesService(
                (arguments, _, _) =>
                {
                    string command = string.Join(' ', arguments);
                    Commands.Add(command);
                    return Respond(command);
                },
                (arguments, prompts, output, cancellation) =>
                {
                    string command = string.Join(' ', arguments);
                    TerminalCommands.Add(command);
                    return RespondInTerminal(command, prompts, output, cancellation);
                },
                () => Executable);
        }

        public XcodesService Service { get; }

        /// <summary>What was run without a terminal, such as <c>installed</c>.</summary>
        public List<string> Commands { get; } = [];

        /// <summary>What was run in a terminal, since it may ask questions: <c>install</c> and <c>select</c>.</summary>
        public List<string> TerminalCommands { get; } = [];

        public string? Executable { get; set; } = DefaultExecutable;

        public string ListOutput { get; set; } = DefaultList;

        public string InstalledOutput { get; set; } = DefaultInstalled;

        public Func<string, Task<XcodesResult>> Respond { get; set; }

        public delegate Task<XcodesResult> TerminalResponder(
            string command,
            XcodesPromptHandler? prompts,
            Action<XcodesOutputLine>? output,
            CancellationToken cancellation);

        public TerminalResponder RespondInTerminal { get; set; }

        public static XcodesResult Success(string text) => new(0, text, "");

        public static XcodesResult Failure(string text) => new(1, "", text);

        public XcodesResult Answer(string command) => command switch
        {
            "version" => Success("2.1.0\n"),
            "installed" => Success(InstalledOutput),
            "list" or "update" => Success(ListOutput),
            _ => Failure($"unexpected command: {command}"),
        };

        /// <summary>Answers the commands the handler returns a result for, and leaves the rest as they were.</summary>
        public void Override(Func<string, XcodesResult?> handler)
        {
            Func<string, Task<XcodesResult>> previous = Respond;
            Respond = command => handler(command) is { } result ? Task.FromResult(result) : previous(command);
        }

        public void OverrideAsync(Func<string, Task<XcodesResult>?> handler)
        {
            Func<string, Task<XcodesResult>> previous = Respond;
            Respond = command => handler(command) ?? previous(command);
        }
    }
}

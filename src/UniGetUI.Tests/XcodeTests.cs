#if UNIGETUI_XCODE_EXTENSION
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
using UniGetUI.Extensions.Xcode.AppleAuth;
using UniGetUI.Extensions.Xcode.Local;
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

    private static readonly string SingleNewestBuildList =
        DefaultList.Replace("26.5 (17F43) [Apple Silicon]\n", "", StringComparison.Ordinal);

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
        EnsureTheme();
        var sidebar = new SidebarViewModel();
        var view = new SidebarView { DataContext = sidebar };
        var window = new Window { Content = view };
        window.Show();
        try
        {
            ListBox navList = view.FindControl<ListBox>("NavListBox")!;
            Assert.DoesNotContain(PageType.Xcode, sidebar.NavigationPages);
            Assert.DoesNotContain(navList.Items.OfType<ListBoxItem>(), item => Equals(item.Tag, "Xcode"));

            sidebar.XcodeEnabled = true;
            Assert.Equal(PageType.Xcode, sidebar.NavigationPages.Last());
            ListBoxItem xcodeItem = Assert.Single(navList.Items.OfType<ListBoxItem>(), item => Equals(item.Tag, "Xcode"));

            sidebar.GitRepositoriesEnabled = true;
            Assert.Equal([PageType.GitRepositories, PageType.Xcode], sidebar.NavigationPages.TakeLast(2).ToArray());

            Panel panel = Assert.IsType<Panel>(xcodeItem.Content);
            Border badge = panel.Children.OfType<Border>().Single();
            TextBlock badgeText = Assert.IsType<TextBlock>(badge.Child);
            ProgressBar progress = panel.Children.OfType<ProgressBar>().Single();

            Assert.False(sidebar.XcodeBadgeVisible);
            Assert.False(badge.IsVisible);
            sidebar.XcodeBadgeCount = 1;
            Assert.True(sidebar.XcodeBadgeVisible);
            Assert.True(badge.IsVisible);
            Assert.Equal("1", badgeText.Text);
            sidebar.XcodeBadgeCount = 120;
            Assert.Equal("99+", badgeText.Text);

            sidebar.XcodeIsLoading = true;
            Assert.True(progress.IsVisible);
            sidebar.XcodeEnabled = false;
            Assert.DoesNotContain(PageType.Xcode, sidebar.NavigationPages);
            Assert.DoesNotContain(navList.Items.OfType<ListBoxItem>(), item => Equals(item.Tag, "Xcode"));
            Assert.Contains(PageType.GitRepositories, sidebar.NavigationPages);
            Assert.False(sidebar.XcodeBadgeVisible);
            Assert.False(sidebar.XcodeIsLoading);
        }
        finally
        {
            window.Close();
        }
    }, CancellationToken.None);

    [Fact]
    public void XcodeIsAnIpcNavigationTarget()
    {
        Assert.Contains("xcode", IpcAppPages.SupportedPages);
        Assert.Equal("xcode", IpcAppPages.NormalizePageName(" Xcode "));
        Assert.Equal("xcode", IpcAppPages.ToPageName(nameof(PageType.Xcode)));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void TheExtensionRequiresMacOS13(bool isMacOS, bool isMacOS13OrLater, bool expected)
        => Assert.Equal(expected, XcodePlatform.IsSupportedPlatform(isMacOS, isMacOS13OrLater));

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
        Assert.Equal(XcodePlatform.IsSupported, extension.IsEnabled);
        Assert.Equal(XcodePlatform.IsSupported, CoreSettings.Get(CoreSettings.K.EnableXcode));
        Assert.False(AppExtensions.GitRepositories.IsEnabled);

        extension.SetEnabled(false);
        Assert.False(extension.IsEnabled);
        Assert.False(CoreSettings.Get(CoreSettings.K.EnableXcode));
    }, CancellationToken.None);

    [Fact]
    public void AnUnavailableExtensionKeepsButDoesNotActivateItsSavedPreference()
    {
        bool applied = false;
        var extension = new AppExtension
        {
            Id = "unavailable",
            Name = "Unavailable",
            Description = "",
            Icon = IconType.Xcode,
            EnabledSetting = CoreSettings.K.EnableXcode,
            Page = PageType.Xcode,
            SettingsPage = typeof(XcodeSettings),
            CreateSettingsPage = () => new XcodeSettings(),
            Start = _ =>
            {
                applied = true;
                return new(() => new Control(), () => { });
            },
            ApplyEnabledOverride = _ => applied = true,
            IsAvailable = () => false,
            UnavailableReason = () => "Unavailable",
        };
        CoreSettings.Set(CoreSettings.K.EnableXcode, true);

        Assert.False(extension.IsEnabled);
        extension.SetEnabled(true);
        Assert.True(CoreSettings.Get(CoreSettings.K.EnableXcode));
        Assert.False(applied);
    }

    [Fact]
    public Task UnsupportedSettingsDoNotStartXcodes() => _session.Dispatch(() =>
    {
        var fake = new FakeXcodes();
        CoreSettings.Set(CoreSettings.K.EnableXcode, true);
        var settings = new XcodeSettingsViewModel(fake.Service, isSupported: false);

        Assert.False(settings.IsSupported);
        Assert.False(settings.IsEnabled);
        Assert.True(CoreSettings.Get(CoreSettings.K.EnableXcode));
        Assert.Empty(fake.Commands);
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

        Assert.Null(MainWindow.Instance);
        var fake = new FakeXcodes();
        XcodesService? previous = XcodeViewModel.SharedServiceForTests;
        XcodeViewModel.SharedServiceForTests = fake.Service;
        try
        {
            var page = Assert.IsType<XcodeSettings>(AppExtensions.Xcode.CreateSettingsPage());
            Assert.Equal(typeof(XcodeSettings), AppExtensions.Xcode.SettingsPage);
            Assert.Equal("Xcode", page.ShortTitle);
            Assert.True(page.CanGoBack);
            Assert.True(fake.AccountReadCount > 0);
            var directory = page.FindControl<UniGetUI.Avalonia.Views.Controls.Settings.SettingsCard>("XcodeInstallDirectoryCard");
            Assert.Contains("Spotlight", Assert.IsType<string>(directory!.Description));
            var archives = page.FindControl<UniGetUI.Avalonia.Views.Controls.Settings.SettingsCard>("XcodeArchiveDirectoryCard");
            Assert.Contains("cache folder", Assert.IsType<string>(archives!.Description));
        }
        finally
        {
            XcodeViewModel.SharedServiceForTests = previous;
        }
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
        Assert.False(XcodePreferences.AutoInstallUpdates);
        Assert.False(XcodePreferences.IncludePrereleases);
        Assert.False(XcodePreferences.SelectAfterInstall);
        Assert.False(XcodePreferences.SkipAdministratorSteps);
        Assert.False(XcodePreferences.DeleteArchive);
        Assert.Equal(new XcodeInstallOptions(), XcodePreferences.InstallOptions);
    }

    [Fact]
    public void AutomaticInstallationRequiresAnExplicitSavedOptIn()
    {
        Assert.False(XcodePreferences.AutoInstallUpdates);
        XcodePreferences.AutoInstallUpdates = true;
        Assert.True(CoreSettings.Get(CoreSettings.K.XcodeAutoInstallUpdates));
        XcodePreferences.AutoInstallUpdates = false;
        Assert.False(CoreSettings.Get(CoreSettings.K.XcodeAutoInstallUpdates));
    }

    [Fact]
    public void TheInstallOptionsFollowTheSettings()
    {
        XcodePreferences.SelectAfterInstall = true;
        Assert.Equal(new XcodeInstallOptions(Select: true), XcodePreferences.InstallOptions);

        XcodePreferences.SkipAdministratorSteps = true;
        XcodePreferences.DeleteArchive = true;
        Assert.Equal(new XcodeInstallOptions(true, true, true), XcodePreferences.InstallOptions);

        XcodePreferences.SelectAfterInstall = false;
        Assert.Equal(new XcodeInstallOptions(false, true, true), XcodePreferences.InstallOptions);
    }

    [Fact]
    public Task ChangingTheInstallationDirectoryReloadsAndUsesItForChecksAndInstalls()
    {
        string directory = Path.Combine(_testRoot, "Xcode builds");
        Directory.CreateDirectory(directory);
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            var settings = new XcodeSettingsViewModel(fake.Service, vm.Account) { GetXcodePage = () => vm };
            fake.InstalledOutput = $"26.4 (17E192) [Apple Silicon] (Selected)\t{directory}/Xcode-26.4.app\n";
            fake.Commands.Clear();
            settings.InstallDirectoryDraft = directory;

            await settings.ApplyInstallDirectoryCommand.ExecuteAsync(null);

            Assert.Equal(directory, XcodePreferences.InstallDirectory);
            Assert.Equal("Xcode installation directory saved.", settings.InstallDirectoryStatus);
            Assert.Equal($"{directory}/Xcode-26.4.app", Assert.Single(vm.Installed).Location);
            AssertCommands(fake.Commands, $"installed --directory {directory}", $"list --directory {directory}");

            fake.Commands.Clear();
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            AssertCommands(fake.Commands, $"installed --directory {directory}", $"update --directory {directory}");

            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);
            fake.Commands.Clear();
            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);
            AssertCommands(fake.TerminalCommands, $"install 26.5 --directory {directory}");
            AssertCommands(fake.Commands, $"installed --directory {directory}", $"list --directory {directory}");

            fake.Commands.Clear();
            settings.InstallDirectoryDraft = "";
            await settings.ApplyInstallDirectoryCommand.ExecuteAsync(null);
            Assert.Null(XcodePreferences.InstallDirectory);
            Assert.Null(vm.LastChecked);
            Assert.Equal("Showing saved Xcode versions; their age is unknown.", vm.VersionListStatusText);
            AssertCommands(fake.Commands, "installed", "list");
        });
    }

    [Fact]
    public Task InvalidInstallationDirectoryLeavesThePreviousChoiceUntouched()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            var settings = new XcodeSettingsViewModel(fake.Service, vm.Account) { GetXcodePage = () => vm };
            fake.Commands.Clear();
            settings.InstallDirectoryDraft = "relative/path";
            await settings.ApplyInstallDirectoryCommand.ExecuteAsync(null);
            Assert.Null(XcodePreferences.InstallDirectory);
            Assert.Equal("Enter an absolute path for the Xcode installation directory.", settings.InstallDirectoryStatus);
            Assert.Empty(fake.Commands);

            settings.InstallDirectoryDraft = Path.Combine(_testRoot, "missing");
            await settings.ApplyInstallDirectoryCommand.ExecuteAsync(null);
            Assert.Null(XcodePreferences.InstallDirectory);
            Assert.Equal("The Xcode installation directory does not exist or is inaccessible.", settings.InstallDirectoryStatus);
            Assert.Empty(fake.Commands);
            Assert.True(vm.HasLoaded);
        });
    }

    [Fact]
    public Task SelectingAnInstalledXcodeUsesTheConfiguredDirectory()
    {
        string directory = Path.Combine(_testRoot, "Xcode builds");
        Directory.CreateDirectory(directory);
        Assert.True(XcodePreferences.TrySetInstallDirectory(directory));
        var fake = new FakeXcodes
        {
            InstalledOutput = $"26.4 (17E192) [Apple Silicon] (Selected)\t{directory}/Xcode-26.4.app\n"
                + $"26.2 (17C52) [Apple Silicon]\t{directory}/Xcode-26.2.app\n",
        };
        return WithPageAsync(fake, async vm =>
        {
            fake.Commands.Clear();

            await vm.SelectCommand.ExecuteAsync(vm.Installed[1]);

            AssertCommands(fake.TerminalCommands, $"select {directory}/Xcode-26.2.app");
            AssertCommands(fake.Commands, $"installed --directory {directory}", $"list --directory {directory}");
        });
    }

    [Fact]
    public Task ASettingsWriteFailureDoesNotClaimTheDirectoryWasSaved()
    {
        string directory = Path.Combine(_testRoot, "Xcode builds");
        Directory.CreateDirectory(directory);
        string settingPath = Path.Combine(CoreData.UniGetUIUserConfigurationDirectory,
            CoreSettings.ResolveKey(CoreSettings.K.XcodeInstallDirectory));
        Directory.CreateDirectory(settingPath);
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            var settings = new XcodeSettingsViewModel(fake.Service, vm.Account) { GetXcodePage = () => vm };
            fake.Commands.Clear();
            settings.InstallDirectoryDraft = directory;

            await settings.ApplyInstallDirectoryCommand.ExecuteAsync(null);

            Assert.Null(XcodePreferences.InstallDirectory);
            Assert.Equal("Could not change the Xcode installation directory. Check the Xcode page for details.",
                settings.InstallDirectoryStatus);
            AssertNotice(vm, InfoBarSeverity.Error, "Could not save the Xcode installation directory",
                "Check the application log for details.");
            Assert.True(vm.HasLoaded);
            Assert.Empty(fake.Commands);
        });
    }

    [Fact]
    public Task AFailedDirectoryRefreshClearsOldRowsAndCannotInstallFromThem()
    {
        string directory = Path.Combine(_testRoot, "Xcode builds");
        Directory.CreateDirectory(directory);
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            AvailableXcodeItemViewModel oldRelease = vm.Available[0];
            var settings = new XcodeSettingsViewModel(fake.Service, vm.Account) { GetXcodePage = () => vm };
            fake.Override(command => command.StartsWith("installed --directory ", StringComparison.Ordinal)
                ? FakeXcodes.Failure("cannot read directory") : null);
            settings.InstallDirectoryDraft = directory;

            await settings.ApplyInstallDirectoryCommand.ExecuteAsync(null);

            Assert.Equal(directory, XcodePreferences.InstallDirectory);
            Assert.False(vm.HasLoaded);
            Assert.Empty(vm.Installed);
            Assert.Empty(vm.BrowserItems);
            Assert.False(vm.HasUpdate);
            Assert.Contains("could not be refreshed", settings.InstallDirectoryStatus);
            AssertNotice(vm, InfoBarSeverity.Error, "Could not read the Xcode versions", "cannot read directory");

            await vm.InstallCommand.ExecuteAsync(oldRelease);
            Assert.Empty(fake.TerminalCommands);
            AssertNotice(vm, InfoBarSeverity.Error, "Could not read the Xcode versions",
                "Read the Xcode versions before starting an installation.");
        });
    }

    [Fact]
    public Task AnInstallSnapshotsItsOptionsAndBlocksDirectoryChanges()
    {
        string first = Path.Combine(_testRoot, "First");
        string second = Path.Combine(_testRoot, "Second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Assert.True(XcodePreferences.TrySetInstallDirectory(first));
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            XcodeInstallOptions? confirmed = null;
            vm.ConfirmInstall = (_, _, _, options) =>
            {
                confirmed = options;
                started.SetResult(true);
                return decision.Task;
            };
            var settings = new XcodeSettingsViewModel(fake.Service, vm.Account) { GetXcodePage = () => vm };

            Task installing = vm.InstallCommand.ExecuteAsync(vm.Available[0]);
            await started.Task;
            XcodePreferences.SelectAfterInstall = true;
            settings.InstallDirectoryDraft = second;
            await settings.ApplyInstallDirectoryCommand.ExecuteAsync(null);

            Assert.Equal(first, XcodePreferences.InstallDirectory);
            Assert.Equal("Wait until the current Xcode operation finishes.", settings.InstallDirectoryStatus);
            decision.SetResult(true);
            await installing;

            Assert.False(confirmed!.Select);
            Assert.Equal(first, confirmed.InstallDirectory);
            AssertCommands(fake.TerminalCommands, $"install 26.5 --directory {first}");
        });
    }

    [Fact]
    public Task ApplyingAnArchiveDirectoryForwardsItToInstallsAndDownloads()
    {
        string archiveDirectory = Path.Combine(_testRoot, "Xcode archives");
        Directory.CreateDirectory(archiveDirectory);
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            var settings = new XcodeSettingsViewModel(fake.Service, vm.Account) { GetXcodePage = () => vm };
            settings.ArchiveDirectoryDraft = archiveDirectory;

            await settings.ApplyArchiveDirectoryCommand.ExecuteAsync(null);

            Assert.Equal(archiveDirectory, XcodePreferences.ArchiveDirectory);
            Assert.Equal("Xcode archive directory saved.", settings.ArchiveDirectoryStatus);

            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);
            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);
            Assert.Equal(archiveDirectory, fake.LastArchiveDirectory);

            vm.ConfirmDownload = _ => Task.FromResult(true);
            XcodeBrowserItemViewModel target = vm.BrowserItems.First(item => item.IsAvailable);
            await vm.DownloadXcodeCommand.ExecuteAsync(target);
            Assert.Equal(archiveDirectory, fake.LastArchiveDirectory);

            settings.ArchiveDirectoryDraft = "";
            await settings.ApplyArchiveDirectoryCommand.ExecuteAsync(null);
            Assert.Null(XcodePreferences.ArchiveDirectory);
            Assert.Equal("Xcode archive directory saved.", settings.ArchiveDirectoryStatus);
        });
    }

    [Fact]
    public Task InvalidOrFailedArchiveDirectoryLeavesThePreviousChoiceUntouched()
    {
        string directory = Path.Combine(_testRoot, "Xcode archives");
        Directory.CreateDirectory(directory);
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            var settings = new XcodeSettingsViewModel(fake.Service, vm.Account) { GetXcodePage = () => vm };
            settings.ArchiveDirectoryDraft = "relative/archives";
            await settings.ApplyArchiveDirectoryCommand.ExecuteAsync(null);
            Assert.Null(XcodePreferences.ArchiveDirectory);
            Assert.Equal("Enter an absolute path for the Xcode archive directory.", settings.ArchiveDirectoryStatus);

            settings.ArchiveDirectoryDraft = Path.Combine(_testRoot, "missing-archives");
            await settings.ApplyArchiveDirectoryCommand.ExecuteAsync(null);
            Assert.Null(XcodePreferences.ArchiveDirectory);
            Assert.Equal("The Xcode archive directory does not exist or is inaccessible.", settings.ArchiveDirectoryStatus);

            string settingPath = Path.Combine(CoreData.UniGetUIUserConfigurationDirectory,
                CoreSettings.ResolveKey(CoreSettings.K.XcodeArchiveDirectory));
            Directory.CreateDirectory(settingPath);
            settings.ArchiveDirectoryDraft = directory;
            await settings.ApplyArchiveDirectoryCommand.ExecuteAsync(null);
            Assert.Null(XcodePreferences.ArchiveDirectory);
            Assert.Equal("Could not change the Xcode archive directory. Check the Xcode page for details.",
                settings.ArchiveDirectoryStatus);
            AssertNotice(vm, InfoBarSeverity.Error, "Could not save the Xcode archive directory",
                "Check the application log for details.");
        });
    }

    [Fact]
    public Task ADownloadSnapshotsItsArchiveDirectoryAndBlocksArchiveDirectoryChanges()
    {
        string first = Path.Combine(_testRoot, "FirstArchives");
        string second = Path.Combine(_testRoot, "SecondArchives");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Assert.True(XcodePreferences.TrySetArchiveDirectory(first));
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.ConfirmDownload = _ =>
            {
                started.SetResult(true);
                return decision.Task;
            };
            var settings = new XcodeSettingsViewModel(fake.Service, vm.Account) { GetXcodePage = () => vm };
            XcodeBrowserItemViewModel target = vm.BrowserItems.First(item => item.IsAvailable);

            Task downloading = vm.DownloadXcodeCommand.ExecuteAsync(target);
            await started.Task;
            settings.ArchiveDirectoryDraft = second;
            await settings.ApplyArchiveDirectoryCommand.ExecuteAsync(null);

            Assert.Equal(first, XcodePreferences.ArchiveDirectory);
            Assert.Equal("Wait until the current Xcode operation finishes.", settings.ArchiveDirectoryStatus);
            decision.SetResult(true);
            await downloading;

            Assert.Equal(first, fake.LastArchiveDirectory);
        });
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public Task TheInstallConfirmationUsesOptionsSnapshottedBeforeTheCatalogRequest(bool initial, bool changed)
    {
        XcodePreferences.SelectAfterInstall = initial;
        var fake = new FakeXcodes();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<IReadOnlyList<XcodeCatalogRelease>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new FakeCatalog
        {
            Response = _ =>
            {
                started.SetResult();
                return response.Task;
            },
        };
        return WithPageAsync(fake, async vm =>
        {
            XcodeInstallOptions? confirmed = null;
            vm.ConfirmInstall = (_, _, _, options) =>
            {
                confirmed = options;
                return Task.FromResult(true);
            };

            Task installing = vm.InstallCommand.ExecuteAsync(vm.Available[0]);
            await started.Task;
            XcodePreferences.SelectAfterInstall = changed;
            response.SetResult(CatalogReleases());
            await installing;

            Assert.NotNull(confirmed);
            Assert.Equal(initial, confirmed.Select);
            AssertCommands(fake.TerminalCommands,
                initial ? "install 26.5 --select" : "install 26.5");
        }, catalog: catalog);
    }

    [Fact]
    public void BrowserFiltersAreStoredSeparatelyFromUpdatePreferences()
    {
        Assert.False(XcodePreferences.BrowserInstalledOnly);
        Assert.Equal(0, XcodePreferences.BrowserReleaseFilter);
        Assert.Equal(0, XcodePreferences.BrowserArchitectureFilter);

        XcodePreferences.BrowserInstalledOnly = true;
        XcodePreferences.BrowserReleaseFilter = 2;
        XcodePreferences.BrowserArchitectureFilter = 1;

        Assert.True(XcodePreferences.BrowserInstalledOnly);
        Assert.Equal(2, XcodePreferences.BrowserReleaseFilter);
        Assert.Equal(1, XcodePreferences.BrowserArchitectureFilter);
        Assert.False(XcodePreferences.IncludePrereleases);
    }

    [Fact]
    public void SimulatorFilterPreferencesRoundTripAndRejectUnknownValues()
    {
        for (int index = 0; index <= 4; index++)
        {
            XcodePreferences.RuntimePlatformFilter = index;
            Assert.Equal(index, XcodePreferences.RuntimePlatformFilter);
        }
        for (int index = 0; index <= 2; index++)
        {
            XcodePreferences.RuntimeSourceFilter = index;
            XcodePreferences.RuntimeReleaseFilter = index;
            Assert.Equal(index, XcodePreferences.RuntimeSourceFilter);
            Assert.Equal(index, XcodePreferences.RuntimeReleaseFilter);
        }
        for (int index = 0; index <= 3; index++)
        {
            XcodePreferences.RuntimeArchitectureFilter = index;
            Assert.Equal(index, XcodePreferences.RuntimeArchitectureFilter);
        }

        CoreSettings.SetValue(CoreSettings.K.XcodeRuntimePlatformFilter, "unknown");
        CoreSettings.SetValue(CoreSettings.K.XcodeRuntimeSourceFilter, "unknown");
        CoreSettings.SetValue(CoreSettings.K.XcodeRuntimeReleaseFilter, "unknown");
        CoreSettings.SetValue(CoreSettings.K.XcodeRuntimeArchitectureFilter, "unknown");

        Assert.Equal(0, XcodePreferences.RuntimePlatformFilter);
        Assert.Equal(0, XcodePreferences.RuntimeSourceFilter);
        Assert.Equal(0, XcodePreferences.RuntimeReleaseFilter);
        Assert.Equal(0, XcodePreferences.RuntimeArchitectureFilter);
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

    [Fact]
    public Task AnAppleOnlyVersionWithUnknownBuildCannotBeInstalled() => WithPageAsync(new FakeXcodes
    {
        ExtraReleases = [new XcodeRelease("27.0 Beta", null, XcodeArchitecture.Unspecified, false, false) { IsIdentityUnverified = true }],
    }, async vm =>
    {
        XcodeBrowserItemViewModel unknown = Assert.Single(vm.BrowserItems, item => item.Version == "27.0 Beta");
        Assert.False(unknown.CanInstall);
        Assert.False(unknown.CanDownload);
        Assert.Equal("Exact build unknown", unknown.DetailText);
        Assert.False(unknown.AvailableItem!.CanInstall);

        await vm.InstallCommand.ExecuteAsync(unknown.AvailableItem);

        Assert.Equal(InfoBarSeverity.Warning, vm.Notice.Severity);
        Assert.Contains("exact build is not yet known", vm.Notice.Message);
        Assert.False(vm.IsOperationRunning);
    });

    [Fact]
    public Task TheBrowserExplainsWhyAnUnknownBuildCannotBeInstalled() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes
        {
            ExtraReleases = [new XcodeRelease("27.0 Beta", null, XcodeArchitecture.Unspecified, false, false) { IsIdentityUnverified = true }],
        };
        var vm = new XcodeViewModel(fake.Service);
        var view = new XcodePage(vm);
        var window = new Window { Width = 1100, Height = 1100, Content = view };
        try
        {
            await vm.InitializeAsync();
            vm.SelectedBrowserItem = Assert.Single(vm.BrowserItems, item => item.Version == "27.0 Beta");
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.False(view.FindControl<Button>("InstallSelectedXcode")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Button>("DownloadSelectedXcode")!.IsEffectivelyVisible);
            Assert.Contains(
                "The exact build is not yet known. Check again after release details are available.",
                VisibleTexts(view));
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task AReleaseWithAnUnrecognizedCatalogBuildDoesNotOfferInstallOrDownload()
    {
        var catalog = new XcodeCatalogRelease("27.0", "18A1\n", XcodeArchitecture.Universal,
            null, null, null, [], [], null)
        {
            Download = new Uri("https://download.developer.apple.com/Xcode_27.xip"),
        };
        var fake = new FakeXcodes
        {
            ExtraReleases = XcodeNativeBackend.Merge([catalog], [], XcodeArchitecture.AppleSilicon),
        };

        return WithPage(fake, vm =>
        {
            XcodeBrowserItemViewModel item = Assert.Single(vm.BrowserItems, entry => entry.Version == "27.0");
            Assert.False(item.CanInstall);
            Assert.False(item.CanDownload);
            Assert.Null(item.AvailableItem!.Release.Build);
            Assert.Equal("Exact build unknown", item.DetailText);
        });
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
            AssertCommands(fake.Commands, "installed", "list");
            AssertCommands(fake.TerminalCommands);

            Assert.Equal(["26.4", "26.2"], InstalledVersions(vm));
            Assert.True(vm.HasInstalled);
            Assert.False(vm.NoneInstalled);
            Assert.True(vm.Installed[0].IsSelected);
            Assert.False(vm.Installed[0].CanSelect);
            Assert.True(vm.Installed[1].CanSelect);
            Assert.Equal("/Applications/Xcode-26.4.app", vm.Installed[0].Location);

            Assert.Equal(["26.5", "26.5", "26.0"], AvailableVersions(vm));
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
    public Task TheBrowserCombinesVersionsAndKeepsUpdatePolicySeparate() => WithPage(new FakeXcodes(), vm =>
    {
        Assert.Equal(
            ["26.6 Beta", "26.5", "26.5", "26.4", "26.2", "26.1 Beta 2", "26.0"],
            vm.BrowserItems.Select(item => item.Version));
        Assert.False(vm.IncludePrereleases);
        Assert.Equal("26.5", vm.UpdateVersion);
        Assert.True(vm.SelectedBrowserItem?.IsActive);
        Assert.Equal("/Applications/Xcode-26.4.app", vm.SelectedBrowserItem?.Location);

        vm.ReleaseFilterIndex = 2;
        Assert.Equal(["26.6 Beta", "26.1 Beta 2"], vm.BrowserItems.Select(item => item.Version));
        Assert.Equal("26.5", vm.UpdateVersion);

        vm.ReleaseFilterIndex = 0;
        vm.InstalledOnly = true;
        Assert.Equal(["26.4", "26.2"], vm.BrowserItems.Select(item => item.Version));
        vm.SearchText = "17C52";
        Assert.Equal("26.2", Assert.Single(vm.BrowserItems).Version);
        vm.SearchText = "Xcode-26.4.app";
        Assert.Equal("26.4", Assert.Single(vm.BrowserItems).Version);
    });

    [Fact]
    public Task BrowserFiltersAndSelectionSurviveRefreshes()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            vm.SelectedBrowserItem = Assert.Single(vm.BrowserItems, item => item.Location == "/Applications/Xcode-26.2.app");
            vm.ArchitectureFilterIndex = 1;
            vm.InstalledOnly = true;
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            Assert.Equal("/Applications/Xcode-26.2.app", vm.SelectedBrowserItem?.Location);
            Assert.True(XcodePreferences.BrowserInstalledOnly);
            Assert.Equal(1, XcodePreferences.BrowserArchitectureFilter);

            vm.SearchText = "no match";
            Assert.Null(vm.SelectedBrowserItem);
            vm.SearchText = "";
            Assert.Equal("/Applications/Xcode-26.2.app", vm.SelectedBrowserItem?.Location);

            vm.ArchitectureFilterIndex = 2;
            Assert.Empty(vm.BrowserItems);
            vm.ArchitectureFilterIndex = 1;
            Assert.Equal("/Applications/Xcode-26.2.app", vm.SelectedBrowserItem?.Location);
        });
    }

    [Fact]
    public Task BrowserKeepsDuplicateInstalledVersionsAsDistinctPaths()
    {
        var fake = new FakeXcodes
        {
            InstalledOutput = DefaultInstalled + "26.2 (17C52) [Apple Silicon]\t/Applications/Xcode-copy.app\n",
        };
        return WithPage(fake, vm =>
        {
            XcodeBrowserItemViewModel[] copies = [.. vm.BrowserItems.Where(item => item.Version == "26.2")];
            Assert.Equal(2, copies.Length);
            Assert.Equal(["/Applications/Xcode-26.2.app", "/Applications/Xcode-copy.app"],
                copies.Select(item => item.Location));
            vm.SelectedBrowserItem = copies[1];
            vm.SearchText = "Xcode-copy";
            Assert.Equal("/Applications/Xcode-copy.app", vm.SelectedBrowserItem?.Location);
        });
    }

    [Fact]
    public Task BrowserKeepsDifferentBuildsOfOneVersionDistinct()
    {
        var fake = new FakeXcodes
        {
            InstalledOutput = DefaultInstalled
                + "26.5 (17F42) [Apple Silicon]\t/Applications/Xcode-26.5.app\n",
        };
        return WithPage(fake, vm =>
        {
            XcodeBrowserItemViewModel[] rows = [.. vm.BrowserItems.Where(item => item.Version == "26.5")];
            XcodeBrowserItemViewModel installed = Assert.Single(rows, item => item.IsInstalled);
            XcodeBrowserItemViewModel available = Assert.Single(rows, item => item.IsAvailable);
            Assert.Equal("17F42", installed.InstalledItem!.Xcode.Build);
            Assert.Equal("17F43", available.AvailableItem!.Release.Build);
            Assert.Contains("17F43", available.DetailText);

            vm.SelectedBrowserItem = available;
            vm.SearchText = "17F43";
            Assert.Equal("17F43", Assert.Single(vm.BrowserItems).AvailableItem!.Release.Build);
            Assert.Equal("17F43", vm.SelectedBrowserItem?.AvailableItem?.Release.Build);
        });
    }

    [Fact]
    public Task BrowserListsAllUninstalledBuildsOfTheSameVersion()
    {
        return WithPage(new FakeXcodes(), vm =>
        {
            XcodeBrowserItemViewModel[] rows = [.. vm.BrowserItems
                .Where(item => item.Version == "26.5" && item.IsAvailable)];
            Assert.Equal(["17F42", "17F43"], rows.Select(item => item.AvailableItem!.Release.Build));
            Assert.NotEqual(rows[0].Key, rows[1].Key);
            Assert.Contains("17F42", rows[0].DetailText);
            Assert.Contains("17F43", rows[1].DetailText);
        });
    }

    [Fact]
    public Task BrowserRestoresTheSelectedBuildAcrossFilteringAndRefresh()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            vm.GroupVersions = true;
            vm.SelectedBrowserItem = AvailableBuild(vm, "26.5", "17F43");
            vm.SearchText = "17F42";
            Assert.Equal("17F42", Assert.Single(vm.BrowserItems).AvailableItem!.Release.Build);
            vm.SearchText = "";
            Assert.Equal("17F43", vm.SelectedBrowserItem?.AvailableItem?.Release.Build);

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            Assert.Equal("17F43", vm.SelectedBrowserItem?.AvailableItem?.Release.Build);
            Assert.Same(vm.SelectedBrowserItem, vm.SelectedTreeNode?.Item);
        });
    }

    [Fact]
    public Task InstallingASelectedBuildPassesItsExactIdentity()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            XcodeBrowserItemViewModel selected = AvailableBuild(vm, "26.5", "17F43");
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);

            await vm.InstallCommand.ExecuteAsync(selected.AvailableItem);

            Assert.Equal(("17F43", XcodeArchitecture.AppleSilicon),
                (fake.LastInstalledRelease?.Build, fake.LastInstalledRelease?.Architecture));
        });
    }

    [Fact]
    public Task BrowserDetailsAndCompatibilityRemainSpecificToEachBuild()
    {
        var fake = new FakeXcodes();
        var catalog = new FakeCatalog
        {
            Releases = [.. CatalogReleases().Select(release => release.Version == "26.5"
                ? release with { RequiredMacOS = release.Build == "17F42" ? new Version(26, 6) : new Version(25, 0) }
                : release)],
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(26, 5);
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            XcodeBrowserItemViewModel first = AvailableBuild(vm, "26.5", "17F42");
            XcodeBrowserItemViewModel second = AvailableBuild(vm, "26.5", "17F43");
            Assert.True(first.IsIncompatible);
            Assert.Equal("Requires macOS 26.6 or later", first.RequiredMacOSText);
            Assert.False(second.IsIncompatible);
            Assert.Equal("Requires macOS 25.0 or later", second.RequiredMacOSText);
        }, catalog: catalog);
    }

    [Theory]
    [InlineData(1, XcodeArchitecture.AppleSilicon)]
    [InlineData(2, XcodeArchitecture.Universal)]
    public Task ArchitectureFilterSelectsTheDisplayedBuildVariant(int filter, XcodeArchitecture expected)
    {
        XcodeCatalogRelease native = new("27.0", "18A1", XcodeArchitecture.AppleSilicon, null, null, null, [], [], null)
        {
            Download = new Uri("https://download.developer.apple.com/Xcode_Native.xip"),
        };
        XcodeCatalogRelease universal = native with
        {
            Architecture = XcodeArchitecture.Universal,
            Download = new Uri("https://download.developer.apple.com/Xcode_Universal.xip"),
        };
        var fake = new FakeXcodes
        {
            ExtraReleases = XcodeNativeBackend.Merge([universal, native], [], XcodeArchitecture.AppleSilicon),
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.ArchitectureFilterIndex = filter;
            XcodeBrowserItemViewModel available = Assert.Single(vm.BrowserItems, item => item.Version == "27.0");
            Assert.Equal(expected, available.AvailableItem!.Release.Architecture);
            Assert.Contains(XcodeText.Architecture(expected), available.DetailText);
            vm.ConfirmDownload = _ => Task.FromResult(true);

            await vm.DownloadXcodeCommand.ExecuteAsync(available);

            Assert.Equal(expected, fake.LastDownloadedRelease?.Architecture);
        });
    }

    [Fact]
    public Task OperationProgressTargetsOnlyTheSelectedBuild()
    {
        var fake = new FakeXcodes();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<XcodesResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.RespondInTerminal = (_, _, _, cancellation) =>
        {
            started.TrySetResult();
            return finish.Task.WaitAsync(cancellation);
        };
        return WithPageAsync(fake, async vm =>
        {
            XcodeBrowserItemViewModel selected = Assert.Single(vm.BrowserItems,
                item => item.Version == "26.5" && item.AvailableItem?.Release.Build == "17F42");
            vm.ConfirmDownload = _ => Task.FromResult(true);

            Task download = vm.DownloadXcodeCommand.ExecuteAsync(selected);
            await started.Task;
            XcodeBrowserItemViewModel[] rows = [.. vm.BrowserItems.Where(item => item.Version == "26.5")];
            Assert.True(Assert.Single(rows, item => item.AvailableItem?.Release.Build == "17F42").IsOperationTarget);
            Assert.False(Assert.Single(rows, item => item.AvailableItem?.Release.Build == "17F43").IsOperationTarget);
            vm.SearchText = "26.5";
            rows = [.. vm.BrowserItems];
            Assert.True(Assert.Single(rows, item => item.AvailableItem?.Release.Build == "17F42").IsOperationTarget);
            Assert.False(Assert.Single(rows, item => item.AvailableItem?.Release.Build == "17F43").IsOperationTarget);
            finish.SetResult(FakeXcodes.Success(""));
            await download;
        });
    }

    [Fact]
    public Task StableCounterpartIsInformationalAndFollowsTheActivePath()
    {
        var fake = new FakeXcodes
        {
            InstalledOutput = "26.5 Beta 2 (17F42) [Apple Silicon] (Selected)\t/Applications/Xcode-beta.app\n"
                + "26.5 Beta 2 (17F42) [Apple Silicon]\t/Applications/Xcode-copy.app\n",
        };
        return WithPage(fake, vm =>
        {
            XcodeBrowserItemViewModel active = Assert.Single(vm.BrowserItems, item => item.Location == "/Applications/Xcode-beta.app");
            XcodeBrowserItemViewModel other = Assert.Single(vm.BrowserItems, item => item.Location == "/Applications/Xcode-copy.app");
            Assert.True(active.HasStableCounterpart);
            Assert.Equal(
                "A stable Xcode 26.5 for this architecture is installed or available. It is not selected automatically.",
                active.StableCounterpartText);
            Assert.False(other.HasStableCounterpart);
            Assert.DoesNotContain(vm.BrowserItems.Where(item => item.IsAvailable), item => item.HasStableCounterpart);
            Assert.DoesNotContain(fake.Commands, command => command.StartsWith("select ", StringComparison.Ordinal));

            vm.ReleaseFilterIndex = 2;
            vm.InstalledOnly = true;
            Assert.True(Assert.Single(vm.BrowserItems, item => item.IsActive).HasStableCounterpart);
        });
    }

    [Fact]
    public Task GroupingAndExpandedStateSurviveFilteringAndANewPage()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            Assert.False(vm.GroupVersions);
            vm.GroupVersions = true;
            XcodeBrowserNodeViewModel major = Assert.Single(vm.BrowserGroups);
            Assert.Equal("Xcode 26", major.Title);
            Assert.Equal(["26.6", "26.5", "26.4", "26.2", "26.1", "26.0"],
                major.Children.Select(group => group.Title));
            XcodeBrowserNodeViewModel minor = Assert.Single(major.Children, group => group.Title == "26.4");
            minor.IsExpanded = false;
            Assert.Contains("minor:26.4", XcodePreferences.BrowserCollapsedGroups);

            vm.SearchText = "26.4";
            minor = Assert.Single(Assert.Single(vm.BrowserGroups).Children);
            Assert.False(minor.IsExpanded);
            vm.SearchText = "";
            Assert.False(Assert.Single(Assert.Single(vm.BrowserGroups).Children, group => group.Title == "26.4").IsExpanded);

            var reopened = new XcodeViewModel(fake.Service);
            try
            {
                await reopened.InitializeAsync();
                Assert.True(reopened.GroupVersions);
                Assert.False(Assert.Single(Assert.Single(reopened.BrowserGroups).Children,
                    group => group.Title == "26.4").IsExpanded);
            }
            finally
            {
                reopened.Stop();
            }
        });
    }

    [Fact]
    public Task AvailableBuildRowsShowTheirSelectedIdentity() => WithPage(new FakeXcodes(), vm =>
    {
        XcodeBrowserItemViewModel available = AvailableBuild(vm, "26.5", "17F42");
        Assert.True(available.IsAvailable);
        Assert.Equal("Build 17F42 · Apple Silicon", available.DetailText);
        Assert.Equal("Architectures available: Apple Silicon", available.ListedArchitecturesText);
    });

    [Theory]
    [InlineData(XcodeArchitecture.AppleSilicon)]
    [InlineData(XcodeArchitecture.Intel)]
    public Task InstallingAGroupedVersionUsesThePreferredNativeVariant(XcodeArchitecture machine)
    {
        var universal = new XcodeCatalogRelease("27.0", "18A1", XcodeArchitecture.Universal, null, null, null, [], [], null)
        {
            Download = new Uri("https://download.developer.apple.com/Xcode_Universal.xip"),
        };
        XcodeCatalogRelease native = universal with
        {
            Architecture = machine,
            Download = new Uri("https://download.developer.apple.com/Xcode_Native.xip"),
        };
        var fake = new FakeXcodes
        {
            ExtraReleases = XcodeNativeBackend.Merge([universal, native], [], machine),
        };

        return WithPageAsync(fake, async vm =>
        {
            XcodeBrowserItemViewModel available = Assert.Single(vm.BrowserItems, item => item.Version == "27.0");
            Assert.Equal(machine, available.AvailableItem!.Release.Architecture);
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);

            await vm.InstallCommand.ExecuteAsync(available.AvailableItem);

            Assert.Equal(machine, fake.LastInstalledRelease?.Architecture);
            Assert.Equal("18A1", fake.LastInstalledRelease?.Build);
        });
    }

    [Fact]
    public Task ARecognizedBuildRemainsAvailableAlongsideAnInvalidVariant()
    {
        var invalidNative = new XcodeCatalogRelease("27.0", "18A1\n", XcodeArchitecture.AppleSilicon,
            null, null, null, [], [], null)
        {
            Download = new Uri("https://download.developer.apple.com/Xcode_Invalid.xip"),
        };
        XcodeCatalogRelease validUniversal = invalidNative with
        {
            Build = "18A1",
            Architecture = XcodeArchitecture.Universal,
            Download = new Uri("https://download.developer.apple.com/Xcode_Universal.xip"),
        };
        var fake = new FakeXcodes
        {
            ExtraReleases = XcodeNativeBackend.Merge([invalidNative, validUniversal], [], XcodeArchitecture.AppleSilicon),
        };

        return WithPage(fake, vm =>
        {
            XcodeBrowserItemViewModel[] rows = [.. vm.BrowserItems.Where(entry => entry.Version == "27.0")];
            XcodeBrowserItemViewModel item = Assert.Single(rows, entry => entry.AvailableItem?.Release.Build == "18A1");
            Assert.True(item.CanInstall);
            Assert.True(item.CanDownload);
            Assert.Equal("18A1", item.AvailableItem!.Release.Build);
            Assert.Equal(XcodeArchitecture.Universal, item.AvailableItem.Release.Architecture);
            XcodeBrowserItemViewModel unknown = Assert.Single(rows, entry => entry.AvailableItem?.Release.Build is null);
            Assert.False(unknown.CanInstall);
            Assert.False(unknown.CanDownload);
        });
    }

    [Fact]
    public Task ReleaseDetailsMatchAllListedBuildsAndWarnBeforeAnIncompatibleInstall()
    {
        var fake = new FakeXcodes();
        var catalog = new FakeCatalog { Releases = CatalogReleases() };
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(26, 5, 1);
            Assert.Equal(0, catalog.Requests);
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            XcodeBrowserItemViewModel available = AvailableBuild(vm, "26.5", "17F42");
            Assert.True(available.HasReleaseDetails);
            Assert.True(available.IsIncompatible);
            Assert.Equal("Requires macOS 26.6 or later", available.RequiredMacOSText);
            Assert.Contains("26.5.1", available.CompatibilityText);
            Assert.Contains("iOS 26.5", available.SDKsText);
            Assert.True(available.HasReleaseNotes);
            Assert.Contains("Xcode Releases", vm.CatalogStatusText);

            XcodeCompatibilityAssessment? asked = null;
            vm.ConfirmInstall = (_, _, compatibility, _) =>
            {
                asked = compatibility;
                return Task.FromResult(false);
            };
            await vm.InstallCommand.ExecuteAsync(available.AvailableItem);

            Assert.Equal(2, catalog.Requests);
            Assert.Equal(XcodeCompatibility.Unsupported, asked?.Status);
            Assert.Equal(new Version(26, 6, 0), asked?.RequiredMacOS);
            Assert.Empty(fake.TerminalCommands);
        }, catalog: catalog);
    }

    [Theory]
    [InlineData(true, XcodeCompatibility.Unsupported)]
    [InlineData(false, XcodeCompatibility.Supported)]
    public Task InstallConfirmationUsesTheSelectedBuildsCompatibility(
        bool selectedBuildIsIncompatible, XcodeCompatibility expected)
    {
        var fake = new FakeXcodes();
        var catalog = new FakeCatalog
        {
            Releases = [.. CatalogReleases().Select(release => release.Version == "26.5"
                ? release with
                {
                    RequiredMacOS = (release.Build == "17F42") == selectedBuildIsIncompatible
                        ? new Version(26, 6, 0) : new Version(25, 0, 0),
                }
                : release)],
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(26, 5);
            XcodeCompatibilityAssessment? asked = null;
            vm.ConfirmInstall = (_, _, compatibility, _) =>
            {
                asked = compatibility;
                return Task.FromResult(false);
            };

            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);

            Assert.Equal(expected, asked?.Status);
            Assert.Equal(new Version(selectedBuildIsIncompatible ? 26 : 25,
                selectedBuildIsIncompatible ? 6 : 0, 0), asked?.RequiredMacOS);
            Assert.Empty(fake.TerminalCommands);
        }, catalog: catalog);
    }

    [Fact]
    public Task ReleaseDetailsRenderOtherVersionsWithTheSameBuild() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes();
        XcodeCatalogRelease release = CatalogReleases().Single(item => item.Version == "26.4");
        var catalog = new FakeCatalog
        {
            Releases = [.. CatalogReleases(), release with { Version = "26.4 Release Candidate" }],
        };
        var vm = new XcodeViewModel(fake.Service, catalog: catalog);
        var view = new XcodePage(vm);
        var window = new Window { Width = 900, Height = 700, Content = view };
        try
        {
            await vm.InitializeAsync();
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            vm.SelectedBrowserItem = Assert.Single(vm.BrowserItems, item => item.Version == "26.4" && item.IsInstalled);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var related = view.FindControl<TextBlock>("SameBuildVersions")!;
            Assert.True(related.IsEffectivelyVisible);
            Assert.Equal("The catalog reports the same build and architecture for: 26.4 Release Candidate", related.Text);

            vm.SelectedBrowserItem = Assert.Single(vm.BrowserItems, item => item.Version == "26.2" && item.IsInstalled);
            Dispatcher.UIThread.RunJobs();
            Assert.False(related.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task AFailedCatalogRefreshKeepsDetailsButMarksCompatibilityUnknown()
    {
        var fake = new FakeXcodes();
        var catalog = new FakeCatalog { Releases = CatalogReleases() };
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(26, 5, 1);
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            catalog.Error = new HttpRequestException("network unavailable");

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            XcodeBrowserItemViewModel available = AvailableBuild(vm, "26.5", "17F42");
            Assert.True(available.HasReleaseDetails);
            Assert.False(available.IsIncompatible);
            Assert.Contains("unknown", available.CompatibilityText);
            Assert.StartsWith("Release details may be out of date; last refreshed ", vm.CatalogStatusText);
            Assert.Equal(InfoBarSeverity.Warning, vm.Notice.Severity);
            Assert.Contains("network unavailable", vm.Notice.Message);
            XcodeCompatibilityAssessment? asked = null;
            vm.ConfirmInstall = (_, _, compatibility, _) =>
            {
                asked = compatibility;
                return Task.FromResult(false);
            };

            await vm.InstallCommand.ExecuteAsync(available.AvailableItem);

            Assert.Equal(XcodeCompatibility.Unknown, asked?.Status);
            Assert.Empty(fake.TerminalCommands);
        }, catalog: catalog);
    }

    [Fact]
    public Task SavedReleaseDetailsAreShownAsStaleWithoutARequest()
    {
        var fake = new FakeXcodes();
        var savedAt = new DateTime(2026, 3, 10, 15, 30, 0, DateTimeKind.Utc);
        var catalog = new FakeCatalog { Saved = new XcodeSavedReleaseCatalog(CatalogReleases(), savedAt) };
        return WithPageAsync(fake, async vm =>
        {
            XcodeBrowserItemViewModel available = AvailableBuild(vm, "26.5", "17F42");
            Assert.True(available.HasReleaseDetails);
            Assert.False(available.IsIncompatible);
            Assert.Contains("unknown", available.CompatibilityText);
            Assert.StartsWith("Saved Xcode Releases details may be out of date; last saved ", vm.CatalogStatusText);
            Assert.Contains(savedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), vm.CatalogStatusText);
            Assert.Equal(0, catalog.Requests);
            Assert.Equal(1, catalog.SavedRequests);

            await vm.ReloadAsync();
            Assert.Equal(1, catalog.SavedRequests);
            catalog.Error = new HttpRequestException("network unavailable");
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            available = AvailableBuild(vm, "26.5", "17F42");
            Assert.True(available.HasReleaseDetails);
            Assert.Contains("unknown", available.CompatibilityText);
            Assert.StartsWith("Saved Xcode Releases details may be out of date; last saved ", vm.CatalogStatusText);
            Assert.Contains("network unavailable", vm.Notice.Message);
        }, catalog: catalog);
    }

    [Fact]
    public Task ASuccessfulRefreshReplacesSavedDetailsAndChecksCompatibility()
    {
        var fake = new FakeXcodes();
        var saved = CatalogReleases().Select(release => release with { RequiredMacOS = new Version(26, 0) }).ToArray();
        var catalog = new FakeCatalog
        {
            Saved = new XcodeSavedReleaseCatalog(saved, new DateTime(2026, 3, 10, 15, 30, 0, DateTimeKind.Utc)),
            Releases = CatalogReleases(),
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(26, 5, 1);
            Assert.Contains("unknown", AvailableBuild(vm, "26.5", "17F42").CompatibilityText);

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            XcodeBrowserItemViewModel available = AvailableBuild(vm, "26.5", "17F42");
            Assert.True(available.IsIncompatible);
            Assert.Equal("Requires macOS 26.6 or later", available.RequiredMacOSText);
            Assert.Contains("Xcode Releases", vm.CatalogStatusText);
            Assert.DoesNotContain("out of date", vm.CatalogStatusText);
            Assert.Equal(1, catalog.Requests);

            catalog.Error = new HttpRequestException("network unavailable");
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            Assert.StartsWith("Release details may be out of date; last refreshed ", vm.CatalogStatusText);
            Assert.Contains("unknown", AvailableBuild(vm, "26.5", "17F42").CompatibilityText);
            Assert.Equal(2, catalog.Requests);
        }, catalog: catalog);
    }

    [Fact]
    public Task AnUnreadableSavedCatalogDoesNotPreventTheVersionListFromLoading()
    {
        var fake = new FakeXcodes();
        var catalog = new FakeCatalog { SavedError = new IOException("cache could not be read"), Releases = CatalogReleases() };
        return WithPageAsync(fake, async vm =>
        {
            Assert.True(vm.HasLoaded);
            Assert.False(AvailableBuild(vm, "26.5", "17F42").HasReleaseDetails);
            Assert.Equal("Release details are unavailable.", vm.CatalogStatusText);

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            Assert.True(AvailableBuild(vm, "26.5", "17F42").HasReleaseDetails);
            Assert.Contains("Xcode Releases", vm.CatalogStatusText);
            Assert.Equal(1, catalog.SavedRequests);
        }, catalog: catalog);
    }

    [Fact]
    public Task ThePageRendersSavedCatalogDetailsAndTheirStaleStatus() => DispatchAsync(async () =>
    {
        EnsureTheme();
        Directory.CreateDirectory(_testRoot);
        string path = Path.Combine(_testRoot, "XcodeReleases.json");
        await File.WriteAllTextAsync(path, """
            [
              {"version":{"number":"26.5","build":"17F42","release":{"release":true}},"requires":"26.6",
               "links":{"download":{"architectures":["arm64"]}}},
              {"version":{"number":"26.5","build":"17F43","release":{"release":true}},"requires":"26.6",
               "links":{"download":{"architectures":["arm64"]}}}
            ]
            """);
        using var client = new HttpClient(new NoNetworkHandler());
        var catalog = new XcodeReleasesCatalog(client, cacheFile: () => path);
        var fake = new FakeXcodes();
        var vm = new XcodeViewModel(fake.Service, catalog: catalog);
        var view = new XcodePage(vm);
        var window = new Window { Width = 900, Height = 700, Content = view };
        try
        {
            await vm.InitializeAsync();
            vm.SelectedBrowserItem = AvailableBuild(vm, "26.5", "17F42");
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var status = view.FindControl<TextBlock>("CatalogStatus")!;
            Assert.True(status.IsEffectivelyVisible);
            Assert.StartsWith("Saved Xcode Releases details may be out of date; last saved ", status.Text);
            Assert.Equal(status.Text, AutomationProperties.GetName(status));
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
            Assert.True(vm.SelectedBrowserItem.HasReleaseDetails);
            Assert.Contains("unknown", vm.SelectedBrowserItem.CompatibilityText);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task TheIncompatibleInstallDialogRequiresAnExplicitOverride() => _session.Dispatch(() =>
    {
        EnsureTheme();
        var release = new XcodeRelease("26.5", "17F42", XcodeArchitecture.AppleSilicon, false, false);
        var compatibility = new XcodeCompatibilityAssessment(
            XcodeCompatibility.Unsupported, new Version(26, 6, 0), new Version(26, 5, 1));

        ImmersiveConfirmationDialog dialog = XcodeViewModel.CreateInstallDialog(
            release, null, compatibility, new XcodeInstallOptions());
        var body = ConfirmationDialogBody(dialog);

        Assert.Equal("Install Anyway", dialog.FindControl<Button>("PrimaryButton")!.Content);
        Assert.Equal("Cancel", dialog.FindControl<Button>("SecondaryButton")!.Content);
        Assert.False(dialog.FocusPrimaryButton);
        Assert.Contains(body.Children.OfType<TextBlock>(),
            text => text.Text!.Contains("macOS 26.6", StringComparison.Ordinal)
                && text.Text.Contains("macOS 26.5.1", StringComparison.Ordinal));
        Assert.Contains(body.Children.OfType<TextBlock>(),
            text => text.Text == "Build 17F42 · Apple Silicon");
        Assert.Contains(body.Children.OfType<TextBlock>(),
            text => text.Text!.Contains("never keeps the macOS password", StringComparison.Ordinal));
    }, CancellationToken.None);

    [Fact]
    public Task TheDownloadDialogExplainsItsAccountCacheAndVerificationLimits() => _session.Dispatch(() =>
    {
        EnsureTheme();
        var release = new XcodeRelease("26.5", "17F42", XcodeArchitecture.AppleSilicon, false, false);

        ImmersiveConfirmationDialog dialog = XcodeViewModel.CreateDownloadDialog(
            release, "Saved Apple ID: test@example.test", "");
        string[] texts = ConfirmationDialogBody(dialog).Children.OfType<TextBlock>()
            .Select(text => text.Text!).ToArray();

        Assert.Equal("Download", dialog.FindControl<Button>("PrimaryButton")!.Content);
        Assert.Equal("Cancel", dialog.FindControl<Button>("SecondaryButton")!.Content);
        Assert.False(dialog.FocusPrimaryButton);
        Assert.Contains("Saved Apple ID: test@example.test", texts);
        Assert.Contains(texts, text => text.Contains("17F42", StringComparison.Ordinal)
            && text.Contains("Apple Silicon", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("cache", StringComparison.Ordinal)
            && text.Contains("without installing", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("checksum", StringComparison.Ordinal)
            && text.Contains("signature and build", StringComparison.Ordinal)
            && text.Contains("not during this download", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("may be removed", StringComparison.Ordinal));
    }, CancellationToken.None);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public Task TheInstallDialogDescribesTheSnapshottedSelectionOption(bool selected, bool currentPreference)
        => _session.Dispatch(() =>
        {
            EnsureTheme();
            XcodePreferences.SelectAfterInstall = currentPreference;
            var release = new XcodeRelease("26.5", "17F42", XcodeArchitecture.AppleSilicon, false, false);
            var compatibility = new XcodeCompatibilityAssessment(
                XcodeCompatibility.Supported, new Version(26, 4), new Version(26, 5));

            ImmersiveConfirmationDialog dialog = XcodeViewModel.CreateInstallDialog(
                release, null, compatibility, new XcodeInstallOptions(Select: selected));
            var body = ConfirmationDialogBody(dialog);

            Assert.Equal(selected, body.Children.OfType<TextBlock>()
                .Any(text => text.Text == "It will become the Xcode that command line tools use."));
        }, CancellationToken.None);

    [Theory]
    [InlineData("Saved Apple ID: old@example.test", "")]
    [InlineData("Not signed in to Apple", "")]
    [InlineData("Not signed in to Apple", "Could not read the saved Apple ID: The keychain is locked.")]
    public Task TheInstallDialogShowsTheConfiguredAccountWithoutClaimingAnActiveSession(string status, string warning)
        => _session.Dispatch(() =>
        {
            EnsureTheme();
            var release = new XcodeRelease("26.5", "17F42", XcodeArchitecture.AppleSilicon, false, false);
            var compatibility = new XcodeCompatibilityAssessment(
                XcodeCompatibility.Supported, new Version(26, 4), new Version(26, 5));

            ImmersiveConfirmationDialog dialog = XcodeViewModel.CreateInstallDialog(
                release, null, compatibility, new XcodeInstallOptions(), status, warning);
            var body = ConfirmationDialogBody(dialog);
            string?[] text = [.. body.Children.OfType<TextBlock>().Select(block => block.Text)];

            Assert.Contains(status, text);
            Assert.DoesNotContain(text, line => line?.Contains("xcodes", StringComparison.OrdinalIgnoreCase) == true);
            if (warning.Length > 0)
                Assert.Contains(warning, text);
        }, CancellationToken.None);

    [Fact]
    public Task TheInstallDialogKeepsItsDetailsScrollableInAShortWindow() => _session.Dispatch(() =>
    {
        EnsureTheme();
        var release = new XcodeRelease("26.5 Beta 2", "17F42", XcodeArchitecture.AppleSilicon, false, false);
        var previous = new InstalledXcode("26.4", "17E192", XcodeArchitecture.AppleSilicon, true, "/Applications/Xcode.app");
        var compatibility = new XcodeCompatibilityAssessment(
            XcodeCompatibility.Unsupported, new Version(26, 6), new Version(26, 5));
        ImmersiveConfirmationDialog dialog = XcodeViewModel.CreateInstallDialog(
            release, previous, compatibility, new XcodeInstallOptions(Select: true),
            "Saved Apple ID: test@example.test",
            "Could not read the saved Apple ID: The keychain is locked.");
        var window = new Window { Width = 420, Height = 320, Content = dialog };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var scroll = Assert.IsType<ScrollViewer>(dialog.FindControl<ContentControl>("BodyPresenter")!.Content);
            Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            var body = Assert.IsType<StackPanel>(scroll.Content);
            var compatibilityWarning = Assert.Single(body.Children.OfType<TextBlock>(),
                block => block.Text?.Contains("requires macOS 26.6", StringComparison.Ordinal) == true);
            var accountStatus = Assert.Single(body.Children.OfType<TextBlock>(),
                block => block.Text == "Saved Apple ID: test@example.test");
            var environmentWarning = Assert.Single(body.Children.OfType<TextBlock>(),
                block => block.Text?.StartsWith("Could not read the saved Apple ID", StringComparison.Ordinal) == true);
            foreach (TextBlock important in new[] { compatibilityWarning, accountStatus, environmentWarning })
            {
                var location = important.TranslatePoint(new Point(0, 0), scroll);
                Assert.NotNull(location);
                Assert.True(location.Value.Y >= 0 && location.Value.Y + important.Bounds.Height <= scroll.Viewport.Height,
                    $"Important confirmation text is outside the initial viewport: {important.Text}");
            }
            Assert.True(scroll.Focus(NavigationMethod.Tab));
            window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(scroll.Offset.Y > 0);

            var primary = dialog.FindControl<Button>("PrimaryButton")!;
            var position = primary.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(position);
            Assert.True(position.Value.Y + primary.Bounds.Height <= window.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }, CancellationToken.None);

    [Theory]
    [InlineData("install")]
    [InlineData("export")]
    [InlineData("download")]
    public Task RuntimeConfirmationsKeepAllDetailsAccessibleInAShortWindow(string operation) => _session.Dispatch(() =>
    {
        EnsureTheme();
        XcodeSimulatorRuntime runtime = SimulatorRuntimes().Catalog[0];
        XcodeVersion.TryParse("26.5", out XcodeVersion version);
        string developerDirectory = "/Applications/" +
            string.Join("/", Enumerable.Repeat("Development Builds and Testing", 18)) +
            "/Example Xcode.app/Contents/Developer";
        var plan = new XcodeSimulatorRuntimeInstallPlan(
            runtime, developerDirectory, version);
        ImmersiveConfirmationDialog dialog = operation switch
        {
            "install" => XcodeViewModel.CreateRuntimeInstallDialog(plan),
            "export" => XcodeViewModel.CreateRuntimeExportDialog(plan, "/Users/Example/Runtime images"),
            _ => XcodeViewModel.CreateRuntimeDownloadDialog(LegacySimulatorRuntime(), "/Users/Example/Runtime images"),
        };
        var window = new Window { Width = 420, Height = 320, Content = dialog };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var scroll = Assert.IsType<ScrollViewer>(dialog.FindControl<ContentControl>("BodyPresenter")!.Content);
            Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height,
                $"The long confirmation did not overflow: extent {scroll.Extent}, viewport {scroll.Viewport}.");
            Assert.True(scroll.Focus(NavigationMethod.Tab));

            for (int page = 0; page < 20; page++)
                window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.True(scroll.Offset.Y > 0);
            var body = Assert.IsType<StackPanel>(scroll.Content);
            TextBlock lastDetail = body.Children.OfType<TextBlock>().Last();
            var location = lastDetail.TranslatePoint(new Point(0, 0), scroll);
            Assert.NotNull(location);
            Assert.True(location.Value.Y >= 0 && location.Value.Y + lastDetail.Bounds.Height <= scroll.Viewport.Height,
                $"The last confirmation detail is not reachable: {lastDetail.Text}");
            foreach (string name in new[] { "PrimaryButton", "SecondaryButton" })
            {
                var button = dialog.FindControl<Button>(name)!;
                var position = button.TranslatePoint(new Point(0, 0), window);
                Assert.NotNull(position);
                Assert.True(position.Value.Y >= 0 && position.Value.Y + button.Bounds.Height <= window.Bounds.Height,
                    $"{name} is outside the window.");
            }
            Assert.False(dialog.FocusPrimaryButton);
        }
        finally
        {
            window.Close();
        }
    }, CancellationToken.None);

    [Fact]
    public Task TheUninstallConfirmationKeepsTheExactPathAndWarningReachableInAShortWindow() => _session.Dispatch(() =>
    {
        EnsureTheme();
        string path = "/Applications/" + string.Join("/", Enumerable.Repeat("Development Builds and Testing", 18)) +
            "/Example Xcode.app";
        var item = new InstalledXcodeItemViewModel(
            new InstalledXcode("26.5", "17F42", XcodeArchitecture.AppleSilicon, true, path));
        ImmersiveConfirmationDialog dialog = XcodeViewModel.CreateUninstallDialog(item);
        var window = new Window { Width = 420, Height = 320, Content = dialog };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var scroll = Assert.IsType<ScrollViewer>(dialog.FindControl<ContentControl>("BodyPresenter")!.Content);
            var body = Assert.IsType<StackPanel>(scroll.Content);
            Assert.Contains(body.Children.OfType<TextBlock>(), text => text.Text?.Contains(path, StringComparison.Ordinal) == true);
            TextBlock warning = Assert.Single(body.Children.OfType<TextBlock>(),
                text => text.Text?.Contains("command line tools use", StringComparison.Ordinal) == true);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height,
                $"The long confirmation did not overflow: extent {scroll.Extent}, viewport {scroll.Viewport}.");
            Assert.True(scroll.Focus(NavigationMethod.Tab));
            for (int page = 0; page < 20; page++)
                window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var position = warning.TranslatePoint(new Point(0, 0), scroll);
            Assert.NotNull(position);
            Assert.True(position.Value.Y >= 0 && position.Value.Y + warning.Bounds.Height <= scroll.Viewport.Height);
            Assert.False(dialog.FocusPrimaryButton);
        }
        finally
        {
            window.Close();
        }
    }, CancellationToken.None);

    [Fact]
    public Task SignOutFromSettingsIsBlockedDuringAnInstallation()
    {
        var fake = new FakeXcodes { Account = new("developer@example.test", null, HasSavedPassword: true) };
        fake.RespondInTerminal = async (_, _, _, cancellation) =>
        {
            await Task.Delay(Timeout.Infinite, cancellation);
            return FakeXcodes.Success("");
        };
        return WithPageAsync(fake, async vm =>
        {
            var settings = new XcodeSettingsViewModel(fake.Service, vm.Account);
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);
            fake.Commands.Clear();
            Task installation = vm.InstallCommand.ExecuteAsync(vm.Available[0]);
            Assert.True(vm.IsOperationRunning);
            Assert.False(settings.Account.SignOutCommand.CanExecute(null));
            await settings.Account.SignOutCommand.ExecuteAsync(null);
            Assert.Empty(fake.Commands);

            vm.CancelOperation();
            await installation;
            Assert.True(settings.Account.CanSignOut);
            Assert.Equal("developer@example.test", settings.Account.Username);
        });
    }

    [Fact]
    public Task APreReleaseIsOfferedOnlyWhenAsked() => WithPage(new FakeXcodes(), vm =>
    {
        Assert.False(vm.IncludePrereleases);

        vm.IncludePrereleases = true;

        Assert.True(CoreSettings.Get(CoreSettings.K.XcodeIncludePrereleases));
        Assert.Equal(["26.6 Beta", "26.5", "26.5", "26.1 Beta 2", "26.0"], AvailableVersions(vm));
        Assert.Equal([true, false, false, true, false], vm.Available.Select(item => item.IsPrerelease).ToArray());
        Assert.Equal("26.6 Beta", vm.UpdateVersion);

        vm.IncludePrereleases = false;

        Assert.False(CoreSettings.Get(CoreSettings.K.XcodeIncludePrereleases));
        Assert.Equal(["26.5", "26.5", "26.0"], AvailableVersions(vm));
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
        AssertSearch(vm, "", "26.6 Beta", "26.5", "26.5", "26.1 Beta 2", "26.0");

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
            Assert.True(vm.HasVersionListStatus);
            Assert.Equal("Showing saved Xcode versions; their age is unknown.", vm.VersionListStatusText);
            fake.Commands.Clear();

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            AssertCommands(fake.Commands, "installed", "update");
            Assert.NotNull(vm.LastChecked);
            Assert.StartsWith("Checked ", vm.LastCheckedText);
            Assert.Contains(vm.LastChecked.Value.ToString("d", CultureInfo.CurrentCulture), vm.LastCheckedText);
            Assert.False(vm.HasVersionListStatus);
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
            AssertCommands(fake.Commands, "installed", "update");
            Assert.NotNull(vm.LastChecked);
            Assert.True(vm.HasUpdate);
            Assert.False(vm.Notice.IsOpen);
        });
    }

    [Theory]
    [InlineData(false, 1440)]
    [InlineData(true, 0)]
    public Task AutomaticInstallationRequiresBothOptInAndPeriodicChecking(bool enabled, int interval)
    {
        XcodePreferences.AutoInstallUpdates = enabled;
        XcodePreferences.CheckIntervalMinutes = interval;
        var fake = new FakeXcodes { Account = FakeXcodes.SignedIn("test@example.test"), ListOutput = SingleNewestBuildList };
        var catalog = new FakeCatalog { Releases = CatalogReleases() };
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(26, 7);
            await vm.InitializeAsync();
            await vm.CheckAutomaticallyAsync();

            Assert.Empty(fake.TerminalCommands);
            Assert.DoesNotContain("verify", fake.Commands);
        }, initialize: false, catalog: catalog);
    }

    [Fact]
    public Task AutomaticInstallationStartsTheExactCompatibleUpdateWithoutAnApplePrompt()
    {
        XcodePreferences.AutoInstallUpdates = true;
        XcodePreferences.CheckIntervalMinutes = 1440;
        var fake = new FakeXcodes { Account = FakeXcodes.SignedIn("test@example.test"), ListOutput = SingleNewestBuildList };
        var catalog = new FakeCatalog { Releases = CatalogReleases() };
        fake.RespondInTerminal = (_, prompts, _, _) =>
        {
            Assert.Null(prompts);
            fake.InstalledOutput = WithNewestInstalled;
            return Task.FromResult(FakeXcodes.Success(""));
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(26, 7);
            vm.ConfirmInstall = (_, _, _, _) => throw new InvalidOperationException("Automatic installs must not prompt.");

            await vm.InitializeAsync();

            Assert.Equal(["install 26.5"], fake.TerminalCommands);
            Assert.Equal("17F42", fake.LastInstalledRelease?.Build);
            Assert.Equal(XcodeArchitecture.AppleSilicon, fake.LastInstalledRelease?.Architecture);
            Assert.Contains("verify", fake.Commands);
            Assert.False(vm.IsBusy);
        }, initialize: false, catalog: catalog);
    }

    [Fact]
    public Task AutomaticInstallationPausesWhenTheNewestVersionHasDifferentBuilds()
    {
        XcodePreferences.AutoInstallUpdates = true;
        XcodePreferences.CheckIntervalMinutes = 1440;
        var fake = new FakeXcodes { Account = FakeXcodes.SignedIn("test@example.test") };
        var catalog = new FakeCatalog { Releases = CatalogReleases() };
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(27, 0);

            await vm.InitializeAsync();

            Assert.Empty(fake.TerminalCommands);
            Assert.DoesNotContain("verify", fake.Commands);
            AssertNotice(vm, InfoBarSeverity.Warning, "Automatic Xcode update paused",
                "The available builds of Xcode 26.5 are ambiguous. Choose one in the version list before installing.");

            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);
            await vm.InstallCommand.ExecuteAsync(AvailableBuild(vm, "26.5", "17F43").AvailableItem);
            Assert.Equal("17F43", fake.LastInstalledRelease?.Build);
        }, initialize: false, catalog: catalog);
    }

    [Fact]
    public Task AutomaticInstallationPausesWhenAnotherBuildIdentityIsUnverified()
    {
        XcodePreferences.AutoInstallUpdates = true;
        XcodePreferences.CheckIntervalMinutes = 1440;
        var fake = new FakeXcodes
        {
            Account = FakeXcodes.SignedIn("test@example.test"),
            ListOutput = SingleNewestBuildList,
            ExtraReleases = [new XcodeRelease("26.5", null, XcodeArchitecture.Unspecified, false, false)
                { IsIdentityUnverified = true }],
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(27, 0);

            await vm.InitializeAsync();

            Assert.Empty(fake.TerminalCommands);
            Assert.DoesNotContain("verify", fake.Commands);
            AssertNotice(vm, InfoBarSeverity.Warning, "Automatic Xcode update paused",
                "The available builds of Xcode 26.5 are ambiguous. Choose one in the version list before installing.");
        }, initialize: false, catalog: new FakeCatalog { Releases = CatalogReleases() });
    }

    [Fact]
    public Task AutomaticInstallationAllowsArchitectureVariantsOfTheSameBuild()
    {
        XcodePreferences.AutoInstallUpdates = true;
        XcodePreferences.CheckIntervalMinutes = 1440;
        var fake = new FakeXcodes
        {
            Account = FakeXcodes.SignedIn("test@example.test"),
            ListOutput = SingleNewestBuildList,
            ExtraReleases = [new XcodeRelease("26.5", "17F42", XcodeArchitecture.Universal, false, false)],
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(27, 0);

            await vm.InitializeAsync();

            Assert.Equal(["install 26.5"], fake.TerminalCommands);
            Assert.Equal(("17F42", XcodeArchitecture.AppleSilicon),
                (fake.LastInstalledRelease?.Build, fake.LastInstalledRelease?.Architecture));
        }, initialize: false, catalog: new FakeCatalog { Releases = CatalogReleases() });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task AutomaticInstallationUsesTheSelectedBuildsCompatibility(bool selectedBuildIsCompatible)
    {
        XcodePreferences.AutoInstallUpdates = true;
        XcodePreferences.CheckIntervalMinutes = 1440;
        var fake = new FakeXcodes { Account = FakeXcodes.SignedIn("test@example.test"), ListOutput = SingleNewestBuildList };
        var catalog = new FakeCatalog
        {
            Releases = [.. CatalogReleases().Select(release => release.Version == "26.5"
                ? release with
                {
                    RequiredMacOS = (release.Build == "17F42") == selectedBuildIsCompatible
                        ? new Version(26, 0, 0) : new Version(26, 7, 0),
                }
                : release)],
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(26, 5);
            await vm.InitializeAsync();

            if (selectedBuildIsCompatible)
            {
                Assert.Equal(["install 26.5"], fake.TerminalCommands);
                Assert.Equal("17F42", fake.LastInstalledRelease?.Build);
            }
            else
            {
                Assert.Empty(fake.TerminalCommands);
                Assert.DoesNotContain("verify", fake.Commands);
                Assert.Equal("Automatic Xcode update paused", vm.Notice.Title);
            }
        }, initialize: false, catalog: catalog);
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("unknown")]
    [InlineData("stale")]
    [InlineData("unknown-build")]
    [InlineData("unknown-architecture")]
    public Task AutomaticInstallationWaitsForKnownSupportedCompatibilityAndIdentity(string reason)
    {
        XcodePreferences.AutoInstallUpdates = true;
        XcodePreferences.CheckIntervalMinutes = 1440;
        var fake = new FakeXcodes { Account = FakeXcodes.SignedIn("test@example.test"), ListOutput = SingleNewestBuildList };
        var catalog = new FakeCatalog { Releases = CatalogReleases() };
        if (reason == "unknown")
            catalog.Releases = [.. CatalogReleases().Select(item => item with { RequiredMacOS = null })];
        if (reason == "stale")
            catalog.Error = new IOException("offline");
        if (reason == "unknown-build")
            fake.ListOutput = SingleNewestBuildList.Replace("26.5 (17F42)", "26.5");
        if (reason == "unknown-architecture")
            fake.ListOutput = SingleNewestBuildList.Replace("26.5 (17F42) [Apple Silicon]", "26.5 (17F42)");
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(reason == "unsupported" ? 26 : 27, 0);
            await vm.InitializeAsync();

            Assert.Empty(fake.TerminalCommands);
            Assert.DoesNotContain("verify", fake.Commands);
            AssertNotice(vm, InfoBarSeverity.Warning, "Automatic Xcode update paused",
                "The update cannot start automatically because its build or macOS compatibility is unknown or unsupported.");
        }, initialize: false, catalog: catalog);
    }

    [Fact]
    public Task AFailedAutomaticCheckNeverInstallsFromTheSavedList()
    {
        XcodePreferences.AutoInstallUpdates = true;
        XcodePreferences.CheckIntervalMinutes = 1440;
        var fake = new FakeXcodes { Account = FakeXcodes.SignedIn("test@example.test"), ListOutput = SingleNewestBuildList };
        fake.Override(command => command == "update" ? FakeXcodes.Failure("offline") : null);
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(27, 0);
            await vm.InitializeAsync();

            Assert.True(vm.HasUpdate);
            Assert.Empty(fake.TerminalCommands);
            Assert.DoesNotContain("verify", fake.Commands);
        }, initialize: false, catalog: new FakeCatalog { Releases = CatalogReleases() });
    }

    [Fact]
    public Task AutomaticCheckingDoesNotInstallWhenThereIsNoUpdate()
    {
        XcodePreferences.AutoInstallUpdates = true;
        XcodePreferences.CheckIntervalMinutes = 1440;
        var fake = new FakeXcodes
        {
            Account = FakeXcodes.SignedIn("test@example.test"),
            ListOutput = OnlyInstalledList,
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(27, 0);
            await vm.InitializeAsync();

            Assert.False(vm.HasUpdate);
            Assert.Empty(fake.TerminalCommands);
            Assert.DoesNotContain("verify", fake.Commands);
        }, initialize: false, catalog: new FakeCatalog { Releases = CatalogReleases() });
    }

    [Theory]
    [InlineData("none")]
    [InlineData("unreadable")]
    [InlineData("expired")]
    public Task AutomaticInstallationWaitsForAReadableValidAppleSession(string state)
    {
        XcodePreferences.AutoInstallUpdates = true;
        XcodePreferences.CheckIntervalMinutes = 1440;
        var fake = new FakeXcodes
        {
            Account = state == "none" ? XcodeAccountStatus.None : new("test@example.test", null, HasSavedPassword: true),
            ListOutput = SingleNewestBuildList,
        };
        if (state == "unreadable")
            fake.AccountError = new XcodesException("The keychain is locked.");
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(27, 0);
            await vm.InitializeAsync();

            Assert.Empty(fake.TerminalCommands);
            Assert.True(vm.Notice.IsOpen);
            Assert.Equal("Automatic Xcode update paused", vm.Notice.Title);
            Assert.Equal(state == "expired", fake.Commands.Contains("verify"));
        }, initialize: false, catalog: new FakeCatalog { Releases = CatalogReleases() });
    }

    [Fact]
    public Task AutomaticInstallationStopsIfTheAccountChangesDuringVerification()
    {
        XcodePreferences.AutoInstallUpdates = true;
        XcodePreferences.CheckIntervalMinutes = 1440;
        var fake = new FakeXcodes { Account = FakeXcodes.SignedIn("old@example.test"), ListOutput = SingleNewestBuildList };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<XcodesResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.OverrideAsync(command =>
        {
            if (command != "verify") return null;
            started.SetResult();
            return finish.Task;
        });
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(27, 0);
            Task initialize = vm.InitializeAsync();
            await started.Task;
            fake.Account = FakeXcodes.SignedIn("new@example.test");
            finish.SetResult(FakeXcodes.Success(""));
            await initialize;

            Assert.Empty(fake.TerminalCommands);
            Assert.Equal("Automatic Xcode update paused", vm.Notice.Title);
        }, initialize: false, catalog: new FakeCatalog { Releases = CatalogReleases() });
    }

    [Fact]
    public Task TurningOffAutomaticInstallationDuringVerificationStopsIt()
    {
        XcodePreferences.AutoInstallUpdates = true;
        XcodePreferences.CheckIntervalMinutes = 1440;
        var fake = new FakeXcodes { Account = FakeXcodes.SignedIn("test@example.test"), ListOutput = SingleNewestBuildList };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<XcodesResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.OverrideAsync(command =>
        {
            if (command != "verify") return null;
            started.SetResult();
            return finish.Task;
        });
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(27, 0);
            Task initialize = vm.InitializeAsync();
            await started.Task;
            XcodePreferences.AutoInstallUpdates = false;
            finish.SetResult(FakeXcodes.Success(""));
            await initialize;

            Assert.Empty(fake.TerminalCommands);
        }, initialize: false, catalog: new FakeCatalog { Releases = CatalogReleases() });
    }

    [Fact]
    public Task AFailedAutomaticInstallationDoesNotRepeatInTheSameSession()
    {
        XcodePreferences.AutoInstallUpdates = true;
        XcodePreferences.CheckIntervalMinutes = 1440;
        var fake = new FakeXcodes { Account = FakeXcodes.SignedIn("test@example.test"), ListOutput = SingleNewestBuildList };
        fake.RespondInTerminal = (_, _, _, _) => Task.FromResult(FakeXcodes.Failure("Download failed"));
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(27, 0);
            await vm.InitializeAsync();
            await vm.CheckAutomaticallyAsync();

            Assert.Equal(["install 26.5"], fake.TerminalCommands);
            Assert.True(vm.HasOperation);
            Assert.Equal(InfoBarSeverity.Error, vm.Notice.Severity);
        }, initialize: false, catalog: new FakeCatalog { Releases = CatalogReleases() });
    }

    [Fact]
    public Task AutomaticCheckingDoesNotInterruptAnExistingOperation()
    {
        XcodePreferences.CheckIntervalMinutes = 1440;
        var fake = new FakeXcodes { Account = FakeXcodes.SignedIn("test@example.test") };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<XcodesResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.RespondInTerminal = (_, _, _, cancellation) =>
        {
            started.SetResult();
            return finish.Task.WaitAsync(cancellation);
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(27, 0);
            Task installing = InstallAsync(vm);
            await started.Task;
            XcodePreferences.AutoInstallUpdates = true;
            int previousChecks = fake.Commands.Count;

            await vm.CheckAutomaticallyAsync();

            Assert.Single(fake.TerminalCommands);
            Assert.Equal(previousChecks, fake.Commands.Count);
            finish.SetResult(FakeXcodes.Success(""));
            await installing;
        }, catalog: new FakeCatalog { Releases = CatalogReleases() });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CancellingOrStoppingAnAutomaticInstallationDoesNotStartItAgain(bool stop)
    {
        XcodePreferences.AutoInstallUpdates = true;
        XcodePreferences.CheckIntervalMinutes = 1440;
        var fake = new FakeXcodes { Account = FakeXcodes.SignedIn("test@example.test"), ListOutput = SingleNewestBuildList };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = new TaskCompletionSource<XcodesResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.RespondInTerminal = (_, _, _, cancellation) =>
        {
            started.SetResult();
            return wait.Task.WaitAsync(cancellation);
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.CurrentMacOS = () => new Version(27, 0);
            Task initialize = vm.InitializeAsync();
            await started.Task;
            if (stop)
                vm.Stop();
            else
                vm.CancelOperation();
            await initialize;
            await vm.CheckAutomaticallyAsync();

            Assert.Single(fake.TerminalCommands);
            Assert.False(vm.IsOperationRunning);
        }, initialize: false, catalog: new FakeCatalog { Releases = CatalogReleases() });
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

            AssertCommands(fake.Commands, "installed", "update", "list");
            Assert.Null(vm.LastChecked);
            Assert.True(vm.HasLoaded);
            Assert.Equal("26.5", vm.UpdateVersion);
            Assert.True(vm.Notice.IsOpen);
            Assert.Equal(InfoBarSeverity.Warning, vm.Notice.Severity);
            Assert.Equal("Could not check for new versions", vm.Notice.Title);
            Assert.Contains("The network is down", vm.Notice.Message);
            Assert.Equal("Could not check for new Xcode versions. Showing the saved list.",
                vm.VersionListStatusText);
        });
    }

    [Fact]
    public Task AFailedAutomaticCheckLabelsTheSavedListUntilAnUpdateSucceeds()
    {
        XcodePreferences.CheckIntervalMinutes = 1440;
        var fake = new FakeXcodes();
        bool updateFails = true;
        fake.Override(command => command == "update" && updateFails ? FakeXcodes.Failure("The network is down") : null);
        return WithPageAsync(fake, async vm =>
        {
            const string failed = "Could not check for new Xcode versions. Showing the saved list.";
            Assert.True(vm.HasLoaded);
            Assert.False(vm.Notice.IsOpen);
            Assert.Equal(failed, vm.VersionListStatusText);
            Assert.Null(vm.LastChecked);

            await vm.ReloadAsync();
            Assert.Equal(failed, vm.VersionListStatusText);

            updateFails = false;
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            Assert.False(vm.HasVersionListStatus);
            Assert.NotNull(vm.LastChecked);

            await vm.ReloadAsync();
            Assert.Equal("Showing saved Xcode versions.", vm.VersionListStatusText);
        });
    }

    [Fact]
    public Task AFailedCheckKeepsTheLastSuccessfulTimeAndAVisibleSavedListStatus()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            DateTime? lastChecked = vm.LastChecked;
            fake.Override(command => command == "update" ? FakeXcodes.Failure("The network is down") : null);

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            vm.Notice.IsOpen = false;

            Assert.Equal(lastChecked, vm.LastChecked);
            Assert.Equal("Could not check for new Xcode versions. Showing the saved list.",
                vm.VersionListStatusText);
            await vm.ReloadAsync();
            Assert.Equal(lastChecked, vm.LastChecked);
            Assert.Equal("Could not check for new Xcode versions. Showing the saved list.",
                vm.VersionListStatusText);
        });
    }

    [Fact]
    public Task RecoveringTheSavedListDoesNotHideTheLastFailedUpdateCheck()
    {
        XcodePreferences.CheckIntervalMinutes = 1440;
        var fake = new FakeXcodes();
        bool savedListFails = true;
        bool updateFails = true;
        fake.Override(command => command switch
        {
            "update" when updateFails => FakeXcodes.Failure("network unavailable"),
            "list" when savedListFails => FakeXcodes.Failure("saved list unavailable"),
            _ => null,
        });
        return WithPageAsync(fake, async vm =>
        {
            Assert.Equal("Xcode versions are unavailable. Try checking for updates again.", vm.VersionListStatusText);
            Assert.False(vm.HasLoaded);

            savedListFails = false;
            await vm.ReloadAsync();
            Assert.True(vm.HasLoaded);
            Assert.False(vm.Notice.IsOpen);
            Assert.Equal("Could not check for new Xcode versions. Showing the saved list.",
                vm.VersionListStatusText);

            updateFails = false;
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            Assert.False(vm.HasVersionListStatus);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task AReleaseListFailureKeepsInstalledXcodesAndDoesNotClaimThePageIsLoading(bool hasInstalled)
    {
        var fake = new FakeXcodes { InstalledOutput = hasInstalled ? DefaultInstalled : "" };
        fake.Override(command => command == "list" ? FakeXcodes.Failure("saved list unavailable") : null);
        return WithPageAsync(fake, async vm =>
        {
            Assert.False(vm.HasLoaded);
            Assert.True(vm.HasVersionLoadError);
            Assert.Equal(hasInstalled ? ["26.4", "26.2"] : [], InstalledVersions(vm));
            Assert.All(vm.BrowserItems, item => Assert.True(item.IsInstalled));
            Assert.Empty(vm.Available);
            Assert.False(vm.HasUpdate);
            Assert.Equal("Xcode versions are unavailable. Try checking for updates again.", vm.AvailableEmptyText);
            Assert.Equal("Xcode versions are unavailable. Try checking for updates again.", vm.BrowserEmptyText);
            Assert.Equal("Xcode versions are unavailable. Try checking for updates again.", vm.VersionListStatusText);
            AssertNotice(vm, InfoBarSeverity.Error, "Could not read the Xcode versions", "saved list unavailable");

            var oldRelease = new AvailableXcodeItemViewModel(
                new XcodeRelease("26.5", null, XcodeArchitecture.AppleSilicon, false, false));
            await vm.InstallCommand.ExecuteAsync(oldRelease);
            Assert.Empty(fake.TerminalCommands);
        });
    }

    [Fact]
    public Task AFailedRefreshAndSavedListUseTheNewInstalledResultsAndClearStaleUpdates()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            Assert.NotNull(vm.LastChecked);
            AvailableXcodeItemViewModel oldRelease = vm.Available[0];
            fake.InstalledOutput = OnlyNewestInstalled;
            fake.Override(command => command switch
            {
                "update" => FakeXcodes.Failure("network unavailable"),
                "list" => FakeXcodes.Failure("saved list unavailable"),
                _ => null,
            });

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            Assert.False(vm.HasLoaded);
            Assert.True(vm.HasVersionLoadError);
            Assert.Equal(["26.4"], InstalledVersions(vm));
            Assert.Single(vm.BrowserItems);
            Assert.True(vm.BrowserItems[0].IsInstalled);
            Assert.Empty(vm.Available);
            Assert.False(vm.HasUpdate);
            Assert.Null(vm.LastChecked);
            AssertNotice(vm, InfoBarSeverity.Error, "Could not read the Xcode versions",
                "network unavailable The saved Xcode list could not be read: saved list unavailable");

            await vm.InstallCommand.ExecuteAsync(oldRelease);
            Assert.Empty(fake.TerminalCommands);
        });
    }

    [Fact]
    public Task TheReleaseListCanRecoverWithoutLeavingAnOutdatedFailureNotice()
    {
        var fake = new FakeXcodes();
        bool broken = true;
        fake.Override(command => command == "list" && broken ? FakeXcodes.Failure("saved list unavailable") : null);
        return WithPageAsync(fake, async vm =>
        {
            Assert.True(vm.HasVersionLoadError);
            Assert.True(vm.Notice.IsOpen);
            broken = false;

            await vm.ReloadAsync();

            Assert.True(vm.HasLoaded);
            Assert.False(vm.HasVersionLoadError);
            Assert.False(vm.Notice.IsOpen);
            Assert.Equal(["26.4", "26.2"], InstalledVersions(vm));
            Assert.True(vm.HasUpdate);
        });
    }

    [Fact]
    public Task AnInstalledListFailureRetainsKnownPathsButClearsAvailableVersions()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            fake.Override(command => command == "installed" ? FakeXcodes.Failure("Permission denied") : null);

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);

            Assert.False(vm.HasLoaded);
            Assert.True(vm.HasVersionLoadError);
            Assert.Equal(["26.4", "26.2"], InstalledVersions(vm));
            Assert.All(vm.BrowserItems, item => Assert.True(item.IsInstalled));
            Assert.Empty(vm.Available);
            Assert.False(vm.HasUpdate);
            AssertNotice(vm, InfoBarSeverity.Error, "Could not read the Xcode versions", "Permission denied");
        });
    }

    [Fact]
    public Task WhenTheVersionsCannotBeReadOnlyAnAskedCheckSaysSo()
    {
        var fake = new FakeXcodes();
        fake.Override(command => command == "installed" ? FakeXcodes.Failure("Permission denied") : null);
        return WithPageAsync(fake, async vm =>
        {
            Assert.False(vm.HasLoaded);
            Assert.False(vm.Notice.IsOpen);
            Assert.Equal("Xcode versions are unavailable. Try checking for updates again.", vm.BrowserEmptyText);

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

            AssertCommands(fake.Commands, "installed", "list");
            Assert.True(vm.IsBusy);
            Assert.False(vm.CanStartOperation);
            Assert.True(vm.ShowBusyBar);

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            AssertCommands(fake.Commands, "installed", "list");

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
            await vm.ReloadAsync();

            AssertCommands(fake.Commands);
            Assert.False(vm.HasLoaded);
        }, initialize: false);
    }

    // ─── Selecting ───────────────────────────────────────────────────────────

    [Fact]
    public Task TheSavedAccountIsVisibleAndCanBeSignedOutWithoutInstalling()
    {
        var fake = new FakeXcodes { Account = new("wrong@example.test", null, HasSavedPassword: true) };
        return WithPageAsync(fake, async vm =>
        {
            Assert.Equal("Saved Apple ID: wrong@example.test", vm.Account.Status);
            Assert.True(vm.Account.SignOutCommand.CanExecute(null));
            fake.Commands.Clear();

            await vm.Account.SignOutCommand.ExecuteAsync(null);

            AssertCommands(fake.Commands, "signout");
            Assert.Empty(fake.TerminalCommands);
            Assert.Null(vm.Account.Username);
            Assert.Equal("Not signed in to Apple", vm.Account.Status);
            Assert.False(vm.Account.SignOutCommand.CanExecute(null));
            Assert.False(vm.Account.VerifyCommand.CanExecute(null));
            Assert.Equal(InfoBarSeverity.Success, vm.Account.Notice.Severity);
            Assert.True(vm.Account.Notice.IsOpen);
            Assert.Equal(["26.4", "26.2"], InstalledVersions(vm));
        });
    }

    [Fact]
    public Task SigningInFromTheAccountCardAsksForTheAppleIdAndPassword()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            Assert.Equal("Not signed in to Apple", vm.Account.Status);
            Assert.False(vm.Account.HasAccount);
            Assert.Contains("Accounts that require security keys or federated sign-in are not supported yet.",
                vm.Account.Description);
            List<XcodesPrompt> asked = [];
            vm.Account.Ask = (prompt, _) =>
            {
                asked.Add(prompt);
                return Task.FromResult<string?>(prompt.Kind == XcodesPromptKind.AppleId ? "correct@example.test" : "synthetic-password");
            };
            fake.Commands.Clear();

            await vm.Account.SignInCommand.ExecuteAsync(null);

            AssertCommands(fake.Commands, "signin");
            Assert.Equal([XcodesPromptKind.AppleId, XcodesPromptKind.Password], asked.Select(prompt => prompt.Kind));
            Assert.Equal("correct@example.test", asked[1].AppleId);
            Assert.Equal("correct@example.test", vm.Account.Username);
            Assert.Equal("Signed in as Test Developer (correct@example.test)", vm.Account.Status);
            AssertNotice(vm.Account.Notice, InfoBarSeverity.Success, "Signed in to Apple",
                "Xcode downloads now use correct@example.test.");
            Assert.False(vm.Account.IsBusy);
            Assert.True(vm.CanStartOperation);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SigningInAgainSuggestsTheSavedAppleIdUnlessAnotherIsWanted(bool differentAppleId)
    {
        var fake = new FakeXcodes { Account = new("wrong@example.test", null, HasSavedPassword: false) };
        return WithPageAsync(fake, async vm =>
        {
            List<XcodesPromptKind> asked = [];
            vm.Account.Ask = (prompt, _) =>
            {
                asked.Add(prompt.Kind);
                return Task.FromResult<string?>(prompt.Kind == XcodesPromptKind.AppleId ? "correct@example.test" : "synthetic-password");
            };
            fake.Commands.Clear();

            await (differentAppleId ? vm.Account.UseDifferentAppleIdCommand : vm.Account.SignInCommand).ExecuteAsync(null);

            AssertCommands(fake.Commands, differentAppleId ? "signin" : "signin wrong@example.test");
            Assert.Equal(differentAppleId
                    ? [XcodesPromptKind.AppleId, XcodesPromptKind.Password]
                    : [XcodesPromptKind.Password],
                asked);
            Assert.Equal(differentAppleId ? "correct@example.test" : "wrong@example.test", vm.Account.Username);
        });
    }

    [Fact]
    public Task ACancelledSignInChangesNothing()
    {
        var fake = new FakeXcodes { Account = new("wrong@example.test", null, HasSavedPassword: true) };
        return WithPageAsync(fake, async vm =>
        {
            vm.Account.Ask = (_, _) => Task.FromResult<string?>(null);

            await vm.Account.UseDifferentAppleIdCommand.ExecuteAsync(null);

            Assert.Equal("wrong@example.test", vm.Account.Username);
            Assert.False(vm.Account.Notice.IsOpen);
            Assert.False(vm.Account.IsBusy);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SignInFailuresAreReportedAndAccessFailuresOfferTheDeveloperAccount(bool accessFailure)
    {
        var fake = new FakeXcodes
        {
            SignIn = (_, _, _) => throw new XcodesException("Apple did not accept the Apple ID or password.")
            {
                IsAccessFailure = accessFailure,
            },
        };
        return WithPageAsync(fake, async vm =>
        {
            await vm.Account.SignInCommand.ExecuteAsync(null);

            AssertNotice(vm.Account.Notice, InfoBarSeverity.Error, "Could not sign in to Apple",
                "Apple did not accept the Apple ID or password.");
            Assert.Equal(accessFailure ? "Apple Developer account" : "", vm.Account.Notice.ActionButtonText);
            Assert.Equal(accessFailure, vm.Account.Notice.ActionButtonCommand is not null);
            Assert.Null(vm.Account.Username);
            Assert.False(vm.Account.IsBusy);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task VerifyingChecksTheSessionWithApple(bool valid)
    {
        var fake = new FakeXcodes { Account = new("developer@example.test", null, HasSavedPassword: true) };
        return WithPageAsync(fake, async vm =>
        {
            Assert.True(vm.Account.VerifyCommand.CanExecute(null));
            if (valid)
                fake.Account = FakeXcodes.SignedIn("developer@example.test");
            fake.Commands.Clear();

            await vm.Account.VerifyCommand.ExecuteAsync(null);

            AssertCommands(fake.Commands, "verify");
            if (valid)
            {
                AssertNotice(vm.Account.Notice, InfoBarSeverity.Success, "Apple session is valid",
                    "Xcode downloads use developer@example.test.");
                Assert.Equal("Signed in as Test Developer (developer@example.test)", vm.Account.Status);
            }
            else
            {
                Assert.Equal("Apple session expired", vm.Account.Notice.Title);
                Assert.Equal("Saved Apple ID: developer@example.test", vm.Account.Status);
            }
        });
    }

    [Fact]
    public Task SignOutFailuresKeepTheAccountAndShowTheReason()
    {
        var fake = new FakeXcodes { Account = new("wrong@example.test", null, HasSavedPassword: true) };
        fake.Override(command => command == "signout" ? FakeXcodes.Failure("Keychain access denied") : null);
        return WithPageAsync(fake, async vm =>
        {
            await vm.Account.SignOutCommand.ExecuteAsync(null);

            Assert.Equal("wrong@example.test", vm.Account.Username);
            Assert.True(vm.Account.CanSignOut);
            Assert.False(vm.Account.IsBusy);
            Assert.Equal(InfoBarSeverity.Error, vm.Account.Notice.Severity);
            Assert.Contains("Keychain access denied", vm.Account.Notice.Message);
        });
    }

    [Theory]
    [InlineData("other@example.test")]
    [InlineData(null)]
    public Task SignOutRequiresReviewWhenTheAccountChangedElsewhere(string? username)
    {
        var fake = new FakeXcodes { Account = new("wrong@example.test", null, HasSavedPassword: true) };
        return WithPageAsync(fake, async vm =>
        {
            fake.Commands.Clear();
            fake.Account = username is null ? XcodeAccountStatus.None : new(username, null, HasSavedPassword: true);

            await vm.Account.SignOutCommand.ExecuteAsync(null);

            Assert.Empty(fake.Commands);
            Assert.Equal(username, vm.Account.Username);
            AssertNotice(vm.Account.Notice, InfoBarSeverity.Warning, "Apple ID changed",
                "Review the current Apple ID before signing out.");
            Assert.False(vm.Account.IsBusy);
            Assert.True(vm.CanStartOperation);

            if (username is not null)
            {
                await vm.Account.SignOutCommand.ExecuteAsync(null);
                AssertCommands(fake.Commands, "signout");
                Assert.Null(vm.Account.Username);
            }
        });
    }

    [Fact]
    public Task AccountRefreshReflectsExternalChangesAndReportsUnreadableState()
    {
        var fake = new FakeXcodes();
        return WithPage(fake, vm =>
        {
            fake.Account = new("other@example.test", null, HasSavedPassword: true);
            vm.Account.RefreshCommand.Execute(null);
            Assert.Equal("other@example.test", vm.Account.Username);

            fake.AccountError = new XcodesException("The keychain is locked.");
            vm.Account.RefreshCommand.Execute(null);
            Assert.Null(vm.Account.Username);
            Assert.True(vm.Account.HasWarning);
            Assert.Equal("Not signed in to Apple", vm.Account.Status);
            Assert.Equal("Could not read the saved Apple ID: The keychain is locked.", vm.Account.Warning);
            Assert.False(vm.Account.CanSignOut);
        });
    }

    [Fact]
    public Task SigningOutBlocksPageOperationsAndDuplicateSignOuts()
    {
        var fake = new FakeXcodes { Account = new("wrong@example.test", null, HasSavedPassword: true) };
        var gate = new TaskCompletionSource<XcodesResult>();
        fake.OverrideAsync(command => command == "signout" ? gate.Task : null);
        return WithPageAsync(fake, async vm =>
        {
            var settings = new XcodeSettingsViewModel(fake.Service, vm.Account);
            Assert.Same(vm.Account, settings.Account);
            fake.Commands.Clear();
            Task signingOut = vm.Account.SignOutCommand.ExecuteAsync(null);
            Assert.True(vm.Account.IsBusy);
            Assert.False(vm.CanStartOperation);
            Assert.False(settings.Account.SignOutCommand.CanExecute(null));
            Assert.False(settings.Account.SignInCommand.CanExecute(null));

            await settings.Account.SignOutCommand.ExecuteAsync(null);
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            await vm.SelectCommand.ExecuteAsync(vm.Installed[1]);
            AssertCommands(fake.Commands, "signout");
            Assert.Empty(fake.TerminalCommands);

            fake.Account = XcodeAccountStatus.None;
            gate.SetResult(FakeXcodes.Success(""));
            await signingOut;
            Assert.True(vm.CanStartOperation);
        });
    }

    [Fact]
    public Task InstallPromptsReachTheUiNameTheAccountAndStayOutOfTheLog()
    {
        var fake = new FakeXcodes();
        List<XcodesPrompt> asked = [];
        fake.RespondInTerminal = (_, prompts, output, token) => Task.Run(async () =>
        {
            output!(new XcodesOutputLine("Signing in to Apple"));
            string? appleId = await prompts!(new XcodesPrompt(XcodesPromptKind.AppleId, "Apple ID:", []), token);
            await prompts(new XcodesPrompt(XcodesPromptKind.Password, "Password:", []) { AppleId = appleId }, token);
            fake.Account = FakeXcodes.SignedIn(appleId!);
            return FakeXcodes.Failure("The Xcode download failed with HTTP 403.");
        }, CancellationToken.None);
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);
            vm.Ask = async (prompt, _) => await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Assert.True(Dispatcher.UIThread.CheckAccess());
                asked.Add(prompt);
                return prompt.Kind == XcodesPromptKind.AppleId ? "correct@example.test" : "synthetic-password";
            });

            await vm.InstallCommand.ExecuteAsync(vm.Available.Single(item => item.Release.Build == "17F42"));

            Assert.Equal(2, asked.Count);
            Assert.Equal("correct@example.test", asked[1].AppleId);
            Assert.Equal("correct@example.test", vm.Account.Username);
            Assert.Contains("Signing in to Apple", vm.OperationLog);
            Assert.DoesNotContain("correct@example.test", vm.OperationLog);
            Assert.DoesNotContain("synthetic-password", vm.OperationLog);
            Assert.True(vm.HasAccessRecovery);
        });
    }

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

            AssertCommands(fake.TerminalCommands, "select /Applications/Xcode-26.2.app");
            AssertCommands(fake.Commands, "installed", "list");
            Assert.False(vm.HasOperation);
            Assert.False(vm.IsOperationRunning);
            AssertNotice(
                vm, InfoBarSeverity.Success,
                "Xcode 26.2 is now the selected Xcode", "Command line tools such as xcodebuild use it.");
            Assert.True(Assert.Single(vm.Installed, item => item.IsSelected).Version == "26.2");
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SelectionSuccessRequiresTheExactPathAndKnownBuild(bool anotherPathWasSelected)
    {
        var fake = new FakeXcodes
        {
            InstalledOutput = DefaultInstalled + "26.2 (17C52) [Apple Silicon]\t/Applications/Xcode-copy.app\n",
        };
        fake.RespondInTerminal = (_, _, _, _) =>
        {
            fake.InstalledOutput = anotherPathWasSelected
                ? "26.2 (17C52) [Apple Silicon] (Selected)\t/Applications/Xcode-26.2.app\n"
                    + "26.2 (17C52) [Apple Silicon]\t/Applications/Xcode-copy.app\n"
                : "26.2 (17C52) [Apple Silicon]\t/Applications/Xcode-26.2.app\n"
                    + "26.2 (17C53) [Apple Silicon] (Selected)\t/Applications/Xcode-copy.app\n";
            return Task.FromResult(FakeXcodes.Success(""));
        };
        return WithPageAsync(fake, async vm =>
        {
            InstalledXcodeItemViewModel copy = Assert.Single(vm.Installed,
                item => item.Location == "/Applications/Xcode-copy.app");

            await vm.SelectCommand.ExecuteAsync(copy);

            AssertCommands(fake.TerminalCommands, "select /Applications/Xcode-copy.app");
            AssertNotice(vm, InfoBarSeverity.Warning, "Finished selecting Xcode 26.2",
                "Selecting finished, but the selected Xcode path could not be confirmed. Refresh the list before trying again.");
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
        var fake = new FakeXcodes { Account = new("developer@example.test", null, HasSavedPassword: true) };
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
            Assert.False(vm.Account.CanSignOut);
            await vm.Account.SignOutCommand.ExecuteAsync(null);
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
    public Task TheProgressPanelFollowsWhatTheOperationWrites()
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

            Assert.Equal("Starting...", startStatus);
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
    public Task ADelayedOutputCallbackCannotWriteIntoANewerOperation()
    {
        var fake = new FakeXcodes();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<XcodesOutputLine>? oldOutput = null;
        int calls = 0;
        fake.RespondInTerminal = async (_, _, output, cancellation) =>
        {
            if (++calls == 1)
            {
                oldOutput = output;
                return FakeXcodes.Failure("First operation failed");
            }

            output!(new XcodesOutputLine("(1/6) Downloading Xcode 26.2: 12%"));
            started.SetResult();
            await finish.Task.WaitAsync(cancellation);
            return FakeXcodes.Failure("Second operation failed");
        };
        return WithPageAsync(fake, async vm =>
        {
            await vm.SelectCommand.ExecuteAsync(vm.Installed[1]);
            Task second = vm.SelectCommand.ExecuteAsync(vm.Installed[1]);
            await started.Task;

            oldOutput!(new XcodesOutputLine("(1/6) Downloading old operation: 99%", ReplacesPrevious: true));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Downloading Xcode 26.2: 12%", vm.OperationStatus);
            Assert.Equal(12, vm.OperationProgress);
            Assert.DoesNotContain("old operation", vm.OperationLog);
            Assert.Contains("26.2: 12%", vm.OperationLog);
            finish.SetResult();
            await second;
            Assert.DoesNotContain("old operation", vm.OperationLog);
        });
    }

    [Fact]
    public Task OutputFloodKeepsRecentProgressAndReportsOmittedLines()
    {
        var fake = new FakeXcodes();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.RespondInTerminal = async (_, _, output, cancellation) =>
        {
            for (int i = 0; i < 500; i++)
                output!(new XcodesOutputLine($"line-{i:000}"));
            output!(new XcodesOutputLine("private-fragment" + new string('x', 17_000)));
            output(new XcodesOutputLine("(1/6) Downloading Xcode 26.2: 88%"));
            started.SetResult();
            await finish.Task.WaitAsync(cancellation);
            return FakeXcodes.Failure("Download failed");
        };
        return WithPageAsync(fake, async vm =>
        {
            Task operation = vm.SelectCommand.ExecuteAsync(vm.Installed[1]);
            await started.Task;
            Dispatcher.UIThread.RunJobs();

            Assert.StartsWith("[Earlier output was omitted.]", vm.OperationLog);
            Assert.DoesNotContain("line-000", vm.OperationLog);
            Assert.Contains("line-499", vm.OperationLog);
            Assert.Contains("[A long output line was omitted.]", vm.OperationLog);
            Assert.DoesNotContain("private-fragment", vm.OperationLog);
            Assert.Equal("Downloading Xcode 26.2: 88%", vm.OperationStatus);
            Assert.Equal(88, vm.OperationProgress);
            Assert.True(vm.OperationLog.Length < 525_000);
            finish.SetResult();
            await operation;
            Assert.Contains("line-499", vm.OperationLog);
        });
    }

    [Fact]
    public Task OmittedProgressCannotReplaceAnEarlierRetainedDiagnostic()
    {
        var fake = new FakeXcodes();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.RespondInTerminal = async (_, _, output, cancellation) =>
        {
            output!(new XcodesOutputLine("important diagnostic"));
            Dispatcher.UIThread.RunJobs();
            for (int i = 0; i < 350; i++)
                output(new XcodesOutputLine($"progress-{i:000}", ReplacesPrevious: true));
            started.SetResult();
            await finish.Task.WaitAsync(cancellation);
            return FakeXcodes.Failure("Download failed");
        };
        return WithPageAsync(fake, async vm =>
        {
            Task operation = vm.SelectCommand.ExecuteAsync(vm.Installed[1]);
            await started.Task;
            Dispatcher.UIThread.RunJobs();

            Assert.StartsWith("[Earlier output was omitted.]", vm.OperationLog);
            Assert.Contains("important diagnostic", vm.OperationLog);
            Assert.Contains("progress-349", vm.OperationLog);
            finish.SetResult();
            await operation;
        });
    }

    [Fact]
    public Task RetainedOutputIsBoundedByCharactersEvenWhenLinesArriveSeparately()
    {
        var fake = new FakeXcodes();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.RespondInTerminal = async (_, _, output, cancellation) =>
        {
            for (int i = 0; i < 40; i++)
            {
                output!(new XcodesOutputLine($"line-{i:00}:" + new string('x', 16_000)));
                Dispatcher.UIThread.RunJobs();
            }
            started.SetResult();
            await finish.Task.WaitAsync(cancellation);
            return FakeXcodes.Failure("Download failed");
        };
        return WithPageAsync(fake, async vm =>
        {
            Task operation = vm.SelectCommand.ExecuteAsync(vm.Installed[1]);
            await started.Task;

            Assert.StartsWith("[Earlier output was omitted.]", vm.OperationLog);
            Assert.DoesNotContain("line-00:", vm.OperationLog);
            Assert.Contains("line-39:", vm.OperationLog);
            Assert.True(vm.OperationLog.Length < 525_000);
            finish.SetResult();
            await operation;
        });
    }

    [Fact]
    public Task ADelayedPromptCallbackCannotOpenOrChangeANewerOperation()
    {
        var fake = new FakeXcodes { Account = new("current@example.test", null, HasSavedPassword: true) };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        XcodesPromptHandler? oldPrompt = null;
        XcodesPromptHandler? currentPrompt = null;
        int calls = 0;
        fake.RespondInTerminal = async (_, prompts, _, cancellation) =>
        {
            if (++calls == 1)
            {
                oldPrompt = prompts;
                return FakeXcodes.Failure("First operation failed");
            }
            currentPrompt = prompts;
            started.SetResult();
            await finish.Task.WaitAsync(cancellation);
            return FakeXcodes.Failure("Second operation failed");
        };
        return WithPageAsync(fake, async vm =>
        {
            int asked = 0;
            XcodesPrompt? lastPrompt = null;
            vm.Ask = (prompt, _) =>
            {
                asked++;
                lastPrompt = prompt;
                return Task.FromResult<string?>("answer");
            };
            await InstallAsync(vm);
            Task second = InstallAsync(vm);
            await started.Task;

            string? oldAnswer = await oldPrompt!(
                new XcodesPrompt(XcodesPromptKind.AppleId, "Apple ID:", []), CancellationToken.None);
            Assert.Null(oldAnswer);
            Assert.Equal(0, asked);

            string? currentAnswer = await currentPrompt!(
                new XcodesPrompt(XcodesPromptKind.Password, "Apple ID Password:", []), CancellationToken.None);
            Assert.Equal("answer", currentAnswer);
            Assert.Equal("current@example.test", lastPrompt?.AppleId);
            finish.SetResult();
            await second;
        });
    }

    [Fact]
    public Task AnOldPromptAnswerCannotChangeTheAccountShownByANewerOperation()
    {
        var fake = new FakeXcodes { Account = new("current@example.test", null, HasSavedPassword: true) };
        var oldAnswer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?>? oldPrompt = null;
        XcodesPromptHandler? currentPrompt = null;
        int calls = 0;
        fake.RespondInTerminal = async (_, prompts, _, cancellation) =>
        {
            if (++calls == 1)
            {
                oldPrompt = prompts!(new XcodesPrompt(XcodesPromptKind.AppleId, "Apple ID:", []), cancellation);
                return FakeXcodes.Failure("First operation failed");
            }
            currentPrompt = prompts;
            started.SetResult();
            await finish.Task.WaitAsync(cancellation);
            return FakeXcodes.Failure("Second operation failed");
        };
        return WithPageAsync(fake, async vm =>
        {
            XcodesPrompt? passwordPrompt = null;
            vm.Ask = (prompt, _) =>
            {
                if (prompt.Kind == XcodesPromptKind.AppleId)
                    return oldAnswer.Task;
                passwordPrompt = prompt;
                return Task.FromResult<string?>("password");
            };
            await InstallAsync(vm);
            Task second = InstallAsync(vm);
            await started.Task;

            oldAnswer.SetResult("old@example.test");
            Assert.Null(await oldPrompt!);
            await currentPrompt!(new XcodesPrompt(XcodesPromptKind.Password, "Apple ID Password:", []), CancellationToken.None);
            Assert.Equal("current@example.test", passwordPrompt?.AppleId);
            finish.SetResult();
            await second;
        });
    }

    [Fact]
    public Task ACancelledPromptCannotChangeTheAccountDuringTheSameOperation()
    {
        var fake = new FakeXcodes { Account = new("current@example.test", null, HasSavedPassword: true) };
        var oldAnswer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        XcodesPromptHandler? currentPrompt = null;
        fake.RespondInTerminal = async (_, prompts, _, cancellation) =>
        {
            currentPrompt = prompts;
            started.SetResult();
            await finish.Task.WaitAsync(cancellation);
            return FakeXcodes.Failure("Operation failed");
        };
        return WithPageAsync(fake, async vm =>
        {
            XcodesPrompt? passwordPrompt = null;
            int asked = 0;
            vm.Ask = (prompt, _) =>
            {
                asked++;
                if (prompt.Kind == XcodesPromptKind.AppleId)
                    return oldAnswer.Task;
                passwordPrompt = prompt;
                return Task.FromResult<string?>("password");
            };
            Task operation = InstallAsync(vm);
            await started.Task;
            using var alreadyCancelled = new CancellationTokenSource();
            alreadyCancelled.Cancel();
            Assert.Null(await currentPrompt!(
                new XcodesPrompt(XcodesPromptKind.AppleId, "Apple ID:", []), alreadyCancelled.Token));
            Assert.Equal(0, asked);

            using var promptCancellation = new CancellationTokenSource();
            Task<string?> cancelledPrompt = currentPrompt!(
                new XcodesPrompt(XcodesPromptKind.AppleId, "Apple ID:", []), promptCancellation.Token);
            promptCancellation.Cancel();
            oldAnswer.SetResult("discarded@example.test");
            Assert.Null(await cancelledPrompt);
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual("Waiting for your answer...", vm.OperationStatus);

            await currentPrompt!(new XcodesPrompt(XcodesPromptKind.Password, "Apple ID Password:", []), CancellationToken.None);
            Assert.Equal("current@example.test", passwordPrompt?.AppleId);
            finish.SetResult();
            await operation;
        });
    }

    [Fact]
    public Task InstallationProgressFollowsTheVersionAcrossBrowserRebuilds()
    {
        var fake = new FakeXcodes();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.RespondInTerminal = async (_, _, output, cancellation) =>
        {
            output!(new XcodesOutputLine("(1/6) Downloading Xcode 26.5: 42%"));
            started.SetResult();
            await finish.Task.WaitAsync(cancellation);
            fake.InstalledOutput = WithNewestInstalled;
            return FakeXcodes.Success("");
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);
            Task installing = vm.InstallCommand.ExecuteAsync(vm.Available[0]);
            await started.Task;
            Dispatcher.UIThread.RunJobs();

            XcodeBrowserItemViewModel first = Assert.Single(vm.BrowserItems, item => item.IsOperationTarget);
            Assert.Equal("26.5", first.Version);
            Assert.True(first.IsAvailable);
            Assert.Equal("Cancel operation for Xcode 26.5", first.CancelOperationText);
            Assert.Equal("Step 1 of 6", vm.OperationStep);
            Assert.Equal(42, vm.OperationProgress);
            Assert.False(vm.OperationIsIndeterminate);

            vm.SearchText = "26.5";
            vm.GroupVersions = true;
            Assert.False(first.IsOperationTarget);
            XcodeBrowserItemViewModel filtered = Assert.Single(vm.BrowserItems, item => item.IsOperationTarget);
            Assert.True(filtered.IsOperationTarget);
            XcodeBrowserNodeViewModel group = Assert.Single(Assert.Single(vm.BrowserGroups).Children);
            Assert.Same(filtered, Assert.Single(group.Children, node => node.Item == filtered).Item);

            vm.SearchText = "26.4";
            Assert.False(filtered.IsOperationTarget);
            Assert.DoesNotContain(vm.BrowserItems, item => item.IsOperationTarget);
            Assert.True(vm.HasOperation);
            vm.SearchText = "26.5";
            XcodeBrowserItemViewModel restored = Assert.Single(vm.BrowserItems, item => item.IsOperationTarget);
            Assert.True(restored.IsOperationTarget);

            vm.InstalledOnly = true;
            Assert.False(restored.IsOperationTarget);
            Assert.Empty(vm.BrowserItems);
            vm.InstalledOnly = false;
            XcodeBrowserItemViewModel beforeCompletion = Assert.Single(vm.BrowserItems, item => item.IsOperationTarget);
            Assert.True(beforeCompletion.IsOperationTarget);

            finish.SetResult();
            await installing;
            Assert.False(beforeCompletion.IsOperationTarget);
            Assert.DoesNotContain(vm.BrowserItems, item => item.IsOperationTarget);
            Assert.True(Assert.Single(vm.BrowserItems, item => item.IsInstalled).IsInstalled);
            Assert.Equal("17F43", Assert.Single(vm.BrowserItems, item => item.IsAvailable).AvailableItem!.Release.Build);
            Assert.False(vm.HasOperation);
        });
    }

    [Fact]
    public Task SelectionProgressFollowsOnlyTheExactInstalledPathAndClearsAfterFailure()
    {
        var fake = new FakeXcodes
        {
            InstalledOutput = DefaultInstalled + "26.2 (17C52) [Apple Silicon]\t/Applications/Xcode-copy.app\n",
        };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.RespondInTerminal = async (_, _, output, cancellation) =>
        {
            output!(new XcodesOutputLine("(2/6) Selecting Xcode"));
            started.SetResult();
            await finish.Task.WaitAsync(cancellation);
            return FakeXcodes.Failure("Unable to select Xcode");
        };
        return WithPageAsync(fake, async vm =>
        {
            InstalledXcodeItemViewModel copy = Assert.Single(vm.Installed, item => item.Location == "/Applications/Xcode-copy.app");
            Task selecting = vm.SelectCommand.ExecuteAsync(copy);
            await started.Task;
            Dispatcher.UIThread.RunJobs();

            XcodeBrowserItemViewModel target = Assert.Single(vm.BrowserItems, item => item.IsOperationTarget);
            Assert.Equal(copy.Location, target.Location);
            Assert.Equal("Cancel operation for Xcode 26.2 at /Applications/Xcode-copy.app", target.CancelOperationText);
            Assert.False(Assert.Single(vm.BrowserItems, item => item.Location == "/Applications/Xcode-26.2.app").IsOperationTarget);
            Assert.True(vm.OperationIsIndeterminate);

            vm.SearchText = "Xcode-26.2.app";
            Assert.False(target.IsOperationTarget);
            Assert.DoesNotContain(vm.BrowserItems, item => item.IsOperationTarget);
            vm.SearchText = "";
            vm.GroupVersions = true;
            XcodeBrowserItemViewModel restored = Assert.Single(vm.BrowserItems, item => item.IsOperationTarget);
            Assert.Equal(copy.Location, restored.Location);
            vm.SelectedBrowserItem = Assert.Single(vm.BrowserItems, item => item.Location == "/Applications/Xcode-26.2.app");
            Assert.False(vm.SelectedBrowserItem.IsOperationTarget);
            Assert.True(restored.IsOperationTarget);

            finish.SetResult();
            await selecting;
            Assert.False(restored.IsOperationTarget);
            Assert.DoesNotContain(vm.BrowserItems, item => item.IsOperationTarget);
            Assert.True(vm.IsOperationFinished);
            Assert.True(vm.IsLogExpanded);
            Assert.Contains("(2/6) Selecting Xcode", vm.OperationLog);
            AssertNotice(vm, InfoBarSeverity.Error, "Could not select Xcode 26.2", "Unable to select Xcode");
        });
    }

    [Fact]
    public Task AnOldCancellationConfirmationDoesNotCancelANewOperation()
    {
        var fake = new FakeXcodes();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmationShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken secondCancellation = default;
        int calls = 0;
        fake.RespondInTerminal = async (_, _, _, cancellation) =>
        {
            if (++calls == 1)
            {
                firstStarted.SetResult();
                await firstFinish.Task.WaitAsync(cancellation);
                return FakeXcodes.Success("");
            }

            secondCancellation = cancellation;
            secondStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return FakeXcodes.Success("");
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmCancel = () =>
            {
                confirmationShown.SetResult();
                return confirmation.Task;
            };
            Task first = vm.SelectCommand.ExecuteAsync(vm.Installed[1]);
            await firstStarted.Task;
            Task requestingCancellation = vm.RequestCancelOperationCommand.ExecuteAsync(null);
            await confirmationShown.Task;

            firstFinish.SetResult();
            await first;
            Task second = vm.SelectCommand.ExecuteAsync(vm.Installed[1]);
            await secondStarted.Task;
            confirmation.SetResult(true);
            await requestingCancellation;

            Assert.True(vm.IsOperationRunning);
            Assert.False(secondCancellation.IsCancellationRequested);
            vm.CancelOperation();
            await second;
            Assert.False(vm.IsOperationRunning);
        });
    }

    [Fact]
    public Task WhatTheOperationWritesJustBeforeItFailsIsKeptInTheLog()
    {
        var fake = new FakeXcodes();
        fake.RespondInTerminal = async (_, _, output, _) =>
        {
            // The terminal is read off the interface thread, so its last lines are posted just before it ends.
            await Task.Run(() =>
            {
                output!(new XcodesOutputLine("(1/6) Downloading Xcode 26.2: 3%"));
                output(new XcodesOutputLine("Unable to authenticate"));
            }, CancellationToken.None);
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
    public Task AQuestionFromAnEarlierOperationDoesNotChangeTheNextOperationStatus()
    {
        var fake = new FakeXcodes();
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        fake.RespondInTerminal = async (_, prompts, _, cancellation) =>
        {
            if (++calls == 1)
            {
                await prompts!(new XcodesPrompt(XcodesPromptKind.AppleId, "Apple ID:", []), cancellation);
                return FakeXcodes.Failure("Unable to authenticate");
            }

            secondStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return FakeXcodes.Success("");
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.Ask = (_, _) => Task.FromResult<string?>("me@example.test");
            await vm.SelectCommand.ExecuteAsync(vm.Installed[1]);
            Task second = vm.SelectCommand.ExecuteAsync(vm.Installed[1]);
            await secondStarted.Task;

            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Starting...", vm.OperationStatus);
            vm.CancelOperation();
            await second;
        });
    }

    [Fact]
    public Task SignInQuestionsAreAskedInTheWindow()
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

            await InstallAsync(vm);

            Assert.Equal("me@example.com", answer);
            Assert.Equal(XcodesPromptKind.AppleId, asked?.Kind);
            Assert.Equal("Waiting for your answer...", status);
        });
    }

    // ─── Installing and updating ─────────────────────────────────────────────

    [Fact]
    public Task InstallConfirmationReadsTheAccountAfterRefreshingReleaseDetails()
    {
        var fake = new FakeXcodes { Account = new("old@example.test", null, HasSavedPassword: true) };
        var catalog = new FakeCatalog();
        return WithPageAsync(fake, async vm =>
        {
            var gate = new TaskCompletionSource<IReadOnlyList<XcodeCatalogRelease>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            catalog.Response = cancellation => gate.Task.WaitAsync(cancellation);
            string? confirmedAccount = null;
            vm.ConfirmInstall = (_, _, _, _) =>
            {
                confirmedAccount = vm.Account.Username;
                return Task.FromResult(false);
            };

            Task installing = vm.InstallCommand.ExecuteAsync(vm.Available[0]);
            fake.Account = new("new@example.test", null, HasSavedPassword: true);
            gate.SetResult(CatalogReleases());
            await installing;

            Assert.Equal("new@example.test", confirmedAccount);
            Assert.Empty(fake.TerminalCommands);
        }, catalog: catalog);
    }

    [Theory]
    [InlineData("new@example.test", false)]
    [InlineData(null, false)]
    [InlineData("old@example.test", true)]
    public Task InstallationStopsWhenTheAccountChangesDuringConfirmation(string? username, bool unreadable)
    {
        var fake = new FakeXcodes { Account = new("old@example.test", null, HasSavedPassword: true) };
        return WithPageAsync(fake, async vm =>
        {
            var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.ConfirmInstall = (_, _, _, _) =>
            {
                shown.SetResult();
                return confirmation.Task;
            };

            Task installing = vm.InstallCommand.ExecuteAsync(vm.Available[0]);
            await shown.Task;
            fake.Account = username is null ? XcodeAccountStatus.None : new(username, null, HasSavedPassword: true);
            if (unreadable)
                fake.AccountError = new XcodesException("The keychain is locked.");
            confirmation.SetResult(true);
            await installing;

            Assert.Empty(fake.TerminalCommands);
            AssertNotice(vm, InfoBarSeverity.Warning, "Apple ID changed",
                "Review the current Apple ID before trying the installation again.");
            Assert.Equal(unreadable ? null : username, vm.Account.Username);
            Assert.False(vm.Account.IsBusy);
            Assert.False(vm.IsBusy);
        });
    }

    [Fact]
    public Task InstallationDoesNotOfferConfirmationWhenTheAccountIsUnreadable()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            fake.AccountError = new XcodesException("The keychain is locked.");
            bool confirmed = false;
            vm.ConfirmInstall = (_, _, _, _) =>
            {
                confirmed = true;
                return Task.FromResult(true);
            };

            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);

            Assert.False(confirmed);
            Assert.Empty(fake.TerminalCommands);
            AssertNotice(vm, InfoBarSeverity.Warning, "Could not read the saved Apple ID",
                "Could not read the saved Apple ID: The keychain is locked.");
        });
    }

    [Fact]
    public Task InstallingAVersionSaysWhereItWent()
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
            vm.ConfirmInstall = (release, replacing, _, _) =>
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
            Assert.Equal(["26.5", "26.0"], AvailableVersions(vm));
            Assert.Null(vm.UpdateRelease);
        });
    }

    [Fact]
    public Task ASuccessWithoutTheInstalledVersionIsNotClaimedAsAnInstallation()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);

            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);

            AssertCommands(fake.TerminalCommands, "install 26.5");
            AssertNotice(vm, InfoBarSeverity.Warning, "Finished installing Xcode 26.5",
                "The installation finished, but this Xcode could not be confirmed in the installation directory. Refresh the list and check the folder before trying again.");
        });
    }

    [Theory]
    [InlineData("17F43", "Apple Silicon")]
    [InlineData("17F42", "Universal")]
    public Task APreviouslyInstalledDifferentBuildOrArchitectureDoesNotConfirmAnInstallation(
        string build, string architecture)
    {
        var fake = new FakeXcodes();
        fake.RespondInTerminal = (_, _, _, _) =>
        {
            fake.InstalledOutput = DefaultInstalled
                + $"26.5 ({build}) [{architecture}]\t/Applications/Xcode-26.5.app\n";
            return Task.FromResult(FakeXcodes.Success(""));
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);

            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);

            Assert.Equal("17F42", fake.LastInstalledRelease?.Build);
            AssertNotice(vm, InfoBarSeverity.Warning, "Finished installing Xcode 26.5",
                "The installation finished, but this Xcode could not be confirmed in the installation directory. Refresh the list and check the folder before trying again.");
        });
    }

    [Fact]
    public Task TheSameBuildAtAnotherPathDoesNotConfirmTheInstallation()
    {
        var fake = new FakeXcodes();
        fake.RespondInTerminal = (_, _, _, _) =>
        {
            fake.InstalledOutput = DefaultInstalled
                + "26.5 (17F42) [Apple Silicon]\t/Applications/Xcode-elsewhere.app\n";
            return Task.FromResult(FakeXcodes.Success(""));
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);

            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);

            AssertNotice(vm, InfoBarSeverity.Warning, "Finished installing Xcode 26.5",
                "The installation finished, but this Xcode could not be confirmed in the installation directory. Refresh the list and check the folder before trying again.");
        });
    }

    [Fact]
    public Task InstallWithSelectionChecksTheInstalledPathNotAnotherCopy()
    {
        XcodePreferences.SelectAfterInstall = true;
        var fake = new FakeXcodes();
        fake.RespondInTerminal = (_, _, _, _) =>
        {
            fake.InstalledOutput = DefaultInstalled
                + "26.5 (17F42) [Apple Silicon] (Selected)\t/Applications/Xcode-elsewhere.app\n"
                + "26.5 (17F42) [Apple Silicon]\t/Applications/Xcode-26.5.app\n";
            return Task.FromResult(FakeXcodes.Success(""));
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);

            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);

            AssertNotice(vm, InfoBarSeverity.Warning, "Xcode 26.5 was installed",
                "The installation finished, but UniGetUI could not confirm an installed copy was selected for command-line tools.");
        });
    }

    [Theory]
    [InlineData("17F43", XcodeArchitecture.AppleSilicon)]
    [InlineData("17F42", XcodeArchitecture.Universal)]
    public Task AReportedInstallationMustMatchTheRequestedBuildAndArchitecture(
        string build, XcodeArchitecture architecture)
    {
        var fake = new FakeXcodes();
        fake.RespondInTerminal = (_, _, _, _) =>
        {
            fake.InstallationResult = new XcodeInstallationResult("/Applications/Xcode-26.5.app", build, architecture);
            fake.InstalledOutput = DefaultInstalled
                + $"26.5 ({build}) [{XcodeText.Architecture(architecture)}]\t/Applications/Xcode-26.5.app\n";
            return Task.FromResult(FakeXcodes.Success(""));
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);

            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);

            AssertNotice(vm, InfoBarSeverity.Warning, "Finished installing Xcode 26.5",
                "The installation finished, but this Xcode could not be confirmed in the installation directory. Refresh the list and check the folder before trying again.");
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task InstallWithSelectionReportsWhetherAnInstalledCopyIsActive(bool selected)
    {
        XcodePreferences.SelectAfterInstall = true;
        var fake = new FakeXcodes();
        fake.RespondInTerminal = (_, _, _, _) =>
        {
            fake.InstalledOutput = selected
                ? "26.2 (17C52) [Apple Silicon]\t/Applications/Xcode-26.2.app\n"
                    + "26.4 (17E192) [Apple Silicon]\t/Applications/Xcode-26.4.app\n"
                    + "26.5 (17F42) [Apple Silicon] (Selected)\t/Applications/Xcode-26.5.app\n"
                : WithNewestInstalled;
            return Task.FromResult(FakeXcodes.Success(""));
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);

            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);

            AssertCommands(fake.TerminalCommands, "install 26.5 --select");
            if (selected)
                AssertNotice(vm, InfoBarSeverity.Success, "Xcode 26.5 was installed", "/Applications/Xcode-26.5.app");
            else
                AssertNotice(vm, InfoBarSeverity.Warning, "Xcode 26.5 was installed",
                    "The installation finished, but UniGetUI could not confirm an installed copy was selected for command-line tools.");
        });
    }

    [Fact]
    public Task InstallingFollowsTheChosenOptions()
    {
        XcodePreferences.SelectAfterInstall = true;
        XcodePreferences.SkipAdministratorSteps = true;
        XcodePreferences.DeleteArchive = true;
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);

            await vm.InstallCommand.ExecuteAsync(Assert.Single(vm.Available, item => item.Version == "26.0"));

            AssertCommands(
                fake.TerminalCommands,
                "install 26.0 --select --no-superuser --empty-trash");
        });
    }

    [Fact]
    public Task DownloadOnlyUsesTheSelectedReleaseAndOffersToRevealTheCachedArchive()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            XcodeBrowserItemViewModel selected = AvailableBuild(vm, "26.5", "17F42");
            XcodeRelease? confirmed = null;
            string? revealed = null;
            vm.ConfirmDownload = release =>
            {
                confirmed = release;
                return Task.FromResult(true);
            };
            vm.ShowArchiveInFinder = path =>
            {
                revealed = path;
                return Task.CompletedTask;
            };

            await vm.DownloadXcodeCommand.ExecuteAsync(selected);

            Assert.Same(selected.DownloadRelease, confirmed);
            Assert.Same(confirmed, fake.LastDownloadedRelease);
            Assert.Equal(["download 26.5"], fake.TerminalCommands);
            Assert.Equal(InfoBarSeverity.Success, vm.Notice.Severity);
            Assert.Equal("Xcode archive downloaded", vm.Notice.Title);
            Assert.Contains(fake.DownloadedArchivePath, vm.Notice.Message);
            Assert.Contains("checked during installation, not during this download", vm.Notice.Message);
            Assert.Equal("Show in Finder", vm.Notice.ActionButtonText);
            await Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(
                vm.Notice.ActionButtonCommand).ExecuteAsync(null);
            Assert.Equal(fake.DownloadedArchivePath, revealed);
            Assert.False(vm.HasOperation);

            vm.ShowArchiveInFinder = _ => Task.FromException(new IOException("The cached archive is missing."));
            await Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(
                vm.Notice.ActionButtonCommand).ExecuteAsync(null);
            AssertNotice(vm, InfoBarSeverity.Error, "Could not show the Xcode archive in Finder",
                "The cached archive is missing.");
        });
    }

    [Fact]
    public Task AnInstalledXcodeCanBeDownloadedOnlyWhenItsExactBuildAndArchitectureAreListed()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            XcodeBrowserItemViewModel installed = Assert.Single(vm.BrowserItems, item => item.Location == "/Applications/Xcode-26.4.app");
            Assert.True(installed.CanDownload);
            Assert.Equal(("17E192", XcodeArchitecture.AppleSilicon),
                (installed.DownloadRelease!.Build, installed.DownloadRelease.Architecture));
            vm.ConfirmDownload = _ => Task.FromResult(true);
            await vm.DownloadXcodeCommand.ExecuteAsync(installed);
            Assert.Equal(["download 26.4"], fake.TerminalCommands);
            Assert.Equal("17E192", fake.LastDownloadedRelease!.Build);

            fake.ListOutput = DefaultList.Replace("26.4 (17E192)", "26.4 (17E999)", StringComparison.Ordinal);
            await vm.ReloadAsync();
            installed = Assert.Single(vm.BrowserItems, item => item.Location == "/Applications/Xcode-26.4.app");
            Assert.False(installed.CanDownload);
            Assert.Null(installed.DownloadRelease);

            fake.ListOutput = DefaultList.Replace("26.4 (17E192) [Apple Silicon]",
                "26.4 (17E192) [Universal]", StringComparison.Ordinal);
            await vm.ReloadAsync();
            installed = Assert.Single(vm.BrowserItems, item => item.Location == "/Applications/Xcode-26.4.app");
            Assert.False(installed.CanDownload);
            Assert.Null(installed.DownloadRelease);
        });
    }

    [Fact]
    public Task DecliningDownloadConfirmationOrChangingTheAccountStartsNoDownload()
    {
        var fake = new FakeXcodes { Account = FakeXcodes.SignedIn("old@example.test") };
        return WithPageAsync(fake, async vm =>
        {
            XcodeBrowserItemViewModel selected = AvailableBuild(vm, "26.5", "17F42");
            vm.ConfirmDownload = _ => Task.FromResult(false);
            await vm.DownloadXcodeCommand.ExecuteAsync(selected);
            Assert.Empty(fake.TerminalCommands);

            vm.ConfirmDownload = _ =>
            {
                fake.Account = FakeXcodes.SignedIn("other@example.test");
                return Task.FromResult(true);
            };
            await vm.DownloadXcodeCommand.ExecuteAsync(selected);

            Assert.Empty(fake.TerminalCommands);
            Assert.Equal("Apple ID changed", vm.Notice.Title);
            Assert.Contains("current Apple ID", vm.Notice.Message);
        });
    }

    [Fact]
    public Task DownloadDoesNotUseAReleaseThatDisappearedAfterSelection()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            XcodeBrowserItemViewModel old = AvailableBuild(vm, "26.5", "17F42");
            fake.ListOutput = DefaultList
                .Replace("26.5 (17F42) [Apple Silicon]\n", "", StringComparison.Ordinal)
                .Replace("26.5 (17F43) [Apple Silicon]\n", "", StringComparison.Ordinal);
            await vm.ReloadAsync();
            vm.ConfirmDownload = _ => throw new InvalidOperationException("A missing release must not be confirmed.");

            await vm.DownloadXcodeCommand.ExecuteAsync(old);

            Assert.Empty(fake.TerminalCommands);
            Assert.Equal("Could not download Xcode 26.5", vm.Notice.Title);
            Assert.Contains("no longer listed", vm.Notice.Message);
        });
    }

    [Theory]
    [InlineData("build")]
    [InlineData("architecture")]
    [InlineData("unverified")]
    public Task InstallDoesNotConfirmAReleaseWhoseExactIdentityChanged(string changed)
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            AvailableXcodeItemViewModel old = Assert.Single(vm.Available, item => item.Release.Build == "17F42");
            string original = "26.5 (17F42) [Apple Silicon]\n";
            fake.ListOutput = DefaultList.Replace(original, changed switch
            {
                "build" => "26.5 (17F44) [Apple Silicon]\n",
                "architecture" => "26.5 (17F42) [Universal]\n",
                _ => "",
            }, StringComparison.Ordinal);
            if (changed == "unverified")
                fake.ExtraReleases = [old.Release with { IsIdentityUnverified = true }];
            await vm.ReloadAsync();
            Assert.Contains(vm.Available, item => item.Version == "26.5");
            bool confirmed = false;
            vm.ConfirmInstall = (_, _, _, _) =>
            {
                confirmed = true;
                return Task.FromResult(false);
            };

            await vm.InstallCommand.ExecuteAsync(old);

            Assert.False(confirmed);
            Assert.Empty(fake.TerminalCommands);
            AssertNotice(vm, InfoBarSeverity.Error, "Could not install Xcode 26.5",
                "This build is no longer listed as available. Check for updates before trying again.");
        });
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public Task DownloadAccessRecoveryRetriesTheDownloadRatherThanInstalling(int status)
    {
        var fake = new FakeXcodes();
        fake.RespondInTerminal = (_, _, output, _) =>
        {
            output?.Invoke(new XcodesOutputLine($"HTTP {status}: access denied"));
            return Task.FromResult(FakeXcodes.Failure($"HTTP {status}: access denied"));
        };
        return WithPageAsync(fake, async vm =>
        {
            int confirmations = 0;
            vm.ConfirmDownload = _ =>
            {
                confirmations++;
                return Task.FromResult(true);
            };
            XcodeBrowserItemViewModel selected = AvailableBuild(vm, "26.5", "17F42");

            await vm.DownloadXcodeCommand.ExecuteAsync(selected);
            Assert.True(vm.HasAccessRecovery);
            Assert.Contains($"HTTP {status}", vm.OperationLog);
            Assert.Equal("Could not download Xcode 26.5", vm.Notice.Title);

            fake.RespondInTerminal = (_, _, _, _) => Task.FromResult(FakeXcodes.Success(""));
            await vm.RetryInstallationCommand.ExecuteAsync(null);

            Assert.Equal(["download 26.5", "download 26.5"], fake.TerminalCommands);
            Assert.Equal(2, confirmations);
            Assert.False(vm.HasAccessRecovery);
            Assert.Equal("Xcode archive downloaded", vm.Notice.Title);
        });
    }

    [Fact]
    public Task DownloadShowsProgressAndCancellationWithoutStartingAnInstallation()
    {
        var fake = new FakeXcodes();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.RespondInTerminal = async (_, _, output, cancellation) =>
        {
            output?.Invoke(new XcodesOutputLine("(1/1) Downloading Xcode 26.5: 42%"));
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return FakeXcodes.Success("");
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmDownload = _ => Task.FromResult(true);
            XcodeBrowserItemViewModel selected = AvailableBuild(vm, "26.5", "17F42");

            Task downloading = vm.DownloadXcodeCommand.ExecuteAsync(selected);
            await started.Task;
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsOperationRunning);
            Assert.True(selected.IsOperationTarget);
            Assert.Equal(42, vm.OperationProgress);
            Assert.Equal("Step 1 of 1", vm.OperationStep);
            Assert.False(vm.Account.CanSignOut);
            Assert.False(vm.Account.SignOutCommand.CanExecute(null));
            await vm.Account.SignOutCommand.ExecuteAsync(null);
            Assert.DoesNotContain("signout", fake.Commands);

            vm.CancelOperation();
            await downloading;

            Assert.False(vm.IsOperationRunning);
            Assert.False(vm.HasAccessRecovery);
            Assert.Equal("The download of Xcode 26.5 was cancelled", vm.Notice.Title);
            Assert.Contains("partial archive", vm.Notice.Message);
            Assert.Equal(["download 26.5"], fake.TerminalCommands);
        });
    }

    [Fact]
    public Task DownloadButtonIsBoundForAvailableAndMatchingInstalledVersions() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes
        {
            ExtraReleases = [new XcodeRelease("27.0 Beta", null, XcodeArchitecture.Unspecified, false, false) { IsIdentityUnverified = true }],
        };
        var vm = new XcodeViewModel(fake.Service) { ConfirmDownload = _ => Task.FromResult(true) };
        var view = new XcodePage(vm);
        var window = new Window { Width = 1100, Height = 1100, Content = view };
        try
        {
            await vm.InitializeAsync();
            vm.SelectedBrowserItem = AvailableBuild(vm, "26.5", "17F42");
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Button download = view.FindControl<Button>("DownloadSelectedXcode")!;
            Assert.True(download.IsEffectivelyVisible);
            Assert.Same(vm.DownloadXcodeCommand, download.Command);
            Assert.Same(vm.SelectedBrowserItem, download.CommandParameter);
            vm.IsBusy = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(download.IsEnabled);
            vm.IsBusy = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(download.IsEnabled);

            vm.SelectedBrowserItem = Assert.Single(vm.BrowserItems, item => item.Location == "/Applications/Xcode-26.4.app");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(download.IsEffectivelyVisible);
            Assert.Same(vm.SelectedBrowserItem, download.CommandParameter);
            download.Command!.Execute(download.CommandParameter);
            await vm.DownloadXcodeCommand.ExecutionTask!;
            Assert.Equal(["download 26.4"], fake.TerminalCommands);

            vm.SelectedBrowserItem = Assert.Single(vm.BrowserItems, item => item.Version == "27.0 Beta");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(download.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task RevealingADownloadedArchiveUsesFinderAndReportsMissingFiles() => DispatchAsync(async () =>
    {
        Directory.CreateDirectory(_testRoot);
        string path = Path.Combine(_testRoot, "Xcode.xip");
        await File.WriteAllTextAsync(path, "fixture");
        ProcessStartInfo? captured = null;

        await XcodeViewModel.ShowArchiveInFinderCoreAsync(path, start =>
        {
            captured = start;
            return Task.FromResult(0);
        });

        Assert.NotNull(captured);
        Assert.Equal("/usr/bin/open", captured.FileName);
        Assert.Equal(["-R", path], captured.ArgumentList);
        File.Delete(path);
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            XcodeViewModel.ShowArchiveInFinderCoreAsync(path, _ => Task.FromResult(0)));
    });

    [Fact]
    public Task DecliningTheConfirmationInstallsNothing()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(false);
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
    public Task WithoutAWindowNothingIsInstalledOrRevealed()
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
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);

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
    public Task ASuccessfulInstallDoesNotHideAReleaseListFailure()
    {
        var fake = new FakeXcodes();
        bool broken = false;
        fake.Override(command => command == "list" && broken ? FakeXcodes.Failure("saved list unavailable") : null);
        fake.RespondInTerminal = (_, _, _, _) =>
        {
            fake.InstalledOutput = WithNewestInstalled;
            broken = true;
            return Task.FromResult(FakeXcodes.Success(""));
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);

            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);

            Assert.False(vm.HasOperation);
            Assert.False(vm.HasLoaded);
            Assert.True(vm.HasVersionLoadError);
            Assert.Equal(["26.5", "26.4", "26.2"], InstalledVersions(vm));
            AssertNotice(vm, InfoBarSeverity.Warning, "Finished installing Xcode 26.5",
                "The Xcode versions could not be refreshed: saved list unavailable");

            broken = false;
            await vm.ReloadAsync();
            Assert.True(vm.HasLoaded);
            Assert.False(vm.Notice.IsOpen);
        });
    }

    [Fact]
    public Task AFailedInstallKeepsItsOriginalErrorWhenTheReleaseListAlsoFails()
    {
        var fake = new FakeXcodes();
        fake.RespondInTerminal = (_, _, output, _) =>
        {
            fake.Override(command => command == "list" ? FakeXcodes.Failure("saved list unavailable") : null);
            output?.Invoke(new XcodesOutputLine("HTTP 403: access denied"));
            return Task.FromResult(FakeXcodes.Failure("HTTP 403: access denied"));
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);

            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);

            Assert.True(vm.IsOperationFinished);
            Assert.True(vm.HasAccessRecovery);
            Assert.Contains("HTTP 403", vm.OperationLog);
            Assert.Equal("Could not install Xcode 26.5", vm.Notice.Title);
            Assert.Equal(InfoBarSeverity.Error, vm.Notice.Severity);
            Assert.Contains("HTTP 403", vm.Notice.Message);
            Assert.Contains("saved list unavailable", vm.Notice.Message);
            Assert.False(vm.CanRetryInstallation);

            await vm.RetryInstallationCommand.ExecuteAsync(null);
            Assert.Single(fake.TerminalCommands);
            Assert.Contains("HTTP 403", vm.Notice.Message);
        });
    }

    [Fact]
    public Task AccessRecoveryKeepsAnEarlierDiagnosticEvenWhenTheOutputQueueOverflows()
    {
        var fake = new FakeXcodes();
        fake.RespondInTerminal = (_, _, output, _) =>
        {
            output!(new XcodesOutputLine("HTTP 403: access denied"));
            for (int i = 0; i < 500; i++)
                output(new XcodesOutputLine($"later-output-{i:000}"));
            return Task.FromResult(FakeXcodes.Failure("Download failed"));
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);

            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);

            Assert.True(vm.HasAccessRecovery);
            Assert.Contains("HTTP 403: access denied", vm.OperationLog);
            Assert.Contains("later-output-499", vm.OperationLog);
            Assert.Contains("[Earlier output was omitted.]", vm.OperationLog);

            fake.RespondInTerminal = (_, _, output, _) =>
            {
                output!(new XcodesOutputLine("Unrelated failure"));
                return Task.FromResult(FakeXcodes.Failure("Unrelated failure"));
            };
            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);
            Assert.False(vm.HasAccessRecovery);
            Assert.DoesNotContain("HTTP 403", vm.OperationLog);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task RetryingDoesNotInstallAVersionThatIsNoLongerAvailable(bool installedSinceFailure)
    {
        var fake = new FakeXcodes();
        bool broken = false;
        fake.Override(command => command == "list" && broken ? FakeXcodes.Failure("saved list unavailable") : null);
        fake.RespondInTerminal = (_, _, _, _) =>
        {
            broken = true;
            return Task.FromResult(FakeXcodes.Failure("HTTP 403: access denied"));
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);
            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);
            Assert.True(vm.HasAccessRecovery);
            Assert.False(vm.CanRetryInstallation);
            broken = false;
            fake.ListOutput = installedSinceFailure
                ? OnlyInstalledList + "26.5 (17F42) [Apple Silicon] (Installed)\n"
                : OnlyInstalledList;
            if (installedSinceFailure)
                fake.InstalledOutput = WithNewestInstalled;

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            Assert.True(vm.CanRetryInstallation);
            await vm.RetryInstallationCommand.ExecuteAsync(null);

            Assert.Single(fake.TerminalCommands);
            AssertNotice(vm, InfoBarSeverity.Error, "Could not install Xcode 26.5",
                "This build is no longer listed as available. Check for updates before trying again.");
        });
    }

    [Fact]
    public Task ASuccessfulSelectionDoesNotHideAReleaseListFailure()
    {
        var fake = new FakeXcodes();
        fake.RespondInTerminal = (_, _, _, _) =>
        {
            fake.InstalledOutput = SecondSelected;
            fake.Override(command => command == "list" ? FakeXcodes.Failure("saved list unavailable") : null);
            return Task.FromResult(FakeXcodes.Success(""));
        };
        return WithPageAsync(fake, async vm =>
        {
            await vm.SelectCommand.ExecuteAsync(vm.Installed[1]);

            Assert.False(vm.HasLoaded);
            Assert.True(vm.HasVersionLoadError);
            Assert.True(Assert.Single(vm.Installed, item => item.Version == "26.2").IsSelected);
            AssertNotice(vm, InfoBarSeverity.Warning, "Finished selecting Xcode 26.2",
                "The Xcode versions could not be refreshed: saved list unavailable");
        });
    }

    [Fact]
    public Task AccessFailureKeepsTheLogAndOffersExplicitRecovery()
    {
        var fake = new FakeXcodes { Account = new("wrong@example.test", null, HasSavedPassword: true) };
        int attempts = 0;
        fake.RespondInTerminal = (_, _, output, _) =>
        {
            attempts++;
            output?.Invoke(new XcodesOutputLine("HTTP 403: access denied"));
            if (attempts == 1)
                return Task.FromResult(FakeXcodes.Failure("HTTP 403: access denied"));
            fake.InstalledOutput = WithNewestInstalled;
            return Task.FromResult(FakeXcodes.Success(""));
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);

            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);

            Assert.True(vm.HasAccessRecovery);
            Assert.Contains("HTTP 403", vm.OperationLog);
            Assert.Contains("HTTP 403", vm.Notice.Message);
            Assert.Equal(1, attempts);
            await vm.Account.SignOutCommand.ExecuteAsync(null);
            Assert.True(vm.HasAccessRecovery);
            Assert.Null(vm.Account.Username);
            Assert.Equal(1, attempts);

            await vm.RetryInstallationCommand.ExecuteAsync(null);

            Assert.Equal(2, attempts);
            Assert.False(vm.HasAccessRecovery);
            Assert.Equal("Xcode 26.5 was installed", vm.Notice.Title);
        });
    }

    [Fact]
    public Task UnrelatedInstallFailuresDoNotOfferAccountRecovery()
    {
        var fake = new FakeXcodes();
        fake.RespondInTerminal = (_, _, _, _) => Task.FromResult(FakeXcodes.Failure("The archive is damaged."));
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);

            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);

            Assert.False(vm.HasAccessRecovery);
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
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);

            Task installing = vm.InstallCommand.ExecuteAsync(vm.Available[0]);
            await started.Task;
            Assert.Equal("Installing Xcode 26.5", vm.OperationTitle);

            vm.CancelOperation();
            await installing;

            Assert.False(vm.HasOperation);
            AssertNotice(vm, InfoBarSeverity.Informational, "The installation of Xcode 26.5 was cancelled",
                "An Xcode folder may have been created. Check it before retrying.");
        });
    }

    [Fact]
    public Task AStillRunningInstallationBlocksQuitAfterCancellation()
    {
        var fake = new FakeXcodes();
        var started = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        fake.RespondInTerminal = async (_, _, _, _) =>
        {
            started.SetResult();
            await finish.Task;
            return FakeXcodes.Success("");
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);
            Task installing = vm.InstallCommand.ExecuteAsync(vm.Available[0]);
            await started.Task;

            vm.CancelOperation();
            Assert.False(vm.CanQuitAfterCancellation);

            finish.SetResult();
            await installing;
            Assert.True(vm.CanQuitAfterCancellation);
        });
    }

    [Fact]
    public Task CancelButtonRequiresConfirmation()
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
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);
            vm.ConfirmCancel = () => Task.FromResult(false);
            Task installing = vm.InstallCommand.ExecuteAsync(vm.Available[0]);
            await started.Task;

            await vm.RequestCancelOperationCommand.ExecuteAsync(null);
            Assert.True(vm.IsOperationRunning);

            vm.ConfirmCancel = () => Task.FromResult(true);
            await vm.RequestCancelOperationCommand.ExecuteAsync(null);
            await installing;
            Assert.False(vm.IsOperationRunning);
            Assert.Equal("The installation of Xcode 26.5 was cancelled", vm.Notice.Title);
        });
    }

    [Fact]
    public Task UpdatingInstallsTheNewestReleaseNextToTheOldOneAndOffersToShowIt()
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
            vm.ConfirmInstall = (release, replacing, _, _) =>
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
            Assert.Equal("Show Xcode 26.4 in Finder", vm.Notice.ActionButtonText);
            Assert.NotNull(vm.Notice.ActionButtonCommand);
            string? revealed = null;
            vm.ShowPathInFinder = path =>
            {
                revealed = path;
                return Task.CompletedTask;
            };
            await Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(
                vm.Notice.ActionButtonCommand).ExecuteAsync(null);
            Assert.Equal("/Applications/Xcode-26.4.app", revealed);
            Assert.Equal(["26.5", "26.4", "26.2"], InstalledVersions(vm));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task UpdateRecoveryTargetsTheOriginalInstalledPathWhenCopiesAreReordered(bool originalRemains)
    {
        const string original = "26.4 (17E192) [Apple Silicon] (Selected)\t/Applications/Xcode-original.app\n";
        const string copy = "26.4 (17E192) [Apple Silicon]\t/Applications/Xcode-copy.app\n";
        const string updated = "26.5 (17F42) [Apple Silicon]\t/Applications/Xcode-26.5.app\n";
        var fake = new FakeXcodes { InstalledOutput = original + copy };
        fake.RespondInTerminal = (_, _, _, _) =>
        {
            fake.InstalledOutput = copy + (originalRemains ? original : "") + updated;
            return Task.FromResult(FakeXcodes.Success(""));
        };
        return WithPageAsync(fake, async vm =>
        {
            InstalledXcode? replacing = null;
            string? revealed = null;
            vm.ConfirmInstall = (_, old, _, _) =>
            {
                replacing = old;
                return Task.FromResult(true);
            };
            vm.ShowPathInFinder = path =>
            {
                revealed = path;
                return Task.CompletedTask;
            };

            await vm.UpdateXcodeCommand.ExecuteAsync(null);

            Assert.Equal("/Applications/Xcode-original.app", replacing?.Path);
            if (originalRemains)
            {
                Assert.Equal("Xcode 26.4 is still installed.", vm.Notice.Message);
                Assert.NotNull(vm.Notice.ActionButtonCommand);
                await Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(
                    vm.Notice.ActionButtonCommand).ExecuteAsync(null);
                Assert.Equal("/Applications/Xcode-original.app", revealed);
            }
            else
            {
                Assert.Equal("/Applications/Xcode-26.5.app", vm.Notice.Message);
                Assert.Null(vm.Notice.ActionButtonCommand);
                Assert.Null(revealed);
            }
        });
    }

    [Theory]
    [InlineData("26.4 (17E192) [Apple Silicon]", "26.4 (17E192) [Apple Silicon]", true)]
    [InlineData("26.4 (17E192) [Apple Silicon]", "26.4 (17E193) [Apple Silicon]", false)]
    [InlineData("26.4 (17E192) [Apple Silicon]", "26.4 (17e192) [Apple Silicon]", false)]
    [InlineData("26.4 (17E192) [Apple Silicon]", "26.4 (17E192) [Universal]", false)]
    [InlineData("26.4 (17E192)", "26.4 (17E192)", true)]
    [InlineData("26.4 (17E192) [Apple Silicon]", "26.4 [Apple Silicon]", false)]
    [InlineData("26.4 [Apple Silicon]", "26.4 [Apple Silicon]", false)]
    [InlineData("26.4 [Apple Silicon]", "26.4 (17E192) [Apple Silicon]", false)]
    public Task UpdateRecoveryRequiresTheOriginalBuildAndArchitecture(
        string original, string afterUpdate, bool sameIdentity)
    {
        const string path = "/Applications/Xcode-original.app";
        const string updated = "26.5 (17F42) [Apple Silicon]\t/Applications/Xcode-26.5.app\n";
        var fake = new FakeXcodes { InstalledOutput = $"{original} (Selected)\t{path}\n" };
        fake.RespondInTerminal = (_, _, _, _) =>
        {
            fake.InstalledOutput = $"{afterUpdate} (Selected)\t{path}\n{updated}";
            return Task.FromResult(FakeXcodes.Success(""));
        };
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);

            await vm.UpdateXcodeCommand.ExecuteAsync(null);

            if (sameIdentity)
            {
                Assert.Equal("Xcode 26.4 is still installed.", vm.Notice.Message);
                Assert.NotNull(vm.Notice.ActionButtonCommand);
            }
            else
            {
                Assert.Equal("/Applications/Xcode-26.5.app", vm.Notice.Message);
                Assert.Null(vm.Notice.ActionButtonCommand);
            }
        });
    }

    [Fact]
    public Task WithNothingNewerThereIsNothingToUpdate()
    {
        var fake = new FakeXcodes { ListOutput = OnlyInstalledList };
        return WithPageAsync(fake, async vm =>
        {
            var asked = false;
            vm.ConfirmInstall = (_, _, _, _) =>
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
    public Task UninstallingShowsTheSelectedCopyInFinderWithoutRunningAnUninstallCommand()
    {
        var fake = new FakeXcodes
        {
            InstalledOutput = DefaultInstalled + "26.2 (17C52) [Apple Silicon]\t/Applications/Xcode-copy.app\n",
        };
        return WithPageAsync(fake, async vm =>
        {
            InstalledXcodeItemViewModel? asked = null;
            string? revealed = null;
            vm.ConfirmUninstall = item =>
            {
                asked = item;
                return Task.FromResult(true);
            };
            vm.ShowPathInFinder = path =>
            {
                revealed = path;
                return Task.CompletedTask;
            };
            fake.Commands.Clear();

            await vm.UninstallCommand.ExecuteAsync(vm.Installed.Single(item => item.Location.EndsWith("Xcode-copy.app")));

            Assert.Equal("26.2", asked?.Version);
            Assert.Equal("/Applications/Xcode-copy.app", revealed);
            Assert.Empty(fake.Commands);
            Assert.Empty(fake.TerminalCommands);
            Assert.Equal(3, vm.Installed.Count);
        });
    }

    [Fact]
    public Task DecliningTheConfirmationDoesNotOpenFinder()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            bool revealed = false;
            vm.ConfirmUninstall = _ => Task.FromResult(false);
            vm.ShowPathInFinder = _ =>
            {
                revealed = true;
                return Task.CompletedTask;
            };
            InstalledXcodeItemViewModel item = vm.Installed[1];
            fake.Commands.Clear();

            await vm.UninstallCommand.ExecuteAsync(item);
            await vm.UninstallCommand.ExecuteAsync(null);

            AssertCommands(fake.Commands);
            Assert.False(revealed);
            Assert.False(item.IsBusy);
            Assert.False(vm.Notice.IsOpen);
            Assert.Equal(["26.4", "26.2"], InstalledVersions(vm));
        });
    }

    [Fact]
    public Task AFailedFinderRevealIsReported()
    {
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            vm.ConfirmUninstall = _ => Task.FromResult(true);
            vm.ShowPathInFinder = _ => throw new InvalidOperationException("Finder failed");
            InstalledXcodeItemViewModel item = vm.Installed[1];

            await vm.UninstallCommand.ExecuteAsync(item);

            Assert.False(item.IsBusy);
            Assert.True(vm.Notice.IsOpen);
            Assert.Equal(InfoBarSeverity.Error, vm.Notice.Severity);
            Assert.Equal("Could not show the Xcode in Finder", vm.Notice.Title);
            Assert.Contains("Finder failed", vm.Notice.Message);
            Assert.Equal(["26.4", "26.2"], InstalledVersions(vm));
        });
    }

    [Fact]
    public Task RevealUsesTheExactPathAndReportsAsynchronousFailures()
    {
        var fake = new FakeXcodes
        {
            InstalledOutput = DefaultInstalled + "26.2 (17C52) [Apple Silicon]\t/Applications/Xcode-copy.app\n",
        };
        return WithPageAsync(fake, async vm =>
        {
            string? revealed = null;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.ShowPathInFinder = path =>
            {
                revealed = path;
                return gate.Task;
            };
            InstalledXcodeItemViewModel item = vm.Installed.Single(xcode => xcode.Location == "/Applications/Xcode-copy.app");

            Task show = vm.RevealInFinderCommand.ExecuteAsync(item);
            try
            {
                Assert.Equal("/Applications/Xcode-copy.app", revealed);
                Assert.False(show.IsCompleted);
            }
            finally
            {
                gate.TrySetResult();
            }
            await show;

            vm.ShowPathInFinder = _ => Task.FromException(new InvalidOperationException("Finder failed"));
            await vm.RevealInFinderCommand.ExecuteAsync(item);
            AssertNotice(vm, InfoBarSeverity.Error, "Could not show the Xcode in Finder", "Finder failed");
        });
    }

    [Fact]
    public Task UninstallWaitsForFinderBeforeReleasingTheOperationLock() => WithPageAsync(new FakeXcodes(), async vm =>
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.ConfirmUninstall = _ => Task.FromResult(true);
        vm.ShowPathInFinder = _ => gate.Task;

        Task uninstall = vm.UninstallCommand.ExecuteAsync(vm.Installed[1]);
        try
        {
            Assert.True(vm.IsBusy);
            Assert.False(vm.CanStartOperation);
            Assert.False(uninstall.IsCompleted);
        }
        finally
        {
            gate.TrySetResult();
        }
        await uninstall;
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanStartOperation);
    });

    [Fact]
    public Task FinderLaunchValidatesThePathArgumentsAndExitCode() => DispatchAsync(async () =>
    {
        string path = Path.Combine(_testRoot, "Xcode & Test.app");
        Directory.CreateDirectory(path);
        ProcessStartInfo? started = null;
        await XcodeViewModel.ShowPathInFinderCoreAsync(path, info =>
        {
            started = info;
            return Task.FromResult(0);
        });

        Assert.NotNull(started);
        Assert.Equal("/usr/bin/open", started.FileName);
        Assert.Equal(["-R", path], started.ArgumentList);
        Assert.False(started.UseShellExecute);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            XcodeViewModel.ShowPathInFinderCoreAsync(path, _ => Task.FromResult(1)));
        Assert.Equal("Finder could not be opened.", failure.Message);

        bool ran = false;
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            XcodeViewModel.ShowPathInFinderCoreAsync(path + ".missing", _ =>
            {
                ran = true;
                return Task.FromResult(0);
            }));
        Assert.False(ran);
    });

    [Fact]
    public async Task FinderProcessReportsItsExitCodeAndTimeout()
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        start.ArgumentList.Add("exit 7");
        Assert.Equal(7, await XcodeViewModel.RunFinderProcessAsync(start, TimeSpan.FromSeconds(10)));

        TimeoutException timeout = await Assert.ThrowsAsync<TimeoutException>(() =>
            XcodeViewModel.WaitForFinderExitAsync(
                cancellation => Task.Delay(Timeout.InfiniteTimeSpan, cancellation), TimeSpan.FromMilliseconds(20)));
        Assert.Equal("Could not confirm that Finder opened. It may still open.", timeout.Message);
    }

    [Fact]
    public Task CopyPathUsesTheSelectedInstalledPath() => WithPageAsync(new FakeXcodes(), async vm =>
    {
        string? copied = null;
        vm.CopyText = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };

        await vm.CopyPathCommand.ExecuteAsync(vm.Installed[1]);

        Assert.Equal("/Applications/Xcode-26.2.app", copied);
        Assert.Equal("Path copied", vm.Notice.Title);
    });

    [Fact]
    public Task AClipboardFailureIsReported() => WithPageAsync(new FakeXcodes(), async vm =>
    {
        vm.CopyText = _ => throw new InvalidOperationException("clipboard failed");

        await vm.CopyPathCommand.ExecuteAsync(vm.Installed[1]);

        Assert.Equal(InfoBarSeverity.Error, vm.Notice.Severity);
        Assert.Contains("clipboard failed", vm.Notice.Message);
    });

    [Fact]
    public Task OpeningAnInstalledXcodeUsesItsExactPathAndReportsFailures() => WithPageAsync(new FakeXcodes(), async vm =>
    {
        string missingPath = Path.Combine(_testRoot, "Missing.app");
        var missing = new InstalledXcodeItemViewModel(
            new InstalledXcode("26.4", "17E192", XcodeArchitecture.AppleSilicon, false, missingPath));
        await vm.OpenCommand.ExecuteAsync(missing);
        AssertNotice(vm, InfoBarSeverity.Error, "Could not open Xcode",
            $"The Xcode at {missingPath} is no longer available.");

        string? opened = null;
        vm.OpenXcode = path =>
        {
            opened = path;
            return Task.CompletedTask;
        };
        InstalledXcodeItemViewModel selected = vm.Installed[1];

        await vm.OpenCommand.ExecuteAsync(selected);
        Assert.Equal(selected.Location, opened);

        vm.OpenXcode = _ => throw new IOException("Xcode is unavailable");
        await vm.OpenCommand.ExecuteAsync(selected);
        AssertNotice(vm, InfoBarSeverity.Error, "Could not open Xcode", "Xcode is unavailable");
    });

    // ─── Simulator runtimes ──────────────────────────────────────────────────

    [Fact]
    public Task SimulatorMappingsUseTheSelectedXcodesExactSDKBuildAndPreserveFilters()
    {
        var sdk = new XcodeSdkIdentity("iOS", "26.4", "23E100");
        var catalog = new FakeCatalog
        {
            Releases = [.. CatalogReleases().Select(release => release.Version == "26.4"
                ? release with { SDKIdentities = [sdk] } : release)],
        };
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        runtimes.SDKMappings = [new(sdk, "23E100")];
        runtimes.Installed =
        [
            .. runtimes.Installed,
            runtimes.Installed[0] with { Build = "23E999", Path = "/fake/other-build.simruntime" },
            runtimes.Installed[0] with { Version = "26.5", Path = "/fake/other-version.simruntime" },
            runtimes.Installed[0] with { Architecture = XcodeArchitecture.Unspecified, Path = "/fake/unknown.simruntime" },
        ];
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            vm.SelectedBrowserItem = Assert.Single(vm.BrowserItems, item => item.Version == "26.4" && item.IsInstalled);
            vm.Runtimes.SearchText = "26.4";
            vm.Runtimes.PlatformFilterIndex = 1;
            int accountReads = fake.AccountReadCount;
            fake.Commands.Clear();

            await vm.OpenMappedSimulatorRuntimesCommand.ExecuteAsync(null);

            Assert.True(vm.ShowSimulatorRuntimes);
            Assert.True(vm.Runtimes.HasMappingContext);
            Assert.Contains("/Applications/Xcode-26.4.app", vm.Runtimes.MappingStatus);
            Assert.Contains("not necessarily incompatible", vm.Runtimes.MappingStatus);
            Assert.Equal(4, vm.Runtimes.Items.Count);
            Assert.All(vm.Runtimes.Items, item => Assert.Equal("23E100", item.Runtime.Build));
            Assert.DoesNotContain(vm.Runtimes.Items, item => item.Runtime.Architecture == XcodeArchitecture.Unspecified);
            Assert.Contains(vm.Runtimes.Items, item => item.CanInstall);
            Assert.Equal(accountReads, fake.AccountReadCount);
            Assert.Empty(fake.Commands);

            runtimes.CatalogError = new HttpRequestException("offline");
            await vm.RefreshSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.Contains("out of date", vm.Runtimes.CatalogStatus);
            Assert.Equal(4, vm.Runtimes.Items.Count);
            Assert.DoesNotContain(vm.Runtimes.Items, item => item.CanInstall || item.CanDownload);

            vm.Runtimes.ShowAllRuntimesCommand.Execute(null);
            Assert.False(vm.Runtimes.HasMappingContext);
            Assert.Equal("26.4", vm.Runtimes.SearchText);
            Assert.Equal(1, vm.Runtimes.PlatformFilterIndex);
            Assert.Contains(vm.Runtimes.Items, item => item.Runtime.Path == "/fake/unknown.simruntime");
        }, catalog: catalog, runtimes: runtimes);
    }

    [Fact]
    public Task SimulatorMappingsExplainMissingMetadataAndRetainReleaseStaleness()
    {
        var sdk = new XcodeSdkIdentity("iOS", "26.4", "23E100");
        var catalog = new FakeCatalog
        {
            Releases = [.. CatalogReleases().Select(release => release.Version == "26.4"
                ? release with { SDKIdentities = [sdk] } : release)],
        };
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            catalog.Error = new HttpRequestException("offline");
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            vm.SelectedBrowserItem = Assert.Single(vm.BrowserItems, item => item.Version == "26.4" && item.IsInstalled);

            await vm.OpenMappedSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.Empty(vm.Runtimes.Items);
            Assert.Contains("mappings are unavailable", vm.Runtimes.MappingStatus);
            Assert.Contains("does not mean it is incompatible", vm.Runtimes.MappingStatus);
            Assert.Contains("out of date", vm.Runtimes.MappingCatalogStatus);

            vm.ShowXcodeVersionsCommand.Execute(null);
            vm.SelectedBrowserItem = Assert.Single(vm.BrowserItems, item => item.Version == "26.2" && item.IsInstalled);
            await vm.OpenMappedSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.Empty(vm.Runtimes.Items);
            Assert.Contains("SDK build information", vm.Runtimes.MappingStatus);
            Assert.Contains("does not mean it is incompatible", vm.Runtimes.MappingStatus);

            vm.ShowXcodeVersionsCommand.Execute(null);
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.False(vm.Runtimes.HasMappingContext);
            Assert.NotEmpty(vm.Runtimes.Items);
        }, catalog: catalog, runtimes: runtimes);
    }

    [Fact]
    public Task TheSelectedXcodeMappingControlsRenderAndReturnToAllRuntimes() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var sdk = new XcodeSdkIdentity("iOS", "26.4", "23E100");
        var catalog = new FakeCatalog
        {
            Releases = [.. CatalogReleases().Select(release => release.Version == "26.4"
                ? release with { SDKIdentities = [sdk] } : release)],
        };
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        runtimes.SDKMappings = [new(sdk, "23E100")];
        var vm = new XcodeViewModel(new FakeXcodes().Service, catalog: catalog, runtimes: runtimes);
        var view = new XcodePage(vm);
        var window = new Window { Width = 1100, Height = 900, Content = view };
        try
        {
            await vm.InitializeAsync();
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            vm.SelectedBrowserItem = Assert.Single(vm.BrowserItems, item => item.Version == "26.4" && item.IsInstalled);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var open = view.FindControl<Button>("OpenMappedSimulatorRuntimes")!;
            Assert.True(open.IsEffectivelyVisible);
            Assert.Same(vm.OpenMappedSimulatorRuntimesCommand, open.Command);
            open.Command!.Execute(open.CommandParameter);
            await vm.OpenMappedSimulatorRuntimesCommand.ExecutionTask!;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var status = view.FindControl<TextBlock>("RuntimeMappingStatus")!;
            Assert.True(status.IsEffectivelyVisible);
            Assert.Contains("Xcode 26.4", status.Text);
            Assert.Equal(4, vm.Runtimes.Items.Count);
            var showAll = view.FindControl<Button>("ShowAllSimulatorRuntimes")!;
            Assert.True(showAll.IsEffectivelyVisible);
            Assert.Same(vm.Runtimes.ShowAllRuntimesCommand, showAll.Command);
            showAll.Command!.Execute(showAll.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.False(status.IsEffectivelyVisible);
            Assert.True(vm.Runtimes.Items.Count > 4);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task SavedSimulatorCatalogIsInformationalUntilRefreshed()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimesWithLegacy();
        var sdk = new XcodeSdkIdentity("iOS", "26.4", "23E100");
        runtimes.SDKMappings = [new(sdk, "23E100")];
        var savedAt = new DateTime(2026, 3, 10, 15, 30, 0, DateTimeKind.Utc);
        runtimes.Saved = new XcodeSavedSimulatorRuntimeCatalog(
            new XcodeSimulatorRuntimeCatalog(runtimes.Catalog, runtimes.SDKMappings), savedAt);
        runtimes.CatalogError = new HttpRequestException("catalog offline");
        var installer = new FakeSimulatorInstaller();
        var downloader = new FakePublicSimulatorDownloader();
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);

            Assert.StartsWith("Saved Apple simulator catalog may be out of date; last saved ", vm.Runtimes.CatalogStatus);
            Assert.Contains(savedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), vm.Runtimes.CatalogStatus);
            Assert.Contains("catalog offline", vm.Runtimes.CatalogStatus);
            Assert.Equal(9, vm.Runtimes.Items.Count);
            Assert.DoesNotContain(vm.Runtimes.Items, item => item.CanInstall || item.CanDownload);
            Assert.Null(Assert.Single(vm.Runtimes.Items,
                item => item.Runtime.Path == "/Library/Developer/Runtime/iOS.simruntime").IsPrerelease);

            XcodeSimulatorRuntimeItemViewModel modern = Assert.Single(vm.Runtimes.Items, item =>
                item.Runtime.Origin == XcodeSimulatorRuntimeOrigin.AppleCatalog
                && item.Runtime.Build == "23E100" && item.Runtime.Architecture == XcodeArchitecture.AppleSilicon);
            XcodeSimulatorRuntimeItemViewModel legacy = Assert.Single(vm.Runtimes.Items,
                item => item.Runtime == LegacySimulatorRuntime());
            await vm.InstallSimulatorRuntimeCommand.ExecuteAsync(modern);
            await vm.ExportSimulatorRuntimeCommand.ExecuteAsync(modern);
            await vm.DownloadSimulatorRuntimeCommand.ExecuteAsync(legacy);
            Assert.Equal(0, installer.PrepareRequests);
            Assert.Equal(0, installer.ExportPrepareRequests);
            Assert.Equal(0, downloader.Requests);

            vm.Runtimes.ShowMappedFor("Xcode 26.4", [sdk], "Saved Xcode details");
            Assert.Equal(4, vm.Runtimes.Items.Count);
            Assert.DoesNotContain(vm.Runtimes.Items, item => item.CanInstall || item.CanDownload);
            await vm.RefreshSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.Equal(1, runtimes.SavedRequests);
        }, runtimes: runtimes, runtimeInstaller: installer, runtimeDownloader: downloader);
    }

    [Fact]
    public Task ACurrentSimulatorCatalogReplacesSavedDataAndLaterFailuresUseTheRefreshTime()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimesWithLegacy();
        var savedAt = new DateTime(2026, 3, 10, 15, 30, 0, DateTimeKind.Utc);
        runtimes.Saved = new XcodeSavedSimulatorRuntimeCatalog(
            new XcodeSimulatorRuntimeCatalog([LegacySimulatorRuntime()], null), savedAt);
        runtimes.CatalogError = new HttpRequestException("offline");
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.Equal(4, vm.Runtimes.Items.Count);
            Assert.StartsWith("Saved Apple simulator catalog may be out of date; last saved ", vm.Runtimes.CatalogStatus);

            runtimes.CatalogError = null;
            await vm.RefreshSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.Equal(9, vm.Runtimes.Items.Count);
            Assert.StartsWith("Apple simulator catalog refreshed at ", vm.Runtimes.CatalogStatus);
            Assert.Contains(vm.Runtimes.Items, item => item.CanInstall);
            Assert.Contains(vm.Runtimes.Items, item => item.CanDownload);

            runtimes.CatalogError = new HttpRequestException("offline again");
            await vm.RefreshSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.StartsWith("Apple simulator catalog may be out of date; last refreshed ", vm.Runtimes.CatalogStatus);
            Assert.Contains("offline again", vm.Runtimes.CatalogStatus);
            Assert.DoesNotContain(vm.Runtimes.Items, item => item.CanInstall || item.CanDownload);
            Assert.Equal(1, runtimes.SavedRequests);
        }, runtimes: runtimes);
    }

    [Fact]
    public Task SavedSimulatorDataAppearsWhileTheLiveRefreshIsPending()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        runtimes.Saved = new XcodeSavedSimulatorRuntimeCatalog(
            new XcodeSimulatorRuntimeCatalog(runtimes.Catalog, null),
            new DateTime(2026, 3, 10, 15, 30, 0, DateTimeKind.Utc));
        var gate = new TaskCompletionSource<IReadOnlyList<XcodeSimulatorRuntime>>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtimes.CatalogResponse = cancellation => gate.Task.WaitAsync(cancellation);
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            Task opening = vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);

            Assert.True(vm.Runtimes.IsRefreshing);
            Assert.StartsWith("Saved Apple simulator catalog may be out of date; last saved ", vm.Runtimes.CatalogStatus);
            Assert.Contains(vm.Runtimes.Items, item => item.Runtime.Origin == XcodeSimulatorRuntimeOrigin.AppleCatalog);
            Assert.DoesNotContain(vm.Runtimes.Items, item => item.CanInstall || item.CanDownload);
            Assert.False(vm.CanStartOperation);

            gate.SetResult(runtimes.Catalog);
            await opening;
            Assert.False(vm.Runtimes.IsRefreshing);
            Assert.StartsWith("Apple simulator catalog refreshed at ", vm.Runtimes.CatalogStatus);
            Assert.Contains(vm.Runtimes.Items, item => item.CanInstall);
        }, runtimes: runtimes);
    }

    [Fact]
    public Task AnUnreadableSavedSimulatorCatalogDoesNotPreventAnOnlineRefresh()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        runtimes.SavedError = new System.Xml.XmlException("invalid saved catalog");
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.Equal(8, vm.Runtimes.Items.Count);
            Assert.Contains("refreshed", vm.Runtimes.CatalogStatus);
            Assert.Contains(vm.Runtimes.Items, item => item.CanInstall);
            Assert.Equal(1, runtimes.SavedRequests);
        }, runtimes: runtimes);
    }

    [Fact]
    public Task CancellingASavedSimulatorCatalogReadDoesNotStartANetworkRequest()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        runtimes.SavedResponse = async cancellation =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return null;
        };
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            Task opening = vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.True(vm.Runtimes.IsRefreshing);
            vm.Stop();
            await opening;
            Assert.False(vm.Runtimes.IsRefreshing);
            Assert.Equal(1, runtimes.SavedRequests);
            Assert.Equal(0, runtimes.CatalogRequests);
            Assert.Equal(0, runtimes.InstalledRequests);
        }, runtimes: runtimes);
    }

    [Fact]
    public Task TheRuntimePageRendersTheSavedCatalogStatusAsALiveRegion() => DispatchAsync(async () =>
    {
        EnsureTheme();
        Directory.CreateDirectory(_testRoot);
        string path = Path.Combine(_testRoot, "AppleSimulatorCatalog.plist");
        await File.WriteAllTextAsync(path, """
            <plist><dict><key>downloadables</key><array><dict>
              <key>category</key><string>simulator</string>
              <key>platform</key><string>com.apple.platform.iphoneos</string>
              <key>name</key><string>iOS 26.4 Simulator Runtime</string>
              <key>simulatorVersion</key><dict>
                <key>version</key><string>26.4</string><key>buildUpdate</key><string>23E100</string>
              </dict>
              <key>architectures</key><array><string>arm64</string></array>
              <key>contentType</key><string>cryptexDiskImage</string>
              <key>downloadMethod</key><string>mobileAsset</string>
            </dict></array></dict></plist>
            """);
        var handler = new OfflineHandler();
        using var client = new HttpClient(handler);
        var runtimes = new XcodeSimulatorRuntimeService(client,
            (_, _, _) => Task.FromResult(new XcodesResult(0, "{}", "")), cacheFile: () => path);
        var vm = new XcodeViewModel(new FakeXcodes().Service, runtimes: runtimes);
        var view = new XcodePage(vm);
        var window = new Window { Width = 1100, Height = 900, Content = view };
        try
        {
            await vm.InitializeAsync();
            window.Show();
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var status = view.FindControl<TextBlock>("SimulatorCatalogStatus")!;
            Assert.True(status.IsEffectivelyVisible);
            Assert.StartsWith("Saved Apple simulator catalog may be out of date; last saved ", status.Text);
            Assert.Contains("offline", status.Text);
            Assert.Equal(status.Text, AutomationProperties.GetName(status));
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
            XcodeSimulatorRuntimeItemViewModel item = Assert.Single(vm.Runtimes.Items);
            Assert.Equal("23E100", item.Runtime.Build);
            Assert.False(item.CanInstall);
            Assert.False(item.CanDownload);
            Assert.Equal(1, handler.Requests);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task SimulatorBrowserKeepsLocalAndCatalogEntriesDistinct()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            Assert.False(vm.ShowSimulatorRuntimes);
            Assert.Contains("not been read", vm.Runtimes.CatalogStatus);
            Assert.Contains("not been read", vm.Runtimes.InstalledStatus);

            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);

            Assert.True(vm.ShowRuntimeBrowser);
            Assert.False(vm.ShowVersionBrowser);
            Assert.Equal(1, runtimes.CatalogRequests);
            Assert.Equal(1, runtimes.InstalledRequests);
            Assert.Equal(8, vm.Runtimes.Items.Count);
            Assert.Equal(5, vm.Runtimes.Items.Count(item => item.Runtime.Origin == XcodeSimulatorRuntimeOrigin.AppleCatalog));
            XcodeSimulatorRuntimeItemViewModel installed = Assert.Single(vm.Runtimes.Items,
                item => item.Runtime.Path == "/Library/Developer/Runtime/iOS.simruntime");
            XcodeSimulatorRuntimeItemViewModel bundled = Assert.Single(vm.Runtimes.Items,
                item => item.Runtime.Path == "/Applications/Xcode.app/Bundled/iOS.simruntime");
            Assert.Equal(XcodeSimulatorRuntimeOrigin.Installed, installed.Runtime.Origin);
            Assert.Equal(XcodeSimulatorRuntimeOrigin.Bundled, bundled.Runtime.Origin);
            Assert.False(installed.IsPrerelease);
            Assert.False(bundled.IsPrerelease);
            Assert.Contains("Installed", installed.Description);
            Assert.Contains("Bundled with selected developer tools", bundled.Description);
            Assert.Contains("Build 23E100", bundled.Description);
            Assert.Contains("Ready", installed.Description);
            Assert.Contains("MB", installed.Description);
            Assert.Contains("refreshed", vm.Runtimes.CatalogStatus);
            Assert.Contains("refreshed", vm.Runtimes.InstalledStatus);

            vm.ShowXcodeVersionsCommand.Execute(null);
            Assert.True(vm.ShowVersionBrowser);
            Assert.False(vm.ShowRuntimeBrowser);
        }, runtimes: runtimes);
    }

    [Theory]
    [InlineData(XcodeSimulatorRuntimeOrigin.Installed, "/Library/runtime.dmg", true)]
    [InlineData(XcodeSimulatorRuntimeOrigin.Bundled, "/Xcode/iOS.simruntime", true)]
    [InlineData(XcodeSimulatorRuntimeOrigin.AppleCatalog, "/Library/runtime.dmg", false)]
    [InlineData(XcodeSimulatorRuntimeOrigin.Installed, null, false)]
    [InlineData(XcodeSimulatorRuntimeOrigin.Installed, "", false)]
    [InlineData(XcodeSimulatorRuntimeOrigin.Installed, "relative/runtime.dmg", false)]
    [InlineData(XcodeSimulatorRuntimeOrigin.Installed, "-R", false)]
    [InlineData(XcodeSimulatorRuntimeOrigin.Installed, "/Library/runtime\0.dmg", false)]
    public void RuntimePathActionsRequireAnAbsoluteLocalPath(
        XcodeSimulatorRuntimeOrigin origin, string? path, bool expected)
    {
        XcodeSimulatorRuntime runtime = SimulatorRuntimes().Installed[0] with { Origin = origin, Path = path };
        var item = new XcodeSimulatorRuntimeItemViewModel(runtime, null, false, false);

        Assert.Equal(expected, item.HasLocalPath);
    }

    [Fact]
    public Task RuntimePathActionsUseTheExactReportedPathAndReportFailures()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            XcodeSimulatorRuntimeItemViewModel installed = Assert.Single(vm.Runtimes.Items,
                item => item.Runtime.Path == "/Library/Developer/Runtime/iOS.simruntime");
            XcodeSimulatorRuntimeItemViewModel bundled = Assert.Single(vm.Runtimes.Items,
                item => item.Runtime.Path == "/Applications/Xcode.app/Bundled/iOS.simruntime");
            var revealed = new List<string>();
            var copied = new List<string>();
            vm.ShowSimulatorPathInFinder = path =>
            {
                revealed.Add(path);
                return Task.CompletedTask;
            };
            vm.CopyText = path =>
            {
                copied.Add(path);
                return Task.CompletedTask;
            };

            await vm.RevealSimulatorRuntimeInFinderCommand.ExecuteAsync(installed);
            await vm.RevealSimulatorRuntimeInFinderCommand.ExecuteAsync(bundled);
            await vm.CopySimulatorRuntimePathCommand.ExecuteAsync(installed);
            await vm.CopySimulatorRuntimePathCommand.ExecuteAsync(bundled);

            Assert.Equal([installed.Path, bundled.Path], revealed);
            Assert.Equal([installed.Path, bundled.Path], copied);
            AssertNotice(vm, InfoBarSeverity.Success, "Path copied", bundled.Path);

            vm.ShowSimulatorPathInFinder = _ => Task.FromException(new IOException("Finder failed"));
            await vm.RevealSimulatorRuntimeInFinderCommand.ExecuteAsync(installed);
            AssertNotice(vm, InfoBarSeverity.Error, "Could not show the simulator runtime in Finder", "Finder failed");
            vm.CopyText = _ => Task.FromException(new InvalidOperationException("clipboard failed"));
            await vm.CopySimulatorRuntimePathCommand.ExecuteAsync(installed);
            AssertNotice(vm, InfoBarSeverity.Error, "Could not copy the path", "clipboard failed");

            XcodeSimulatorRuntime remote = runtimes.Catalog[0] with { Path = "/Library/runtime.dmg" };
            var catalogItem = new XcodeSimulatorRuntimeItemViewModel(remote, null, false, false);
            await vm.RevealSimulatorRuntimeInFinderCommand.ExecuteAsync(catalogItem);
            AssertNotice(vm, InfoBarSeverity.Error, "Simulator runtime path unavailable",
                "This simulator runtime has no absolute local path.");
            await vm.CopySimulatorRuntimePathCommand.ExecuteAsync(catalogItem);
            AssertNotice(vm, InfoBarSeverity.Error, "Simulator runtime path unavailable",
                "This simulator runtime has no absolute local path.");
            Assert.Equal([installed.Path, bundled.Path], revealed);
            Assert.Equal([installed.Path, bundled.Path], copied);
        }, runtimes: runtimes);
    }

    [Fact]
    public Task FinderRevealsRuntimeDirectoriesAndImagesAndRejectsMissingPaths() => DispatchAsync(async () =>
    {
        string directory = Path.Combine(_testRoot, "iOS Simulator & beta.simruntime");
        string image = Path.Combine(_testRoot, "iOS Runtime (archive).dmg");
        Directory.CreateDirectory(directory);
        File.WriteAllText(image, "fake runtime image");
        var starts = new List<ProcessStartInfo>();
        Task<int> Run(ProcessStartInfo start)
        {
            starts.Add(start);
            return Task.FromResult(0);
        }

        await XcodeViewModel.ShowSimulatorPathInFinderCoreAsync(directory, Run);
        await XcodeViewModel.ShowSimulatorPathInFinderCoreAsync(image, Run);
        Assert.Equal(2, starts.Count);
        Assert.All(starts, start =>
        {
            Assert.Equal("/usr/bin/open", start.FileName);
            Assert.False(start.UseShellExecute);
        });
        Assert.Equal(["-R", directory], starts[0].ArgumentList);
        Assert.Equal(["-R", image], starts[1].ArgumentList);
        FileNotFoundException missing = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            XcodeViewModel.ShowSimulatorPathInFinderCoreAsync(image + ".missing", Run));
        Assert.Contains("is no longer available", missing.Message);
        Assert.Equal(2, starts.Count);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            XcodeViewModel.ShowSimulatorPathInFinderCoreAsync(image, _ => Task.FromResult(1)));
        Assert.Equal("Finder could not be opened.", failure.Message);
    });

    [Fact]
    public Task SimulatorFiltersPreserveUnknownsAndSearchNamesBuildsAndPaths()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            XcodeSimulatorRuntimesViewModel browser = vm.Runtimes;

            browser.SourceFilterIndex = 1;
            Assert.Equal(3, browser.Items.Count);
            browser.PlatformFilterIndex = 1;
            Assert.Equal(2, browser.Items.Count);
            browser.ArchitectureFilterIndex = 1;
            Assert.Equal("/Library/Developer/Runtime/iOS.simruntime", Assert.Single(browser.Items).Runtime.Path);
            browser.ReleaseFilterIndex = 1;
            Assert.Single(browser.Items);

            browser.SourceFilterIndex = 2;
            browser.PlatformFilterIndex = 0;
            browser.ReleaseFilterIndex = 2;
            Assert.Equal("iOS 27.0 beta Simulator Runtime", Assert.Single(browser.Items).Title);
            browser.ArchitectureFilterIndex = 0;
            Assert.Equal(2, browser.Items.Count);
            Assert.All(browser.Items, item => Assert.True(item.IsPrerelease));

            browser.ReleaseFilterIndex = 0;
            browser.SourceFilterIndex = 0;
            browser.SearchText = "23e100";
            Assert.Equal(4, browser.Items.Count);
            browser.SearchText = "/BUNDLED/";
            Assert.Equal(XcodeSimulatorRuntimeOrigin.Bundled, Assert.Single(browser.Items).Runtime.Origin);
            browser.SearchText = "mystery";
            XcodeSimulatorRuntimeItemViewModel unknown = Assert.Single(browser.Items);
            Assert.Null(unknown.IsPrerelease);
            Assert.Equal(XcodeArchitecture.Unspecified, unknown.Runtime.Architecture);
            Assert.Contains("Architecture unknown", unknown.Description);
            Assert.Contains("Release status unknown", unknown.Description);
            browser.ReleaseFilterIndex = 1;
            Assert.True(browser.IsEmpty);
            browser.ReleaseFilterIndex = 0;
            browser.ArchitectureFilterIndex = 3;
            Assert.True(browser.IsEmpty);
        }, runtimes: runtimes);
    }

    [Fact]
    public Task SimulatorFiltersSurvivePageRecreationWithoutChangingVersionFilters()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            XcodePreferences.BrowserReleaseFilter = 1;
            XcodePreferences.BrowserArchitectureFilter = 2;
            XcodePreferences.IncludePrereleases = false;

            XcodeSimulatorRuntimesViewModel browser = vm.Runtimes;
            Assert.Equal(0, browser.PlatformFilterIndex);
            Assert.Equal(0, browser.SourceFilterIndex);
            Assert.Equal(0, browser.ReleaseFilterIndex);
            Assert.Equal(0, browser.ArchitectureFilterIndex);
            browser.PlatformFilterIndex = 1;
            browser.SourceFilterIndex = 2;
            browser.ReleaseFilterIndex = 2;
            browser.ArchitectureFilterIndex = 1;
            browser.SearchText = "27.0";
            Assert.Equal("iOS 27.0 beta Simulator Runtime", Assert.Single(browser.Items).Title);

            string settingsDirectory = CoreData.UniGetUIUserConfigurationDirectory;
            Assert.Equal("ios", File.ReadAllText(Path.Combine(settingsDirectory, "XcodeRuntimePlatformFilter")));
            Assert.Equal("apple-catalog", File.ReadAllText(Path.Combine(settingsDirectory, "XcodeRuntimeSourceFilter")));
            Assert.Equal("prerelease", File.ReadAllText(Path.Combine(settingsDirectory, "XcodeRuntimeReleaseFilter")));
            Assert.Equal("apple-silicon", File.ReadAllText(Path.Combine(settingsDirectory, "XcodeRuntimeArchitectureFilter")));
            var restored = new XcodeSimulatorRuntimesViewModel(runtimes);
            Assert.Equal(1, restored.PlatformFilterIndex);
            Assert.Equal(2, restored.SourceFilterIndex);
            Assert.Equal(2, restored.ReleaseFilterIndex);
            Assert.Equal(1, restored.ArchitectureFilterIndex);
            Assert.Equal("", restored.SearchText);
            await restored.RefreshAsync(CancellationToken.None);
            Assert.Equal("iOS 27.0 beta Simulator Runtime", Assert.Single(restored.Items).Title);

            restored.PlatformFilterIndex = -1;
            restored.SourceFilterIndex = 3;
            restored.ReleaseFilterIndex = -1;
            restored.ArchitectureFilterIndex = 4;
            Assert.Equal(1, restored.PlatformFilterIndex);
            Assert.Equal(2, restored.SourceFilterIndex);
            Assert.Equal(2, restored.ReleaseFilterIndex);
            Assert.Equal(1, restored.ArchitectureFilterIndex);
            Assert.Equal(1, XcodePreferences.RuntimePlatformFilter);
            Assert.Equal(2, XcodePreferences.RuntimeSourceFilter);
            Assert.Equal(2, XcodePreferences.RuntimeReleaseFilter);
            Assert.Equal(1, XcodePreferences.RuntimeArchitectureFilter);
            Assert.Equal(1, XcodePreferences.BrowserReleaseFilter);
            Assert.Equal(2, XcodePreferences.BrowserArchitectureFilter);
            Assert.False(XcodePreferences.IncludePrereleases);
        }, runtimes: runtimes);
    }

    [Fact]
    public Task SimulatorReleaseStageNeedsAnUnambiguousFreshExactMatch()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        XcodeSimulatorRuntime contradictory = runtimes.Catalog[0] with { IsPrerelease = true };
        runtimes.Catalog = [.. runtimes.Catalog, contradictory];
        runtimes.Installed =
        [
            .. runtimes.Installed,
            runtimes.Installed[0] with { Version = "26.5", Path = "/different-version" },
            runtimes.Installed[0] with { Architecture = XcodeArchitecture.Intel, Path = "/different-architecture" },
        ];
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            XcodeSimulatorRuntimeItemViewModel installed = Assert.Single(vm.Runtimes.Items,
                item => item.Runtime.Path == "/Library/Developer/Runtime/iOS.simruntime");
            Assert.Null(installed.IsPrerelease);
            Assert.Null(Assert.Single(vm.Runtimes.Items, item => item.Runtime.Platform == "watchOS"
                && item.Runtime.Origin == XcodeSimulatorRuntimeOrigin.Installed).IsPrerelease);
            Assert.Null(Assert.Single(vm.Runtimes.Items,
                item => item.Runtime.Path == "/different-version").IsPrerelease);
            Assert.Null(Assert.Single(vm.Runtimes.Items,
                item => item.Runtime.Path == "/different-architecture").IsPrerelease);

            runtimes.Catalog = [.. runtimes.Catalog.Where(item => item != contradictory)];
            await vm.RefreshSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.False(Assert.Single(vm.Runtimes.Items,
                item => item.Runtime.Path == installed.Runtime.Path).IsPrerelease);

            runtimes.CatalogError = new HttpRequestException("catalog unavailable");
            await vm.RefreshSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.Null(Assert.Single(vm.Runtimes.Items,
                item => item.Runtime.Path == installed.Runtime.Path).IsPrerelease);
            Assert.Contains("may be out of date", vm.Runtimes.CatalogStatus);
        }, runtimes: runtimes);
    }

    [Fact]
    public Task SimulatorSourcesReportTimeoutsAndMalformedLocalDataWithoutAnEmptySuccess()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        runtimes.CatalogError = new TaskCanceledException("request timed out");
        runtimes.InstalledError = new System.Text.Json.JsonException("invalid JSON");
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.Empty(vm.Runtimes.Items);
            Assert.Equal("Simulator runtimes are unavailable.", vm.Runtimes.EmptyText);
            Assert.Contains("The request timed out.", vm.Runtimes.CatalogStatus);
            Assert.Contains("Could not read installed simulator runtimes", vm.Runtimes.InstalledStatus);
            Assert.Contains("invalid JSON", vm.Runtimes.InstalledStatus);
        }, runtimes: runtimes);
    }

    [Fact]
    public Task SimulatorSourcesFailIndependentlyAndRetainTheirOwnSnapshots()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        runtimes.CatalogError = new HttpRequestException("catalog offline");
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.Equal(3, vm.Runtimes.Items.Count);
            Assert.Contains("Could not read the Apple simulator catalog", vm.Runtimes.CatalogStatus);
            Assert.Contains("catalog offline", vm.Runtimes.CatalogStatus);
            Assert.DoesNotContain("out of date", vm.Runtimes.CatalogStatus);
            Assert.Contains("refreshed", vm.Runtimes.InstalledStatus);

            runtimes.CatalogError = null;
            runtimes.InstalledError = new XcodesException("simctl failed");
            await vm.RefreshSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.Equal(8, vm.Runtimes.Items.Count);
            Assert.Contains("may be out of date", vm.Runtimes.InstalledStatus);
            Assert.Contains("simctl failed", vm.Runtimes.InstalledStatus);
            Assert.Contains("refreshed", vm.Runtimes.CatalogStatus);

            runtimes.CatalogError = new HttpRequestException("catalog offline again");
            runtimes.InstalledError = null;
            runtimes.Installed = [runtimes.Installed[0]];
            await vm.RefreshSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.Equal(6, vm.Runtimes.Items.Count);
            Assert.Contains("may be out of date", vm.Runtimes.CatalogStatus);
            Assert.Contains("catalog offline again", vm.Runtimes.CatalogStatus);
            Assert.Contains("refreshed", vm.Runtimes.InstalledStatus);
        }, runtimes: runtimes);
    }

    [Fact]
    public Task SimulatorRefreshLocksXcodeAndAccountOperationsAndStopsWithThePage()
    {
        var fake = new FakeXcodes { Account = new("developer@example.test", null, HasSavedPassword: true) };
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        var gate = new TaskCompletionSource<IReadOnlyList<XcodeSimulatorRuntime>>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtimes.CatalogResponse = cancellation => gate.Task.WaitAsync(cancellation);
        return WithPageAsync(fake, async vm =>
        {
            fake.Commands.Clear();
            Task opening = vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.True(vm.IsBusy);
            Assert.True(vm.Account.IsBusy);
            Assert.False(vm.CanStartOperation);
            Assert.True(vm.Runtimes.IsRefreshing);
            Assert.False(vm.Account.SignOutCommand.CanExecute(null));

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            await vm.SelectCommand.ExecuteAsync(vm.Installed[1]);
            await vm.Account.SignOutCommand.ExecuteAsync(null);
            Assert.Empty(fake.Commands);
            Assert.Empty(fake.TerminalCommands);

            vm.Stop();
            await opening;
            Assert.True(runtimes.CatalogCancellation.IsCancellationRequested);
            Assert.False(vm.IsBusy);
            Assert.False(vm.Account.IsBusy);
            Assert.False(vm.Runtimes.IsRefreshing);
        }, runtimes: runtimes);
    }

    [Fact]
    public Task AnAccountActionBlocksOpeningTheSimulatorBrowser()
    {
        var fake = new FakeXcodes { Account = new("developer@example.test", null, HasSavedPassword: true) };
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        var gate = new TaskCompletionSource<XcodesResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.OverrideAsync(command => command == "signout" ? gate.Task : null);
        return WithPageAsync(fake, async vm =>
        {
            Task signOut = vm.Account.SignOutCommand.ExecuteAsync(null);
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.False(vm.ShowSimulatorRuntimes);
            Assert.Equal(0, runtimes.CatalogRequests);
            Assert.Equal(0, runtimes.InstalledRequests);

            fake.Account = XcodeAccountStatus.None;
            gate.SetResult(FakeXcodes.Success("Successfully signed out\n"));
            await signOut;
        }, runtimes: runtimes);
    }

    [Fact]
    public Task InstallingAModernRuntimeConfirmsItsExactIdentityAndRefreshesTheLists()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        var installer = new FakeSimulatorInstaller();
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            XcodeSimulatorRuntimeItemViewModel item = Assert.Single(vm.Runtimes.Items, entry =>
                entry.Runtime.Origin == XcodeSimulatorRuntimeOrigin.AppleCatalog
                && entry.Runtime.Build == "23E100" && entry.Runtime.Architecture == XcodeArchitecture.AppleSilicon);
            Assert.True(item.CanInstall);
            XcodeSimulatorRuntimeInstallPlan? confirmed = null;
            vm.ConfirmRuntimeInstall = plan =>
            {
                confirmed = plan;
                return Task.FromResult(true);
            };

            await vm.InstallSimulatorRuntimeCommand.ExecuteAsync(item);

            Assert.Same(item.Runtime, confirmed?.Runtime);
            Assert.Equal(installer.Plan, confirmed);
            Assert.Same(confirmed, installer.InstalledPlan);
            Assert.Equal(1, installer.InstallRequests);
            Assert.Equal(0, installer.ExportPrepareRequests);
            Assert.Equal(2, runtimes.CatalogRequests);
            Assert.Equal(2, runtimes.InstalledRequests);
            AssertNotice(vm, InfoBarSeverity.Success, "Simulator runtime is ready",
                $"Developer tools: {installer.DeveloperDirectory}");
        }, runtimes: runtimes, runtimeInstaller: installer);
    }

    [Fact]
    public Task RuntimeInstallConfirmationNamesTheBuildToolsAndAccountBoundary() => DispatchAsync(() =>
    {
        XcodeSimulatorRuntime runtime = SimulatorRuntimes().Catalog[0];
        XcodeVersion.TryParse("26.5", out XcodeVersion version);
        var plan = new XcodeSimulatorRuntimeInstallPlan(runtime, "/Applications/Example Xcode.app/Contents/Developer", version);
        ImmersiveConfirmationDialog dialog = XcodeViewModel.CreateRuntimeInstallDialog(plan);
        var body = ConfirmationDialogBody(dialog);
        string[] texts = body.Children.OfType<TextBlock>().Select(text => text.Text!).ToArray();

        Assert.Equal("Install", dialog.FindControl<Button>("PrimaryButton")!.Content);
        Assert.Equal("Cancel", dialog.FindControl<Button>("SecondaryButton")!.Content);
        Assert.False(dialog.FocusPrimaryButton);
        Assert.Contains(texts, text => text.Contains("23E100", StringComparison.Ordinal)
            && text.Contains("Apple Silicon", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains(plan.DeveloperDirectory, StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("own Apple account", StringComparison.Ordinal)
            && text.Contains("does not use the Apple ID UniGetUI downloads Xcode with", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("may continue processing", StringComparison.Ordinal));
        return Task.CompletedTask;
    });

    [Fact]
    public Task DecliningARuntimeInstallOrLosingFreshCatalogDataCannotStartIt()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        var installer = new FakeSimulatorInstaller();
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            XcodeSimulatorRuntimeItemViewModel item = Assert.Single(vm.Runtimes.Items, entry =>
                entry.Runtime.Origin == XcodeSimulatorRuntimeOrigin.AppleCatalog
                && entry.Runtime.Build == "23E100" && entry.Runtime.Architecture == XcodeArchitecture.AppleSilicon);
            vm.ConfirmRuntimeInstall = _ => Task.FromResult(false);
            await vm.InstallSimulatorRuntimeCommand.ExecuteAsync(item);
            Assert.Equal(1, installer.PrepareRequests);
            Assert.Equal(0, installer.InstallRequests);

            runtimes.CatalogError = new HttpRequestException("offline");
            await vm.RefreshSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.False(Assert.Single(vm.Runtimes.Items, entry => entry.Runtime == item.Runtime).CanInstall);
            await vm.InstallSimulatorRuntimeCommand.ExecuteAsync(item);
            Assert.Equal(1, installer.PrepareRequests);
            AssertNotice(vm, InfoBarSeverity.Error, "Cannot install this simulator runtime",
                "Refresh the Apple simulator catalog and choose a supported runtime.");
        }, runtimes: runtimes, runtimeInstaller: installer);
    }

    [Fact]
    public Task RuntimeInstallCancellationLocksOtherActionsAndDoesNotClaimSuccess()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        var installer = new FakeSimulatorInstaller
        {
            InstallResponse = async (_, output, cancellation) =>
            {
                output?.Invoke(new XcodesOutputLine("Downloading: 42.5%"));
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                return XcodeSimulatorRuntimeInstallOutcome.Installed;
            },
        };
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            vm.ConfirmRuntimeInstall = _ => Task.FromResult(true);
            XcodeSimulatorRuntimeItemViewModel item = vm.Runtimes.Items.First(entry => entry.CanInstall);

            Task operation = vm.InstallSimulatorRuntimeCommand.ExecuteAsync(item);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsOperationRunning);
            Assert.True(vm.IsBusy);
            Assert.True(vm.Account.IsBusy);
            Assert.False(vm.CanStartOperation);
            Assert.DoesNotContain(vm.BrowserItems, browserItem => browserItem.IsOperationTarget);
            Assert.Same(item.Runtime, Assert.Single(vm.Runtimes.Items, entry => entry.IsOperationTarget).Runtime);
            Assert.False(vm.OperationIsIndeterminate);
            Assert.Equal(42.5, vm.OperationProgress);
            Assert.Contains("Downloading: 42.5%", vm.OperationLog);
            fake.Commands.Clear();
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            Assert.Empty(fake.Commands);

            vm.CancelOperation();
            await operation;
            Assert.False(vm.IsOperationRunning);
            Assert.False(vm.IsBusy);
            Assert.False(vm.Account.IsBusy);
            Assert.DoesNotContain(vm.Runtimes.Items, entry => entry.IsOperationTarget);
            AssertNotice(vm, InfoBarSeverity.Informational,
                $"Cancellation requested for {item.Runtime.Name}",
                "macOS or Xcode may continue processing the runtime. Refresh the list to check.");
            Assert.Equal(2, runtimes.InstalledRequests);
        }, runtimes: runtimes, runtimeInstaller: installer);
    }

    [Fact]
    public Task RuntimeProgressTargetsTheSelectedCatalogInstanceAcrossFiltering()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        XcodeSimulatorRuntime duplicate = runtimes.Catalog[0] with { };
        runtimes.Catalog = [.. runtimes.Catalog, duplicate];
        var sdk = new XcodeSdkIdentity("iOS", "26.4", "23E100");
        runtimes.SDKMappings = [new(sdk, "23E100")];
        var installer = new FakeSimulatorInstaller
        {
            InstallResponse = async (_, _, cancellation) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                return XcodeSimulatorRuntimeInstallOutcome.Installed;
            },
        };
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            vm.ConfirmRuntimeInstall = _ => Task.FromResult(true);
            XcodeSimulatorRuntimeItemViewModel selected = Assert.Single(
                vm.Runtimes.Items, entry => ReferenceEquals(entry.Runtime, duplicate));

            Task operation = vm.InstallSimulatorRuntimeCommand.ExecuteAsync(selected);
            Assert.Same(duplicate, Assert.Single(vm.Runtimes.Items, entry => entry.IsOperationTarget).Runtime);
            Assert.Contains(selected.Description, selected.CancelOperationText);

            vm.Runtimes.SearchText = "not in the catalog";
            Assert.False(selected.IsOperationTarget);
            Assert.DoesNotContain(vm.Runtimes.Items, entry => entry.IsOperationTarget);
            vm.Runtimes.SearchText = "26.4";
            Assert.Same(duplicate, Assert.Single(vm.Runtimes.Items, entry => entry.IsOperationTarget).Runtime);

            vm.Runtimes.ArchitectureFilterIndex = 2;
            Assert.DoesNotContain(vm.Runtimes.Items, entry => entry.IsOperationTarget);
            vm.Runtimes.ArchitectureFilterIndex = 1;
            Assert.Same(duplicate, Assert.Single(vm.Runtimes.Items, entry => entry.IsOperationTarget).Runtime);
            vm.Runtimes.SourceFilterIndex = 1;
            Assert.DoesNotContain(vm.Runtimes.Items, entry => entry.IsOperationTarget);
            vm.Runtimes.SourceFilterIndex = 2;
            Assert.Same(duplicate, Assert.Single(vm.Runtimes.Items, entry => entry.IsOperationTarget).Runtime);

            vm.Runtimes.ShowMappedFor("Xcode 26.4", [sdk], "");
            Assert.Same(duplicate, Assert.Single(vm.Runtimes.Items, entry => entry.IsOperationTarget).Runtime);
            vm.ShowXcodeVersionsCommand.Execute(null);
            int catalogRequests = runtimes.CatalogRequests;
            Assert.True(vm.CanOpenSimulatorRuntimes);
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.True(vm.ShowSimulatorRuntimes);
            Assert.True(vm.Runtimes.HasMappingContext);
            Assert.Equal(catalogRequests, runtimes.CatalogRequests);
            Assert.Same(duplicate, Assert.Single(vm.Runtimes.Items, entry => entry.IsOperationTarget).Runtime);
            vm.Runtimes.ShowAllRuntimesCommand.Execute(null);
            Assert.Same(duplicate, Assert.Single(vm.Runtimes.Items, entry => entry.IsOperationTarget).Runtime);

            runtimes.Catalog = [.. runtimes.Catalog.Select(runtime => runtime with { })];
            await vm.Runtimes.RefreshAsync(CancellationToken.None);
            Assert.True(vm.IsOperationRunning);
            Assert.DoesNotContain(vm.Runtimes.Items, entry => entry.IsOperationTarget);

            vm.CancelOperation();
            await operation;
            Assert.DoesNotContain(vm.Runtimes.Items, entry => entry.IsOperationTarget);
        }, runtimes: runtimes, runtimeInstaller: installer);
    }

    [Fact]
    public Task RuntimeInstallFailureRetainsTheOriginalDiagnostics()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        var installer = new FakeSimulatorInstaller
        {
            InstallResponse = (_, output, _) =>
            {
                output?.Invoke(new XcodesOutputLine("Download failed"));
                return Task.FromException<XcodeSimulatorRuntimeInstallOutcome>(new XcodesException("Could not verify build"));
            },
        };
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            vm.ConfirmRuntimeInstall = _ => Task.FromResult(true);
            XcodeSimulatorRuntimeItemViewModel item = vm.Runtimes.Items.First(entry => entry.CanInstall);

            await vm.InstallSimulatorRuntimeCommand.ExecuteAsync(item);

            Assert.True(vm.HasOperation);
            Assert.True(vm.IsLogExpanded);
            Assert.Contains("Download failed", vm.OperationLog);
            Assert.DoesNotContain(vm.Runtimes.Items, entry => entry.IsOperationTarget);
            AssertNotice(vm, InfoBarSeverity.Error, $"Could not install {item.Runtime.Name}", "Could not verify build");
            Assert.Equal(2, runtimes.InstalledRequests);
        }, runtimes: runtimes, runtimeInstaller: installer);
    }

    [Fact]
    public Task AnAlreadyAvailableRuntimeIsNotReportedAsANewInstallation()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        var installer = new FakeSimulatorInstaller
        {
            InstallResponse = (_, _, _) => Task.FromResult(XcodeSimulatorRuntimeInstallOutcome.AlreadyAvailable),
        };
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            vm.ConfirmRuntimeInstall = _ => Task.FromResult(true);
            await vm.InstallSimulatorRuntimeCommand.ExecuteAsync(vm.Runtimes.Items.First(item => item.CanInstall));
            AssertNotice(vm, InfoBarSeverity.Success, "Simulator runtime is already available",
                $"Developer tools: {installer.DeveloperDirectory}");
        }, runtimes: runtimes, runtimeInstaller: installer);
    }

    [Fact]
    public Task RuntimeExportConfirmationDisclosesIdentityAccountSideEffectsAndTemporaryFiles() => DispatchAsync(() =>
    {
        XcodeSimulatorRuntime runtime = SimulatorRuntimes().Catalog[0];
        XcodeVersion.TryParse("26.5", out XcodeVersion version);
        var plan = new XcodeSimulatorRuntimeInstallPlan(runtime, "/Applications/Example Xcode.app/Contents/Developer", version);
        ImmersiveConfirmationDialog dialog = XcodeViewModel.CreateRuntimeExportDialog(plan, "/Users/Example/Runtime images");
        var body = ConfirmationDialogBody(dialog);
        string[] texts = body.Children.OfType<TextBlock>().Select(text => text.Text!).ToArray();

        Assert.Equal("Export", dialog.FindControl<Button>("PrimaryButton")!.Content);
        Assert.Equal("Cancel", dialog.FindControl<Button>("SecondaryButton")!.Content);
        Assert.False(dialog.FocusPrimaryButton);
        Assert.Contains(texts, text => text.Contains("23E100", StringComparison.Ordinal)
            && text.Contains("Apple Silicon", StringComparison.Ordinal)
            && text.Contains("may also install", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains(plan.DeveloperDirectory, StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("/Users/Example/Runtime images", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("own Apple account", StringComparison.Ordinal)
            && text.Contains("does not use the Apple ID UniGetUI downloads Xcode with", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("without overwriting", StringComparison.Ordinal)
            && text.Contains("space in both locations", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("does not independently verify", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("may continue processing", StringComparison.Ordinal)
            && text.Contains("incomplete files may remain", StringComparison.Ordinal));
        return Task.CompletedTask;
    });

    [Fact]
    public Task ExportingAModernRuntimeUsesTheConfirmedPlanAndRefreshesTheLists()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        var installer = new FakeSimulatorInstaller();
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            XcodeSimulatorRuntimeItemViewModel item = vm.Runtimes.Items.First(entry => entry.CanInstall);
            vm.ChooseRuntimeExportFolder = () => Task.FromResult<string?>("/tmp/Runtime images");
            XcodeSimulatorRuntimeInstallPlan? confirmed = null;
            string? confirmedDirectory = null;
            vm.ConfirmRuntimeExport = (plan, directory) =>
            {
                confirmed = plan;
                confirmedDirectory = directory;
                return Task.FromResult(true);
            };
            string? copied = null;
            vm.CopyText = path =>
            {
                copied = path;
                return Task.CompletedTask;
            };

            await vm.ExportSimulatorRuntimeCommand.ExecuteAsync(item);

            Assert.Same(item.Runtime, confirmed?.Runtime);
            Assert.Same(confirmed, installer.ExportedPlan);
            Assert.Equal("/tmp/Runtime images", confirmedDirectory);
            Assert.Equal(confirmedDirectory, installer.ExportDirectory);
            Assert.Equal(1, installer.ExportRequests);
            Assert.Equal(1, installer.ExportPrepareRequests);
            Assert.Equal(2, runtimes.CatalogRequests);
            Assert.Equal(2, runtimes.InstalledRequests);
            string imagePath = Path.Combine(confirmedDirectory!, "runtime.dmg");
            AssertNotice(vm, InfoBarSeverity.Success, "Simulator runtime image exported", $"Saved to {imagePath}");
            Assert.Equal("Copy path", vm.Notice.ActionButtonText);
            await Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(
                vm.Notice.ActionButtonCommand).ExecuteAsync(null);
            Assert.Equal(imagePath, copied);
        }, runtimes: runtimes, runtimeInstaller: installer);
    }

    [Fact]
    public Task DecliningARuntimeExportOrLosingFreshCatalogDataCannotStartIt()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        var installer = new FakeSimulatorInstaller();
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            XcodeSimulatorRuntimeItemViewModel item = vm.Runtimes.Items.First(entry => entry.CanInstall);
            bool confirmed = false;
            vm.ConfirmRuntimeExport = (_, _) =>
            {
                confirmed = true;
                return Task.FromResult(false);
            };
            vm.ChooseRuntimeExportFolder = () => Task.FromResult<string?>(null);
            await vm.ExportSimulatorRuntimeCommand.ExecuteAsync(item);
            Assert.False(confirmed);
            Assert.Equal(0, installer.ExportRequests);

            vm.ChooseRuntimeExportFolder = () => Task.FromResult<string?>("/tmp/Runtime images");
            await vm.ExportSimulatorRuntimeCommand.ExecuteAsync(item);
            Assert.True(confirmed);
            Assert.Equal(0, installer.ExportRequests);

            runtimes.CatalogError = new HttpRequestException("offline");
            await vm.RefreshSimulatorRuntimesCommand.ExecuteAsync(null);
            await vm.ExportSimulatorRuntimeCommand.ExecuteAsync(item);
            Assert.Equal(2, installer.PrepareRequests);
            Assert.Equal(0, installer.ExportRequests);
            AssertNotice(vm, InfoBarSeverity.Error, "Cannot export this simulator runtime",
                "Refresh the Apple simulator catalog and choose a supported runtime.");
        }, runtimes: runtimes, runtimeInstaller: installer);
    }

    [Fact]
    public Task RuntimeExportCancellationLocksOtherActionsAndShowsRecoveryLocations()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        var installer = new FakeSimulatorInstaller
        {
            ExportResponse = async (_, _, _, output, cancellation) =>
            {
                output?.Invoke(new XcodesOutputLine("Copying runtime image: 42%"));
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                throw new InvalidOperationException("The export should have been cancelled.");
            },
        };
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            vm.ChooseRuntimeExportFolder = () => Task.FromResult<string?>("/tmp/Runtime images");
            vm.ConfirmRuntimeExport = (_, _) => Task.FromResult(true);
            string? copied = null;
            vm.CopyText = value =>
            {
                copied = value;
                return Task.CompletedTask;
            };
            XcodeSimulatorRuntimeItemViewModel item = vm.Runtimes.Items.First(entry => entry.CanInstall);

            Task operation = vm.ExportSimulatorRuntimeCommand.ExecuteAsync(item);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsOperationRunning);
            Assert.True(vm.IsBusy);
            Assert.True(vm.Account.IsBusy);
            Assert.False(vm.CanStartOperation);
            Assert.Same(item.Runtime, Assert.Single(vm.Runtimes.Items, entry => entry.IsOperationTarget).Runtime);
            Assert.False(vm.OperationIsIndeterminate);
            Assert.Equal(42, vm.OperationProgress);
            Assert.Equal("Copying runtime image: 42%", vm.OperationStatus);
            fake.Commands.Clear();
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            Assert.Empty(fake.Commands);

            vm.CancelOperation();
            await operation;
            Assert.False(vm.IsOperationRunning);
            Assert.False(vm.IsBusy);
            Assert.False(vm.Account.IsBusy);
            Assert.DoesNotContain(vm.Runtimes.Items, entry => entry.IsOperationTarget);
            Assert.Equal(InfoBarSeverity.Informational, vm.Notice.Severity);
            Assert.Equal($"Cancellation requested for {item.Runtime.Name}", vm.Notice.Title);
            Assert.Contains("may continue processing the runtime", vm.Notice.Message);
            Assert.Contains(installer.ExportPaths!.TemporaryDirectory, vm.Notice.Message);
            Assert.Contains(installer.ExportPaths.PartialPath, vm.Notice.Message);
            Assert.Equal("Copy recovery paths", vm.Notice.ActionButtonText);
            await Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(
                vm.Notice.ActionButtonCommand).ExecuteAsync(null);
            Assert.Equal(installer.ExportPaths.TemporaryDirectory + Environment.NewLine + installer.ExportPaths.PartialPath, copied);
            Assert.Equal(2, runtimes.InstalledRequests);
        }, runtimes: runtimes, runtimeInstaller: installer);
    }

    [Fact]
    public Task RuntimeExportReportsRetainedTemporaryDataAfterSuccess()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        var installer = new FakeSimulatorInstaller
        {
            ExportResponse = (_, _, paths, output, _) =>
            {
                output?.Invoke(new XcodesOutputLine("Verifying copied runtime image..."));
                return Task.FromResult(new XcodeSimulatorRuntimeExportResult(paths.ImagePath, paths.TemporaryDirectory));
            },
        };
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            vm.ChooseRuntimeExportFolder = () => Task.FromResult<string?>("/tmp/Runtime images");
            vm.ConfirmRuntimeExport = (_, _) => Task.FromResult(true);

            await vm.ExportSimulatorRuntimeCommand.ExecuteAsync(vm.Runtimes.Items.First(entry => entry.CanInstall));

            Assert.DoesNotContain(vm.Runtimes.Items, entry => entry.IsOperationTarget);
            AssertNotice(vm, InfoBarSeverity.Success, "Simulator runtime image exported",
                $"Saved to {installer.ExportPaths!.ImagePath} Temporary data remains in {installer.ExportPaths.TemporaryDirectory}.");
        }, runtimes: runtimes, runtimeInstaller: installer);
    }

    [Fact]
    public Task RuntimeExportFailureKeepsTheOriginalDiagnosticsAndRecoveryLocations()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        var installer = new FakeSimulatorInstaller
        {
            ExportResponse = (_, _, _, output, _) =>
            {
                output?.Invoke(new XcodesOutputLine("Export failed"));
                return Task.FromException<XcodeSimulatorRuntimeExportResult>(new XcodesException("Could not verify image"));
            },
        };
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            vm.ChooseRuntimeExportFolder = () => Task.FromResult<string?>("/tmp/Runtime images");
            vm.ConfirmRuntimeExport = (_, _) => Task.FromResult(true);
            XcodeSimulatorRuntimeItemViewModel item = vm.Runtimes.Items.First(entry => entry.CanInstall);

            await vm.ExportSimulatorRuntimeCommand.ExecuteAsync(item);

            Assert.True(vm.HasOperation);
            Assert.True(vm.IsLogExpanded);
            Assert.Contains("Export failed", vm.OperationLog);
            Assert.Equal(InfoBarSeverity.Error, vm.Notice.Severity);
            Assert.Equal($"Could not export {item.Runtime.Name}", vm.Notice.Title);
            Assert.Contains("Could not verify image", vm.Notice.Message);
            Assert.Contains(installer.ExportPaths!.TemporaryDirectory, vm.Notice.Message);
            Assert.Contains(installer.ExportPaths.PartialPath, vm.Notice.Message);
        }, runtimes: runtimes, runtimeInstaller: installer);
    }

    [Fact]
    public Task PublicRuntimeDownloadConfirmationExplainsAccountAndVerificationLimits() => DispatchAsync(() =>
    {
        XcodeSimulatorRuntime runtime = LegacySimulatorRuntime();
        ImmersiveConfirmationDialog dialog =
            XcodeViewModel.CreateRuntimeDownloadDialog(runtime, "/Users/Example/Runtime images");
        var body = ConfirmationDialogBody(dialog);
        string[] texts = body.Children.OfType<TextBlock>().Select(text => text.Text!).ToArray();

        Assert.Equal("Download", dialog.FindControl<Button>("PrimaryButton")!.Content);
        Assert.Equal("Cancel", dialog.FindControl<Button>("SecondaryButton")!.Content);
        Assert.False(dialog.FocusPrimaryButton);
        Assert.Contains(texts, text => text.Contains("16G73", StringComparison.Ordinal)
            && text.Contains("public CDN", StringComparison.Ordinal)
            && text.Contains("Architecture unknown", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("/Users/Example/Runtime images", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("actual download size may differ", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("no Apple account", StringComparison.Ordinal)
            && text.Contains("does not install", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("does not independently verify", StringComparison.Ordinal)
            && text.Contains("Compatibility", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("incomplete file", StringComparison.Ordinal)
            && text.Contains("cannot be resumed", StringComparison.Ordinal));
        return Task.CompletedTask;
    });

    [Fact]
    public Task PublicRuntimeCancelConfirmationDoesNotPromiseResume() => DispatchAsync(() =>
    {
        ImmersiveConfirmationDialog dialog = XcodeViewModel.CreateCancelDialog(downloadsPublicRuntime: true);
        var body = Assert.IsType<TextBlock>(dialog.FindControl<ContentControl>("BodyPresenter")!.Content);

        Assert.Contains("incomplete file", body.Text);
        Assert.Contains("cannot be resumed", body.Text);
        Assert.False(dialog.FocusPrimaryButton);
        return Task.CompletedTask;
    });

    [Fact]
    public Task SimulatorOperationsDoNotReadTheXcodesAccount()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimesWithLegacy();
        var installer = new FakeSimulatorInstaller();
        var downloader = new FakePublicSimulatorDownloader();
        var fake = new FakeXcodes { Account = new("developer@example.test", null, HasSavedPassword: true) };
        return WithPageAsync(fake, async vm =>
        {
            int accountReads = fake.AccountReadCount;
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            await vm.RefreshSimulatorRuntimesCommand.ExecuteAsync(null);
            vm.ConfirmRuntimeInstall = _ => Task.FromResult(true);
            await vm.InstallSimulatorRuntimeCommand.ExecuteAsync(vm.Runtimes.Items.First(item => item.CanInstall));
            vm.ChooseRuntimeExportFolder = () => Task.FromResult<string?>("/tmp/Runtime images");
            vm.ConfirmRuntimeExport = (_, _) => Task.FromResult(true);
            await vm.ExportSimulatorRuntimeCommand.ExecuteAsync(vm.Runtimes.Items.First(item => item.CanInstall));
            vm.ConfirmRuntimeDownload = (_, _) => Task.FromResult(true);
            await vm.DownloadSimulatorRuntimeCommand.ExecuteAsync(vm.Runtimes.Items.First(item => item.CanDownload));

            Assert.Equal(1, installer.InstallRequests);
            Assert.Equal(1, installer.ExportRequests);
            Assert.Equal(1, downloader.Requests);
            Assert.Equal(accountReads, fake.AccountReadCount);
            Assert.False(vm.Account.IsBusy);
        }, runtimes: runtimes, runtimeInstaller: installer, runtimeDownloader: downloader);
    }

    [Fact]
    public Task PublicRuntimeDownloadUsesTheConfirmedEntryWithoutUsingXcodes()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimesWithLegacy();
        var downloader = new FakePublicSimulatorDownloader();
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            XcodeSimulatorRuntimeItemViewModel item = Assert.Single(vm.Runtimes.Items, entry => entry.CanDownload);
            Assert.False(item.CanInstall);
            vm.ChooseRuntimeExportFolder = () => Task.FromResult<string?>("/tmp/Runtime images");
            XcodeSimulatorRuntime? confirmed = null;
            string? confirmedDirectory = null;
            vm.ConfirmRuntimeDownload = (runtime, directory) =>
            {
                confirmed = runtime;
                confirmedDirectory = directory;
                return Task.FromResult(true);
            };
            string? copied = null;
            vm.CopyText = value =>
            {
                copied = value;
                return Task.CompletedTask;
            };
            fake.Commands.Clear();
            fake.TerminalCommands.Clear();

            await vm.DownloadSimulatorRuntimeCommand.ExecuteAsync(item);

            Assert.Same(item.Runtime, confirmed);
            Assert.Same(confirmed, downloader.Runtime);
            Assert.Equal("/tmp/Runtime images", confirmedDirectory);
            Assert.Equal(confirmedDirectory, downloader.Destination);
            Assert.Equal(1, downloader.Requests);
            Assert.Empty(fake.Commands);
            Assert.Empty(fake.TerminalCommands);
            AssertNotice(vm, InfoBarSeverity.Success, "Simulator runtime image downloaded",
                $"Saved to {downloader.Paths!.ImagePath} Its build and Apple signature have not been independently verified.");
            await Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(
                vm.Notice.ActionButtonCommand).ExecuteAsync(null);
            Assert.Equal(downloader.Paths.ImagePath, copied);
        }, runtimes: runtimes, runtimeDownloader: downloader);
    }

    [Fact]
    public Task DecliningOrUsingStalePublicRuntimeDataCannotStartADownload()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimesWithLegacy();
        var downloader = new FakePublicSimulatorDownloader();
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            XcodeSimulatorRuntimeItemViewModel item = Assert.Single(vm.Runtimes.Items, entry => entry.CanDownload);
            int confirmations = 0;
            vm.ConfirmRuntimeDownload = (_, _) =>
            {
                confirmations++;
                return Task.FromResult(false);
            };
            vm.ChooseRuntimeExportFolder = () => Task.FromResult<string?>(null);
            await vm.DownloadSimulatorRuntimeCommand.ExecuteAsync(item);
            Assert.Equal(0, confirmations);

            vm.ChooseRuntimeExportFolder = () => Task.FromResult<string?>("/tmp/Runtime images");
            await vm.DownloadSimulatorRuntimeCommand.ExecuteAsync(item);
            Assert.Equal(1, confirmations);
            Assert.Equal(0, downloader.Requests);

            runtimes.CatalogError = new HttpRequestException("offline");
            await vm.RefreshSimulatorRuntimesCommand.ExecuteAsync(null);
            Assert.DoesNotContain(vm.Runtimes.Items, entry => entry.CanDownload);
            await vm.DownloadSimulatorRuntimeCommand.ExecuteAsync(item);
            Assert.Equal(0, downloader.Requests);
            AssertNotice(vm, InfoBarSeverity.Error, "Cannot download this simulator runtime",
                "Refresh the Apple simulator catalog and choose a supported runtime.");
        }, runtimes: runtimes, runtimeDownloader: downloader);
    }

    [Fact]
    public Task PublicRuntimeDownloadCancellationLocksOtherActionsAndRetainsThePartialPath()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimesWithLegacy();
        var downloader = new FakePublicSimulatorDownloader
        {
            Response = async (_, _, _, output, cancellation) =>
            {
                output?.Invoke(new XcodesOutputLine("Downloading runtime image: 42%"));
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                throw new InvalidOperationException("The download should have been cancelled.");
            },
        };
        var fake = new FakeXcodes();
        return WithPageAsync(fake, async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            vm.ChooseRuntimeExportFolder = () => Task.FromResult<string?>("/tmp/Runtime images");
            vm.ConfirmRuntimeDownload = (_, _) => Task.FromResult(true);
            string? copied = null;
            vm.CopyText = value =>
            {
                copied = value;
                return Task.CompletedTask;
            };
            XcodeSimulatorRuntimeItemViewModel item = Assert.Single(vm.Runtimes.Items, entry => entry.CanDownload);

            Task operation = vm.DownloadSimulatorRuntimeCommand.ExecuteAsync(item);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsOperationRunning);
            Assert.True(vm.IsBusy);
            Assert.True(vm.Account.IsBusy);
            Assert.False(vm.CanStartOperation);
            Assert.Same(item.Runtime, Assert.Single(vm.Runtimes.Items, entry => entry.IsOperationTarget).Runtime);
            Assert.False(vm.OperationIsIndeterminate);
            Assert.Equal(42, vm.OperationProgress);
            Assert.Equal("Downloading runtime image: 42%", vm.OperationStatus);
            fake.Commands.Clear();
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            Assert.Empty(fake.Commands);

            vm.CancelOperation();
            await operation;
            Assert.False(vm.IsOperationRunning);
            Assert.False(vm.Account.IsBusy);
            Assert.DoesNotContain(vm.Runtimes.Items, entry => entry.IsOperationTarget);
            AssertNotice(vm, InfoBarSeverity.Informational, $"Cancellation requested for {item.Runtime.Name}",
                $"An incomplete download may remain at {downloader.Paths!.PartialPath}.");
            await Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(
                vm.Notice.ActionButtonCommand).ExecuteAsync(null);
            Assert.Equal(downloader.Paths.PartialPath, copied);
        }, runtimes: runtimes, runtimeDownloader: downloader);
    }

    [Fact]
    public Task PublicRuntimeDownloadReportsHttpFailureAndDoesNotRetryOrGuessAnAccountCause()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimesWithLegacy();
        var downloader = new FakePublicSimulatorDownloader
        {
            Response = (_, _, _, _, _) => Task.FromException<string>(
                new HttpRequestException("Denied", null, System.Net.HttpStatusCode.Forbidden)),
        };
        return WithPageAsync(new FakeXcodes(), async vm =>
        {
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            vm.ChooseRuntimeExportFolder = () => Task.FromResult<string?>("/tmp/Runtime images");
            vm.ConfirmRuntimeDownload = (_, _) => Task.FromResult(true);
            XcodeSimulatorRuntimeItemViewModel item = Assert.Single(vm.Runtimes.Items, entry => entry.CanDownload);

            await vm.DownloadSimulatorRuntimeCommand.ExecuteAsync(item);

            Assert.Equal(1, downloader.Requests);
            Assert.False(vm.HasAccessRecovery);
            AssertNotice(vm, InfoBarSeverity.Error, $"Could not download {item.Runtime.Name}",
                $"The Apple runtime download returned HTTP 403. An incomplete download may remain at {downloader.Paths!.PartialPath}.");
        }, runtimes: runtimes, runtimeDownloader: downloader);
    }

    [Fact]
    public Task TheSimulatorBrowserBindsThePublicDownloadOnlyForEligibleRows() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes();
        FakeSimulatorRuntimes runtimes = SimulatorRuntimesWithLegacy();
        var downloader = new FakePublicSimulatorDownloader();
        var vm = new XcodeViewModel(fake.Service, runtimes: runtimes, runtimeDownloader: downloader);
        var view = new XcodePage(vm);
        var window = new Window { Width = 1100, Height = 900, Content = view };
        try
        {
            await vm.InitializeAsync();
            window.Show();
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            vm.Runtimes.SearchText = "12.4";
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            XcodeSimulatorRuntimeItemViewModel item = Assert.Single(vm.Runtimes.Items);
            Assert.True(item.CanDownload);
            Assert.False(item.CanInstall);
            Button download = view.GetVisualDescendants().OfType<Button>()
                .First(button => Equals(button.Content, "Download from Apple") && button.IsEffectivelyVisible);
            Assert.Same(vm.DownloadSimulatorRuntimeCommand, download.Command);
            Assert.Same(item, download.CommandParameter);
            Assert.True(download.IsEnabled);
            vm.IsBusy = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(download.IsEnabled);
            vm.IsBusy = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(download.IsEnabled);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), button =>
                button.IsEffectivelyVisible && (Equals(button.Content, "Install with Xcode")
                    || Equals(button.Content, "Export with Xcode")));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), button =>
                button.IsEffectivelyVisible && (ReferenceEquals(button.Command, vm.RevealSimulatorRuntimeInFinderCommand)
                    || ReferenceEquals(button.Command, vm.CopySimulatorRuntimePathCommand)));
        }
        finally
        {
            vm.Stop();
            window.Close();
        }
    });

    [Fact]
    public Task SimulatorRowShowsLiveProgressAndConfirmsKeyboardCancellation() => DispatchAsync(async () =>
    {
        EnsureTheme();
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<XcodesOutputLine>? output = null;
        var installer = new FakeSimulatorInstaller
        {
            InstallResponse = async (_, report, cancellation) =>
            {
                output = report;
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                return XcodeSimulatorRuntimeInstallOutcome.Installed;
            },
        };
        var vm = new XcodeViewModel(new FakeXcodes().Service, runtimes: runtimes, runtimeInstaller: installer)
        {
            ConfirmRuntimeInstall = _ => Task.FromResult(true),
            ConfirmCancel = () => Task.FromResult(false),
        };
        var view = new XcodePage(vm);
        var window = new Window { Width = 1100, Height = 900, Content = view };
        Task? installing = null;
        try
        {
            await vm.InitializeAsync();
            window.Show();
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            vm.Runtimes.SearchText = "26.4";
            vm.Runtimes.SourceFilterIndex = 2;
            vm.Runtimes.ArchitectureFilterIndex = 1;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            XcodeSimulatorRuntimeItemViewModel selected = Assert.Single(vm.Runtimes.Items);
            installing = vm.InstallSimulatorRuntimeCommand.ExecuteAsync(selected);
            await started.Task;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var list = view.FindControl<ListBox>("SimulatorRuntimeList")!;
            StackPanel row = Assert.Single(list.GetVisualDescendants().OfType<StackPanel>(),
                panel => panel.Name == "RuntimeRowProgress" && panel.IsEffectivelyVisible);
            var progress = Assert.Single(row.GetVisualDescendants().OfType<ProgressBar>(),
                bar => bar.Name == "RuntimeRowOperationProgress");
            var status = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Name == "RuntimeRowOperationStatus");
            Button cancel = Assert.Single(list.GetVisualDescendants().OfType<Button>(),
                button => button.Name == "CancelRuntimeRowOperation" && button.IsEffectivelyVisible);
            Assert.Equal("Starting xcodebuild...", status.Text);
            Assert.True(progress.IsIndeterminate);
            Assert.Same(vm.RequestCancelOperationCommand, cancel.Command);
            Assert.Equal(selected.CancelOperationText, AutomationProperties.GetName(cancel));

            int catalogRequests = runtimes.CatalogRequests;
            view.FindControl<Button>("ShowXcodeVersions")!.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var openRuntimes = view.FindControl<Button>("OpenSimulatorRuntimes")!;
            Assert.True(openRuntimes.IsEffectivelyVisible);
            Assert.True(openRuntimes.IsEnabled);
            openRuntimes.Command!.Execute(null);
            await vm.OpenSimulatorRuntimesCommand.ExecutionTask!;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(catalogRequests, runtimes.CatalogRequests);
            Assert.True(row.IsEffectivelyVisible);

            output!(new XcodesOutputLine("Downloading: 42%"));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal("Downloading: 42%", status.Text);
            Assert.False(progress.IsIndeterminate);
            Assert.Equal(42, progress.Value);

            vm.Runtimes.SourceFilterIndex = 1;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.DoesNotContain(vm.Runtimes.Items, item => item.IsOperationTarget);
            Assert.True(view.FindControl<Border>("OperationPanel")!.IsEffectivelyVisible);
            vm.Runtimes.SourceFilterIndex = 2;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            row = Assert.Single(list.GetVisualDescendants().OfType<StackPanel>(),
                panel => panel.Name == "RuntimeRowProgress" && panel.IsEffectivelyVisible);
            cancel = Assert.Single(list.GetVisualDescendants().OfType<Button>(),
                button => button.Name == "CancelRuntimeRowOperation" && button.IsEffectivelyVisible);
            Assert.Same(selected.Runtime, Assert.IsType<XcodeSimulatorRuntimeItemViewModel>(row.DataContext).Runtime);

            window.Width = 480;
            window.Height = 360;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(cancel.IsEnabled);
            Assert.True(cancel.Focus(NavigationMethod.Tab));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var pageScroll = view.FindControl<ScrollViewer>("XcodePageScroll")!;
            Point? cancelPosition = cancel.TranslatePoint(new Point(0, 0), pageScroll);
            Assert.NotNull(cancelPosition);
            Assert.InRange(cancelPosition.Value.X, 0, pageScroll.Viewport.Width - cancel.Bounds.Width);
            Assert.InRange(cancelPosition.Value.Y, 0, pageScroll.Viewport.Height - cancel.Bounds.Height);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await vm.RequestCancelOperationCommand.ExecutionTask!;
            Assert.True(vm.IsOperationRunning);

            vm.ConfirmCancel = () => Task.FromResult(true);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await vm.RequestCancelOperationCommand.ExecutionTask!;
            await installing;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(vm.IsOperationRunning);
            Assert.DoesNotContain(vm.Runtimes.Items, item => item.IsOperationTarget);
            Assert.DoesNotContain(list.GetVisualDescendants().OfType<StackPanel>(),
                panel => panel.Name == "RuntimeRowProgress" && panel.IsEffectivelyVisible);
        }
        finally
        {
            vm.Stop();
            if (installing is not null)
                await installing;
            window.Close();
        }
    });

    // ─── The page itself ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TheBrowsersRemainReachableInAShortWindow(bool simulators) => DispatchAsync(async () =>
    {
        EnsureTheme();
        var vm = new XcodeViewModel(new FakeXcodes().Service, runtimes: SimulatorRuntimes());
        var view = new XcodePage(vm);
        var window = new Window { Width = 480, Height = 360, Content = view };
        try
        {
            await vm.InitializeAsync();
            if (simulators)
                await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            else
                vm.SelectedBrowserItem = vm.BrowserItems.First(item => item.IsInstalled);

            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var scroll = Assert.IsType<ScrollViewer>(view.FindControl<ScrollViewer>("XcodePageScroll"));
            Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height,
                $"The page did not expose its clipped content: extent {scroll.Extent}, viewport {scroll.Viewport}.");
            Assert.True(scroll.Focus(NavigationMethod.Tab));
            window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(scroll.Offset.Y > 0);

            for (int page = 0; page < 20; page++)
                window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
            scroll.Offset = new Vector(Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width), scroll.Offset.Y);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var list = view.FindControl<ListBox>(simulators ? "SimulatorRuntimeList" : "VersionList")!;
            var location = list.TranslatePoint(new Point(0, 0), scroll);
            Assert.NotNull(location);
            Assert.True(list.Bounds.Height >= 80, $"The list is too short to use: {list.Bounds}.");
            Assert.True(location.Value.Y < scroll.Viewport.Height && location.Value.Y + list.Bounds.Height > 0,
                $"The list remains outside the viewport: {location}, {list.Bounds}, {scroll.Viewport}.");

            if (simulators)
            {
                list.ScrollIntoView(vm.Runtimes.Items.First(item => item.CanInstall));
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
            Button action = simulators
                ? view.GetVisualDescendants().OfType<Button>()
                    .First(button => Equals(button.Content, "Install with Xcode") && button.IsEffectivelyVisible)
                : view.FindControl<Button>("OpenSelectedXcode")!;
            Assert.True(action.Focus(NavigationMethod.Tab));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var actionLocation = action.TranslatePoint(new Point(0, 0), scroll);
            Assert.NotNull(actionLocation);
            Assert.True(actionLocation.Value.X >= 0
                && actionLocation.Value.X + action.Bounds.Width <= scroll.Viewport.Width
                && actionLocation.Value.Y >= 0
                && actionLocation.Value.Y + action.Bounds.Height <= scroll.Viewport.Height,
                $"The browser action remains outside the viewport: {actionLocation}, {action.Bounds}, "
                + $"offset {scroll.Offset}, extent {scroll.Extent}, viewport {scroll.Viewport}.");

            if (simulators)
            {
                list.ScrollIntoView(vm.Runtimes.Items.First(item => item.HasLocalPath));
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Button reveal = view.GetVisualDescendants().OfType<Button>().First(button =>
                    button.IsEffectivelyVisible && ReferenceEquals(button.Command, vm.RevealSimulatorRuntimeInFinderCommand));
                Assert.True(reveal.Focus(NavigationMethod.Tab));
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var revealLocation = reveal.TranslatePoint(new Point(0, 0), scroll);
                Assert.NotNull(revealLocation);
                Assert.True(revealLocation.Value.X >= 0
                    && revealLocation.Value.X + reveal.Bounds.Width <= scroll.Viewport.Width
                    && revealLocation.Value.Y >= 0
                    && revealLocation.Value.Y + reveal.Bounds.Height <= scroll.Viewport.Height,
                    $"The runtime reveal action remains outside the viewport: {revealLocation}, {reveal.Bounds}, "
                    + $"offset {scroll.Offset}, extent {scroll.Extent}, viewport {scroll.Viewport}.");
            }
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task TheVersionBrowserKeepsItsOwnScrollingInALargeWindow() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes
        {
            ListOutput = string.Join("\n", Enumerable.Range(1, 100)
                .Select(index => $"26.{index} (17A{index:D3}) [Apple Silicon]")),
        };
        var vm = new XcodeViewModel(fake.Service);
        var view = new XcodePage(vm);
        var window = new Window { Width = 1100, Height = 900, Content = view };
        try
        {
            await vm.InitializeAsync();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var scroll = view.FindControl<ScrollViewer>("XcodePageScroll")!;
            var list = view.FindControl<ListBox>("VersionList")!;
            Assert.True(vm.BrowserItems.Count >= 100);
            Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1,
                $"The page grew with its version list: list {list.Bounds}, extent {scroll.Extent}, viewport {scroll.Viewport}.");
            Assert.True(list.Bounds.Height >= 300, $"The list did not fill the available space: {list.Bounds}.");
            var listScroll = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.True(listScroll.Extent.Height > listScroll.Viewport.Height);
            list.ScrollIntoView(vm.BrowserItems.Last());
            Dispatcher.UIThread.RunJobs();
            Assert.True(listScroll.Offset.Y > 0);

            vm.GroupVersions = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var tree = view.FindControl<TreeView>("GroupedVersionList")!;
            Assert.True(tree.IsEffectivelyVisible);
            Assert.True(tree.Bounds.Height >= 300);
            Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1);

            vm.GroupVersions = false;
            window.Height = 360;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            Assert.True(list.Bounds.Height >= 80);

            window.Height = 900;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1);
            Assert.True(list.Bounds.Height >= 300);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task TheSimulatorBrowserBoundsLongCatalogsAndResizes() => DispatchAsync(async () =>
    {
        EnsureTheme();
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        XcodeSimulatorRuntime template = runtimes.Catalog[0];
        runtimes.Catalog = [.. Enumerable.Range(1, 100).Select(index => template with
        {
            Name = $"iOS 26.{index} Simulator Runtime",
            Version = $"26.{index}",
            Build = $"23E{index:D3}",
        })];
        var vm = new XcodeViewModel(new FakeXcodes().Service, runtimes: runtimes);
        var view = new XcodePage(vm);
        var window = new Window { Width = 1100, Height = 900, Content = view };
        try
        {
            await vm.InitializeAsync();
            window.Show();
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var scroll = view.FindControl<ScrollViewer>("XcodePageScroll")!;
            var list = view.FindControl<ListBox>("SimulatorRuntimeList")!;
            var listScroll = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.True(vm.Runtimes.Items.Count >= 100);
            Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1,
                $"The page grew with its runtime list: list {list.Bounds}, extent {scroll.Extent}, viewport {scroll.Viewport}.");
            Assert.True(list.Bounds.Height >= 200);
            Assert.True(listScroll.Extent.Height > listScroll.Viewport.Height);
            list.ScrollIntoView(vm.Runtimes.Items.Last());
            Dispatcher.UIThread.RunJobs();
            Assert.True(listScroll.Offset.Y > 0);

            window.Width = 480;
            window.Height = 360;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            Assert.True(list.Bounds.Height >= 80);
            window.Width = 1100;
            window.Height = 900;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1);
            Assert.True(list.Bounds.Height >= 200);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task AccessRecoveryRemainsReachableWhenItAppearsInAShortWindow() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes { Account = new("wrong@example.test", null, HasSavedPassword: true) };
        fake.RespondInTerminal = (_, _, output, _) =>
        {
            output?.Invoke(new XcodesOutputLine("HTTP 403: access denied"));
            return Task.FromResult(FakeXcodes.Failure("HTTP 403: access denied"));
        };
        var vm = new XcodeViewModel(fake.Service) { ConfirmInstall = (_, _, _, _) => Task.FromResult(true) };
        var view = new XcodePage(vm);
        var window = new Window { Width = 480, Height = 360, Content = view };
        try
        {
            await vm.InitializeAsync();
            window.Show();
            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.True(vm.HasAccessRecovery);
            Assert.Contains("HTTP 403", vm.OperationLog);
            var scroll = view.FindControl<ScrollViewer>("XcodePageScroll")!;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            Button retry = Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "Retry") && button.IsEffectivelyVisible);
            Assert.True(retry.Focus(NavigationMethod.Tab));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var retryLocation = retry.TranslatePoint(new Point(0, 0), scroll);
            Assert.NotNull(retryLocation);
            Assert.True(retryLocation.Value.X >= 0
                && retryLocation.Value.X + retry.Bounds.Width <= scroll.Viewport.Width
                && retryLocation.Value.Y >= 0
                && retryLocation.Value.Y + retry.Bounds.Height <= scroll.Viewport.Height);

            var list = view.FindControl<ListBox>("VersionList")!;
            Assert.True(scroll.Focus(NavigationMethod.Tab));
            for (int page = 0; page < 20; page++)
                window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var listLocation = list.TranslatePoint(new Point(0, 0), scroll);
            Assert.NotNull(listLocation);
            Assert.True(list.Bounds.Height >= 80);
            Assert.True(listLocation.Value.Y < scroll.Viewport.Height
                && listLocation.Value.Y + list.Bounds.Height > 0);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task LongRuntimeMappingDetailsDoNotHideTheList() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var vm = new XcodeViewModel(new FakeXcodes().Service, runtimes: SimulatorRuntimes());
        var view = new XcodePage(vm);
        var window = new Window { Width = 480, Height = 360, Content = view };
        try
        {
            await vm.InitializeAsync();
            window.Show();
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var scroll = view.FindControl<ScrollViewer>("XcodePageScroll")!;
            double originalExtent = scroll.Extent.Height;
            string title = string.Join(" ", Enumerable.Repeat("Xcode development build location", 12));
            string status = string.Join(" ", Enumerable.Repeat("Saved release details may be out of date.", 12));
            vm.Runtimes.ShowMappedFor(title, [], status);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.True(scroll.Extent.Height > originalExtent);
            scroll.ScrollToEnd();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var region = view.FindControl<Grid>("RuntimeListRegion")!;
            var location = region.TranslatePoint(new Point(0, 0), scroll);
            Assert.NotNull(location);
            Assert.True(region.Bounds.Height >= 120);
            Assert.True(location.Value.Y < scroll.Viewport.Height
                && location.Value.Y + region.Bounds.Height > 0);

            var showAll = view.FindControl<Button>("ShowAllSimulatorRuntimes")!;
            Assert.True(showAll.Focus(NavigationMethod.Tab));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var buttonLocation = showAll.TranslatePoint(new Point(0, 0), scroll);
            Assert.NotNull(buttonLocation);
            Assert.True(buttonLocation.Value.Y >= 0
                && buttonLocation.Value.Y + showAll.Bounds.Height <= scroll.Viewport.Height);
            showAll.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(vm.Runtimes.HasMappingContext);
            Assert.True(view.FindControl<ListBox>("SimulatorRuntimeList")!.Bounds.Height >= 80);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task ThePageFitsTheWindowWidthAndSilencesLiveRegionsWhenEmptyOrDetached() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes();
        var vm = new XcodeViewModel(fake.Service, runtimes: SimulatorRuntimes());
        var view = new XcodePage(vm);
        var window = new Window { Width = 1100, Height = 900, Content = view };
        try
        {
            await vm.InitializeAsync();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var scroll = view.FindControl<ScrollViewer>("XcodePageScroll")!;
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1,
                $"The Xcode page overflowed horizontally: extent {scroll.Extent}, viewport {scroll.Viewport}.");

            Button signIn = Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "Sign in") && button.IsEffectivelyVisible);
            Point? signInRight = signIn.TranslatePoint(new Point(signIn.Bounds.Width, 0), scroll);
            Assert.NotNull(signInRight);
            Assert.InRange(signInRight.Value.X, 0, scroll.Viewport.Width + 1);

            var versionStatus = view.FindControl<TextBlock>("VersionListStatus")!;
            var catalogStatus = view.FindControl<TextBlock>("CatalogStatus")!;
            var simulatorStatus = view.FindControl<TextBlock>("SimulatorCatalogStatus")!;
            Assert.NotEmpty(versionStatus.Text!);
            Assert.Equal(versionStatus.Text, AutomationProperties.GetName(versionStatus));
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(versionStatus));

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(string.Empty, versionStatus.Text);
            Assert.Equal(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(versionStatus));
            Assert.Equal(string.Empty, AutomationProperties.GetName(versionStatus));

            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1,
                $"The simulator runtimes view overflowed horizontally: extent {scroll.Extent}, viewport {scroll.Viewport}.");

            Button export = view.GetVisualDescendants().OfType<Button>()
                .First(button => Equals(button.Content, "Export with Xcode") && button.IsEffectivelyVisible);
            Point? exportRight = export.TranslatePoint(new Point(export.Bounds.Width, 0), scroll);
            Assert.NotNull(exportRight);
            Assert.InRange(exportRight.Value.X, 0, scroll.Viewport.Width + 1);

            Assert.NotEmpty(simulatorStatus.Text!);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(simulatorStatus));

            // Detaching the page must turn off LiveSetting before clearing Name so macOS Avalonia.Native
            // never raises a live-region notification with an empty Name.
            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            foreach (TextBlock status in new[] { versionStatus, catalogStatus, simulatorStatus })
            {
                Assert.Equal(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(status));
                Assert.Equal(string.Empty, AutomationProperties.GetName(status));
            }

            window.Content = view;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(simulatorStatus.Text, AutomationProperties.GetName(simulatorStatus));
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(simulatorStatus));
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task InstalledSummaryAndFocusCommandSurfaceInstalledXcodesClearly() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes();
        var vm = new XcodeViewModel(fake.Service, runtimes: SimulatorRuntimes());
        var view = new XcodePage(vm);
        var window = new Window { Width = 1100, Height = 800, Content = view };
        try
        {
            await vm.InitializeAsync();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var summaryButton = view.FindControl<Button>("InstalledSummaryButton")!;
            var noneBadge = view.FindControl<Border>("NoneInstalledBadge")!;
            Assert.True(summaryButton.IsEffectivelyVisible);
            Assert.False(noneBadge.IsEffectivelyVisible);
            Assert.Equal("2", vm.InstalledCountText);
            Assert.Equal("2 versions installed", vm.InstalledSummaryTitle);
            Assert.Equal("Active: Xcode 26.4 · /Applications/Xcode-26.4.app", vm.InstalledSummaryDetail);
            Assert.Contains("Installed on this Mac", VisibleTexts(view));

            XcodeBrowserNodeViewModel major = Assert.Single(vm.BrowserGroups);
            Assert.True(major.HasActiveChild);
            Assert.False(major.HasInactiveInstalledChild);
            XcodeBrowserNodeViewModel minor262 = Assert.Single(major.Children, child => child.Title == "26.2");
            Assert.False(minor262.HasActiveChild);
            Assert.True(minor262.HasInactiveInstalledChild);

            // Selecting an uninstalled build then invoking FocusInstalled jumps back to the active installed Xcode.
            vm.SelectedBrowserItem = AvailableBuild(vm, "26.5", "17F42");
            Assert.False(vm.SelectedBrowserItem!.IsInstalled);
            vm.FocusInstalledCommand.Execute(null);
            Assert.True(vm.SelectedBrowserItem!.IsInstalled);
            Assert.Equal("/Applications/Xcode-26.4.app", vm.SelectedBrowserItem.Location);
            Assert.False(vm.InstalledOnly);

            // Invoking FocusInstalled again while an installed Xcode is selected toggles "Installed only".
            vm.FocusInstalledCommand.Execute(null);
            Assert.True(vm.InstalledOnly);
            Assert.All(vm.BrowserItems, item => Assert.True(item.IsInstalled));
            vm.FocusInstalledCommand.Execute(null);
            Assert.False(vm.InstalledOnly);

            // Invoking FocusInstalled from Simulator Runtimes or with blocking filters reveals the installed Xcode.
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            vm.SearchText = "26.5";
            vm.GroupVersions = true;
            major.IsExpanded = false;
            vm.FocusInstalledCommand.Execute(null);
            Assert.False(vm.ShowSimulatorRuntimes);
            Assert.Equal("", vm.SearchText);
            Assert.Equal("/Applications/Xcode-26.4.app", vm.SelectedBrowserItem?.Location);
            Assert.True(Assert.Single(vm.BrowserGroups).IsExpanded);

            // When no Xcode is installed, the summary pill gives way to "No Xcode installed".
            fake.InstalledOutput = "";
            await vm.ReloadAsync();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(summaryButton.IsEffectivelyVisible);
            Assert.True(noneBadge.IsEffectivelyVisible);
            Assert.Equal("0", vm.InstalledCountText);
            Assert.Contains("No Xcode installed", VisibleTexts(view));
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task TheSimulatorBrowserRestoresSavedFiltersIntoItsControls() => DispatchAsync(async () =>
    {
        EnsureTheme();
        string settingsDirectory = CoreData.UniGetUIUserConfigurationDirectory;
        File.WriteAllText(Path.Combine(settingsDirectory, "XcodeRuntimePlatformFilter"), "ios");
        File.WriteAllText(Path.Combine(settingsDirectory, "XcodeRuntimeSourceFilter"), "apple-catalog");
        File.WriteAllText(Path.Combine(settingsDirectory, "XcodeRuntimeReleaseFilter"), "prerelease");
        File.WriteAllText(Path.Combine(settingsDirectory, "XcodeRuntimeArchitectureFilter"), "apple-silicon");

        var fake = new FakeXcodes();
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        var vm = new XcodeViewModel(fake.Service, runtimes: runtimes);
        var view = new XcodePage(vm);
        var window = new Window { Width = 1100, Height = 900, Content = view };

        try
        {
            await vm.InitializeAsync();
            window.Show();
            await vm.OpenSimulatorRuntimesCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.Equal(1, view.FindControl<ComboBox>("SimulatorRuntimePlatformFilter")!.SelectedIndex);
            Assert.Equal(2, view.FindControl<ComboBox>("SimulatorRuntimeSourceFilter")!.SelectedIndex);
            Assert.Equal(2, view.FindControl<ComboBox>("SimulatorRuntimeReleaseFilter")!.SelectedIndex);
            Assert.Equal(1, view.FindControl<ComboBox>("SimulatorRuntimeArchitectureFilter")!.SelectedIndex);
            Assert.Equal("iOS 27.0 beta Simulator Runtime", Assert.Single(vm.Runtimes.Items).Title);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task TheSimulatorBrowserRendersAndBindsItsControls() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes();
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        var installer = new FakeSimulatorInstaller();
        var vm = new XcodeViewModel(fake.Service, runtimes: runtimes, runtimeInstaller: installer);
        var view = new XcodePage(vm);
        var window = new Window { Width = 1100, Height = 900, Content = view };

        try
        {
            await vm.InitializeAsync();
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Button open = view.FindControl<Button>("OpenSimulatorRuntimes")!;
            Assert.Same(vm.OpenSimulatorRuntimesCommand, open.Command);
            open.Command!.Execute(null);
            await vm.OpenSimulatorRuntimesCommand.ExecutionTask!;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var list = view.FindControl<ListBox>("SimulatorRuntimeList")!;
            Assert.True(list.IsEffectivelyVisible);
            Assert.Same(vm.Runtimes.Items, list.ItemsSource);
            string[] shown = VisibleTexts(view);
            Assert.Contains("Simulator runtimes", shown);
            Assert.Contains(shown, text => text.Contains("DEVELOPER_DIR", StringComparison.Ordinal));
            Assert.Contains(shown, text => text.Contains("Some legacy images can be downloaded", StringComparison.Ordinal));
            Assert.Contains(shown, text => text.Contains("Installed runtimes refreshed", StringComparison.Ordinal));
            Button install = view.GetVisualDescendants().OfType<Button>()
                .First(button => Equals(button.Content, "Install with Xcode") && button.IsEffectivelyVisible);
            Assert.Same(vm.InstallSimulatorRuntimeCommand, install.Command);
            Assert.True(Assert.IsType<XcodeSimulatorRuntimeItemViewModel>(install.CommandParameter).CanInstall);
            Button export = view.GetVisualDescendants().OfType<Button>()
                .First(button => Equals(button.Content, "Export with Xcode") && button.IsEffectivelyVisible);
            Assert.Same(vm.ExportSimulatorRuntimeCommand, export.Command);
            Assert.True(Assert.IsType<XcodeSimulatorRuntimeItemViewModel>(export.CommandParameter).CanInstall);
            vm.IsBusy = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(install.IsEnabled);
            Assert.False(export.IsEnabled);
            vm.IsBusy = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(install.IsEnabled);
            Assert.True(export.IsEnabled);
            bool confirmed = false;
            vm.ConfirmRuntimeInstall = _ =>
            {
                confirmed = true;
                return Task.FromResult(false);
            };
            install.Command!.Execute(install.CommandParameter);
            await vm.InstallSimulatorRuntimeCommand.ExecutionTask!;
            Assert.True(confirmed);
            Assert.Equal(0, installer.InstallRequests);

            var source = view.FindControl<ComboBox>("SimulatorRuntimeSourceFilter")!;
            source.SetCurrentValue(ComboBox.SelectedIndexProperty, 1);
            var platform = view.FindControl<ComboBox>("SimulatorRuntimePlatformFilter")!;
            platform.SetCurrentValue(ComboBox.SelectedIndexProperty, 1);
            var release = view.FindControl<ComboBox>("SimulatorRuntimeReleaseFilter")!;
            release.SetCurrentValue(ComboBox.SelectedIndexProperty, 1);
            var architecture = view.FindControl<ComboBox>("SimulatorRuntimeArchitectureFilter")!;
            architecture.SetCurrentValue(ComboBox.SelectedIndexProperty, 2);
            var search = view.FindControl<TextBox>("SimulatorRuntimeSearch")!;
            search.SetCurrentValue(TextBox.TextProperty, "/Bundled/");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(1, vm.Runtimes.SourceFilterIndex);
            Assert.Equal(1, vm.Runtimes.PlatformFilterIndex);
            Assert.Equal(1, vm.Runtimes.ReleaseFilterIndex);
            Assert.Equal(2, vm.Runtimes.ArchitectureFilterIndex);
            Assert.Equal("/Bundled/", vm.Runtimes.SearchText);
            Assert.Equal(XcodeSimulatorRuntimeOrigin.Bundled, Assert.Single(vm.Runtimes.Items).Runtime.Origin);
            Assert.Same(vm.Runtimes.Items, list.ItemsSource);
            shown = VisibleTexts(view);
            Assert.Contains("iOS 26.4", shown);
            Assert.Contains(shown, text => text.Contains("Bundled with selected developer tools", StringComparison.Ordinal));
            Assert.Contains("/Applications/Xcode.app/Bundled/iOS.simruntime", shown);
            Button reveal = view.GetVisualDescendants().OfType<Button>().Single(button =>
                button.IsEffectivelyVisible && ReferenceEquals(button.Command, vm.RevealSimulatorRuntimeInFinderCommand));
            Button copy = view.GetVisualDescendants().OfType<Button>().Single(button =>
                button.IsEffectivelyVisible && ReferenceEquals(button.Command, vm.CopySimulatorRuntimePathCommand));
            Assert.Same(vm.Runtimes.Items[0], reveal.CommandParameter);
            Assert.Same(vm.Runtimes.Items[0], copy.CommandParameter);
            string? revealed = null;
            string? copied = null;
            vm.ShowSimulatorPathInFinder = path =>
            {
                revealed = path;
                return Task.CompletedTask;
            };
            vm.CopyText = path =>
            {
                copied = path;
                return Task.CompletedTask;
            };
            vm.IsBusy = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(reveal.IsEnabled);
            Assert.True(copy.IsEnabled);
            reveal.Command!.Execute(reveal.CommandParameter);
            copy.Command!.Execute(copy.CommandParameter);
            await vm.RevealSimulatorRuntimeInFinderCommand.ExecutionTask!;
            await vm.CopySimulatorRuntimePathCommand.ExecutionTask!;
            Assert.Equal("/Applications/Xcode.app/Bundled/iOS.simruntime", revealed);
            Assert.Equal(revealed, copied);
            vm.IsBusy = false;
            Dispatcher.UIThread.RunJobs();

            Button refresh = view.FindControl<Button>("RefreshSimulatorRuntimes")!;
            Assert.Same(vm.RefreshSimulatorRuntimesCommand, refresh.Command);
            Assert.True(refresh.IsEnabled);
            var gate = new TaskCompletionSource<IReadOnlyList<XcodeSimulatorRuntime>>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtimes.CatalogResponse = cancellation => gate.Task.WaitAsync(cancellation);
            refresh.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsBusy);
            Assert.False(refresh.IsEnabled);
            gate.SetResult(runtimes.Catalog);
            await vm.RefreshSimulatorRuntimesCommand.ExecutionTask!;
            Dispatcher.UIThread.RunJobs();
            Assert.True(refresh.IsEnabled);
            Assert.Equal(2, runtimes.CatalogRequests);
            Assert.Equal(2, runtimes.InstalledRequests);

            Button back = view.FindControl<Button>("ShowXcodeVersions")!;
            Assert.Same(vm.ShowXcodeVersionsCommand, back.Command);
            back.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(list.IsEffectivelyVisible);
            Assert.True(vm.ShowVersionBrowser);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

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
            Assert.Contains("Installed only", shown);
            Assert.Contains("All versions", shown);
            Assert.Contains("Xcode 26.4", shown);
            Assert.Contains("Xcode 26.5", shown);
            Assert.Contains("Not signed in to Apple", shown);
            Assert.Contains("Sign in", shown);
            Assert.Contains(shown, text => text.Contains("Accounts saved by other apps or command-line tools are not used."));
            Assert.Contains(shown, text => text.Contains("Accounts that require security keys or federated sign-in are not supported yet."));
            Assert.DoesNotContain("Sign out", shown);
            Assert.Contains("Update to Xcode 26.5", shown);
            Assert.DoesNotContain(shown, text => text.Contains("xcodes", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task TheAccountCardShowsTheNewIdentityBeforeSignOut() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes { Account = new("old@example.test", null, HasSavedPassword: true) };
        var vm = new XcodeViewModel(fake.Service, catalog: new FakeCatalog());
        var view = new XcodePage(vm);
        var window = new Window { Width = 900, Height = 900, Content = view };

        try
        {
            await vm.InitializeAsync();
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            string[] before = VisibleTexts(view);
            Assert.Contains("Saved Apple ID: old@example.test", before);
            Assert.Contains("Verify session", before);
            Assert.Contains("Use a different Apple ID", before);

            Button signOut = view.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Sign out"));
            Assert.Same(vm.Account.SignOutCommand, signOut.Command);
            fake.Commands.Clear();
            fake.Account = new("new@example.test", null, HasSavedPassword: true);
            signOut.Command!.Execute(null);
            await vm.Account.SignOutCommand.ExecutionTask!;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.Empty(fake.Commands);
            string[] shown = VisibleTexts(view);
            Assert.Contains("Saved Apple ID: new@example.test", shown);
            Assert.Contains("Apple ID changed", shown);
            Assert.Contains("Review the current Apple ID before signing out.", shown);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task ThePageKeepsTheSavedListStatusVisibleAfterADismissedCheckFailure() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes();
        bool updateFails = true;
        fake.Override(command => command == "update" && updateFails ? FakeXcodes.Failure("The network is down") : null);
        var vm = new XcodeViewModel(fake.Service);
        var view = new XcodePage(vm);
        var window = new Window { Width = 900, Height = 700, Content = view };

        try
        {
            await vm.InitializeAsync();
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var status = view.FindControl<TextBlock>("VersionListStatus")!;
            Assert.True(status.IsEffectivelyVisible);
            Assert.Equal("Showing saved Xcode versions; their age is unknown.", status.Text);

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            vm.Notice.IsOpen = false;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(status.IsEffectivelyVisible);
            Assert.Equal("Could not check for new Xcode versions. Showing the saved list.", status.Text);
            Assert.Contains(status.Text, VisibleTexts(view));

            updateFails = false;
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(status.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task ThePageShowsTheReleaseListFailureAndRecovers(bool hasInstalled) => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes { InstalledOutput = hasInstalled ? DefaultInstalled : "" };
        bool broken = true;
        fake.Override(command => command == "list" && broken ? FakeXcodes.Failure("saved list unavailable") : null);
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
            Assert.Contains("Could not read the Xcode versions", shown);
            Assert.Contains("saved list unavailable", shown);
            Assert.DoesNotContain("Update to Xcode 26.5", shown);
            if (hasInstalled)
                Assert.Contains("Xcode 26.4", shown);
            else
                Assert.Contains("Xcode versions are unavailable. Try checking for updates again.", shown);

            broken = false;
            await vm.ReloadAsync();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            shown = VisibleTexts(view);
            Assert.DoesNotContain("saved list unavailable", shown);
            Assert.Contains("Xcode 26.5", shown);
            Assert.True(vm.HasLoaded);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task AccessRecoveryWaitsForTheVersionListBeforeEnablingRetry() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes();
        bool broken = false;
        fake.Override(command => command == "list" && broken ? FakeXcodes.Failure("saved list unavailable") : null);
        fake.RespondInTerminal = (_, _, output, _) =>
        {
            broken = true;
            output?.Invoke(new XcodesOutputLine("HTTP 403: access denied"));
            return Task.FromResult(FakeXcodes.Failure("HTTP 403: access denied"));
        };
        var vm = new XcodeViewModel(fake.Service) { ConfirmInstall = (_, _, _, _) => Task.FromResult(true) };
        var view = new XcodePage(vm);
        var window = new Window { Width = 900, Height = 700, Content = view };

        try
        {
            await vm.InitializeAsync();
            await vm.InstallCommand.ExecuteAsync(vm.Available[0]);
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.Contains("Refresh the Xcode version list before retrying.", VisibleTexts(view));
            Button retry = Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "Retry") && button.IsEffectivelyVisible);
            Assert.False(retry.IsEnabled);

            broken = false;
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.DoesNotContain("Refresh the Xcode version list before retrying.", VisibleTexts(view));
            Assert.True(retry.IsEnabled);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task ThePageShowsStableCounterpartOnlyForTheActivePrerelease() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes
        {
            InstalledOutput = "26.5 Beta 2 (17F42) [Apple Silicon] (Selected)\t/Applications/Xcode-beta.app\n"
                + "26.5 Beta 2 (17F42) [Apple Silicon]\t/Applications/Xcode-copy.app\n",
        };
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

            var notice = view.FindControl<Border>("StableCounterpartNotice")!;
            Assert.True(notice.IsEffectivelyVisible);
            Assert.Contains("Stable release found", VisibleTexts(view));
            Assert.Contains(vm.SelectedBrowserItem!.StableCounterpartText, VisibleTexts(view));
            var list = view.FindControl<ListBox>("VersionList")!;
            TextBlock badge = Assert.Single(list.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Stable release found" && text.IsEffectivelyVisible);
            Point? badgeRight = badge.TranslatePoint(new Point(badge.Bounds.Width, 0), list);
            Assert.NotNull(badgeRight);
            Assert.InRange(badgeRight.Value.X, 0, list.Bounds.Width);

            vm.GroupVersions = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var tree = view.FindControl<TreeView>("GroupedVersionList")!;
            TextBlock groupedBadge = Assert.Single(tree.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Stable release found" && text.IsEffectivelyVisible);
            Point? groupedBadgeRight = groupedBadge.TranslatePoint(new Point(groupedBadge.Bounds.Width, 0), tree);
            Assert.NotNull(groupedBadgeRight);
            Assert.InRange(groupedBadgeRight.Value.X, 0, tree.Bounds.Width);

            vm.SelectedBrowserItem = Assert.Single(vm.BrowserItems, item => item.Location == "/Applications/Xcode-copy.app");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(notice.IsEffectivelyVisible);
            Assert.DoesNotContain(fake.Commands, command => command.StartsWith("select ", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task VersionRowAndDetailsShowLiveProgressAndConfirmCancellation() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<XcodesOutputLine>? output = null;
        fake.RespondInTerminal = async (_, _, report, cancellation) =>
        {
            output = report;
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return FakeXcodes.Success("");
        };
        var vm = new XcodeViewModel(fake.Service)
        {
            ConfirmInstall = (_, _, _, _) => Task.FromResult(true),
            ConfirmCancel = () => Task.FromResult(false),
        };
        var view = new XcodePage(vm);
        var window = new Window { Width = 1100, Height = 1100, Content = view };
        Task? installing = null;

        try
        {
            await vm.InitializeAsync();
            vm.SelectedBrowserItem = AvailableBuild(vm, "26.5", "17F42");
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            installing = vm.InstallCommand.ExecuteAsync(vm.SelectedBrowserItem!.AvailableItem);
            await started.Task;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(vm.CanOpenSimulatorRuntimes);

            var list = view.FindControl<ListBox>("VersionList")!;
            StackPanel row = Assert.Single(list.GetVisualDescendants().OfType<StackPanel>(),
                panel => panel.Name == "VersionRowProgress" && panel.IsEffectivelyVisible);
            var rowProgress = Assert.Single(row.GetVisualDescendants().OfType<ProgressBar>(),
                bar => bar.Name == "VersionRowOperationProgress");
            var rowStatus = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Name == "VersionRowOperationStatus");
            var rowCancel = Assert.Single(row.GetVisualDescendants().OfType<Button>(),
                button => button.Name == "CancelVersionRowOperation");
            var detail = view.FindControl<StackPanel>("SelectedVersionProgress")!;
            var detailProgress = view.FindControl<ProgressBar>("SelectedVersionOperationProgress")!;
            var detailStatus = view.FindControl<TextBlock>("SelectedVersionOperationStatus")!;
            var detailCancel = view.FindControl<Button>("CancelSelectedVersionOperation")!;

            Assert.True(detail.IsEffectivelyVisible);
            Assert.Equal("Starting...", rowStatus.Text);
            Assert.Equal(rowStatus.Text, detailStatus.Text);
            Assert.True(rowProgress.IsIndeterminate);
            Assert.True(detailProgress.IsIndeterminate);
            Assert.Same(vm.RequestCancelOperationCommand, rowCancel.Command);
            Assert.Same(vm.RequestCancelOperationCommand, detailCancel.Command);
            Assert.Equal("Cancel operation for Xcode 26.5", AutomationProperties.GetName(rowCancel));
            Assert.Equal("Cancel operation for Xcode 26.5", AutomationProperties.GetName(detailCancel));

            output!(new XcodesOutputLine("(1/6) Downloading Xcode 26.5: 42%"));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal("Downloading Xcode 26.5: 42%", rowStatus.Text);
            Assert.Equal(rowStatus.Text, detailStatus.Text);
            Assert.False(rowProgress.IsIndeterminate);
            Assert.False(detailProgress.IsIndeterminate);
            Assert.Equal(42, rowProgress.Value);
            Assert.Equal(42, detailProgress.Value);

            window.Width = 480;
            window.Height = 360;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(detailCancel.IsEnabled);
            Assert.True(detailCancel.Focus(NavigationMethod.Tab));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var pageScroll = view.FindControl<ScrollViewer>("XcodePageScroll")!;
            Point? detailPosition = detailCancel.TranslatePoint(new Point(0, 0), pageScroll);
            Assert.NotNull(detailPosition);
            Assert.InRange(detailPosition.Value.X, 0, pageScroll.Viewport.Width - detailCancel.Bounds.Width);
            Assert.InRange(detailPosition.Value.Y, 0, pageScroll.Viewport.Height - detailCancel.Bounds.Height);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await vm.RequestCancelOperationCommand.ExecutionTask!;
            Assert.True(vm.IsOperationRunning);

            vm.SelectedBrowserItem = Assert.Single(vm.BrowserItems, item => item.Version == "26.4");
            vm.GroupVersions = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(detail.IsEffectivelyVisible);
            var tree = view.FindControl<TreeView>("GroupedVersionList")!;
            StackPanel groupedRow = Assert.Single(tree.GetVisualDescendants().OfType<StackPanel>(),
                panel => panel.Name == "VersionRowProgress" && panel.IsEffectivelyVisible);
            var groupedCancel = Assert.Single(groupedRow.GetVisualDescendants().OfType<Button>(),
                button => button.Name == "CancelVersionRowOperation");
            Assert.Same(vm.RequestCancelOperationCommand, groupedCancel.Command);
            Assert.Equal("Cancel operation for Xcode 26.5", AutomationProperties.GetName(groupedCancel));

            vm.ConfirmCancel = () => Task.FromResult(true);
            Assert.True(groupedCancel.IsEnabled);
            Assert.True(groupedCancel.Focus(NavigationMethod.Tab));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Point? rowPosition = groupedCancel.TranslatePoint(new Point(0, 0), pageScroll);
            Assert.NotNull(rowPosition);
            Assert.InRange(rowPosition.Value.X, 0, pageScroll.Viewport.Width - groupedCancel.Bounds.Width);
            Assert.InRange(rowPosition.Value.Y, 0, pageScroll.Viewport.Height - groupedCancel.Bounds.Height);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await vm.RequestCancelOperationCommand.ExecutionTask!;
            await installing;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(vm.IsOperationRunning);
            Assert.DoesNotContain(vm.BrowserItems, item => item.IsOperationTarget);
            Assert.False(groupedRow.IsEffectivelyVisible);
        }
        finally
        {
            vm.Stop();
            if (installing is not null)
                await installing;
            window.Close();
        }
    });

    [Fact]
    public Task TheVersionListKeepsTheChosenPathAcrossFilteringAndRefresh() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes
        {
            InstalledOutput = DefaultInstalled + "26.2 (17C52) [Apple Silicon]\t/Applications/Xcode-copy.app\n",
        };
        var vm = new XcodeViewModel(fake.Service);
        var view = new XcodePage(vm);
        var window = new Window { Width = 900, Height = 700, Content = view };

        try
        {
            await vm.InitializeAsync();
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<ListBox>("VersionList")!;
            list.SelectedItem = vm.BrowserItems.Single(item => item.Location == "/Applications/Xcode-copy.app");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("/Applications/Xcode-copy.app", vm.SelectedBrowserItem?.Location);

            string? opened = null;
            vm.OpenXcode = path =>
            {
                opened = path;
                return Task.CompletedTask;
            };
            Button open = view.FindControl<Button>("OpenSelectedXcode")!;
            Assert.Same(vm.OpenCommand, open.Command);
            open.Command!.Execute(open.CommandParameter);
            await vm.OpenCommand.ExecutionTask!;
            Assert.Equal("/Applications/Xcode-copy.app", opened);

            string? revealed = null;
            vm.ShowPathInFinder = path =>
            {
                revealed = path;
                return Task.CompletedTask;
            };
            Button reveal = view.FindControl<Button>("RevealSelectedXcode")!;
            Assert.Same(vm.RevealInFinderCommand, reveal.Command);
            reveal.Command!.Execute(reveal.CommandParameter);
            await vm.RevealInFinderCommand.ExecutionTask!;
            Assert.Equal("/Applications/Xcode-copy.app", revealed);

            vm.SearchText = "no match";
            Dispatcher.UIThread.RunJobs();
            Assert.Null(list.SelectedItem);

            vm.SearchText = "";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("/Applications/Xcode-copy.app", ((XcodeBrowserItemViewModel?)list.SelectedItem)?.Location);

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("/Applications/Xcode-copy.app", vm.SelectedBrowserItem?.Location);
            Assert.Same(vm.SelectedBrowserItem, list.SelectedItem);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    [Fact]
    public Task TheGroupedTreeKeepsExactPathSelectionAndPersistsRealExpansionChanges() => DispatchAsync(async () =>
    {
        EnsureTheme();
        var fake = new FakeXcodes
        {
            InstalledOutput = DefaultInstalled + "26.2 (17C52) [Apple Silicon]\t/Applications/Xcode-copy.app\n",
        };
        var vm = new XcodeViewModel(fake.Service);
        var view = new XcodePage(vm);
        var window = new Window { Width = 900, Height = 700, Content = view };

        try
        {
            await vm.InitializeAsync();
            vm.GroupVersions = true;
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var tree = view.FindControl<TreeView>("GroupedVersionList")!;
            XcodeBrowserNodeViewModel major = Assert.Single(vm.BrowserGroups);
            TreeViewItem majorContainer = Assert.Single(tree.GetVisualDescendants().OfType<TreeViewItem>(),
                item => ReferenceEquals(item.DataContext, major));
            Assert.True(majorContainer.IsExpanded);
            majorContainer.SetCurrentValue(TreeViewItem.IsExpandedProperty, false);
            Dispatcher.UIThread.RunJobs();
            Assert.False(major.IsExpanded);
            Assert.Contains("major:26", XcodePreferences.BrowserCollapsedGroups);
            major.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(majorContainer.IsExpanded);

            XcodeBrowserNodeViewModel minor = Assert.Single(major.Children, group => group.Title == "26.2");
            XcodeBrowserNodeViewModel copy = Assert.Single(minor.Children,
                node => node.Item?.Location == "/Applications/Xcode-copy.app");
            tree.SelectedItem = copy;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("/Applications/Xcode-copy.app", vm.SelectedBrowserItem?.Location);

            vm.SearchText = "no match";
            Dispatcher.UIThread.RunJobs();
            Assert.Null(vm.SelectedBrowserItem);
            vm.SearchText = "";
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal("/Applications/Xcode-copy.app", vm.SelectedBrowserItem?.Location);
            Assert.Equal("/Applications/Xcode-copy.app", ((XcodeBrowserNodeViewModel?)tree.SelectedItem)?.Item?.Location);

            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("/Applications/Xcode-copy.app", vm.SelectedBrowserItem?.Location);
            Assert.Equal("/Applications/Xcode-copy.app", ((XcodeBrowserNodeViewModel?)tree.SelectedItem)?.Item?.Location);

            vm.GroupVersions = false;
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<ListBox>("VersionList")!;
            Assert.Equal("/Applications/Xcode-copy.app", ((XcodeBrowserItemViewModel?)list.SelectedItem)?.Location);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    });

    // ─── The question dialog ─────────────────────────────────────────────────

    [Fact]
    public Task PasswordQuestionsShowTheAccountAndHowToSwitch() => _session.Dispatch(() =>
    {
        var dialog = new XcodePromptDialog(new XcodesPrompt(XcodesPromptKind.Password, "Password:", [])
        {
            AppleId = "developer@example.test",
        });
        Assert.Contains("Apple ID: developer@example.test",
            Body(dialog).Children.OfType<SelectableTextBlock>().Select(text => text.Text));
        Assert.Contains("Wrong account? Cancel this operation. If an account is saved, sign out on the Xcode page, then try again.",
            Body(dialog).Children.OfType<TextBlock>().Select(text => text.Text));
    }, CancellationToken.None);

    [Theory]
    [InlineData(XcodesPromptKind.Password)]
    [InlineData(XcodesPromptKind.TwoFactorCode)]
    [InlineData(XcodesPromptKind.PhoneNumber)]
    [InlineData(XcodesPromptKind.FederatedUrl)]
    public Task QuestionsKeepTheirInputReachableInAShortWindow(XcodesPromptKind kind) => _session.Dispatch(() =>
    {
        EnsureTheme();
        string[] context = [.. Enumerable.Repeat("Choose the trusted phone number or follow this sign-in instruction.", 12)];
        var dialog = new XcodePromptDialog(new XcodesPrompt(kind, "Enter your answer:", context)
        {
            AppleId = "developer@example.test",
        });
        var window = new Window { Width = 420, Height = 320, Content = dialog };

        try
        {
            window.Show();
            dialog.NotifyOpened();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var scroll = Assert.IsType<ScrollViewer>(dialog.FindControl<ContentControl>("BodyPresenter")!.Content);
            var body = Assert.IsType<StackPanel>(scroll.Content);
            TextBox answer = Assert.Single(body.Children.OfType<TextBox>());
            Assert.True(answer.IsFocused);
            var location = answer.TranslatePoint(new Point(0, 0), scroll);
            Assert.NotNull(location);
            Assert.True(location.Value.Y >= 0 && location.Value.Y + answer.Bounds.Height <= scroll.Viewport.Height,
                "The focused answer field is outside the visible dialog body.");

            if (kind is not XcodesPromptKind.PhoneNumber)
            {
                Assert.True(scroll.Focus(NavigationMethod.Tab));
                for (int page = 0; page < 20; page++)
                    window.KeyPress(Key.PageUp, RawInputModifiers.None, PhysicalKey.PageUp, null);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var identity = Assert.Single(body.Children.OfType<SelectableTextBlock>());
                var warning = Assert.Single(body.Children.OfType<TextBlock>(),
                    text => text.Text?.StartsWith("Wrong account?", StringComparison.Ordinal) == true);
                foreach (Control important in new Control[] { identity, warning })
                {
                    var position = important.TranslatePoint(new Point(0, 0), scroll);
                    Assert.NotNull(position);
                    Assert.True(position.Value.Y >= 0 && position.Value.Y + important.Bounds.Height <= scroll.Viewport.Height,
                        "The account identity or switching guidance is not reachable.");
                }
            }

            foreach (string name in new[] { "PrimaryButton", "SecondaryButton" })
            {
                var button = dialog.FindControl<Button>(name)!;
                var position = button.TranslatePoint(new Point(0, 0), window);
                Assert.NotNull(position);
                Assert.True(position.Value.Y >= 0 && position.Value.Y + button.Bounds.Height <= window.Bounds.Height,
                    $"{name} is outside the window.");
            }
        }
        finally
        {
            window.Close();
        }
    }, CancellationToken.None);

    [Theory]
    [InlineData(XcodesPromptKind.PhoneNumber)]
    [InlineData(XcodesPromptKind.FederatedUrl)]
    public Task LongQuestionContextCanBeScrolledUsingTheKeyboard(XcodesPromptKind kind) => _session.Dispatch(() =>
    {
        EnsureTheme();
        string[] context = [.. Enumerable.Repeat("An instruction provided by xcodes.", 25)];
        var dialog = new XcodePromptDialog(new XcodesPrompt(kind, "Enter your answer:", context)
        {
            AppleId = "developer@example.test",
        });
        var window = new Window { Width = 420, Height = 320, Content = dialog };

        try
        {
            window.Show();
            dialog.NotifyOpened();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var contextScroll = Assert.Single(Body(dialog).Children.OfType<ScrollViewer>());
            Assert.True(contextScroll.Extent.Height > contextScroll.Viewport.Height);
            Assert.True(contextScroll.Focus(NavigationMethod.Tab));
            contextScroll.BringIntoView();
            for (int page = 0; page < 20; page++)
                window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(contextScroll.Offset.Y > 0);
            Assert.Equal(contextScroll.Extent.Height - contextScroll.Viewport.Height, contextScroll.Offset.Y, 0.01);
        }
        finally
        {
            window.Close();
        }
    }, CancellationToken.None);

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
    [InlineData(XcodesPromptKind.Unknown, "An answer is needed", "Continue", "Answer")]
    public Task EveryQuestionHasATitleAndALabel(XcodesPromptKind kind, string title, string primary, string label)
        => _session.Dispatch(() =>
        {
            var dialog = new XcodePromptDialog(new XcodesPrompt(kind, "", []));

            Assert.Equal(title, dialog.Title);
            Assert.Equal(primary, Primary(dialog).Content);
            Assert.Equal("Cancel", Secondary(dialog).Content);
            Assert.Equal(label, AnswerBox(dialog).PlaceholderText);
        }, CancellationToken.None);

    [Fact]
    public Task TheAppleIdPromptExplainsUnsupportedAccountTypes() => _session.Dispatch(() =>
    {
        var dialog = new XcodePromptDialog(new XcodesPrompt(XcodesPromptKind.AppleId, "", []));

        Assert.Contains("Accounts that require security keys or federated sign-in are not supported yet.",
            Assert.Single(Body(dialog).Children.OfType<TextBlock>()).Text);
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
    public Task AnUnknownQuestionIsShownAsWritten() => _session.Dispatch(() =>
    {
        var asked = new XcodePromptDialog(
            new XcodesPrompt(XcodesPromptKind.Unknown, "Install the extra components? [y/N]", []));
        Assert.Equal(
            "Install the extra components? [y/N]",
            Assert.IsType<TextBlock>(Body(asked).Children[0]).Text);

        var silent = new XcodePromptDialog(new XcodesPrompt(XcodesPromptKind.Unknown, "", []));
        Assert.Equal(
            "The installation is waiting for your answer to continue.",
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

    [Fact]
    public Task DeferredKeychainItemShowsSavedStateWithoutUnlockingUntilNeeded()
    {
        var fake = new FakeXcodes
        {
            Account = new XcodeAccountStatus(null, null, HasSavedPassword: false, HasStoredItem: true),
        };
        return WithPage(fake, vm =>
        {
            Assert.True(vm.Account.HasAccount);
            Assert.True(vm.Account.CanVerify);
            Assert.True(vm.Account.CanSignOut);
            Assert.Equal("Saved Apple ID in Keychain", vm.Account.Status);
            Assert.DoesNotContain("xcodes", vm.Account.Description, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    // The generic overload awaits the operation; Dispatch(Action) would make an async lambda async void.
    private Task DispatchAsync(Func<Task> action) => _session.Dispatch<bool>(async () =>
    {
        await action();
        return true;
    }, CancellationToken.None);

    private Task WithPageAsync(
        FakeXcodes fake, Func<XcodeViewModel, Task> test, bool initialize = true, IXcodeReleasesCatalog? catalog = null,
        IXcodeSimulatorRuntimeService? runtimes = null, IXcodeSimulatorRuntimeInstaller? runtimeInstaller = null,
        IXcodePublicSimulatorRuntimeDownloader? runtimeDownloader = null)
        => DispatchAsync(async () =>
        {
            var vm = new XcodeViewModel(fake.Service, catalog: catalog, runtimes: runtimes,
                runtimeInstaller: runtimeInstaller, runtimeDownloader: runtimeDownloader);
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

    private static XcodeBrowserItemViewModel AvailableBuild(XcodeViewModel vm, string version, string build)
        => Assert.Single(vm.BrowserItems, item => item.Version == version && item.AvailableItem?.Release.Build == build);

    private static void AssertCommands(List<string> actual, params string[] expected)
        => Assert.Equal(expected, actual.ToArray());

    private static void AssertSearch(XcodeViewModel vm, string search, params string[] expected)
    {
        vm.SearchText = search;
        Assert.Equal(expected, AvailableVersions(vm));
    }

    private static void AssertNotice(XcodeViewModel vm, InfoBarSeverity severity, string title, string message)
        => AssertNotice(vm.Notice, severity, title, message);

    private static void AssertNotice(InfoBarViewModel notice, InfoBarSeverity severity, string title, string message)
    {
        Assert.True(notice.IsOpen);
        Assert.Equal(severity, notice.Severity);
        Assert.Equal(title, notice.Title);
        Assert.Equal(message, notice.Message);
    }

    private static StackPanel ConfirmationDialogBody(ImmersiveConfirmationDialog dialog)
    {
        var scroll = Assert.IsType<ScrollViewer>(dialog.FindControl<ContentControl>("BodyPresenter")!.Content);
        return Assert.IsType<StackPanel>(scroll.Content);
    }

    private static Button Primary(XcodePromptDialog dialog) => dialog.FindControl<Button>("PrimaryButton")!;

    private static Button Secondary(XcodePromptDialog dialog) => dialog.FindControl<Button>("SecondaryButton")!;

    private static StackPanel Body(XcodePromptDialog dialog)
        => ConfirmationDialogBody(dialog);

    private static TextBox AnswerBox(XcodePromptDialog dialog) => Body(dialog).Children.OfType<TextBox>().Single();

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static IReadOnlyList<XcodeCatalogRelease> CatalogReleases() =>
    [
        new("26.5", "17F42", XcodeArchitecture.AppleSilicon, new DateOnly(2026, 5, 1), new Version(26, 6, 0),
            new Uri("https://developer.apple.com/documentation/xcode-release-notes"), ["iOS 26.5"], ["swift 6.3"], null)
        {
            Download = new Uri("https://download.developer.apple.com/Xcode_26.5_17F42.xip"),
        },
        new("26.5", "17F43", XcodeArchitecture.AppleSilicon, new DateOnly(2026, 5, 1), new Version(26, 6, 0),
            new Uri("https://developer.apple.com/documentation/xcode-release-notes"), ["iOS 26.5"], ["swift 6.3"], null)
        {
            Download = new Uri("https://download.developer.apple.com/Xcode_26.5_17F43.xip"),
        },
        new("26.4", "17E192", XcodeArchitecture.AppleSilicon, new DateOnly(2026, 3, 1), new Version(26, 4, 0),
            null, [], [], null)
        {
            Download = new Uri("https://download.developer.apple.com/Xcode_26.4.xip"),
        },
    ];

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No network request was expected.");
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"));
        }
    }

    private sealed class FakeCatalog : IXcodeReleasesCatalog, IXcodeReleasesCatalogCache
    {
        public IReadOnlyList<XcodeCatalogRelease> Releases { get; set; } = [];
        public XcodeSavedReleaseCatalog? Saved { get; set; }
        public Exception? SavedError { get; set; }
        public Exception? Error { get; set; }
        public Func<CancellationToken, Task<IReadOnlyList<XcodeCatalogRelease>>>? Response { get; set; }
        public int Requests { get; private set; }
        public int SavedRequests { get; private set; }

        public Task<XcodeSavedReleaseCatalog?> GetSavedAsync(CancellationToken cancellationToken)
        {
            SavedRequests++;
            return SavedError is { } error
                ? Task.FromException<XcodeSavedReleaseCatalog?>(error)
                : Task.FromResult(Saved);
        }

        public Task<IReadOnlyList<XcodeCatalogRelease>> GetAsync(CancellationToken cancellationToken)
        {
            Requests++;
            if (Response is { } response)
                return response(cancellationToken);
            return Error is { } error
                ? Task.FromException<IReadOnlyList<XcodeCatalogRelease>>(error)
                : Task.FromResult(Releases);
        }
    }

    private static FakeSimulatorRuntimes SimulatorRuntimes() => new()
    {
        Catalog =
        [
            new("iOS 26.4 Simulator Runtime", "iOS", "26.4", "23E100", XcodeArchitecture.AppleSilicon,
                false, XcodeSimulatorRuntimeOrigin.AppleCatalog, 1_234_567_890,
                Distribution: XcodeSimulatorRuntimeDistribution.XcodeManaged),
            new("iOS 26.4 Simulator Runtime", "iOS", "26.4", "23E100", XcodeArchitecture.Universal,
                false, XcodeSimulatorRuntimeOrigin.AppleCatalog, 2_000_000_000,
                Distribution: XcodeSimulatorRuntimeDistribution.XcodeManaged),
            new("iOS 27.0 beta Simulator Runtime", "iOS", "27.0", "24A100", XcodeArchitecture.AppleSilicon,
                true, XcodeSimulatorRuntimeOrigin.AppleCatalog, null,
                Distribution: XcodeSimulatorRuntimeDistribution.XcodeManaged),
            new("watchOS 27.0 beta Simulator Runtime", "watchOS", "27.0", "24S100", XcodeArchitecture.Unspecified,
                true, XcodeSimulatorRuntimeOrigin.AppleCatalog, null),
            new("tvOS 26.4 Mystery Simulator Runtime", "tvOS", "26.4", "23K100", XcodeArchitecture.Unspecified,
                null, XcodeSimulatorRuntimeOrigin.AppleCatalog, null),
        ],
        Installed =
        [
            new("iOS 26.4", "iOS", "26.4", "23E100", XcodeArchitecture.AppleSilicon,
                null, XcodeSimulatorRuntimeOrigin.Installed, 1_234_567_890, "Ready",
                "/Library/Developer/Runtime/iOS.simruntime"),
            new("iOS 26.4", "iOS", "26.4", "23E100", XcodeArchitecture.Universal,
                null, XcodeSimulatorRuntimeOrigin.Bundled, null, "Ready",
                "/Applications/Xcode.app/Bundled/iOS.simruntime"),
            new("watchOS 26.4", "watchOS", "26.4", "23S100", XcodeArchitecture.Unspecified,
                null, XcodeSimulatorRuntimeOrigin.Installed, null, "Unavailable", "/Library/Runtime/watch.simruntime"),
        ],
    };

    private static XcodeSimulatorRuntime LegacySimulatorRuntime() => new(
        "iOS 12.4 Simulator", "iOS", "12.4", "16G73", XcodeArchitecture.Unspecified,
        false, XcodeSimulatorRuntimeOrigin.AppleCatalog, 2_568_097_085,
        Distribution: XcodeSimulatorRuntimeDistribution.PublicImage,
        DownloadUrl: "https://devimages-cdn.apple.com/downloads/xcode/simulators/runtime.dmg");

    private static FakeSimulatorRuntimes SimulatorRuntimesWithLegacy()
    {
        FakeSimulatorRuntimes runtimes = SimulatorRuntimes();
        runtimes.Catalog = [.. runtimes.Catalog, LegacySimulatorRuntime()];
        return runtimes;
    }

    private sealed class FakeSimulatorRuntimes : IXcodeSimulatorRuntimeService, IXcodeSimulatorRuntimeCatalogCache
    {
        public IReadOnlyList<XcodeSimulatorRuntime> Catalog { get; set; } = [];
        public IReadOnlyList<XcodeSimulatorSdkMapping>? SDKMappings { get; set; }
        public IReadOnlyList<XcodeSimulatorRuntime> Installed { get; set; } = [];
        public XcodeSavedSimulatorRuntimeCatalog? Saved { get; set; }
        public Exception? SavedError { get; set; }
        public Exception? CatalogError { get; set; }
        public Exception? InstalledError { get; set; }
        public Func<CancellationToken, Task<XcodeSavedSimulatorRuntimeCatalog?>>? SavedResponse { get; set; }
        public Func<CancellationToken, Task<IReadOnlyList<XcodeSimulatorRuntime>>>? CatalogResponse { get; set; }
        public int SavedRequests { get; private set; }
        public int CatalogRequests { get; private set; }
        public int InstalledRequests { get; private set; }
        public CancellationToken CatalogCancellation { get; private set; }

        public Task<XcodeSavedSimulatorRuntimeCatalog?> GetSavedCatalogAsync(CancellationToken cancellationToken)
        {
            SavedRequests++;
            if (SavedResponse is { } response)
                return response(cancellationToken);
            return SavedError is { } error
                ? Task.FromException<XcodeSavedSimulatorRuntimeCatalog?>(error)
                : Task.FromResult(Saved);
        }

        public async Task<XcodeSimulatorRuntimeCatalog> GetCatalogAsync(CancellationToken cancellationToken)
        {
            CatalogRequests++;
            CatalogCancellation = cancellationToken;
            IReadOnlyList<XcodeSimulatorRuntime> runtimes;
            if (CatalogResponse is { } response)
                runtimes = await response(cancellationToken);
            else if (CatalogError is { } error)
                throw error;
            else
                runtimes = Catalog;
            return new XcodeSimulatorRuntimeCatalog(runtimes, SDKMappings);
        }

        public Task<IReadOnlyList<XcodeSimulatorRuntime>> GetInstalledAsync(CancellationToken cancellationToken)
        {
            InstalledRequests++;
            return InstalledError is { } error
                ? Task.FromException<IReadOnlyList<XcodeSimulatorRuntime>>(error) : Task.FromResult(Installed);
        }
    }

    private sealed class FakeSimulatorInstaller : IXcodeSimulatorRuntimeInstaller
    {
        public string DeveloperDirectory { get; } = "/Applications/Test Xcode.app/Contents/Developer";
        public XcodeSimulatorRuntimeInstallPlan? Plan { get; private set; }
        public XcodeSimulatorRuntimeInstallPlan? InstalledPlan { get; private set; }
        public XcodeSimulatorRuntimeInstallPlan? ExportedPlan { get; private set; }
        public XcodeSimulatorRuntimeExportPaths? ExportPaths { get; private set; }
        public string? ExportDirectory { get; private set; }
        public int PrepareRequests { get; private set; }
        public int ExportPrepareRequests { get; private set; }
        public int InstallRequests { get; private set; }
        public int ExportRequests { get; private set; }
        public Func<XcodeSimulatorRuntimeInstallPlan, Action<XcodesOutputLine>?, CancellationToken,
            Task<XcodeSimulatorRuntimeInstallOutcome>>? InstallResponse
        { get; set; }
        public Func<XcodeSimulatorRuntimeInstallPlan, string, XcodeSimulatorRuntimeExportPaths, Action<XcodesOutputLine>?,
            CancellationToken, Task<XcodeSimulatorRuntimeExportResult>>? ExportResponse
        { get; set; }

        public Task<XcodeSimulatorRuntimeInstallPlan> PrepareAsync(
            XcodeSimulatorRuntime runtime, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareRequests++;
            XcodeVersion.TryParse("26.5", out XcodeVersion version);
            Plan = new XcodeSimulatorRuntimeInstallPlan(runtime, DeveloperDirectory, version);
            return Task.FromResult(Plan);
        }

        public Task<XcodeSimulatorRuntimeInstallPlan> PrepareExportAsync(
            XcodeSimulatorRuntime runtime, CancellationToken cancellationToken)
        {
            ExportPrepareRequests++;
            return PrepareAsync(runtime, cancellationToken);
        }

        public Task<XcodeSimulatorRuntimeInstallOutcome> InstallAsync(
            XcodeSimulatorRuntimeInstallPlan plan, Action<XcodesOutputLine>? output, CancellationToken cancellationToken)
        {
            InstallRequests++;
            InstalledPlan = plan;
            return InstallResponse?.Invoke(plan, output, cancellationToken)
                   ?? Task.FromResult(XcodeSimulatorRuntimeInstallOutcome.Installed);
        }

        public Task<XcodeSimulatorRuntimeExportResult> ExportAsync(
            XcodeSimulatorRuntimeInstallPlan plan, string destinationDirectory,
            Action<XcodeSimulatorRuntimeExportPaths> started, Action<XcodesOutputLine>? output,
            CancellationToken cancellationToken)
        {
            ExportRequests++;
            ExportedPlan = plan;
            ExportDirectory = destinationDirectory;
            ExportPaths = new XcodeSimulatorRuntimeExportPaths(
                "/tmp/UniGetUI-runtime-stage", Path.Combine(destinationDirectory, "runtime.dmg"));
            started(ExportPaths);
            return ExportResponse?.Invoke(plan, destinationDirectory, ExportPaths, output, cancellationToken)
                   ?? Task.FromResult(new XcodeSimulatorRuntimeExportResult(ExportPaths.ImagePath, null));
        }
    }

    private sealed class FakePublicSimulatorDownloader : IXcodePublicSimulatorRuntimeDownloader
    {
        public XcodeSimulatorRuntime? Runtime { get; private set; }
        public string? Destination { get; private set; }
        public XcodeSimulatorRuntimeDownloadPaths? Paths { get; private set; }
        public int Requests { get; private set; }
        public Func<XcodeSimulatorRuntime, string, XcodeSimulatorRuntimeDownloadPaths, Action<XcodesOutputLine>?,
            CancellationToken, Task<string>>? Response
        { get; set; }

        public Task<string> DownloadAsync(
            XcodeSimulatorRuntime runtime, string destinationDirectory,
            Action<XcodeSimulatorRuntimeDownloadPaths> started, Action<XcodesOutputLine>? output,
            CancellationToken cancellationToken)
        {
            Runtime = runtime;
            Destination = destinationDirectory;
            Requests++;
            Paths = new XcodeSimulatorRuntimeDownloadPaths(Path.Combine(destinationDirectory, "runtime.dmg"));
            started(Paths);
            return Response?.Invoke(runtime, destinationDirectory, Paths, output, cancellationToken)
                   ?? Task.FromResult(Paths.ImagePath);
        }
    }

    /// <summary>
    /// A backend that answers from memory, and keeps what it was asked as the xcodes-like command it stands for, so
    /// tests can read and script it in a few words.
    /// </summary>
    /// <summary>Installs the newest available version, confirming it; used where an operation must ask questions.</summary>
    private static Task InstallAsync(XcodeViewModel vm)
    {
        vm.ConfirmInstall = (_, _, _, _) => Task.FromResult(true);
        return vm.InstallCommand.ExecuteAsync(vm.Available[0]);
    }

    private sealed class FakeXcodes : IXcodeBackend
    {
        public FakeXcodes()
        {
            Respond = command => Task.FromResult(Answer(command));
            RespondInTerminal = (_, _, _, _) => Task.FromResult(Success(""));
            SignIn = DefaultSignInAsync;
            Service = new XcodesService(this);
        }

        public XcodesService Service { get; }

        /// <summary>What was asked that asks no questions, such as <c>installed</c>.</summary>
        public List<string> Commands { get; } = [];

        /// <summary>What was asked that may ask questions: <c>install</c> and <c>select</c>.</summary>
        public List<string> TerminalCommands { get; } = [];

        public XcodeAccountStatus Account { get; set; } = XcodeAccountStatus.None;
        public XcodesException? AccountError { get; set; }
        public int AccountReadCount { get; private set; }
        public XcodeRelease? LastInstalledRelease { get; private set; }
        public XcodeRelease? LastDownloadedRelease { get; private set; }
        public string? LastArchiveDirectory { get; private set; }
        public XcodeInstallationResult? InstallationResult { get; set; }
        public string DownloadedArchivePath { get; set; } = "/cache/Xcode.xip";

        public string ListOutput { get; set; } = DefaultList;

        public IReadOnlyList<XcodeRelease> ExtraReleases { get; set; } = [];

        public string InstalledOutput { get; set; } = DefaultInstalled;

        public Func<string, Task<XcodesResult>> Respond { get; set; }

        public delegate Task<XcodesResult> TerminalResponder(
            string command,
            XcodesPromptHandler? prompts,
            Action<XcodesOutputLine>? output,
            CancellationToken cancellation);

        public TerminalResponder RespondInTerminal { get; set; }

        public Func<XcodesPromptHandler, string?, CancellationToken, Task<XcodeAccountStatus>> SignIn { get; set; }

        public static XcodesResult Success(string text) => new(0, text, "");

        public static XcodesResult Failure(string text) => new(1, "", text);

        public static XcodeAccountStatus SignedIn(string appleId, string? fullName = "Test Developer")
            => new(appleId, new AppleSession(fullName, appleId, null), HasSavedPassword: true);

        public XcodesResult Answer(string command) => command.Split(' ', 2)[0] switch
        {
            "installed" => Success(InstalledOutput),
            "list" or "update" => Success(ListOutput),
            "signout" => SignOut(),
            "verify" => Success(""),
            _ => Failure($"unexpected command: {command}"),
        };

        private XcodesResult SignOut()
        {
            Account = XcodeAccountStatus.None;
            return Success("");
        }

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

        private static string Directory(string? directory) => directory is null ? "" : $" --directory {directory}";

        private async Task<string> RunAsync(string command)
        {
            Commands.Add(command);
            XcodesResult result = await Respond(command);
            if (!result.Success)
                throw new XcodesException(XcodesError.Summarize(result.Output, result.ExitCode));
            return result.StandardOutput;
        }

        private async Task RunInTerminalAsync(string command, XcodesPromptHandler? prompts,
            Action<XcodesOutputLine>? output, CancellationToken cancellationToken)
        {
            TerminalCommands.Add(command);
            XcodesResult result = await RespondInTerminal(command, prompts, output, cancellationToken);
            if (!result.Success)
                throw new XcodesException(XcodesError.Summarize(result.Output, result.ExitCode));
        }

        public async Task<IReadOnlyList<InstalledXcode>> GetInstalledAsync(string? installDirectory, CancellationToken cancellationToken)
            => XcodesOutputParser.ParseInstalled(await RunAsync("installed" + Directory(installDirectory)))
                .OrderByDescending(xcode => xcode.Version, Comparer<string>.Create(XcodeVersion.Compare))
                .ToList();

        public async Task<IReadOnlyList<XcodeRelease>> GetReleasesAsync(bool refresh, string? installDirectory, CancellationToken cancellationToken)
            => XcodesOutputParser.ParseReleases(await RunAsync((refresh ? "update" : "list") + Directory(installDirectory)))
                .Concat(ExtraReleases)
                .GroupBy(release => (release.Version, release.Build, release.Architecture))
                .Select(group => group.First())
                .OrderByDescending(release => release.Version, Comparer<string>.Create(XcodeVersion.Compare))
                .ToList();

        public async Task<XcodeInstallationResult> InstallAsync(XcodeRelease release, XcodeInstallOptions options, XcodesPromptHandler? prompts,
            Action<XcodesOutputLine>? output, CancellationToken cancellationToken)
        {
            LastInstalledRelease = release;
            LastArchiveDirectory = options.ArchiveDirectory;
            string command = $"install {release.Version}"
                + (options.Select ? " --select" : "")
                + (options.SkipAdministratorSteps ? " --no-superuser" : "")
                + (options.DeleteArchive ? " --empty-trash" : "")
                + Directory(options.InstallDirectory);
            await RunInTerminalAsync(command, prompts, output, cancellationToken);
            string directory = options.InstallDirectory ?? "/Applications";
            string name = string.Join('-', release.Version.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return InstallationResult ?? new XcodeInstallationResult(Path.Combine(directory, $"Xcode-{name}.app"),
                release.Build ?? "", release.Architecture);
        }

        public async Task<string> DownloadAsync(XcodeRelease release, XcodesPromptHandler? prompts,
            Action<XcodesOutputLine>? output, CancellationToken cancellationToken, string? archiveDirectory = null)
        {
            LastDownloadedRelease = release;
            LastArchiveDirectory = archiveDirectory;
            await RunInTerminalAsync($"download {release.Version}", prompts, output, cancellationToken);
            return DownloadedArchivePath;
        }

        public Task SelectAsync(string appPath, Action<XcodesOutputLine>? output, CancellationToken cancellationToken)
            => RunInTerminalAsync($"select {appPath}", null, output, cancellationToken);

        public XcodeAccountStatus GetAccount()
        {
            AccountReadCount++;
            return AccountError is { } error ? throw error : Account;
        }

        public async Task<XcodeAccountStatus> RefreshAccountAsync(CancellationToken cancellationToken)
        {
            await RunAsync("verify");
            return GetAccount();
        }

        public Task<XcodeAccountStatus> SignInAsync(XcodesPromptHandler prompts, string? suggestedAppleId, CancellationToken cancellationToken)
        {
            Commands.Add(suggestedAppleId is null ? "signin" : $"signin {suggestedAppleId}");
            return SignIn(prompts, suggestedAppleId, cancellationToken);
        }

        private async Task<XcodeAccountStatus> DefaultSignInAsync(
            XcodesPromptHandler prompts, string? suggestedAppleId, CancellationToken cancellationToken)
        {
            string? appleId = suggestedAppleId
                ?? await prompts(new XcodesPrompt(XcodesPromptKind.AppleId, "Apple ID:", []), cancellationToken);
            if (string.IsNullOrEmpty(appleId))
                throw new OperationCanceledException();
            string? password = await prompts(new XcodesPrompt(XcodesPromptKind.Password, "Password:", []) { AppleId = appleId },
                cancellationToken);
            if (string.IsNullOrEmpty(password))
                throw new OperationCanceledException();
            return Account = SignedIn(appleId);
        }

        public async Task SignOutAsync(CancellationToken cancellationToken) => await RunAsync("signout");
    }
}
#endif

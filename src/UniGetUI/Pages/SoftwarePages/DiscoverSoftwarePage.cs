using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.Interface.Telemetry;
using UniGetUI.Interface.Widgets;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.Pages.DialogPages;
using Windows.System;
using Windows.UI.Text;

namespace UniGetUI.Interface.SoftwarePages
{
    public partial class DiscoverSoftwarePage : AbstractPackagesPage
    {
        private BetterMenuItem? MenuAsAdmin;
        private BetterMenuItem? MenuInteractive;
        private BetterMenuItem? MenuSkipHash;
        private BetterMenuItem? MenuDownloadInstaller;

        public DiscoverSoftwarePage()
            : base(
                new PackagesPageData
                {
                    DisableAutomaticPackageLoadOnStart = true,
                    DisableFilterOnQueryChange = true,
                    MegaQueryBlockEnabled = true,
                    PackagesAreCheckedByDefault = false,
                    ShowLastLoadTime = false,
                    DisableReload = false,
                    DisableSuggestedResultsRadio = false,
                    PageName = "Discover",

                    Loader = DiscoverablePackagesLoader.Instance,
                    PageRole = OperationType.Install,

                    NoPackages_BackgroundText = CoreTools.Translate(
                        "No results were found matching the input criteria"
                    ),
                    NoPackages_SourcesText = CoreTools.Translate("No packages were found"),
                    NoPackages_SubtitleText_Base = CoreTools.Translate("No packages were found"),
                    MainSubtitle_StillLoading = CoreTools.Translate("Loading packages"),
                    NoMatches_BackgroundText = CoreTools.Translate(
                        "No results were found matching the input criteria"
                    ),

                    PageTitle = CoreTools.Translate("Discover Packages"),
                    Glyph = "\uF6FA",
                }
            )
        {
            InstantSearchCheckbox.IsEnabled = false;
            InstantSearchCheckbox.Visibility = Visibility.Collapsed;

            MegaFindButton.Click += Event_SearchPackages;
            MegaQueryBlock.KeyUp += (s, e) =>
            {
                if (e.Key == VirtualKey.Enter)
                {
                    Event_SearchPackages(s, e);
                }
            };
        }

        public override void SearchBox_QuerySubmitted(
            object? sender,
            AutoSuggestBoxQuerySubmittedEventArgs args
        )
        {
            base.SearchBox_QuerySubmitted(sender, args);
            Event_SearchPackages(sender, new());
        }

        public override BetterMenu GenerateContextMenu()
        {
            BetterMenu menu = new();

            BetterMenuItem menuInstall = new()
            {
                Text = CoreTools.AutoTranslated("Install"),
                IconName = IconType.Download,
                KeyboardAcceleratorTextOverride = "Ctrl+Enter",
            };
            menuInstall.Click += MenuInstall_Invoked;
            menu.Items.Add(menuInstall);

            menu.Items.Add(new MenuFlyoutSeparator { Height = 5 });

            BetterMenuItem menuInstallSettings = new()
            {
                Text = CoreTools.AutoTranslated("Install options"),
                IconName = IconType.Options,
                KeyboardAcceleratorTextOverride = "Alt+Enter",
            };
            menuInstallSettings.Click += MenuInstallSettings_Invoked;
            menu.Items.Add(menuInstallSettings);

            menu.Items.Add(new MenuFlyoutSeparator { Height = 5 });

            MenuAsAdmin = new BetterMenuItem
            {
                Text = CoreTools.AutoTranslated("Install as administrator"),
                IconName = IconType.UAC,
            };
            MenuAsAdmin.Click += MenuAsAdmin_Invoked;
            menu.Items.Add(MenuAsAdmin);

            MenuInteractive = new BetterMenuItem
            {
                Text = CoreTools.AutoTranslated("Interactive installation"),
                IconName = IconType.Interactive,
            };
            MenuInteractive.Click += MenuInteractive_Invoked;
            menu.Items.Add(MenuInteractive);

            MenuSkipHash = new BetterMenuItem
            {
                Text = CoreTools.AutoTranslated("Skip hash check"),
                IconName = IconType.Checksum,
            };
            MenuSkipHash.Click += MenuSkipHash_Invoked;
            menu.Items.Add(MenuSkipHash);

            MenuDownloadInstaller = new BetterMenuItem
            {
                Text = CoreTools.AutoTranslated("Download installer"),
                IconName = IconType.Download,
            };
            MenuDownloadInstaller.Click += (_, _) =>
                _ = MainApp.Operations.AskLocationAndDownload(
                    SelectedItem,
                    TEL_InstallReferral.DIRECT_SEARCH
                );
            menu.Items.Add(MenuDownloadInstaller);

            menu.Items.Add(new MenuFlyoutSeparator { Height = 5 });

            BetterMenuItem menuDetails = new()
            {
                Text = CoreTools.AutoTranslated("Package details"),
                IconName = IconType.Info_Round,
                KeyboardAcceleratorTextOverride = "Enter",
            };
            menuDetails.Click += MenuDetails_Invoked;
            menu.Items.Add(menuDetails);

            return menu;
        }

        public override void GenerateToolBar()
        {
            BetterMenuItem InstallAsAdmin = new();
            BetterMenuItem InstallSkipHash = new();
            BetterMenuItem InstallInteractive = new();
            BetterMenuItem DownloadInstallers = new();

            MainToolbarButtonDropdown.Flyout = new BetterMenu()
            {
                Items =
                {
                    InstallAsAdmin,
                    InstallSkipHash,
                    InstallInteractive,
                    new MenuFlyoutSeparator(),
                    DownloadInstallers,
                },
                Placement = FlyoutPlacementMode.Bottom,
            };
            MainToolbarButtonIcon.Icon = IconType.Download;
            MainToolbarButtonText.Text = CoreTools.Translate("Install selection");

            AppBarButton InstallationSettings = new();

            AppBarButton PackageDetails = new();

            AppBarButton ExportSelection = new();

            ToolBar.PrimaryCommands.Add(new AppBarSeparator());
            ToolBar.PrimaryCommands.Add(InstallationSettings);
            ToolBar.PrimaryCommands.Add(new AppBarSeparator());
            ToolBar.PrimaryCommands.Add(PackageDetails);
            ToolBar.PrimaryCommands.Add(new AppBarSeparator());
            ToolBar.PrimaryCommands.Add(ExportSelection);

            Dictionary<DependencyObject, string> Labels = new()
            { // Entries with a trailing space are collapsed
                // Their texts will be used as the tooltip
                { InstallAsAdmin, CoreTools.Translate("Install as administrator") },
                { InstallSkipHash, CoreTools.Translate("Skip integrity checks") },
                { InstallInteractive, CoreTools.Translate("Interactive installation") },
                { DownloadInstallers, CoreTools.Translate("Download selected installers") },
                { InstallationSettings, CoreTools.Translate("Install options") },
                { PackageDetails, " " + CoreTools.Translate("Package details") },
                { ExportSelection, CoreTools.Translate("Add selection to bundle") },
            };

            Dictionary<DependencyObject, IconType> Icons = new()
            {
                { InstallAsAdmin, IconType.UAC },
                { InstallSkipHash, IconType.Checksum },
                { InstallationSettings, IconType.Options },
                { DownloadInstallers, IconType.Download },
                { InstallInteractive, IconType.Interactive },
                { PackageDetails, IconType.Info_Round },
                { ExportSelection, IconType.AddTo },
            };

            ApplyTextAndIconsToToolbar(Labels, Icons);

            PackageDetails.Click += (_, _) =>
                ShowDetailsForPackage(SelectedItem, TEL_InstallReferral.DIRECT_SEARCH);
            ExportSelection.Click += ExportSelection_Click;
            InstallationSettings.Click += (_, _) =>
                _ = ShowInstallationOptionsForPackage(SelectedItem);

            MainToolbarButton.Click += (_, _) =>
                MainApp.Operations.Install(
                    FilteredPackages.GetCheckedPackages(),
                    TEL_InstallReferral.DIRECT_SEARCH
                );
            InstallAsAdmin.Click += (_, _) =>
                MainApp.Operations.Install(
                    FilteredPackages.GetCheckedPackages(),
                    TEL_InstallReferral.DIRECT_SEARCH,
                    elevated: true
                );
            InstallSkipHash.Click += (_, _) =>
                MainApp.Operations.Install(
                    FilteredPackages.GetCheckedPackages(),
                    TEL_InstallReferral.DIRECT_SEARCH,
                    no_integrity: true
                );
            InstallInteractive.Click += (_, _) =>
                MainApp.Operations.Install(
                    FilteredPackages.GetCheckedPackages(),
                    TEL_InstallReferral.DIRECT_SEARCH,
                    interactive: true
                );
            DownloadInstallers.Click += (_, _) =>
                _ = MainApp.Operations.Download(
                    FilteredPackages.GetCheckedPackages(),
                    TEL_InstallReferral.DIRECT_SEARCH
                );

        }

        public override async Task LoadPackages()
        {
            if (QueryBlock.Text.Trim() != "")
            {
                await LoadPackages(ReloadReason.External);
            }
        }

        private void Event_SearchPackages(object? sender, RoutedEventArgs e)
        {
            if (QueryBlock.Text.Trim() != "")
            {
                _ = (Loader as DiscoverablePackagesLoader)?.ReloadPackages(QueryBlock.Text.Trim());
            }
            else
            {
                Loader.StopLoading();
            }
        }

        protected override void WhenPackageCountUpdated() { }

        protected override void WhenPackagesLoaded(ReloadReason reason) { }

        protected override void WhenShowingContextMenu(IPackage package)
        {
            if (
                MenuAsAdmin is null
                || MenuInteractive is null
                || MenuSkipHash is null
                || MenuDownloadInstaller is null
            )
            {
                Logger.Warn("MenuItems are null on DiscoverPackagesPage");
                return;
            }

            MenuAsAdmin.IsEnabled = package.Manager.Capabilities.CanRunAsAdmin;
            MenuInteractive.IsEnabled = package.Manager.Capabilities.CanRunInteractively;
            MenuSkipHash.IsEnabled = package.Manager.Capabilities.CanSkipIntegrityChecks;
            MenuDownloadInstaller.IsEnabled = package.Manager.Capabilities.CanDownloadInstaller;
        }

        private void ExportSelection_Click(object sender, RoutedEventArgs e) =>
            _ = _exportSelection_Click();

        private async Task _exportSelection_Click()
        {
            MainApp.Instance.MainWindow.NavigationPage.NavigateTo(PageType.Bundles);
            int loadingId = DialogHelper.ShowLoadingDialog(CoreTools.Translate("Please wait..."));
            await PackageBundlesLoader.Instance.AddPackagesAsync(
                FilteredPackages.GetCheckedPackages()
            );
            DialogHelper.HideLoadingDialog(loadingId);
        }

        private void MenuDetails_Invoked(object sender, RoutedEventArgs e)
        {
            ShowDetailsForPackage(SelectedItem, TEL_InstallReferral.DIRECT_SEARCH);
        }

        private void MenuInstall_Invoked(object sender, RoutedEventArgs e) =>
            _ = MainApp.Operations.Install(SelectedItem, TEL_InstallReferral.DIRECT_SEARCH);

        private void MenuSkipHash_Invoked(object sender, RoutedEventArgs e) =>
            _ = MainApp.Operations.Install(
                SelectedItem,
                TEL_InstallReferral.DIRECT_SEARCH,
                no_integrity: true
            );

        private void MenuInteractive_Invoked(object sender, RoutedEventArgs e) =>
            _ = MainApp.Operations.Install(
                SelectedItem,
                TEL_InstallReferral.DIRECT_SEARCH,
                interactive: true
            );

        private void MenuAsAdmin_Invoked(object sender, RoutedEventArgs e) =>
            _ = MainApp.Operations.Install(
                SelectedItem,
                TEL_InstallReferral.DIRECT_SEARCH,
                elevated: true
            );

        private void MenuInstallSettings_Invoked(object sender, RoutedEventArgs e) =>
            _ = ShowInstallationOptionsForPackage(SelectedItem);
    }
}

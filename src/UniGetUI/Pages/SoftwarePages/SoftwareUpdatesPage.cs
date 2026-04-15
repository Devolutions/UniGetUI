using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.Windows.System.Power;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.Interface.Telemetry;
using UniGetUI.Interface.Widgets;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.Pages.DialogPages;
using Windows.Networking.Connectivity;
using Windows.UI.Text;

namespace UniGetUI.Interface.SoftwarePages
{
    public partial class SoftwareUpdatesPage : AbstractPackagesPage
    {
        private BetterMenuItem? MenuAsAdmin;
        private BetterMenuItem? MenuInteractive;
        private BetterMenuItem? MenuskipHash;
        private BetterMenuItem? MenuDownloadInstaller;
        private BetterMenuItem? MenuOpenInstallLocation;

        public SoftwareUpdatesPage()
            : base(
                new PackagesPageData
                {
                    DisableAutomaticPackageLoadOnStart = false,
                    DisableFilterOnQueryChange = false,
                    MegaQueryBlockEnabled = false,
                    ShowLastLoadTime = true,
                    DisableReload = false,
                    PackagesAreCheckedByDefault = true,
                    DisableSuggestedResultsRadio = true,
                    PageName = "Updates",

                    Loader = UpgradablePackagesLoader.Instance,
                    PageRole = OperationType.Update,

                    NoPackages_BackgroundText = CoreTools.Translate(
                        "Hooray! No updates were found."
                    ),
                    NoPackages_SourcesText = CoreTools.Translate("Everything is up to date"),
                    NoPackages_SubtitleText_Base = CoreTools.Translate("Everything is up to date"),
                    MainSubtitle_StillLoading = CoreTools.Translate("Loading packages"),
                    NoMatches_BackgroundText = CoreTools.Translate(
                        "No results were found matching the input criteria"
                    ),

                    PageTitle = CoreTools.Translate("Software Updates"),
                    Glyph = "\uE895",
                }
            )
        { }

        public override BetterMenu GenerateContextMenu()
        {
            BetterMenu ContextMenu = new();

            BetterMenuItem menuInstall = new()
            {
                Text = CoreTools.AutoTranslated("Update"),
                IconName = IconType.Update,
                KeyboardAcceleratorTextOverride = "Ctrl+Enter",
            };
            menuInstall.Click += MenuInstall_Invoked;

            BetterMenuItem menuInstallSettings = new()
            {
                Text = CoreTools.AutoTranslated("Update options"),
                IconName = IconType.Options,
                KeyboardAcceleratorTextOverride = "Alt+Enter",
            };
            menuInstallSettings.Click += (_, _) =>
                _ = ShowInstallationOptionsForPackage(SelectedItem);

            MenuOpenInstallLocation = new()
            {
                Text = CoreTools.AutoTranslated("Open install location"),
                IconName = IconType.Launch,
            };
            MenuOpenInstallLocation.Click += (_, _) => OpenPackageInstallLocation(SelectedItem);

            MenuAsAdmin = new BetterMenuItem
            {
                Text = CoreTools.AutoTranslated("Update as administrator"),
                IconName = IconType.UAC,
            };
            MenuAsAdmin.Click += MenuAsAdmin_Invoked;

            MenuInteractive = new BetterMenuItem
            {
                Text = CoreTools.AutoTranslated("Interactive update"),
                IconName = IconType.Interactive,
            };
            MenuInteractive.Click += MenuInteractive_Invoked;

            MenuskipHash = new BetterMenuItem
            {
                Text = CoreTools.AutoTranslated("Skip hash check"),
                IconName = IconType.Checksum,
            };
            MenuskipHash.Click += MenuSkipHash_Invoked;

            MenuDownloadInstaller = new BetterMenuItem
            {
                Text = CoreTools.AutoTranslated("Download installer"),
                IconName = IconType.Download,
            };
            MenuDownloadInstaller.Click += (_, _) =>
                _ = MainApp.Operations.AskLocationAndDownload(
                    SelectedItem,
                    TEL_InstallReferral.ALREADY_INSTALLED
                );

            BetterMenuItem menuUpdateAfterUninstall = new()
            {
                Text = CoreTools.AutoTranslated("Uninstall package, then update it"),
                IconName = IconType.Undelete,
            };
            menuUpdateAfterUninstall.Click += MenuUpdateAfterUninstall_Invoked;

            BetterMenuItem menuUninstall = new()
            {
                Text = CoreTools.AutoTranslated("Uninstall package"),
                IconName = IconType.Delete,
            };
            menuUninstall.Click += MenuUninstall_Invoked;

            BetterMenuItem menuIgnorePackage = new()
            {
                Text = CoreTools.AutoTranslated("Ignore updates for this package"),
                IconName = IconType.Pin,
            };
            menuIgnorePackage.Click += MenuIgnorePackage_Invoked;

            BetterMenuItem menuSkipVersion = new()
            {
                Text = CoreTools.AutoTranslated("Skip this version"),
                IconName = IconType.Skip,
            };
            menuSkipVersion.Click += MenuSkipVersion_Invoked;

            BetterMenuItem menuDetails = new()
            {
                Text = CoreTools.AutoTranslated("Package details"),
                IconName = IconType.Info_Round,
                KeyboardAcceleratorTextOverride = "Enter",
            };
            menuDetails.Click += (_, _) =>
                ShowDetailsForPackage(SelectedItem, TEL_InstallReferral.ALREADY_INSTALLED);

            MenuFlyoutSubItem menuPause = new()
            {
                Text = CoreTools.Translate("Pause updates for"),
                Icon = new FontIcon { Glyph = "\uE769" },
            };
            foreach (
                IgnoredUpdatesDatabase.PauseTime menuTime in new List<IgnoredUpdatesDatabase.PauseTime>
                {
                    new() { Days = 1 },
                    new() { Days = 3 },
                    new() { Weeks = 1 },
                    new() { Weeks = 2 },
                    new() { Weeks = 4 },
                    new() { Months = 3 },
                    new() { Months = 6 },
                    new() { Months = 12 },
                }
            )
            {
                BetterMenuItem menuItem = new() { Text = menuTime.StringRepresentation() };
                menuItem.Click += (_, _) =>
                {
                    if (SelectedItem != null)
                    {
                        SelectedItem.AddToIgnoredUpdatesAsync("<" + menuTime.GetDateFromNow());
                        UpgradablePackagesLoader.Instance.IgnoredPackages[SelectedItem.Id] =
                            SelectedItem;
                        Loader.Remove(SelectedItem);
                    }
                };
                menuPause.Items.Add(menuItem);
            }

            ContextMenu.Items.Add(menuInstall);
            ContextMenu.Items.Add(new MenuFlyoutSeparator());
            ContextMenu.Items.Add(menuInstallSettings);
            ContextMenu.Items.Add(MenuOpenInstallLocation);
            ContextMenu.Items.Add(new MenuFlyoutSeparator());
            ContextMenu.Items.Add(MenuAsAdmin);
            ContextMenu.Items.Add(MenuInteractive);
            ContextMenu.Items.Add(MenuskipHash);
            ContextMenu.Items.Add(MenuDownloadInstaller);
            ContextMenu.Items.Add(new MenuFlyoutSeparator());
            ContextMenu.Items.Add(menuUpdateAfterUninstall);
            ContextMenu.Items.Add(menuUninstall);
            ContextMenu.Items.Add(new MenuFlyoutSeparator());
            ContextMenu.Items.Add(menuIgnorePackage);
            ContextMenu.Items.Add(menuSkipVersion);
            ContextMenu.Items.Add(menuPause);
            ContextMenu.Items.Add(new MenuFlyoutSeparator());
            ContextMenu.Items.Add(menuDetails);

            return ContextMenu;
        }

        protected override void WhenShowingContextMenu(IPackage package)
        {
            if (
                MenuAsAdmin is null
                || MenuInteractive is null
                || MenuskipHash is null
                || MenuDownloadInstaller is null
                || MenuOpenInstallLocation is null
            )
            {
                Logger.Error("Menu items are null on SoftwareUpdatesTab");
                return;
            }

            MenuAsAdmin.IsEnabled = package.Manager.Capabilities.CanRunAsAdmin;
            MenuInteractive.IsEnabled = package.Manager.Capabilities.CanRunInteractively;
            MenuskipHash.IsEnabled = package.Manager.Capabilities.CanSkipIntegrityChecks;
            MenuDownloadInstaller.IsEnabled = package.Manager.Capabilities.CanDownloadInstaller;

            MenuOpenInstallLocation.IsEnabled =
                package.Manager.DetailsHelper.GetInstallLocation(package) is not null;
        }

        public override void GenerateToolBar()
        {
            BetterMenuItem UpdateAsAdmin = new();
            BetterMenuItem UpdateSkipHash = new();
            BetterMenuItem UpdateInteractive = new();
            BetterMenuItem DownloadInstallers = new();
            BetterMenuItem UninstallSelection = new();

            MainToolbarButtonDropdown.Flyout = new BetterMenu()
            {
                Items =
                {
                    UpdateAsAdmin,
                    UpdateSkipHash,
                    UpdateInteractive,
                    new MenuFlyoutSeparator(),
                    DownloadInstallers,
                    new MenuFlyoutSeparator(),
                    UninstallSelection,
                },
                Placement = FlyoutPlacementMode.Bottom,
            };
            MainToolbarButtonIcon.Icon = IconType.Update;
            MainToolbarButtonText.Text = CoreTools.Translate("Update selection");

            AppBarButton InstallationSettings = new();

            AppBarButton PackageDetails = new();

            AppBarButton IgnoreSelected = new();
            AppBarButton ManageIgnored = new();

            AppBarButton HelpButton = new();

            ToolBar.PrimaryCommands.Add(new AppBarSeparator());
            ToolBar.PrimaryCommands.Add(InstallationSettings);
            ToolBar.PrimaryCommands.Add(new AppBarSeparator());
            ToolBar.PrimaryCommands.Add(PackageDetails);
            ToolBar.PrimaryCommands.Add(new AppBarSeparator());
            ToolBar.PrimaryCommands.Add(IgnoreSelected);
            ToolBar.PrimaryCommands.Add(ManageIgnored);
            ToolBar.PrimaryCommands.Add(new AppBarSeparator());
            ToolBar.PrimaryCommands.Add(HelpButton);

            Dictionary<DependencyObject, string> Labels = new()
            { // Entries with a leading space are collapsed
                // Their texts will be used as the tooltip
                { UpdateAsAdmin, CoreTools.Translate("Update as administrator") },
                { UpdateSkipHash, CoreTools.Translate("Skip integrity checks") },
                { UpdateInteractive, CoreTools.Translate("Interactive update") },
                { DownloadInstallers, CoreTools.Translate("Download selected installers") },
                { UninstallSelection, CoreTools.Translate("Uninstall selected packages") },
                { InstallationSettings, " " + CoreTools.Translate("Update options") },
                { PackageDetails, " " + CoreTools.Translate("Package details") },
                { IgnoreSelected, CoreTools.Translate("Ignore selected packages") },
                { ManageIgnored, CoreTools.Translate("Manage ignored updates") },
                { HelpButton, CoreTools.Translate("Help") },
            };

            Dictionary<DependencyObject, IconType> Icons = new()
            {
                { UpdateAsAdmin, IconType.UAC },
                { UpdateSkipHash, IconType.Checksum },
                { UpdateInteractive, IconType.Interactive },
                { InstallationSettings, IconType.Options },
                { DownloadInstallers, IconType.Download },
                { UninstallSelection, IconType.Delete },
                { PackageDetails, IconType.Info_Round },
                { IgnoreSelected, IconType.Pin },
                { ManageIgnored, IconType.ClipboardList },
                { HelpButton, IconType.Help },
            };

            ApplyTextAndIconsToToolbar(Labels, Icons);

            PackageDetails.Click += (_, _) =>
                ShowDetailsForPackage(SelectedItem, TEL_InstallReferral.ALREADY_INSTALLED);
            HelpButton.Click += (_, _) => MainApp.Instance.MainWindow.NavigationPage.ShowHelp();
            InstallationSettings.Click += (_, _) =>
                _ = ShowInstallationOptionsForPackage(SelectedItem);
            ManageIgnored.Click += async (_, _) => await DialogHelper.ManageIgnoredUpdates();
            IgnoreSelected.Click += async (_, _) =>
            {
                foreach (IPackage package in FilteredPackages.GetCheckedPackages())
                {
                    await package.AddToIgnoredUpdatesAsync();
                    UpgradablePackagesLoader.Instance.Remove(package);
                    UpgradablePackagesLoader.Instance.IgnoredPackages[package.Id] = package;
                }
            };

            MainToolbarButton.Click += (_, _) =>
                MainApp.Operations.Update(FilteredPackages.GetCheckedPackages());
            UpdateAsAdmin.Click += (_, _) =>
                MainApp.Operations.Update(FilteredPackages.GetCheckedPackages(), elevated: true);
            UpdateSkipHash.Click += (_, _) =>
                MainApp.Operations.Update(
                    FilteredPackages.GetCheckedPackages(),
                    no_integrity: true
                );
            UpdateInteractive.Click += (_, _) =>
                MainApp.Operations.Update(FilteredPackages.GetCheckedPackages(), interactive: true);
            DownloadInstallers.Click += (_, _) =>
                _ = MainApp.Operations.Download(
                    FilteredPackages.GetCheckedPackages(),
                    TEL_InstallReferral.ALREADY_INSTALLED
                );
            UninstallSelection.Click += (_, _) =>
                _ = MainApp.Operations.ConfirmAndUninstall(FilteredPackages.GetCheckedPackages());
        }

        protected override void WhenPackageCountUpdated()
        {
            MainApp.Tooltip.AvailableUpdates = Loader.Count();
        }

        protected override async void WhenPackagesLoaded(ReloadReason reason)
        {
            try
            {
                List<IPackage> upgradablePackages = [];
                foreach (IPackage package in Loader.Packages)
                {
                    if (package.Tag is not PackageTag.OnQueue and not PackageTag.BeingProcessed)
                        upgradablePackages.Add(package);
                }

                if (upgradablePackages.Count == 0)
                    return;

                if (
                    Settings.Get(Settings.K.DisableAUPOnMeteredConnections)
                    && NetworkInformation
                        .GetInternetConnectionProfile()
                        ?.GetConnectionCost()
                        .NetworkCostType
                        is NetworkCostType.Fixed
                            or NetworkCostType.Variable
                )
                {
                    Logger.Warn(
                        "Updates will not be installed automatically because the current internet connection is metered."
                    );
                    await ShowAvailableUpdatesNotification(upgradablePackages);
                }
                else if (
                    Settings.Get(Settings.K.DisableAUPOnBattery)
                    && PowerManager.PowerSupplyStatus is PowerSupplyStatus.NotPresent
                )
                {
                    Logger.Warn(
                        "Updates will not be installed automatically because the device is on battery."
                    );
                    await ShowAvailableUpdatesNotification(upgradablePackages);
                }
                else if (
                    Settings.Get(Settings.K.DisableAUPOnBatterySaver)
                    && PowerManager.EnergySaverStatus is EnergySaverStatus.On
                )
                {
                    Logger.Warn(
                        "Updates will not be installed automatically because battery saver is enabled."
                    );
                    await ShowAvailableUpdatesNotification(upgradablePackages);
                }
                else if (Settings.Get(Settings.K.AutomaticallyUpdatePackages))
                {
                    _ = MainApp.Operations.UpdateAll();
                    await ShowUpgradingPackagesNotification(upgradablePackages);
                }
                else if (Environment.GetCommandLineArgs().Contains("--updateapps"))
                {
                    _ = MainApp.Operations.UpdateAll();
                    await ShowUpgradingPackagesNotification(upgradablePackages);
                    Logger.Warn(
                        "Automatic install of updates has been enabled via Command Line (user settings have been overriden)"
                    );
                }
                else
                {
                    foreach (var package in upgradablePackages)
                    {
                        if (
                            (
                                await InstallOptionsFactory.LoadApplicableAsync(package)
                            ).AutoUpdatePackage
                        )
                        {
                            await MainApp.Operations.Update(package);
                        }
                    }
                    await ShowAvailableUpdatesNotification(upgradablePackages);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        static async Task ShowAvailableUpdatesNotification(
            IReadOnlyList<IPackage> upgradablePackages
        )
        {
            if (Settings.AreUpdatesNotificationsDisabled())
                return;

            bool SendNotification = false;
            foreach (var Package in upgradablePackages)
            {
                // This allows to disable update notifications only for certain package managers
                if (
                    !Settings.GetDictionaryItem<string, bool>(
                        Settings.K.DisabledPackageManagerNotifications,
                        Package.Manager.Name
                    )
                )
                {
                    SendNotification = true;
                    break;
                }
            }

            if (!SendNotification)
                return;

            await AppNotificationManager.Default.RemoveByTagAsync(
                CoreData.UpdatesAvailableNotificationTag.ToString()
            );

            AppNotification notification;
            if (upgradablePackages.Count == 1)
            {
                AppNotificationBuilder builder = new AppNotificationBuilder()
                    .SetScenario(AppNotificationScenario.Default)
                    .SetTag(CoreData.UpdatesAvailableNotificationTag.ToString())
                    .AddText(CoreTools.Translate("An update was found!"))
                    .AddText(
                        CoreTools.Translate(
                            "{0} can be updated to version {1}",
                            upgradablePackages[0].Name,
                            upgradablePackages[0].NewVersionString
                        )
                    )
                    .SetAttributionText(
                        CoreTools.Translate(
                            "You have currently version {0} installed",
                            upgradablePackages[0].VersionString
                        )
                    )
                    .AddArgument("action", NotificationArguments.ShowOnUpdatesTab) // Believe it or not, the `'` character is broken
                    .AddButton(
                        new AppNotificationButton(
                            CoreTools.Translate("View on UniGetUI").Replace("'", "´")
                        ).AddArgument("action", NotificationArguments.ShowOnUpdatesTab)
                    )
                    .AddButton(
                        new AppNotificationButton(CoreTools.Translate("Update")).AddArgument(
                            "action",
                            NotificationArguments.UpdateAllPackages
                        )
                    );
                notification = builder.BuildNotification();
            }
            else
            {
                string attribution = "";
                foreach (IPackage package in upgradablePackages)
                {
                    if (
                        !Settings.GetDictionaryItem<string, bool>(
                            Settings.K.DisabledPackageManagerNotifications,
                            package.Manager.Name
                        )
                    )
                        attribution += package.Name + ", ";
                }

                attribution = attribution.TrimEnd(' ').TrimEnd(',');

                AppNotificationBuilder builder = new AppNotificationBuilder()
                    .SetScenario(AppNotificationScenario.Default)
                    .SetTag(CoreData.UpdatesAvailableNotificationTag.ToString())
                    .AddText(CoreTools.Translate("Updates found!"))
                    .AddText(
                        CoreTools.Translate("{0} packages can be updated", upgradablePackages.Count)
                    )
                    .SetAttributionText(attribution)
                    // Believe it or not, the `'` character is broken
                    .AddButton(
                        new AppNotificationButton(
                            CoreTools.Translate("Open UniGetUI").Replace("'", "´")
                        ).AddArgument("action", NotificationArguments.ShowOnUpdatesTab)
                    )
                    .AddButton(
                        new AppNotificationButton(CoreTools.Translate("Update all")).AddArgument(
                            "action",
                            NotificationArguments.UpdateAllPackages
                        )
                    )
                    .AddArgument("action", NotificationArguments.ShowOnUpdatesTab);
                notification = builder.BuildNotification();
            }

            notification.ExpiresOnReboot = true;
            AppNotificationManager.Default.Show(notification);
        }

        static async Task ShowUpgradingPackagesNotification(
            IReadOnlyList<IPackage> upgradablePackages
        )
        {
            if (Settings.AreUpdatesNotificationsDisabled())
                return;

            bool SendNotification = false;
            foreach (var Package in upgradablePackages)
            {
                if (
                    !Settings.GetDictionaryItem<string, bool>(
                        Settings.K.DisabledPackageManagerNotifications,
                        Package.Manager.Name
                    )
                )
                {
                    SendNotification = true;
                    break;
                }
            }

            if (!SendNotification)
                return;

            await AppNotificationManager.Default.RemoveByTagAsync(
                CoreData.UpdatesAvailableNotificationTag.ToString()
            );

            AppNotification notification;
            if (upgradablePackages.Count == 1)
            {
                AppNotificationBuilder builder = new AppNotificationBuilder()
                    .SetScenario(AppNotificationScenario.Default)
                    .SetTag(CoreData.UpdatesAvailableNotificationTag.ToString())
                    .AddText(CoreTools.Translate("An update was found!"))
                    .AddText(
                        CoreTools.Translate(
                            "{0} is being updated to version {1}",
                            upgradablePackages[0].Name,
                            upgradablePackages[0].NewVersionString
                        )
                    )
                    .SetAttributionText(
                        CoreTools.Translate(
                            "You have currently version {0} installed",
                            upgradablePackages[0].VersionString
                        )
                    )
                    .AddArgument("action", NotificationArguments.ShowOnUpdatesTab);
                notification = builder.BuildNotification();
            }
            else
            {
                string attribution = "";
                foreach (IPackage package in upgradablePackages)
                {
                    if (
                        !Settings.GetDictionaryItem<string, bool>(
                            Settings.K.DisabledPackageManagerNotifications,
                            package.Manager.Name
                        )
                    )
                        attribution += package.Name + ", ";
                }

                attribution = attribution.TrimEnd(' ').TrimEnd(',');

                AppNotificationBuilder builder = new AppNotificationBuilder()
                    .SetScenario(AppNotificationScenario.Default)
                    .SetTag(CoreData.UpdatesAvailableNotificationTag.ToString())
                    .AddText(
                        CoreTools.Translate(
                            "{0} packages are being updated",
                            upgradablePackages.Count
                        )
                    )
                    .SetAttributionText(attribution)
                    .AddText(CoreTools.Translate("Updates found!"))
                    .AddArgument("action", NotificationArguments.ShowOnUpdatesTab);
                notification = builder.BuildNotification();
            }

            notification.ExpiresOnReboot = true;
            AppNotificationManager.Default.Show(notification);
        }

        private void MenuInstall_Invoked(object sender, RoutedEventArgs e) =>
            _ = MainApp.Operations.Update(SelectedItem);

        private void MenuSkipHash_Invoked(object sender, RoutedEventArgs e) =>
            _ = MainApp.Operations.Update(SelectedItem, no_integrity: true);

        private void MenuInteractive_Invoked(object sender, RoutedEventArgs e) =>
            _ = MainApp.Operations.Update(SelectedItem, interactive: true);

        private void MenuAsAdmin_Invoked(object sender, RoutedEventArgs e) =>
            _ = MainApp.Operations.Update(SelectedItem, elevated: true);

        private void MenuUpdateAfterUninstall_Invoked(object sender, RoutedEventArgs e) =>
            _ = MainApp.Operations.UninstallThenUpdate(SelectedItem);

        private void MenuUninstall_Invoked(object sender, RoutedEventArgs e) =>
            _ = MainApp.Operations.Uninstall(SelectedItem);

        private void MenuIgnorePackage_Invoked(object sender, RoutedEventArgs e)
        {
            IPackage? package = SelectedItem;
            if (package is null)
            {
                return;
            }

            _ = package.AddToIgnoredUpdatesAsync();
            UpgradablePackagesLoader.Instance.Remove(package);
            UpgradablePackagesLoader.Instance.IgnoredPackages[package.Id] = package;
        }

        private void MenuSkipVersion_Invoked(object sender, RoutedEventArgs e)
        {
            IPackage? package = SelectedItem;
            if (package is null)
            {
                return;
            }

            _ = package.AddToIgnoredUpdatesAsync(package.NewVersionString);
            UpgradablePackagesLoader.Instance.Remove(package);
            UpgradablePackagesLoader.Instance.IgnoredPackages[package.Id] = package;
        }
    }
}

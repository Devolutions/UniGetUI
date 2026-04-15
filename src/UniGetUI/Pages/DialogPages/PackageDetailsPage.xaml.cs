using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.Interface.Telemetry;
using UniGetUI.Interface.Widgets;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.Pages.DialogPages;
using Windows.UI;
using Windows.UI.Text;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Interface.Dialogs
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class PackageDetailsPage : Page
    {
        public IPackage Package;
        public IPackage? AvailablePackage;
        public IPackage? UpgradablePackage;
        public IPackage? InstalledPackage;

        private readonly InstallOptionsPage InstallOptionsPage;
        public event EventHandler? Close;
        private readonly OperationType OperationRole;

        private bool PackageHasScreenshots;
        public ObservableCollection<TextBlock> ShowableTags = [];

        private enum LayoutMode
        {
            Normal,
            Wide,
            Unset,
        }

        private readonly TEL_InstallReferral InstallReferral;

        private LayoutMode __layout_mode = LayoutMode.Unset;

        public PackageDetailsPage(
            IPackage package,
            OperationType role,
            TEL_InstallReferral referral
        )
        {
            if (role == OperationType.None)
                role = OperationType.Install;

            InstallReferral = referral;
            OperationRole = role;
            Package = package;

            InitializeComponent();
            SizeChanged += PackageDetailsPage_SizeChanged;

            PackageName.Text = package.Name;
            LoadingIndicator.Visibility = Visibility.Visible;
            string LoadingString = CoreTools.Translate("Loading...");

            // Basic details section
            SetTextToItem(DescriptionContent, LoadingString);
            SetTextToItem(HomepageUrl_Label, CoreTools.Translate("Homepage") + ": ");
            SetTextToItem(HomepageUrl_Content, LoadingString);
            SetTextToItem(Author_Label, CoreTools.Translate("Author") + ": ");
            SetTextToItem(Author_Content, LoadingString);
            SetTextToItem(Publisher_Label, CoreTools.Translate("Publisher") + ": ");
            SetTextToItem(Publisher_Content, LoadingString);
            SetTextToItem(License_Label, CoreTools.Translate("License") + ": ");
            SetTextToItem(License_Content_Text, LoadingString);
            SetTextToItem(License_Content_Uri, "");
            SetTextToItem(Source_Label, CoreTools.Translate("Package Manager") + ": ");
            SetTextToItem(Source_Content, Package.Source.AsString_DisplayName);

            // Extended details section
            SetTextToItem(PackageId_Label, CoreTools.Translate("Package ID") + ": ");
            SetTextToItem(PackageId_Content, package.Id);
            SetTextToItem(ManifestUrl_Label, CoreTools.Translate("Manifest") + ": ");
            SetTextToItem(ManifestUrl_Content, LoadingString);

            SetTextToItem(InstallerType_Label, CoreTools.Translate("Installer Type") + ": ");
            SetTextToItem(InstallerType_Content, LoadingString);
            SetTextToItem(InstallerHash_Label, CoreTools.Translate("Installer SHA256") + ": ");
            SetTextToItem(InstallerHash_Content, LoadingString);
            SetTextToItem(InstallerUrl_Label, CoreTools.Translate("Installer URL") + ": ");
            SetTextToItem(InstallerUrl_Content, LoadingString);
            DownloadInstaller_Button.Click += DownloadInstallerButton_Click;
            SetTextToItem(DownloadInstaller_Button, CoreTools.Translate("Download installer"));
            SetTextToItem(UpdateDate_Label, CoreTools.Translate("Last updated:") + " ");
            SetTextToItem(UpdateDate_Content, LoadingString);
            SetTextToItem(Dependencies_Label, CoreTools.Translate("Dependencies:") + " ");
            DependenciesParagraph.Inlines.Clear();
            DependenciesParagraph.Inlines.Add(
                new Run()
                {
                    Text = LoadingString,
                    Foreground = new SolidColorBrush(color: Color.FromArgb(255, 127, 127, 127)),
                }
            );
            SetTextToItem(ReleaseNotes_Label, CoreTools.Translate("Release notes") + ": ");
            SetTextToItem(ReleaseNotes_Content, LoadingString);
            SetTextToItem(ReleaseNotesUrl_Label, CoreTools.Translate("Release notes URL") + ": ");
            SetTextToItem(ReleaseNotesUrl_Content, LoadingString);

            AvailablePackage = Package.GetAvailablePackage();
            UpgradablePackage = Package.GetUpgradablePackage();
            InstalledPackage = UpgradablePackage?.GetInstalledPackages().FirstOrDefault();

            var options = InstallOptionsFactory.LoadForPackage(package);
            InstallOptionsPage = new InstallOptionsPage(package, OperationRole, options);
            InstallOptionsPage.Close += (_, _) => Close?.Invoke(this, EventArgs.Empty);
            InstallOptionsPage.HideCloseButton();
            InstallOptionsPage.HideHeaderBar();
            InstallOptionsPage.MaxWidth = double.PositiveInfinity;
            InstallOptionsPage.HorizontalAlignment = HorizontalAlignment.Stretch;
            InstallOptionsPage.Margin = new(0, -16, 0, 0);
            InstallOptionsExpander.Content = InstallOptionsPage;

            MainActionButton.Padding = new Thickness(0);
            var textBlock = new TextBlock()
            {
                TextWrapping = TextWrapping.WrapWholeWords,
                Padding = new Thickness(0),
                TextAlignment = TextAlignment.Center,
            };
            MainActionButton.Content = textBlock;
            if (OperationRole is OperationType.Install)
            {
                textBlock.Text = CoreTools.Translate("Install");
                SetTextToItem(Version_Label, CoreTools.Translate("Version"));
                SetTextToItem(Version_Content, AvailablePackage?.VersionString ?? "NULL");
                SetUpActionButtonAsInstall();
            }
            else if (OperationRole is OperationType.Update)
            {
                textBlock.Text = CoreTools.Translate(
                    "Update to version {0}",
                    UpgradablePackage?.NewVersionString ?? "NULL"
                );
                SetTextToItem(Version_Label, CoreTools.Translate("Installed Version"));
                SetTextToItem(
                    Version_Content,
                    (UpgradablePackage?.VersionString ?? "NULL")
                        + $" - {CoreTools.Translate("Update to {0} available", UpgradablePackage?.NewVersionString ?? "NULL")}"
                );
                SetUpActionButtonAsUpdate();
            }
            else /* OperationRole is OperationType.Uninstall */
            {
                textBlock.Text = CoreTools.Translate("Uninstall");
                SetTextToItem(Version_Label, CoreTools.Translate("Installed Version"));
                SetTextToItem(Version_Content, InstalledPackage?.VersionString ?? "NULL");
                SetUpActionButtonAsUninstall();
            }
            _ = LoadInformation();

            TelemetryHandler.PackageDetails(package, referral.ToString());
        }

        public void SetUpActionButtonAsInstall()
        {
            var AsAdmin = new BetterMenuItem
            {
                Text = CoreTools.Translate("Install as administrator"),
                IconName = IconType.UAC,
                IsEnabled = Package.Manager.Capabilities.CanRunAsAdmin,
            };
            var Interactive = new BetterMenuItem
            {
                Text = CoreTools.Translate("Interactive installation"),
                IconName = IconType.Interactive,
                IsEnabled = Package.Manager.Capabilities.CanRunInteractively,
            };
            var SkipHash = new BetterMenuItem
            {
                Text = CoreTools.Translate("Skip hash check"),
                IconName = IconType.Checksum,
                IsEnabled = Package.Manager.Capabilities.CanSkipIntegrityChecks,
            };

            AsAdmin.Click += (_, _) => _ = DoAction(Package, OperationType.Install, AsAdmin: true);
            Interactive.Click += (_, _) =>
                _ = DoAction(Package, OperationType.Install, Interactive: true);
            SkipHash.Click += (_, _) =>
                _ = DoAction(Package, OperationType.Install, SkipHash: true);

            ExtendedActionsMenu.Items.Add(AsAdmin);
            ExtendedActionsMenu.Items.Add(Interactive);
            ExtendedActionsMenu.Items.Add(SkipHash);

            if (UpgradablePackage is not null)
            {
                ExtendedActionsMenu.Items.Add(new MenuFlyoutSeparator());
                var Upgrade = new BetterMenuItem
                {
                    Text = CoreTools.Translate(
                        "Update to version {0}",
                        UpgradablePackage.NewVersionString
                    ),
                    IconName = IconType.Update,
                };
                Upgrade.Click += (_, _) => _ = DoAction(UpgradablePackage, OperationType.Update);
                ExtendedActionsMenu.Items.Add(Upgrade);
            }

            if (InstalledPackage is not null)
            {
                ExtendedActionsMenu.Items.Add(new MenuFlyoutSeparator());
                var Uninstall = new BetterMenuItem
                {
                    Text = CoreTools.Translate("Uninstall"),
                    IconName = IconType.Delete,
                };
                Uninstall.Click += (_, _) =>
                    _ = DoAction(InstalledPackage, OperationType.Uninstall);
                ExtendedActionsMenu.Items.Add(Uninstall);
            }
        }

        public void SetUpActionButtonAsUpdate()
        {
            var AsAdmin = new BetterMenuItem
            {
                Text = CoreTools.Translate("Update as administrator"),
                IconName = IconType.UAC,
                IsEnabled = Package.Manager.Capabilities.CanRunAsAdmin,
            };
            var Interactive = new BetterMenuItem
            {
                Text = CoreTools.Translate("Interactive update"),
                IconName = IconType.Interactive,
                IsEnabled = Package.Manager.Capabilities.CanRunInteractively,
            };
            var SkipHash = new BetterMenuItem
            {
                Text = CoreTools.Translate("Skip hash check"),
                IconName = IconType.Checksum,
                IsEnabled = Package.Manager.Capabilities.CanSkipIntegrityChecks,
            };

            AsAdmin.Click += (_, _) => _ = DoAction(Package, OperationType.Update, AsAdmin: true);
            Interactive.Click += (_, _) =>
                _ = DoAction(Package, OperationType.Update, Interactive: true);
            SkipHash.Click += (_, _) => _ = DoAction(Package, OperationType.Update, SkipHash: true);

            ExtendedActionsMenu.Items.Add(AsAdmin);
            ExtendedActionsMenu.Items.Add(Interactive);
            ExtendedActionsMenu.Items.Add(SkipHash);

            if (InstalledPackage is not null)
            {
                ExtendedActionsMenu.Items.Add(new MenuFlyoutSeparator());
                var Uninstall = new BetterMenuItem
                {
                    Text = CoreTools.Translate("Uninstall"),
                    IconName = IconType.Delete,
                };
                Uninstall.Click += (_, _) =>
                    _ = DoAction(InstalledPackage, OperationType.Uninstall);
                ExtendedActionsMenu.Items.Add(Uninstall);
            }

            ExtendedActionsMenu.Items.Add(new MenuFlyoutSeparator());
            var Reinstall = new BetterMenuItem
            {
                Text = CoreTools.Translate("Reinstall"),
                IconName = IconType.Download,
            };
            Reinstall.Click += (_, _) => _ = DoAction(Package, OperationType.Install);
            ExtendedActionsMenu.Items.Add(Reinstall);
        }

        public void SetUpActionButtonAsUninstall()
        {
            var AsAdmin = new BetterMenuItem
            {
                Text = CoreTools.Translate("Uninstall as administrator"),
                IconName = IconType.UAC,
                IsEnabled = Package.Manager.Capabilities.CanRunAsAdmin,
            };
            var Interactive = new BetterMenuItem
            {
                Text = CoreTools.Translate("Interactive uninstall"),
                IconName = IconType.Interactive,
                IsEnabled = Package.Manager.Capabilities.CanRunInteractively,
            };
            var RemoveData = new BetterMenuItem
            {
                Text = CoreTools.Translate("Uninstall and remove data"),
                IconName = IconType.Close_Round,
                IsEnabled = Package.Manager.Capabilities.CanRemoveDataOnUninstall,
            };

            AsAdmin.Click += (_, _) =>
                _ = DoAction(Package, OperationType.Uninstall, AsAdmin: true);
            Interactive.Click += (_, _) =>
                _ = DoAction(Package, OperationType.Uninstall, Interactive: true);
            RemoveData.Click += (_, _) =>
                _ = DoAction(Package, OperationType.Uninstall, RemoveData: true);

            ExtendedActionsMenu.Items.Add(AsAdmin);
            ExtendedActionsMenu.Items.Add(Interactive);
            ExtendedActionsMenu.Items.Add(RemoveData);

            if (UpgradablePackage is not null)
            {
                ExtendedActionsMenu.Items.Add(new MenuFlyoutSeparator());
                var Upgrade = new BetterMenuItem
                {
                    Text = CoreTools.Translate(
                        "Update to version {0}",
                        UpgradablePackage.NewVersionString
                    ),
                    IconName = IconType.Update,
                };
                Upgrade.Click += (_, _) => _ = DoAction(UpgradablePackage, OperationType.Update);
                ExtendedActionsMenu.Items.Add(Upgrade);
            }

            ExtendedActionsMenu.Items.Add(new MenuFlyoutSeparator());
            var Reinstall = new BetterMenuItem
            {
                Text = CoreTools.Translate("Reinstall"),
                IconName = IconType.Download,
            };
            Reinstall.Click += (_, _) => _ = DoAction(Package, OperationType.Install);
            ExtendedActionsMenu.Items.Add(Reinstall);
        }

        public async Task LoadInformation()
        {
            LoadingIndicator.Visibility = Visibility.Visible;

            _ = LoadIcon();
            _ = LoadScreenshots();

            IPackageDetails details = Package.Details;
            if (!details.IsPopulated)
            {
                await details.Load();
            }

            LoadingIndicator.Visibility = Visibility.Collapsed;

            // Basic details section
            SetTextToItem(DescriptionContent, details.Description);
            SetTextToItem(HomepageUrl_Content, details.HomepageUrl);
            SetTextToItem(Author_Content, details.Author);
            SetTextToItem(Publisher_Content, details.Publisher);

            if (details.License is not null && details.LicenseUrl is not null)
            {
                SetTextToItem(License_Content_Text, details.License);
                SetTextToItem(License_Content_Uri, details.LicenseUrl, "(", ")");
            }
            else if (details.License is not null && details.LicenseUrl is null)
            {
                SetTextToItem(License_Content_Text, details.License);
                SetTextToItem(License_Content_Uri, "");
            }
            else if (details.License is null && details.LicenseUrl is not null)
            {
                SetTextToItem(License_Content_Text, "");
                SetTextToItem(License_Content_Uri, details.LicenseUrl);
            }
            else
            {
                SetTextToItem(License_Content_Text, null);
                SetTextToItem(License_Content_Uri, details.LicenseUrl);
            }

            // Extended details section
            SetTextToItem(ManifestUrl_Content, details.ManifestUrl);
            if (Package.Manager == PEInterface.Chocolatey)
            {
                SetTextToItem(InstallerHash_Label, CoreTools.Translate("Installer SHA512") + ": ");
            }
            else
            {
                SetTextToItem(InstallerHash_Label, CoreTools.Translate("Installer SHA256") + ": ");
            }

            SetTextToItem(InstallerHash_Content, details.InstallerHash);
            if (Package.Manager.Capabilities.CanDownloadInstaller)
            {
                SetTextToItem(
                    InstallerSize_Content,
                    details.InstallerSize > 0
                        ? $" ({CoreTools.FormatAsSize(details.InstallerSize, 2)})"
                        : $" ({CoreTools.Translate("Unknown size")})"
                );
                SetTextToItem(DownloadInstaller_Button, CoreTools.Translate("Download installer"));
            }
            else
            {
                SetTextToItem(InstallerSize_Content, "");
                SetTextToItem(
                    DownloadInstaller_Button,
                    CoreTools.Translate("Installer not available")
                );
            }
            SetTextToItem(InstallerUrl_Content, details.InstallerUrl);
            SetTextToItem(InstallerType_Content, details.InstallerType);
            SetTextToItem(UpdateDate_Content, details.UpdateDate);
            SetTextToItem(ReleaseNotes_Content, details.ReleaseNotes);
            SetTextToItem(ReleaseNotesUrl_Content, details.ReleaseNotesUrl);

            if (!details.Package.Manager.Capabilities.CanListDependencies)
            {
                DependenciesParagraph.Inlines.Clear();
                DependenciesParagraph.Inlines.Add(
                    new Run()
                    {
                        Text = CoreTools.Translate("Not available"),
                        Foreground = new SolidColorBrush(color: Color.FromArgb(255, 127, 127, 127)),
                    }
                );
            }
            else if (details.Dependencies.Any())
            {
                DependenciesParagraph.Inlines.Clear();

                foreach (var dep in details.Dependencies)
                {
                    DependenciesParagraph.Inlines.Add(
                        new Run()
                        {
                            Text = $"  • {dep.Name}",
                            FontStyle = dep.Mandatory ? FontStyle.Normal : FontStyle.Italic,
                            FontWeight = new FontWeight(600),
                        }
                    );

                    string line = $" (";
                    if (dep.Version.Any())
                        line += CoreTools.Translate("Version:") + $" {dep.Version}, ";
                    line +=
                        $"{(dep.Mandatory ? CoreTools.Translate("mandatory") : CoreTools.Translate("optional"))})";

                    DependenciesParagraph.Inlines.Add(
                        new Run()
                        {
                            Text = line,
                            FontStyle = dep.Mandatory ? FontStyle.Normal : FontStyle.Italic,
                        }
                    );
                    DependenciesParagraph.Inlines.Add(new LineBreak());
                }
                if (
                    DependenciesParagraph.Inlines.Any()
                    && DependenciesParagraph.Inlines.Last() is LineBreak
                )
                    DependenciesParagraph.Inlines.RemoveAt(DependenciesParagraph.Inlines.Count - 1);
            }
            else
            {
                DependenciesParagraph.Inlines.Clear();
                DependenciesParagraph.Inlines.Add(
                    new Run()
                    {
                        Text = "\t" + CoreTools.Translate("No dependencies specified"),
                        Foreground = new SolidColorBrush(color: Color.FromArgb(255, 127, 127, 127)),
                    }
                );
            }

            ShowableTags.Clear();
            foreach (string tag in details.Tags)
            {
                ShowableTags.Add(
                    new TextBlock
                    {
                        Text = tag,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextLineBounds = TextLineBounds.Tight,
                    }
                );
            }
        }

        public void SetTextToItem(Run r, string? s)
        {
            if (s is null)
            {
                r.Text = CoreTools.Translate("Not available");
                r.Foreground = new SolidColorBrush(color: Color.FromArgb(255, 127, 127, 127));
            }
            else
            {
                r.Text = s;
                r.ClearValue(TextElement.ForegroundProperty);
            }
        }

        public void SetTextToItem(Hyperlink h, Uri? u, string prefix = "", string suffix = "")
        {
            if (u is null)
            {
                h.Inlines.Clear();
                h.Inlines.Add(
                    new Run
                    {
                        Text = CoreTools.Translate("Not available"),
                        TextDecorations = TextDecorations.None,
                        Foreground = new SolidColorBrush(color: Color.FromArgb(255, 127, 127, 127)),
                    }
                );
                h.NavigateUri = u;
            }
            else
            {
                h.Inlines.Clear();
                h.Inlines.Add(new Run { Text = prefix + u.ToString() + suffix });
                h.NavigateUri = u;
            }
        }

        public void SetTextToItem(Hyperlink h, string s)
        {
            h.Inlines.Clear();
            h.Inlines.Add(new Run { Text = s });
            h.NavigateUri = null;
        }

        public async Task LoadIcon()
        {
            PackageIcon.Source = new BitmapImage { UriSource = await Task.Run(Package.GetIconUrl) };
        }

        public async Task LoadScreenshots()
        {
            IReadOnlyList<Uri> screenshots = await Task.Run(Package.GetScreenshots);
            PackageHasScreenshots = screenshots.Any();
            if (PackageHasScreenshots)
            {
                PackageHasScreenshots = true;
                IconsExtraBanner.Visibility = Visibility.Visible;
                ScreenshotsCarroussel.Items.Clear();
                foreach (Uri image in screenshots)
                {
                    ScreenshotsCarroussel.Items.Add(new Image { Source = new BitmapImage(image) });
                }
            }

            __layout_mode = LayoutMode.Unset;
            PackageDetailsPage_SizeChanged();
        }

        public void DownloadInstallerButton_Click(object sender, RoutedEventArgs e)
        {
            if (!Package.Manager.Capabilities.CanDownloadInstaller)
                return;
            Close?.Invoke(this, EventArgs.Empty);
            _ = MainApp.Operations.AskLocationAndDownload(Package, InstallReferral);
        }

        public void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close?.Invoke(this, EventArgs.Empty);
        }

        public void PackageDetailsPage_SizeChanged(
            object? sender = null,
            SizeChangedEventArgs? e = null
        )
        {
            if (ActualWidth < 950)
            {
                if (__layout_mode != LayoutMode.Normal)
                {
                    __layout_mode = LayoutMode.Normal;

                    MainGrid.ColumnDefinitions.Clear();
                    MainGrid.ColumnDefinitions.Add(
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                    );
                    Grid.SetColumn(TitlePanel, 0);
                    Grid.SetColumn(BasicInfoPanelText, 0);
                    Grid.SetColumn(ScreenshotsPanel, 0);
                    Grid.SetColumn(ActionsPanel, 0);
                    Grid.SetColumn(InstallOptionsBorder, 0);
                    Grid.SetColumn(DetailsPanelText, 0);

                    MainGrid.RowDefinitions.Clear();
                    MainGrid.RowDefinitions.Add(
                        new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }
                    );
                    MainGrid.RowDefinitions.Add(
                        new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }
                    );
                    MainGrid.RowDefinitions.Add(
                        new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }
                    );
                    MainGrid.RowDefinitions.Add(
                        new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }
                    );
                    MainGrid.RowDefinitions.Add(
                        new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }
                    );
                    MainGrid.RowDefinitions.Add(
                        new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }
                    );
                    MainGrid.RowDefinitions.Add(
                        new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }
                    );
                    Grid.SetRow(TitlePanel, 0);
                    Grid.SetRow(DescriptionPanel, 1);
                    Grid.SetRow(BasicInfoPanelText, 2);
                    Grid.SetRow(ActionsPanel, 3);
                    Grid.SetRow(InstallOptionsBorder, 4);
                    Grid.SetRow(ScreenshotsPanel, 5);
                    Grid.SetRow(DetailsPanelText, 6);

                    LeftPanel.Children.Clear();
                    RightPanel.Children.Clear();
                    MainGrid.Children.Clear();
                    MainGrid.Children.Add(TitlePanel);
                    MainGrid.Children.Add(DescriptionPanel);
                    MainGrid.Children.Add(BasicInfoPanelText);
                    MainGrid.Children.Add(ScreenshotsPanel);
                    MainGrid.Children.Add(ActionsPanel);
                    MainGrid.Children.Add(InstallOptionsBorder);
                    MainGrid.Children.Add(DetailsPanelText);
                    ScreenshotsCarroussel.Height = PackageHasScreenshots ? 225 : 150;

                    InstallOptionsExpander.IsExpanded = false;
                }
            }
            else
            {
                if (__layout_mode != LayoutMode.Wide)
                {
                    __layout_mode = LayoutMode.Wide;

                    MainGrid.ColumnDefinitions.Clear();
                    MainGrid.ColumnDefinitions.Add(
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                    );
                    MainGrid.ColumnDefinitions.Add(
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                    );
                    Grid.SetColumn(LeftPanel, 0);
                    Grid.SetColumn(RightPanel, 1);
                    Grid.SetColumn(TitlePanel, 0);
                    Grid.SetColumnSpan(TitlePanel, 1);

                    MainGrid.RowDefinitions.Clear();
                    MainGrid.RowDefinitions.Add(
                        new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }
                    );
                    Grid.SetRow(LeftPanel, 0);
                    Grid.SetRow(RightPanel, 0);

                    LeftPanel.Children.Clear();
                    RightPanel.Children.Clear();
                    MainGrid.Children.Clear();

                    Grid.SetRow(TitlePanel, 0);
                    Grid.SetRow(DescriptionPanel, 1);
                    Grid.SetRow(BasicInfoPanelText, 2);
                    Grid.SetRow(ActionsPanel, 3);
                    Grid.SetRow(InstallOptionsBorder, 4);

                    LeftPanel.Children.Add(TitlePanel);
                    LeftPanel.Children.Add(DescriptionPanel);
                    LeftPanel.Children.Add(BasicInfoPanelText);
                    LeftPanel.Children.Add(ActionsPanel);
                    LeftPanel.Children.Add(InstallOptionsBorder);

                    Grid.SetRow(ScreenshotsPanel, 0);
                    Grid.SetRow(DetailsPanelText, 1);

                    RightPanel.Children.Add(ScreenshotsPanel);
                    RightPanel.Children.Add(DetailsPanelText);
                    ScreenshotsCarroussel.Height = PackageHasScreenshots ? 400 : 150;

                    InstallOptionsExpander.IsExpanded = true;

                    MainGrid.Children.Add(LeftPanel);
                    MainGrid.Children.Add(RightPanel);
                }
            }
        }

        public void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            _ = DoAction(Package, OperationRole);
        }

        public async Task DoAction(
            IPackage package,
            OperationType action,
            bool? AsAdmin = null,
            bool? Interactive = null,
            bool? SkipHash = null,
            bool? RemoveData = null
        )
        {
            Close?.Invoke(this, EventArgs.Empty);

            var newOptions = await InstallOptionsPage.GetUpdatedOptions();
            await InstallOptionsFactory.SaveForPackageAsync(newOptions, package);

            if (AsAdmin is not null)
                newOptions.RunAsAdministrator = (bool)AsAdmin;
            if (Interactive is not null)
                newOptions.InteractiveInstallation = (bool)Interactive;
            if (SkipHash is not null)
                newOptions.SkipHashCheck = (bool)SkipHash;
            if (RemoveData is not null)
                newOptions.RemoveDataOnUninstall = (bool)RemoveData;

            if (action is OperationType.Install)
            {
                _ = MainApp.Operations.Install(
                    package,
                    InstallReferral,
                    AsAdmin,
                    Interactive,
                    SkipHash
                );
            }
            else if (action is OperationType.Uninstall)
            {
                _ = MainApp.Operations.ConfirmAndUninstall(
                    package,
                    AsAdmin,
                    Interactive,
                    RemoveData
                );
            }
            else if (action is OperationType.Update)
            {
                _ = MainApp.Operations.Update(package, AsAdmin, Interactive, SkipHash);
            }
            else
            {
                throw new ArgumentException(
                    "PackageDetailsPage.DoAction should never be called with action=None"
                );
            }
        }

        private void SaveInstallOptionsButton_Click(object sender, RoutedEventArgs e) =>
            _ = _saveInstallOptionsButton_Click();

        private async Task _saveInstallOptionsButton_Click()
        {
            try
            {
                SaveInstallOptionsButton.IsEnabled = false;
                SaveInstallOptionsButton.Content = new FontIcon { Glyph = "\uE9F5" };
                var options = await InstallOptionsPage.GetUpdatedOptions();
                await InstallOptionsFactory.SaveForPackageAsync(options, Package);
                await Task.Delay(400); // Give feedback to the user that things are being done
                SaveInstallOptionsButton.Content = new FontIcon { Glyph = "\uE73E" };
                SaveInstallOptionsButton.IsEnabled = true;
                await Task.Delay(2000);
                SaveInstallOptionsButton.Content = CoreTools.Translate("Save");
            }
            catch (Exception ex)
            {
                Logger.Error("An error occurred while saving install options");
                Logger.Error(ex);
            }
        }
    }
}

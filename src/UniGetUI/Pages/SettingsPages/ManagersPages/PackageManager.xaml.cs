using System.Diagnostics;
using CommunityToolkit.WinUI.Controls;
using ExternalLibraries.Clipboard;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Widgets;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.CargoManager;
using UniGetUI.PackageEngine.Managers.ChocolateyManager;
using UniGetUI.PackageEngine.Managers.DotNetManager;
using UniGetUI.PackageEngine.Managers.NpmManager;
using UniGetUI.PackageEngine.Managers.PipManager;
using UniGetUI.PackageEngine.Managers.PowerShell7Manager;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.Managers.ScoopManager;
using UniGetUI.PackageEngine.Managers.VcpkgManager;
using UniGetUI.PackageEngine.Managers.WingetManager;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.Pages.DialogPages;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Pages.SettingsPages.GeneralPages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class PackageManagerPage : Page, ISettingsPage
    {
        private static readonly HashSet<string> _managersWithoutUpdateDate =
            new(StringComparer.OrdinalIgnoreCase) { "Homebrew", "Scoop", "vcpkg", "WinGet" };

        IPackageManager? Manager;
        public event EventHandler? RestartRequired;
        public event EventHandler<Type>? NavigationRequested
        {
            add { }
            remove { }
        }
        public event EventHandler? ReapplyProperties;
        public bool CanGoBack => true;
        public string ShortTitle =>
            Manager is null ? "" : CoreTools.Translate("{0} settings", Manager.DisplayName);
        private bool _isLoading;

        public PackageManagerPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            Manager = null;
            if (e.Parameter is not Type Manager_T)
                throw new InvalidDataException("The passed parameter was not a type");
            // Can't do switch with types
            if (Manager_T == typeof(WinGet))
                Manager = PEInterface.WinGet;
            else if (Manager_T == typeof(Chocolatey))
                Manager = PEInterface.Chocolatey;
            else if (Manager_T == typeof(Scoop))
                Manager = PEInterface.Scoop;
            else if (Manager_T == typeof(Npm))
                Manager = PEInterface.Npm;
            else if (Manager_T == typeof(Pip))
                Manager = PEInterface.Pip;
            else if (Manager_T == typeof(PowerShell))
                Manager = PEInterface.PowerShell;
            else if (Manager_T == typeof(PowerShell7))
                Manager = PEInterface.PowerShell7;
            else if (Manager_T == typeof(Cargo))
                Manager = PEInterface.Cargo;
            else if (Manager_T == typeof(Vcpkg))
                Manager = PEInterface.Vcpkg;
            else if (Manager_T == typeof(DotNet))
                Manager = PEInterface.DotNet;
            else
                throw new InvalidCastException("The specified type was not a package manager!");

            ReapplyProperties?.Invoke(this, new());

            ApplyManagerState();
            EnableManager.KeyName = Manager.Name;
            EnableManager.Text = CoreTools
                .Translate("Enable {pm}")
                .Replace("{pm}", Manager.DisplayName);
            InstallOptionsTitle.Text = CoreTools.Translate(
                "Default installation options for {0} packages",
                Manager.DisplayName
            );

            SettingsTitle.Text = CoreTools.Translate("{0} settings", Manager.DisplayName);
            StatusTitle.Text = CoreTools.Translate("{0} status", Manager.DisplayName);

            var DisableNotifsCard = new CheckboxCard_Dict()
            {
                Text = CoreTools
                    .Translate(
                        "Ignore packages from {pm} when showing a notification about updates"
                    )
                    .Replace("{pm}", Manager.DisplayName),
                DictionaryName = Settings.K.DisabledPackageManagerNotifications,
                ForceInversion = true,
                KeyName = Manager.Name,
            };

            ManagerLogsLabel.Text = CoreTools.Translate("View {0} logs", Manager.DisplayName);

            // ----------------- EXECUTABLE FILE PICKER -----------------
            ExeFileWarningText.Visibility = SecureSettings.Get(
                SecureSettings.K.AllowCustomManagerPaths
            )
                ? Visibility.Collapsed
                : Visibility.Visible;
            GoToSecureSettingsBtn.Visibility = SecureSettings.Get(
                SecureSettings.K.AllowCustomManagerPaths
            )
                ? Visibility.Collapsed
                : Visibility.Visible;
            ExecutableComboBox.IsEnabled = SecureSettings.Get(
                SecureSettings.K.AllowCustomManagerPaths
            );

            InstallOptionsPanel.Description = new InstallOptions_Manager(Manager);
            InstallOptionsPanel.Padding = new(18, 24, 18, 24);

            // ----------------------- SOURCES CONTROL -------------------

            ExtraControls.Children.Clear();

            if (Manager.Capabilities.SupportsCustomSources && Manager is not Vcpkg)
            {
                SettingsCard SourceManagerCard = new()
                {
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 0, 0, 16),
                    Padding = new(24, 24, 0, 24),
                };
                var man = new SourceManager(Manager);
                SourceManagerCard.Description = man;
                ExtraControls.Children.Add(SourceManagerCard);

                ExtraControls.Children.Add(
                    new TextBlock()
                    {
                        Margin = new(4, 24, 4, 8),
                        FontWeight = new Windows.UI.Text.FontWeight(600),
                        Text = CoreTools.Translate("Advanced options"),
                    }
                );
            }

            // ------------------------- WINGET EXTRA SETTINGS -----------------------

            if (Manager is WinGet)
            {
                DisableNotifsCard.CornerRadius = new CornerRadius(8, 8, 0, 0);
                DisableNotifsCard.BorderThickness = new Thickness(1, 1, 1, 0);
                ExtraControls.Children.Add(DisableNotifsCard);

                ButtonCard WinGet_ResetWindowsIPackageManager = new()
                {
                    Text =
                        CoreTools.Translate("Reset WinGet")
                        + $" ({CoreTools.Translate("This may help if no packages are listed")})",
                    ButtonText = CoreTools.AutoTranslated("Reset"),
                    CornerRadius = new CornerRadius(0),
                };
                WinGet_ResetWindowsIPackageManager.Click += (_, _) =>
                    _ = DialogHelper.HandleBrokenWinGet();
                ExtraControls.Children.Add(WinGet_ResetWindowsIPackageManager);

                CheckboxCard WinGet_ForceLocationWhenUpdating = new()
                {
                    Text = CoreTools.Translate(
                        "Force install location parameter when updating packages with custom locations"
                    ),
                    SettingName = Settings.K.WinGetForceLocationOnUpdate,
                    CornerRadius = new CornerRadius(0),
                    BorderThickness = new Thickness(1, 0, 1, 1),
                };
                ExtraControls.Children.Add(WinGet_ForceLocationWhenUpdating);

                CheckboxCard WinGet_UseBundled = new()
                {
                    Text =
                        $"{CoreTools.Translate("Use bundled WinGet instead of system WinGet")} ({CoreTools.Translate("This may help if WinGet packages are not shown")})",
                    SettingName = Settings.K.ForceLegacyBundledWinGet,
                    CornerRadius = new CornerRadius(0, 0, 8, 8),
                    BorderThickness = new Thickness(1, 0, 1, 1),
                };
                WinGet_UseBundled.StateChanged += (_, _) => _ = ReloadPackageManager();
                ExtraControls.Children.Add(WinGet_UseBundled);

                /*CheckboxCard WinGet_EnableTroubleshooter = new()
                {
                    Text = CoreTools.Translate("Enable the automatic WinGet troubleshooter"),
                    SettingName = Settings.K.DisableWinGetMalfunctionDetector,
                    CornerRadius = new CornerRadius(0),
                };
                WinGet_EnableTroubleshooter.StateChanged += (_, _) =>
                {
                    MainApp.Instance.MainWindow.WinGetWarningBanner.IsOpen = false;
                    _ = InstalledPackagesLoader.Instance.ReloadPackages();
                };
                ExtraControls.Children.Add(WinGet_EnableTroubleshooter);

                CheckboxCard WinGet_EnableTroubleshooter_v2 = new()
                {
                    Text = CoreTools.Translate("Enable an [experimental] improved WinGet troubleshooter"),
                    SettingName = Settings.K.DisableNewWinGetTroubleshooter,
                    // CornerRadius = new CornerRadius(0),
                    CornerRadius = new CornerRadius(0, 0, 8, 8),
                    BorderThickness = new Thickness(1, 0, 1, 0),
                };
                WinGet_EnableTroubleshooter_v2.StateChanged += (_, _) =>
                {
                    MainApp.Instance.MainWindow.WinGetWarningBanner.IsOpen = false;
                    _ = InstalledPackagesLoader.Instance.ReloadPackages();
                };
                ExtraControls.Children.Add(WinGet_EnableTroubleshooter_v2);*/

                /*CheckboxCard WinGet_HideNonApplicableUpdates = new()
                {
                    Text = CoreTools.Translate("Add updates that fail with a 'no applicable update found' to the ignored updates list"),
                    SettingName = Settings.K.IgnoreUpdatesNotApplicable,
                    CornerRadius = new CornerRadius(0, 0, 8, 8)
                };
                ExtraControls.Children.Add(WinGet_HideNonApplicableUpdates);*/
            }
            // ---------------------------- SCOOP EXTRA SETTINGS -------------------------

            else if (Manager is Scoop)
            {
                DisableNotifsCard.CornerRadius = new CornerRadius(8, 8, 0, 0);
                DisableNotifsCard.BorderThickness = new Thickness(1, 1, 1, 0);
                ExtraControls.Children.Add(DisableNotifsCard);

                ButtonCard Scoop_Install = new()
                {
                    Text = CoreTools.AutoTranslated("Install Scoop"),
                    ButtonText = CoreTools.AutoTranslated("Install"),
                    CornerRadius = new CornerRadius(0),
                };
                Scoop_Install.Click += (_, _) =>
                {
                    _ = CoreTools.LaunchBatchFile(
                        Path.Join(
                            CoreData.UniGetUIExecutableDirectory,
                            "Assets",
                            "Utilities",
                            "install_scoop.cmd"
                        ),
                        CoreTools.Translate("Scoop Installer - UniGetUI")
                    );
                    RestartRequired?.Invoke(this, new());
                };
                ExtraControls.Children.Add(Scoop_Install);

                ButtonCard Scoop_Uninstall = new()
                {
                    Text = CoreTools.AutoTranslated("Uninstall Scoop (and its packages)"),
                    ButtonText = CoreTools.AutoTranslated("Uninstall"),
                    CornerRadius = new CornerRadius(0),
                    BorderThickness = new Thickness(1, 0, 1, 0),
                };
                Scoop_Uninstall.Click += (_, _) =>
                {
                    _ = CoreTools.LaunchBatchFile(
                        Path.Join(
                            CoreData.UniGetUIExecutableDirectory,
                            "Assets",
                            "Utilities",
                            "uninstall_scoop.cmd"
                        ),
                        CoreTools.Translate("Scoop Uninstaller - UniGetUI")
                    );
                    RestartRequired?.Invoke(this, new());
                };
                ExtraControls.Children.Add(Scoop_Uninstall);

                ButtonCard Scoop_ResetAppCache = new()
                {
                    Text = CoreTools.AutoTranslated("Run cleanup and clear cache"),
                    ButtonText = CoreTools.AutoTranslated("Run"),
                    CornerRadius = new CornerRadius(0),
                };
                Scoop_ResetAppCache.Click += (_, _) =>
                {
                    _ = CoreTools.LaunchBatchFile(
                        Path.Join(
                            CoreData.UniGetUIExecutableDirectory,
                            "Assets",
                            "Utilities",
                            "scoop_cleanup.cmd"
                        ),
                        CoreTools.Translate("Clearing Scoop cache - UniGetUI"),
                        RunAsAdmin: true
                    );
                };
                ExtraControls.Children.Add(Scoop_ResetAppCache);

                CheckboxCard Scoop_CleanupOnStart = new()
                {
                    CornerRadius = new CornerRadius(0, 0, 8, 8),
                    BorderThickness = new Thickness(1, 0, 1, 1),
                    SettingName = Settings.K.EnableScoopCleanup,
                    Text = "Enable Scoop cleanup on launch",
                };
                ExtraControls.Children.Add(Scoop_CleanupOnStart);
            }
            // ----------------------------- CHOCO EXTRA SETTINGS ------------------------------

            else if (Manager is Chocolatey)
            {
                DisableNotifsCard.CornerRadius = new CornerRadius(8, 8, 0, 0);
                DisableNotifsCard.BorderThickness = new Thickness(1, 1, 1, 0);
                ExtraControls.Children.Add(DisableNotifsCard);

                CheckboxCard Chocolatey_SystemChoco = new()
                {
                    Text = CoreTools.AutoTranslated("Use system Chocolatey"),
                    SettingName = Settings.K.UseSystemChocolatey,
                    CornerRadius = new CornerRadius(0, 0, 8, 8),
                };
                Chocolatey_SystemChoco.StateChanged += (_, _) => _ = ReloadPackageManager();
                ExtraControls.Children.Add(Chocolatey_SystemChoco);
            }
            // -------------------------------- VCPKG EXTRA SETTINGS --------------------------------------

            else if (Manager is Vcpkg)
            {
                DisableNotifsCard.CornerRadius = new CornerRadius(8, 8, 0, 0);
                DisableNotifsCard.BorderThickness = new Thickness(1, 1, 1, 0);
                ExtraControls.Children.Add(DisableNotifsCard);

                Settings.SetValue(Settings.K.DefaultVcpkgTriplet, Vcpkg.GetDefaultTriplet());
                ComboboxCard Vcpkg_DefaultTriplet = new()
                {
                    Text = CoreTools.Translate("Default vcpkg triplet"),
                    SettingName = Settings.K.DefaultVcpkgTriplet,
                    CornerRadius = new CornerRadius(0),
                };
                foreach (string triplet in Vcpkg.GetSystemTriplets())
                {
                    Vcpkg_DefaultTriplet.AddItem(triplet, triplet);
                }

                Vcpkg_DefaultTriplet.ShowAddedItems();
                ExtraControls.Children.Add(Vcpkg_DefaultTriplet);

                ButtonCard Vcpkg_CustomVcpkgRoot = new()
                {
                    Text = "Change vcpkg root location",
                    ButtonText = "Select",
                    CornerRadius = new CornerRadius(0, 0, 8, 8),
                    BorderThickness = new Thickness(1, 0, 1, 1),
                };
                StackPanel p = new() { Orientation = Orientation.Horizontal, Spacing = 5 };
                var VcPkgRootLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                var ResetVcPkgRootLabel = new HyperlinkButton
                {
                    Content = CoreTools.Translate("Reset"),
                };
                var OpenVcPkgRootLabel = new HyperlinkButton
                {
                    Content = CoreTools.Translate("Open"),
                };

                VcPkgRootLabel.Text = Settings.Get(Settings.K.CustomVcpkgRoot)
                    ? Settings.GetValue(Settings.K.CustomVcpkgRoot)
                    : "%VCPKG_ROOT%";
                OpenVcPkgRootLabel.IsEnabled = Settings.Get(Settings.K.CustomVcpkgRoot);
                ResetVcPkgRootLabel.IsEnabled = Settings.Get(Settings.K.CustomVcpkgRoot);

                ResetVcPkgRootLabel.Click += (_, _) =>
                {
                    VcPkgRootLabel.Text = "%VCPKG_ROOT%";
                    Settings.Set(Settings.K.CustomVcpkgRoot, false);
                    ResetVcPkgRootLabel.IsEnabled = false;
                    OpenVcPkgRootLabel.IsEnabled = false;
                };

                OpenVcPkgRootLabel.Click += (_, _) =>
                {
                    string directory = Settings
                        .GetValue(Settings.K.CustomVcpkgRoot)
                        .Replace("/", "\\");
                    if (directory.Any())
                        CoreTools.Launch(directory);
                };

                Vcpkg_CustomVcpkgRoot.Click += (_, _) =>
                {
                    ExternalLibraries.Pickers.FolderPicker openPicker = new(
                        MainApp.Instance.MainWindow.GetWindowHandle()
                    );
                    string folder = openPicker.Show();
                    if (folder != string.Empty)
                    {
                        Settings.SetValue(Settings.K.CustomVcpkgRoot, folder);
                        VcPkgRootLabel.Text = folder;
                        ResetVcPkgRootLabel.IsEnabled = true;
                        OpenVcPkgRootLabel.IsEnabled = true;
                    }
                };

                p.Children.Add(VcPkgRootLabel);
                p.Children.Add(ResetVcPkgRootLabel);
                p.Children.Add(OpenVcPkgRootLabel);
                Vcpkg_CustomVcpkgRoot.Description = p;
                Vcpkg_CustomVcpkgRoot.Click += (_, _) => _ = ReloadPackageManager();
                ExtraControls.Children.Add(Vcpkg_CustomVcpkgRoot);
            }
            // -------------------------------- DEFAULT EXTRA SETTINGS --------------------------------------
            else
            {
                DisableNotifsCard.CornerRadius = new CornerRadius(8);
                DisableNotifsCard.BorderThickness = new Thickness(1, 1, 1, 1);
                ExtraControls.Children.Add(DisableNotifsCard);
            }

            // ── Per-manager minimum update age
            ExtraControls.Children.Add(new TextBlock
            {
                Margin = new Thickness(4, 24, 4, 8),
                FontWeight = new Windows.UI.Text.FontWeight(600),
                Text = CoreTools.Translate("Update security"),
            });

            (string Label, string Value)[] ageItems =
            [
                (CoreTools.Translate("Use global setting"), ""),
                (CoreTools.Translate("No minimum age"), "0"),
                (CoreTools.Translate("1 day"), "1"),
                (CoreTools.Translate("{0} days", 3), "3"),
                (CoreTools.Translate("{0} days", 7), "7"),
                (CoreTools.Translate("{0} days", 14), "14"),
                (CoreTools.Translate("{0} days", 30), "30"),
                (CoreTools.Translate("Custom..."), "custom"),
            ];

            var ageCombo = new ComboBox { MinWidth = 200 };
            foreach (var (label, _) in ageItems)
                ageCombo.Items.Add(label);

            string? savedAgeVal = Settings.GetDictionaryItem<string, string>(
                Settings.K.PerManagerMinimumUpdateAge, Manager.Name);
            int savedAgeIdx = Array.FindIndex(ageItems, i => i.Value == (savedAgeVal ?? ""));
            ageCombo.SelectedIndex = savedAgeIdx >= 0 ? savedAgeIdx : 0;

            var customAgeInput = new TextBox
            {
                MinWidth = 200,
                PlaceholderText = CoreTools.Translate("e.g. 10"),
                Text = Settings.GetDictionaryItem<string, string>(
                    Settings.K.PerManagerMinimumUpdateAgeCustom, Manager.Name) ?? "",
            };
            customAgeInput.TextChanged += (_, _) =>
            {
                string current = customAgeInput.Text ?? "";
                string filtered = new string(current.Where(char.IsDigit).ToArray());
                if (filtered != current)
                {
                    customAgeInput.Text = filtered;
                    return;
                }
                if (filtered.Length > 0)
                    Settings.SetDictionaryItem(
                        Settings.K.PerManagerMinimumUpdateAgeCustom, Manager.Name, filtered);
                else
                    Settings.RemoveDictionaryKey<string, string>(
                        Settings.K.PerManagerMinimumUpdateAgeCustom, Manager.Name);
            };

            bool initiallyCustomAge = savedAgeVal == "custom";
            bool ageSupported = !_managersWithoutUpdateDate.Contains(Manager.Name);

            object ageCardDescription = !ageSupported
                ? new TextBlock
                {
                    Text = CoreTools.Translate(
                            "{pm} does not provide release dates for its packages, so this setting will have no effect")
                        .Replace("{pm}", Manager.DisplayName),
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(255, 224, 82, 82)),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                }
                : (object)CoreTools.Translate("Override the global minimum update age for this package manager");

            ageCombo.IsEnabled = ageSupported;
            customAgeInput.IsEnabled = ageSupported;

            var minimumAgeCard = new SettingsCard
            {
                Header = CoreTools.Translate("Minimum age for updates"),
                Description = ageCardDescription,
                Content = ageCombo,
                CornerRadius = initiallyCustomAge ? new CornerRadius(8, 8, 0, 0) : new CornerRadius(8),
                BorderThickness = new Thickness(1),
            };
            var customAgeCard = new SettingsCard
            {
                Header = CoreTools.Translate("Custom minimum age (days)"),
                Content = customAgeInput,
                Visibility = initiallyCustomAge ? Visibility.Visible : Visibility.Collapsed,
                CornerRadius = new CornerRadius(0, 0, 8, 8),
                BorderThickness = new Thickness(1, 0, 1, 1),
            };

            ageCombo.SelectionChanged += (_, _) =>
            {
                int idx = ageCombo.SelectedIndex;
                if (idx < 0) return;
                string val = ageItems[idx].Value;

                bool isCustom = val == "custom";
                customAgeCard.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
                minimumAgeCard.CornerRadius = isCustom
                    ? new CornerRadius(8, 8, 0, 0)
                    : new CornerRadius(8);

                if (string.IsNullOrEmpty(val))
                    Settings.RemoveDictionaryKey<string, string>(
                        Settings.K.PerManagerMinimumUpdateAge, Manager.Name);
                else
                    Settings.SetDictionaryItem(
                        Settings.K.PerManagerMinimumUpdateAge, Manager.Name, val);
            };

            ExtraControls.Children.Add(minimumAgeCard);
            ExtraControls.Children.Add(customAgeCard);

            // Hide the AppExecutionAliasWarning element if Manager is not Pip
            if (Manager is Pip)
            {
                ManagerLogs.CornerRadius = new CornerRadius(8, 8, 0, 0);
                AppExecutionAliasWarningLabel.Text =
                    "If Python cannot be found or is not listing packages but is installed on the system, you may need to disable the \"python.exe\" App Execution Alias in the settings.";
            }
            else
            {
                AppExecutionAliasWarning.Visibility = Visibility.Collapsed;
            }

            base.OnNavigatedTo(e);
        }

        private void ShowVersionHyperlink_Click(object sender, RoutedEventArgs e) =>
            ApplyManagerState(true);

        void ApplyManagerState(bool ShowVersion = false)
        {
            if (Manager is null)
                throw new InvalidDataException();

            // Load version and manager path
            ShowVersionHyperlink.Visibility = Visibility.Collapsed;
            LongVersionTextBlock.Visibility = Visibility.Collapsed;
            LongVersionTextBlock.Text = Manager.Status.Version + "\n";
            LocationLabel.Text =
                Manager.Status.ExecutablePath + " " + Manager.Status.ExecutableCallArgs.Trim();
            if (Manager.Status.ExecutablePath == "")
                LocationLabel.Text = CoreTools.Translate(
                    "The executable file for {0} was not found",
                    Manager.DisplayName
                );

            // Load executable selection
            ExecutableComboBox.SelectionChanged -= ExecutableComboBox_SelectionChanged;
            ExecutableComboBox.Items.Clear();
            foreach (var path in Manager.FindCandidateExecutableFiles())
            {
                ExecutableComboBox.Items.Add(path);
            }
            string selectedValue =
                Settings.GetDictionaryItem<string, string>(Settings.K.ManagerPaths, Manager.Name)
                ?? "";
            if (string.IsNullOrEmpty(selectedValue))
            {
                var exe = Manager.GetExecutableFile();
                selectedValue = exe.Item1 ? exe.Item2 : "";
            }

            ExecutableComboBox.SelectedValue = selectedValue;
            ExecutableComboBox.SelectionChanged += ExecutableComboBox_SelectionChanged;

            // Load version block text and style
            if (_isLoading)
            {
                ManagerStatusBar.Severity = InfoBarSeverity.Informational;
                ManagerStatusBar.Title = CoreTools.Translate("Please wait...");
                ManagerStatusBar.Message = "";
            }
            else if (!Manager.IsEnabled())
            {
                ManagerStatusBar.Severity = InfoBarSeverity.Warning;
                ManagerStatusBar.Title = CoreTools
                    .Translate("{pm} is disabled")
                    .Replace("{pm}", Manager.DisplayName);
                ManagerStatusBar.Message = CoreTools
                    .Translate("Enable it to install packages from {pm}.")
                    .Replace("{pm}", Manager.DisplayName);
            }
            else if (Manager.Status.Found)
            {
                ManagerStatusBar.Severity = InfoBarSeverity.Success;
                ManagerStatusBar.Title = CoreTools
                    .Translate("{pm} is enabled and ready to go")
                    .Replace("{pm}", Manager.DisplayName);
                if (!Manager.Status.Version.Contains('\n'))
                {
                    ManagerStatusBar.Message =
                        CoreTools.Translate("{pm} version:").Replace("{pm}", Manager.DisplayName)
                        + $" {Manager.Status.Version}";
                }
                else if (ShowVersion)
                {
                    ManagerStatusBar.Message = CoreTools
                        .Translate("{pm} version:")
                        .Replace("{pm}", Manager.DisplayName);
                    LongVersionTextBlock.Visibility = Visibility.Visible;
                }
                else
                {
                    ManagerStatusBar.Message = "";
                    ShowVersionHyperlink.Visibility = Visibility.Visible;
                }
            }
            else // manager was not found
            {
                ManagerStatusBar.Severity = InfoBarSeverity.Error;
                ManagerStatusBar.Title = CoreTools
                    .Translate("{pm} was not found!")
                    .Replace("{pm}", Manager.DisplayName);
                ManagerStatusBar.Message = CoreTools
                    .Translate("You may need to install {pm} in order to use it with UniGetUI.")
                    .Replace("{pm}", Manager.DisplayName);
            }
        }

        private void ManagerPath_Click(object sender, RoutedEventArgs e) =>
            _ = _managerPath_Click();

        private async Task _managerPath_Click()
        {
            WindowsClipboard.SetText(LocationLabel.Text);
            CopyButtonIcon.Symbol = Symbol.Accept;
            await Task.Delay(1000);
            CopyButtonIcon.Symbol = Symbol.Copy;
        }

        private void ManagerLogs_Click(object sender, RoutedEventArgs e)
        {
            MainApp.Instance.MainWindow.NavigationPage.OpenManagerLogs(Manager);
        }

        private void ExecutableComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(ExecutableComboBox.SelectedValue.ToString()))
                return;

            Settings.SetDictionaryItem(
                Settings.K.ManagerPaths,
                Manager!.Name,
                ExecutableComboBox.SelectedValue.ToString()
            );
            _ = ReloadPackageManager();
        }

        private void GoToSecureSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            MainApp.Instance.MainWindow.NavigationPage.OpenSettingsPage(typeof(Administrator));
        }

        private void EnableManager_OnStateChanged(object? sender, EventArgs e) =>
            _ = ReloadPackageManager();

        private async Task ReloadPackageManager()
        {
            if (Manager is null)
                return;
            int loadingId = DialogHelper.ShowLoadingDialog(CoreTools.Translate("Please wait..."));
            _isLoading = true;
            ApplyManagerState();
            await Task.Run(Manager.Initialize);
            _isLoading = false;
            ApplyManagerState();
            DialogHelper.HideLoadingDialog(loadingId);
        }
    }
}
